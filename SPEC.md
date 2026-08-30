# PC Core — Specification

**Current version: v0.4 (Authenticated Tailscale Remote Transport).**

This document is cumulative. Sections 1–11 describe the v0.1 foundation, 12 covers v0.2, 13–25 cover v0.3, and **26–37 cover v0.4**. Throughout, what is *implemented* is separated from what is *deliberately postponed* — nothing here describes behaviour that does not exist.

| Milestone | Adds | Status |
|---|---|---|
| v0.1 | Protocol, capability layer, filesystem boundary, loopback transport, five read-only capabilities | shipped |
| v0.2 | Security-state service, three `security.*` capabilities, `security.state_changed`, typed caller identity | shipped (§12) |
| v0.3 | State persistence, event-lock refactor, PC identity, one Primary Phone, HMAC authentication, Windows auto-start | shipped (§13–25) |
| v0.4 | Tailscale-bound remote transport, session authentication, LOCAL/DEVICE capability policy, remote events | shipped (§26–37) |
| later | Android client, security recording, activity detection, AI bridge | not started |

**Scope note.** From v0.3 onward this is explicitly a personal-use system: one owner, one Windows PC, one Android phone. Narrower designs were chosen over more general ones throughout (§13).

---

## 1. Purpose

PC Core is a user-level Windows process that exposes a **small, explicit set of capabilities** over a **structured request/response/event protocol**.

It is the local control layer for a larger system in which an Android phone and/or an AI agent may operate the owner's Windows computer. Those callers are **out of scope for v0.1** — but the contracts they will speak are defined here.

### Guiding rule

> External controllers interact with the computer **through capabilities**, never through arbitrary Windows functionality.

A capability is a named, typed action. If PC Core does not expose it, a caller cannot do it. This is the security model and the extensibility model at the same time.

---

## 2. Non-goals for v0.1

Explicitly **not** implemented, and not designed around beyond keeping a seam open:

| Not in v0.1 | Seam that keeps it possible later |
|---|---|
| Android application | Capability contracts are transport-agnostic |
| Claude Code / AI integration | `ICapabilityDispatcher` is a callable facade for any adapter |
| Tailscale / STUN / TURN / ICE / NAT traversal | Transport is a separate project; capabilities never see it |
| Internet- or LAN-facing listener | Transport binds loopback only, by construction |
| Screen *streaming* | `IScreenCapture` is an interface; DXGI Desktop Duplication can replace GDI |
| Keyboard / mouse input | Would be new capabilities, no core changes |
| Microphone / camera | Same |
| Security recording / owner notifications | v0.2 adds the *state* only (§12). No recording, capture, monitoring, or notification is attached to it |
| Arbitrary shell execution | Never. Not a planned capability. |
| File writes / delete / move / copy / upload | Files capability is read-only in v0.1 |
| Administrator privileges | Every v0.1 code path works as a standard user |
| Windows service installation | Runs as a console process |

**Design discipline:** no abstraction exists in this codebase unless a v0.1 capability uses it. The extension points listed above are interfaces that already have exactly one real implementation and one real caller — not speculative frameworks.

---

## 3. Architecture

```
                    +----------------------------------------------+
   future callers   |  Android app | AI bridge | debug tools | ...  |
                    +----------------------+-----------------------+
                                           |  (same protocol)
                    +----------------------v-----------------------+
   TRANSPORT        |  PcCore.Transport.Loopback                   |
   (v0.1: loopback) |  HTTP POST /rpc  ·  WebSocket /ws  ·  wwwroot |
                    |  -- owns sockets, auth, JSON I/O             |
                    +----------------------+-----------------------+
                                           |  RequestEnvelope (typed)
                    +----------------------v-----------------------+
   PROTOCOL         |  PcCore.Protocol                             |
                    |  Envelopes · ErrorCodes · ProtocolCodec      |
                    |  -- the ONLY place System.Text.Json is used  |
                    +----------------------+-----------------------+
                                           |
                    +----------------------v-----------------------+
   CAPABILITY       |  PcCore.Capabilities                         |
                    |  Dispatcher · Registry · CapabilityAction<,> |
                    |  Security/ PathAuthorizer, AllowedRoots      |
                    |  System/ Files/ Screen/ actions              |
                    +----------------------+-----------------------+
                                           |  IScreenCapture, IRealPathResolver,
                                           |  ISystemInfoProvider, IProcessLister
                    +----------------------v-----------------------+
   PLATFORM         |  PcCore.Windows                              |
                    |  GdiScreenCapture · Win32RealPathResolver    |
                    |  WindowsSystemInfo · WindowsProcessLister    |
                    |  -- the only project with P/Invoke           |
                    +----------------------------------------------+

                    +----------------------------------------------+
   HOST             |  PcCore.Host -- composition root, DI, config, |
                    |  logging, lifetime. Knows every layer;        |
                    |  no layer knows the host.                     |
                    +----------------------------------------------+
```

### 3.1 Dependency rules (enforced by project references)

- `PcCore.Protocol` -> **nothing**. Pure contracts.
- `PcCore.Capabilities` -> `Protocol`. No Windows API, no `System.Drawing`, no P/Invoke.
- `PcCore.Windows` -> `Capabilities`, `Protocol`. Implements platform interfaces only.
- `PcCore.Transport.Loopback` -> `Capabilities`, `Protocol`. **Contains no Windows-control logic.**
- `PcCore.Host` -> all of the above. The only place they are wired together.

Two invariants this buys us:

1. **A capability cannot know how the caller arrived.** `CapabilityContext` carries a `CallerDescriptor` (a transport *name* and an opaque connection id) for logging only. There is no socket, no `HttpContext`, no request headers below the transport boundary.
2. **A transport cannot control Windows.** It can only hand a `RequestEnvelope` to `ICapabilityDispatcher` and serialize what comes back.

### 3.2 Why these five projects and not fewer / more

The brief asks to separate host, protocol, capabilities, Windows implementations, transport, security, and tests. That is seven concepts. We use **five source projects + one test project**:

- Security is a *folder* inside `PcCore.Capabilities`, not a project — path authorization is a capability-layer policy and has no independent consumer.
- Transport is a project rather than a folder because the whole point of the milestone is that a second transport (remote) can be added without touching anything else. A project reference is a compiler-enforced statement of that boundary; a folder is not.
- `PcCore.Windows` is a project rather than a folder because it is the only assembly that needs the `net10.0-windows` TFM and `System.Drawing.Common`. Keeping it separate is what allows `PcCore.Capabilities` to stay platform-neutral and unit-testable with fakes.

---

## 4. Protocol

### 4.1 Envelopes

Three message shapes. All JSON, all `snake_case` on the wire.

**Request**

```json
{
  "protocol_version": 1,
  "id": "b1c9de70",
  "type": "request",
  "action": "files.list",
  "params": { "path": "C:\\Users\\Melih\\Desktop" }
}
```

**Response**

```json
{
  "protocol_version": 1,
  "id": "b1c9de70",
  "type": "response",
  "action": "files.list",
  "success": true,
  "result": { "entry_count": 3, "entries": [] },
  "error": null,
  "duration_ms": 4
}
```

**Response (failure)** — `success: false`, `result: null`, and a *structured* error:

```json
{
  "protocol_version": 1,
  "id": "b1c9de70",
  "type": "response",
  "action": "files.list",
  "success": false,
  "result": null,
  "error": {
    "code": "path_not_allowed",
    "message": "Path is outside the configured allowed roots.",
    "details": null
  },
  "duration_ms": 1
}
```

**Event** (server -> client, WebSocket only)

```json
{
  "protocol_version": 1,
  "id": "e77f0a12",
  "type": "event",
  "event": "core.connected",
  "payload": { "server_version": "0.1.0" },
  "timestamp_utc": "2026-08-28T09:31:22.1043210Z"
}
```

### 4.2 Versioning

`protocol_version` is a single integer, currently `1`. v0.1 rule: a request whose `protocol_version` is present and not `1` is rejected with `unsupported_protocol_version`; a request that omits it is accepted as `1`. No negotiation handshake, no capability advertisement — those are future concerns and adding them now would be speculation.

### 4.3 Error codes

A closed set of strings. Callers switch on `code`; `message` is for humans and must never be parsed. `details` is optional and must never contain file contents, secrets, or stack traces.

| Code | Meaning |
|---|---|
| `invalid_request` | Envelope malformed, unparseable, or missing `action` |
| `unsupported_protocol_version` | `protocol_version` not understood |
| `unknown_action` | No capability registered for `action` |
| `invalid_parameters` | Params missing, wrong type, or failed validation |
| `path_not_allowed` | Path resolved outside every configured allowed root |
| `not_found` | Target path does not exist |
| `access_denied` | OS refused access (ACL, lock, in-use) |
| `unsupported` | Action valid but unavailable in this environment |
| `internal_error` | Unexpected fault; details suppressed |

### 4.4 Where JSON lives

`System.Text.Json` is referenced by **`PcCore.Protocol` only**. `ProtocolCodec` is the single type that parses or emits wire text.

The one deliberate compromise: `RequestEnvelope.Params` is a `JsonElement?`, because the envelope must be decoded before we know which action — and therefore which parameter type — it targets. The dispatcher immediately binds it to the action's declared `TParams` record and **capabilities only ever see strongly-typed records**. No capability, no transport, and no Windows code touches `JsonElement`.

---

## 5. Capabilities in v0.1

Each action is a `CapabilityAction<TParams, TResult>` with typed records for both sides.

### `system.get_info` — params: none

Returns machine name, OS description/version, architecture, process architecture, PC Core version, .NET runtime version, machine uptime, current local + UTC timestamp, logical processor count, and whether the process is elevated (reported so callers can *see* we are not elevated).

Deliberately excluded as sensitive: username, domain, serial numbers, MAC/IP addresses, installed software.

### `system.list_processes` — params: none

Returns `{ count, processes: [{ pid, name, working_set_bytes? }] }`.

Lightweight by design. Command lines, file paths, window titles, and owners are **not** returned — they leak far more than a process list needs to. Processes that vanish or refuse access between enumeration and read are skipped silently rather than failing the call; no privilege is requested.

### `files.list` — params: `{ path, include_hidden? = false }`

Lists one directory. Returns `{ path, entry_count, skipped_count, entries: [{ name, type, size_bytes?, modified_utc?, created_utc?, is_hidden, is_read_only, is_link }] }` where `type` is `file` or `directory`. Entries the OS refuses to describe are counted in `skipped_count` and never named — one unreadable entry must not fail the whole listing.

Non-recursive. Symlinks/junctions inside the listing are reported with `is_link: true` and are **not** followed, and their targets are **not** disclosed.

### `files.stat` — params: `{ path }`

Metadata for a single file or directory: `path`, `name`, `type`, `size_bytes?`, `modified_utc`, `created_utc`, `accessed_utc`, `is_hidden`, `is_read_only`, `is_link`. Missing target -> `not_found`.

### `screen.capture` — params: `{ display? = "virtual", max_width? }`

One request, one PNG. Returns `{ display, width, height, source_width, source_height, content_type, encoding: "base64", data, captured_at_utc }`.

There is no `include_cursor`: drawing the cursor is extra Win32 work nobody asked for in this milestone, and a parameter that silently does nothing is worse than no parameter.

`display` accepts `"virtual"` (whole desktop across monitors), `"primary"`, or a zero-based index. `max_width` downscales proportionally to bound payload size. **No streaming** — the interface (`IScreenCapture`) returns a single frame and has no session or subscription concept to accidentally grow into one.

### Not implemented on purpose

No `files.read`, `files.write`, `files.delete`, `files.move`, `files.copy`, `process.kill`, `process.start`, `input.*`, `audio.*`, `camera.*`, `shell.*`.

---

## 6. Filesystem security boundary

This is the most safety-critical component in v0.1, so it is specified in detail.

### 6.1 Allowed roots

Configured explicitly in `appsettings.json`. Nothing is accessible by default beyond what is listed. Roots may be absolute paths or well-known-folder tokens:

```json
"Files": {
  "AllowedRoots": [ "{Desktop}", "{Documents}", "{Downloads}" ]
}
```

Tokens resolve via `Environment.GetFolderPath` and the Downloads known-folder ID. A root that cannot be resolved or does not exist is **logged and dropped**, not silently treated as `C:\`. If *zero* roots resolve, the file capabilities remain registered but every call returns `path_not_allowed` — failing closed.

### 6.2 Authorization pipeline

Every path from a caller goes through `PathAuthorizer.Authorize(string)` before any I/O:

1. **Reject by shape, before touching the filesystem**
   - null / empty / whitespace
   - not rooted (relative paths are never resolved against a "current" directory)
   - UNC paths (`\\server\share`)
   - Win32 device / extended paths (`\\?\`, `\\.\`, `\\?\GLOBALROOT`)
   - any segment past the drive containing `:` — blocks NTFS **alternate data streams** (`secret.txt:hidden`)
   - characters invalid in Windows paths
2. **Canonicalize lexically** — `Path.GetFullPath` collapses `.`, `..`, duplicate and mixed separators, and trailing dots/spaces.
3. **Resolve to the real path** via `IRealPathResolver`. On Windows this opens a handle with `CreateFileW(FILE_FLAG_BACKUP_SEMANTICS)` and calls `GetFinalPathNameByHandle(VOLUME_NAME_DOS)`. This is the authoritative canonical form: it resolves **symlinks, directory junctions, mount points, and 8.3 short names** (`C:\PROGRA~1` -> `C:\Program Files`) that a lexical check cannot see. For a path that does not exist yet, the deepest existing ancestor is resolved and the remaining segments are appended — so a missing file under a junctioned parent is still judged by the junction's real target.
4. **Containment test by path segment**, not by string prefix. The resolved path is split on the directory separator and compared element-wise (case-insensitively, `OrdinalIgnoreCase`) against each resolved root. This is what makes `C:\Users\Me\Desktop2` fail against root `C:\Users\Me\Desktop`, which a naive `StartsWith` would allow.
5. Only then is the path handed to the capability, which performs read-only operations.

Roots themselves are resolved through the same resolver **once at startup**, so a junctioned root is compared in its real form.

### 6.3 What is protected now

- `..` traversal, in any quantity, mixed with valid segments — collapsed at step 2.
- Forward/back slash mixing, duplicated separators, trailing dot/space tricks — step 2.
- Sibling-directory prefix confusion (`Desktop2` vs `Desktop`) — step 4.
- 8.3 short-name aliasing — step 3.
- Directory junctions and symlinks that point outside a root — step 3.
- Alternate data streams — step 1.
- Device-namespace escapes — step 1.
- Case variation — step 4 is case-insensitive by design, matching NTFS default semantics.
- Links *encountered during listing* are reported but never traversed, and never have their targets disclosed.

### 6.4 Known Windows edge cases NOT fully solved in v0.1

Documented honestly rather than papered over:

- **TOCTOU.** Resolution and the subsequent read are two separate syscalls. An attacker who can create a junction in an allowed root *between* those two calls could redirect the read. Mitigations today: v0.1 is read-only (metadata only — no file contents are ever returned), and an attacker who can write into your Desktop already has your privileges. A future milestone should hold the resolved handle and operate on it directly.
- **Hardlinks.** A hardlink inside an allowed root to a file outside it is indistinguishable from a normal file — `GetFinalPathNameByHandle` returns the *opened* name, not the "original". v0.1 exposes only metadata, so the leak is limited to size and timestamps. Content-returning capabilities must address this (e.g. `FindFirstFileNameW` / file-id checks).
- **Volume mount points** inside a root are followed to their target volume. If that target is outside every root, containment rejects it — correct — but a mount point *inside* a root effectively widens the root. Operators should not configure roots containing mount points.
- **Case-sensitive directories.** NTFS supports per-directory case sensitivity (`fsutil file setCaseSensitiveInfo`). Our `OrdinalIgnoreCase` comparison is more permissive than such a directory, so two distinct files could compare equal for containment. Both are inside the same root, so it does not escape the boundary; noted for correctness.
- **Substituted / mapped drives.** `subst` drives and mapped network drives resolve to their targets and will simply fail containment. Not an escape, but the error may confuse.
- **Long paths (>260 chars).** Handled when `LongPathsEnabled` is set; it is not required, so very deep paths may fail with `access_denied` rather than being served.

---

## 7. Transport for v0.1

### 7.1 Choice: Kestrel on loopback, exposing both HTTP and WebSocket

- **`POST /rpc`** — one JSON request, one JSON response. Trivially testable with `curl`, and the natural shape for `system.*` and `files.*`.
- **`GET /ws`** — WebSocket carrying the same envelopes, plus server-pushed **events**. Requests are correlated by `id`, so a client may have several in flight.
- **`GET /`** — the developer test page (static HTML).
- **`GET /healthz`** — liveness, no auth, no information disclosure.

Both HTTP and WebSocket exist because the protocol has three message types and HTTP alone cannot express the third. The event channel is what the Android UI and future `ai.*` progress events will use; building it now costs ~60 lines and proves the envelope design is right. Everything else (Tailscale, TLS, relays) is a *different transport project* later, and by construction requires no change below `ICapabilityDispatcher`.

### 7.2 Binding

`ListenLocalhost(port)` only — `127.0.0.1` and `[::1]`. No `0.0.0.0`, no `+`, and `ASPNETCORE_URLS` is explicitly ignored. The bind address is not configurable in v0.1; making it configurable is exactly how a "localhost-only" service ends up on the LAN.

### 7.3 Why loopback still needs a token

Loopback is **not** an authorization boundary: every process on the machine, and every web page your browser loads, can send requests to `127.0.0.1`. So:

- A 256-bit session token is generated per run, printed to the console, and written to `%LOCALAPPDATA%\PcCore\session.token`.
- `/rpc` and `/ws` require `X-PcCore-Token`, compared in fixed time. The test page is served by the host with the token injected, so local development is unaffected.
- Browsers cannot set custom headers on a WebSocket handshake, so `/ws` also accepts the token as a `Sec-WebSocket-Protocol` entry (`pccore.token.<token>`, alongside `pccore.v1`). Only `pccore.v1` is echoed back as the negotiated subprotocol; the token-bearing entry never is. A URL query parameter was rejected for this: secrets in URLs end up in logs and history.
- `Host` and `Origin` headers are validated to block **DNS rebinding**: a request whose `Host` is not a loopback literal, or whose `Origin` is a remote site, is rejected. This is the attack that turns "it's only on localhost" into "any website can control your PC".
- Request bodies are capped at 64 KB and the WebSocket receive buffer is bounded.

This is ~80 lines and is the difference between a dev toy and a defensible foundation.

---

## 8. Logging

`Microsoft.Extensions.Logging`, console sink, category per component.

Logged: startup (version, port, resolved allowed roots, binding), each request (`id`, `action`, transport, connection id), each completion (outcome, error code, duration ms), capability faults with codes, shutdown.

**Never logged:** file contents, screenshot bytes, the session token, process command lines. Caller-supplied paths are logged at `Debug` only — useful for diagnosis, not printed by default.

---

## 9. Testing strategy

xUnit, plain asserts (fewer dependencies). Categories:

1. **Protocol** — round-trip all three envelopes; snake_case naming; unknown wire fields ignored; malformed JSON -> `invalid_request`; missing `action` -> `invalid_request`; bad `protocol_version` -> `unsupported_protocol_version`; error envelope shape.
2. **Path security** — the largest suite. Allowed: root itself, nested dir, nested file, mixed case, `..` that stays inside. Denied: `..` escaping, relative, UNC, `\\?\`, ADS, sibling-prefix (`Desktop2`), sibling-file-prefix, empty, invalid chars. Plus a **real junction** created in a temp dir (`mklink /J` needs no admin) pointing outside the root -> denied; and a junction pointing *inside* the root -> allowed.
3. **Dispatcher** — unknown action; params missing when required; params of wrong JSON type; `CapabilityException` -> its declared code; unexpected exception -> `internal_error` with no stack in `details`; success path sets `success` / `result` / `duration_ms`.
4. **Files capability** — list a temp tree (files + subdirs + hidden); stat file; stat directory; stat missing -> `not_found`; list outside roots -> `path_not_allowed`; list a file instead of a directory -> `invalid_parameters`.
5. **System capability** — info has non-empty machine name and versions; process list contains the current process; inaccessible processes do not fail the call.
6. **Screen capability** — with a fake `IScreenCapture`: PNG round-trips to base64, `max_width` validation. A real GDI capture test runs only when an interactive desktop is present.

7. **Loopback transport (integration)** — the transport is hosted in-process on a real socket and driven with a real `HttpClient` and `ClientWebSocket`: `/rpc` success and protocol-failure shapes, missing/invalid token, cross-origin `Origin`, spoofed `Host`, unauthenticated `/healthz`, the test page, WebSocket request/response, structured errors, subprotocol negotiation, and a handshake without a token.

Temporary directories are created per-test under the system temp path and deleted afterwards. No test touches the real Desktop / Documents.

Category 7 exists because of a real defect: `core.connected` was originally published *before* the WebSocket connection subscribed to the event stream, so the greeting reached nobody and the entire event path was dead while looking correctly wired. Unit tests of the capability layer could not see it. The regression test is bounded by a timeout so the failure mode — a frame that never arrives — fails in seconds instead of hanging the run.

---

## 10. Configuration

`appsettings.json` beside the executable; overridable by `PCCORE_`-prefixed environment variables.

```json
{
  "PcCore": {
    "Transport": { "Port": 47811, "RequireToken": true },
    "Files": { "AllowedRoots": [ "{Desktop}", "{Documents}", "{Downloads}" ] },
    "Screen": { "Enabled": true, "MaxWidthLimit": 7680 }
  }
}
```

---

## 11. Future extension points (interfaces that exist, features that do not)

| Future need | Where it plugs in | What v0.1 already guarantees |
|---|---|---|
| Android client | Any transport | Same envelopes, actions, and errors |
| Remote connectivity without port forwarding | New `PcCore.Transport.*` project | Capabilities never see the transport |
| AI / Claude Code bridge | New caller of `ICapabilityDispatcher` | PC Core runs and is fully usable with no AI present |
| `ai.started` / `ai.progress` / `ai.approval_required` / `ai.completed` / `ai.failed` | `IEventPublisher` -> WebSocket | Event envelope and delivery path already exist and are exercised |
| Screen streaming | New `IScreenCapture` impl (DXGI) + a streaming capability | Capture is already behind an interface with no session state |
| Input / camera / microphone | New capability classes + registration | Registry is additive; nothing else changes |
| `OWNER_PRESENT` / `ARMED` / `SECURITY_SESSION` | A separate subsystem observing the dispatcher | `CapabilityContext` is the natural place to carry a mode; nothing today assumes one exists |

The last row is the important one: v0.1 contains **no** security-mode code, but also nothing that would have to be unpicked to add it — every capability invocation already passes through one choke point (`CapabilityDispatcher`) where a policy check can be inserted.

---

# v0.2 — Security State Foundation

## 12. Owner presence / security state

v0.2 gives PC Core one reliable internal answer to "is the owner here?". That is **all** it gives it. No detection, no recording, no alerting, no policy — those consume this state in later milestones; none of them exist now.

### 12.1 The three states

Serialized values are SCREAMING_SNAKE_CASE and pinned by attribute (`[JsonStringEnumMemberName]`), not derived from the codec's naming policy, so renaming a C# member cannot silently change the protocol.

| Wire value | C# | Meaning in v0.2 |
|---|---|---|
| `OWNER_PRESENT` | `SecurityState.OwnerPresent` | Owner considered present; machine operates normally. No session is or can be active. |
| `ARMED` | `SecurityState.Armed` | Owner deliberately indicated they are away. **Nothing watches for activity** — v0.2 only records the declaration. |
| `SECURITY_SESSION` | `SecurityState.SecuritySession` | A security session has started. **No behaviour is attached**: no capture, no recording, no notification. |

`SecurityState` lives in `PcCore.Protocol`, not beside the service that owns it, for the same reason `ErrorCodes` does: its spelling is a contract callers switch on. It also keeps `System.Text.Json` confined to that one assembly (§4.4).

### 12.2 The state service

`ISecurityStateService` (`PcCore.Capabilities/Security/SecurityStateService.cs`) is the single authoritative holder. It is registered as a **singleton**; "authoritative" and "one per request" are incompatible. No capability keeps a copy — `security.get_state`, `security.arm` and `security.disarm` all read and mutate through it.

- **Initial state is always `OWNER_PRESENT`**, with `previous_state: null` and `since_utc` = process start.
- **State was in memory only in v0.2.** v0.3 persists it — see §15, which supersedes this bullet. The restart rule is now that every state, including `SECURITY_SESSION`, carries forward.
- Startup is **not** a state change and publishes no event.

### 12.3 Transition table

The complete set, as data in `SecurityStateTransitions.Allowed`:

| From | To | Trigger in v0.2 |
|---|---|---|
| `OWNER_PRESENT` | `ARMED` | `security.arm` |
| `ARMED` | `OWNER_PRESENT` | `security.disarm` |
| `ARMED` | `SECURITY_SESSION` | **internal only** — `ISecurityStateService.RequestTransition`. No capability exposes it. |
| `SECURITY_SESSION` | `OWNER_PRESENT` | `security.disarm` |

Everything else is refused with `invalid_state_transition`. A refused transition **never** lands the system in some other valid state — silently redirecting is how a security state stops meaning anything.

Rejected by construction, and worth naming explicitly: `OWNER_PRESENT -> SECURITY_SESSION` (a session can only follow a deliberate arming) and `SECURITY_SESSION -> ARMED` (below).

A table rather than a state-machine library: three states and four edges, all reviewable at once. A framework would add a dependency and hide the part most worth reading.

#### Why `SECURITY_SESSION -> ARMED` is deliberately **not** allowed

It is superficially attractive — the owner is still away, so after a session "finishes" the machine could quietly resume watching. Two reasons against:

1. **Nothing can legitimately trigger it.** The only honest trigger is a session-lifecycle rule (quiet period, timeout, evidence-complete signal), and none exists in v0.2. Adding the edge now creates a transition with no caller and no definition of when it fires — exactly the speculative machinery this project avoids.
2. **It would let an incident close itself.** Returning to `ARMED` without the owner involved means the system decides on its own that the event is over. In v0.2 ending a session is an owner-visible act (`security.disarm`), which keeps the state trail honest: a session ends because someone came back, not because a timer elapsed.

When session lifecycle rules arrive, enabling it is one row in `Allowed` plus its tests. The decision is pinned by a test, so adding an edge requires changing a test that says so out loud.

### 12.4 Capabilities

#### `security.get_state` — params: none

Returns `{ state, previous_state, since_utc }`. `previous_state` is null until the first change. Nothing else is exposed — no reasons, no caller ids, no configuration. A test pins the property set so internals cannot drift into a response any local caller can read.

#### `security.arm` — params: none

`OWNER_PRESENT -> ARMED`. Returns `{ changed, state, previous_state, since_utc }`.

- **Already `ARMED`:** succeeds with `changed: false`, publishes **no** event. Re-arming is a harmless restatement of intent, and a client retrying after a dropped response must not be punished for it.
- **During `SECURITY_SESSION`:** **refused** with `invalid_state_transition`. That edge is absent from the table on purpose; treating "arm" as "stand the incident down" would be the system inventing an owner decision.

#### `security.disarm` — params: none

Returns to `OWNER_PRESENT` from **both** `ARMED` and `SECURITY_SESSION`. Same result shape as `arm`.

- **Already `OWNER_PRESENT`:** succeeds with `changed: false`, no event.
- Disarm is the one action that must never fail for a legitimate owner: being unable to stand your own machine down is a worse failure than disarming twice.

`changed` is what distinguishes "I armed it" from "it was already armed". Both are successes; a caller that needs to tell them apart reads the flag rather than diffing states itself.

#### There is deliberately no `security.start_session`

`ARMED -> SECURITY_SESSION` is reachable only through `ISecurityStateService.RequestTransition`, the path a future activity detector will use. Exposing it as a capability would let any local caller declare an incident — a decision that should belong to detection logic that does not exist yet. A test asserts no such action is registered.

### 12.5 `security.state_changed`

Published through the **existing** `IEventPublisher` / `EventBus` / WebSocket path from v0.1. No second event bus was introduced.

```json
{
  "protocol_version": 1,
  "id": "9f2c0a1b",
  "type": "event",
  "event": "security.state_changed",
  "payload": { "previous_state": "OWNER_PRESENT", "current_state": "ARMED" },
  "timestamp_utc": "2026-08-29T09:14:02.4471000Z"
}
```

Published **if and only if the state actually changed**. A no-op arm/disarm publishes nothing: a duplicate would make listeners believe the owner acted twice. The occurrence time is the envelope's `timestamp_utc`; repeating it in the payload would create two timestamps that can disagree.

### 12.6 Concurrency

One `Lock` guards the state triple (state, previous, since) **and** the publish. Requests may arrive concurrently over HTTP and WebSocket, so this is not theoretical.

In v0.2 the publish happened **inside** the lock, to keep event order aligned with commit order. **v0.3 replaces this** with a commit-ordered queue drained under a separate lock, so subscribers never run under the mutation lock while ordering is still guaranteed. See §16, which supersedes this paragraph.

No distributed synchronisation, no channels, no actor model: one lock, one file, auditable in a minute.

### 12.7 Caller identity

`CallerDescriptor` gains two fields:

```csharp
CallerDescriptor(string Transport, string ConnectionId, ActorType ActorType, string? DeviceId = null)
```

`ActorType` is `LOCAL` | `DEVICE` | `AI`. **v0.2 only ever produces `LOCAL`,** and `DeviceId` is always null.

This is **typed metadata only**. Nothing reads it to make an authorization decision, and no permission depends on it. It exists so that when device pairing and an AI bridge arrive, identity is already threaded through the dispatcher rather than being retrofitted into every capability.

`ActorType` was made a **required** parameter rather than defaulting to `Local`: a default would let a future device transport silently mislabel its callers as local. The compiler found every construction site instead.

A constructor guard rejects a `DeviceId` on any non-`Device` actor. It is a modelling guard, not a security control — its job is to stop a fabricated device id from being stamped onto an unauthenticated caller where later code could read it and conclude a pairing had happened. **No device authentication exists, and no synthetic device ids are minted anywhere.**

The v0.1 invariant is unchanged and still tested: `CallerDescriptor` carries no socket, no `HttpContext`, no headers, no IP address.

### 12.8 The policy seam (documented, not implemented)

A future policy decision point belongs in exactly one place: **`CapabilityDispatcher.DispatchAsync`**, between resolving the action and binding parameters. Every invocation from every transport already funnels through it, and at that point all four inputs a policy would need are in hand:

- caller identity — `CallerDescriptor.ActorType`
- device identity — `CallerDescriptor.DeviceId`
- requested capability — `capability.Name`
- current security state — inject `ISecurityStateService`

**v0.2 implemented none of it.** **v0.4 occupies this seam** — see §31. The policy remains a small typed rule set rather than RBAC or a permission catalogue.

### 12.9 Testing added in v0.2

47 tests, targeted rather than padded:

- **State service** — initial state; each of the four valid transitions; rejected transitions (`OWNER_PRESENT -> SECURITY_SESSION`, `SECURITY_SESSION -> ARMED`); `CanTransitionTo` agreeing with `RequestTransition`; `since_utc` semantics including that a no-op does not reset the clock; the internal session-escalation path; the transition table pinned edge-for-edge.
- **Idempotency** — arm and disarm from every state, asserting both `changed: false` and *no event*.
- **Events** — payload shape; one event per real change across a full arm → session → disarm cycle with no-ops and a rejection interleaved.
- **Concurrency** — 32 threads racing to arm produce exactly one change and one event; 24 threads × 25 alternating arm/disarm produce an event trail that is a valid walk of the transition table ending at the final state, which is what proves events cannot be reordered relative to the state.
- **Dispatcher integration** — all three capabilities through the real dispatcher, including the `invalid_state_transition` mapping and the state being left untouched by a refusal.
- **Serialization** — every state and actor type pinned to its exact wire string; the event envelope parsed field-by-field against the documented shape.
- **Caller identity** — `Internal` is a local actor with no device; the device-id guard; the property set.
- **Transport end-to-end** — a state change made over `/rpc` reaches a listening WebSocket client; an idempotent command sends nothing.

### 12.10 Explicitly NOT in v0.2

Not implemented, and nothing was added in anticipation of them: activity detection; screen-recording loops; camera or microphone capture; keylogging; window/app activity monitoring; login/session event collection; evidence storage or encryption; phone notifications; live remote viewing; hidden process behaviour; persistence or startup registration; authorization policy; device authentication.

### 12.11 v0.1 guarantees, re-verified

Unchanged by this milestone and confirmed by the full suite plus manual smoke tests: allowed-root filesystem enforcement; loopback-only binding; session token, `Host` and `Origin` checks; no elevation; no shell execution; no file write/delete/move; no input injection; capabilities independent of transport; transports free of Windows-control logic; Windows code isolated to `PcCore.Windows`; typed protocol models.

The only v0.1 behaviour change is additive: `CallerDescriptor` has two more fields, and its `ToString()` now appends the actor type — a log-format change, not a protocol one.

---

# v0.3 — Personal Device Pairing, Persistence, and Startup

## 13. Scope and the personal-use assumption

v0.3 is built for **one owner, one Windows PC, one Android phone**. That is not a temporary
simplification to be generalised later — it is the design. Wherever a more general structure was
available (a device table, an account model, a role system, a key-management hierarchy), the
narrower one was chosen because it is smaller, easier to audit, and sufficient.

Concretely: there is exactly one Primary Phone or none; there is one shared secret rather than a
device PKI; there is one pairing session at a time; and the state store is three small JSON files
rather than a database.

## 14. Persistent state

Everything that must survive a restart lives in `%LOCALAPPDATA%\Nexus`, outside the repository:

| File | Contents |
|---|---|
| `instance.json` | `PcInstanceId` and when it was generated |
| `security-state.json` | current state, previous state, since-timestamp, revision |
| `primary-phone.json` | device id, display name, paired-at, DPAPI-protected shared secret |

### 14.1 Atomic writes

`AtomicJsonFileStore<T>` writes to a sibling `.tmp`, flushes it to the physical device
(`FileStream.Flush(flushToDisk: true)`), then moves it over the target. On Windows that move is
`MoveFileEx` with `MOVEFILE_REPLACE_EXISTING`, atomic within a volume. A crash leaves either the
whole previous document or the whole new one — for security state, a half-written file would be
worse than either.

### 14.2 Corruption handling

An unparseable file is **renamed aside** (`<name>.corrupt-<timestamp>`) rather than deleted, and
reported as `Corrupt` so each caller picks its own safe fallback. Nothing is silently reconstructed
by the store itself. Destroying the evidence of a corrupt security-state file is the wrong default.

## 15. Security state persistence

### 15.1 Restart behaviour

| State before restart | State after restart |
|---|---|
| `OWNER_PRESENT` | `OWNER_PRESENT` |
| `ARMED` | `ARMED` |
| `SECURITY_SESSION` | `SECURITY_SESSION` |

`since_utc` is preserved too: it means "since this state began", not "since the process started".

**`SECURITY_SESSION` is never downgraded by a restart.** If unauthorised activity opened a session,
rebooting the machine must not clear it — otherwise a reboot is the cheapest possible attack on the
entire feature. The session ends when the owner ends it. A future Android milestone will add a
"This was me" response that calls `security.disarm`; that workflow is **not** implemented here.

Restoring a state is **not** a transition and publishes no `security.state_changed` event.
Announcing one would tell listeners the owner just acted when they did nothing.

### 15.2 Documented recovery from corruption

| Situation | Result |
|---|---|
| No file (first run) | `OWNER_PRESENT` |
| Readable file, known state | that state, with its previous state and timestamp |
| Unparseable file | **`ARMED`**, previous state null, quarantined, logged at error |
| Parseable file, unrecognised state value | **`ARMED`**, same handling |

The fallback is `ARMED`, not `OWNER_PRESENT`, and not `SECURITY_SESSION`. A damaged file means the
previous state is *unknown*, and the one thing that must not be inferred from "unknown" is that the
owner is present. `ARMED` asserts only that presence is unconfirmed; it does not fabricate an
incident that may never have happened. The owner clears it with `security.disarm`. The fallback is
itself persisted, so a second restart does not re-run recovery.

If a write fails, the in-memory transition still stands and the failure is logged at error level.
Refusing to arm because the disk is unavailable would be a worse failure than losing the state on
the next restart — but it does silently weaken the restart guarantee, so it is loud.

## 16. The security-state event refactor

v0.2 published `security.state_changed` while holding the state mutation lock. v0.3 does not.

```
RequestTransition
  ├─ lock (_gate)                    ← mutation lock
  │    validate, apply, persist
  │    _pending.Enqueue(payload)     ← enqueued in commit order
  └─ (lock released)
     PublishPending()
       └─ lock (_publishGate)        ← a different lock
            drain queue → EventBus   ← subscribers run here
```

Two properties pull in opposite directions: subscribers must not run under the mutation lock
(arbitrary handler code inside a state lock is how a stall or deadlock arrives), but event order
must still match commit order (or a client replaying the stream believes the wrong current state).

A queue filled under `_gate` and drained under a separate `_publishGate` gives both. Enqueue order
is commit order; a single drainer at a time preserves it. Whichever thread reaches the drain first
may publish another thread's event — what matters is the sequence, not the emitter. Because `_gate`
is free while subscribers run, a handler may safely call back into `RequestTransition`.

Unchanged from v0.2: transitions are atomic, concurrent transitions are safe, and a no-op publishes
nothing. Still one `Lock` type, no state-machine framework, no messaging library.

## 17. PC installation identity

`PcInstanceId` is a random GUID generated once and stored in `instance.json`.

Deliberately **not** derived from any hardware or account identifier — no MAC address, no
motherboard serial, no Windows product id, no user name, no hostname. Deriving it from hardware
would make it a fingerprint that follows the owner across reinstalls and leaks machine details into
every pairing QR code, which is photographed.

If `instance.json` is unreadable, a new id is generated and the loss is logged at error level: a
previously paired phone will no longer recognise the installation.

## 18. The Primary Phone

```
PrimaryPhone
├── DeviceId      random, 128-bit, base64url — an identifier, never a credential
├── DisplayName   owner-facing label
├── PairedAtUtc
└── shared secret 256-bit, DPAPI-protected, never leaves the registry after pairing
```

There is one Primary Phone or none. `IPrimaryPhoneRegistry` is the only type that ever holds the
secret: it is returned exactly once, by `Pair`, because that is the moment the phone must receive
it. Verification happens inside the registry via `VerifyProof`, so no caller ever holds the
credential.

**`DeviceId` is not authentication.** Presenting the right device id proves nothing; presenting a
guessed one simply means there is no secret to check against. The credential is the shared secret.

### 18.1 Where the credential is stored and how it is protected

`primary-phone.json`, with the secret as a base64 DPAPI blob (`ProtectedData.Protect`,
`DataProtectionScope.CurrentUser`, plus a fixed application-scoped entropy string).

DPAPI is the built-in Windows mechanism for exactly this: a per-user key managed by the OS, with no
key material for this application to store, rotate, or leak. No cryptography is invented.

- **What it buys:** the file is useless if copied to another machine or opened under another Windows
  account.
- **What it does not buy:** protection from code already running as this user. Nothing at this layer
  can defend against that, and for a single-user personal system that is the right boundary — an
  attacker with the owner's session can already act as the owner.

If the blob cannot be unprotected (different account, restored profile, tampering), the system
reports itself **unpaired** rather than half-loading a phone it cannot authenticate.

### 18.2 Replacing and unpairing

`pairing.begin` throws `already_paired` when a phone is paired, unless `replace_existing: true` is
passed. Replacing the owner's phone is never implicit — a stray pairing attempt cannot take over a
working setup.

`device.unpair_primary` deletes the record, zeroes the in-memory secret, and cancels any live
pairing session (leaving a code outstanding would let it re-pair moments after the owner revoked
access). It is idempotent: revoking access must never fail because it was already revoked. After
unpairing, the old credential authenticates nothing.

## 19. Pairing

### 19.1 Flow

```
Owner ── pairing.begin ──▶ PC Core
                            ├─ 128-bit session id
                            ├─ 256-bit pairing secret
                            └─ payload, expiring in 5 minutes
                                 │
                    QR code ◀────┴────▶ copyable text
                                 │
Phone ── POST /device/pair ──────┘
            (session id + pairing secret)
                                 │
                            PC Core mints DeviceId + 256-bit shared secret,
                            persists them, consumes the session
                                 │
                    shared secret ◀─── returned exactly once
```

Sessions are **single-use, high-entropy, cancellable, and expiring**. The session is consumed on
*any* completion attempt, right or wrong: that is what makes a code un-guessable by repetition and
what makes concurrent completions resolve to exactly one winner. Starting a new session invalidates
the previous one — only one code is ever live.

There is no low-entropy PIN anywhere in this flow.

### 19.2 Payload format

```
NEXUS1.<base64url(compact JSON)>

{"v":1,"pc":"<PcInstanceId>","s":"<sessionId>","k":"<pairingSecret>"}
```

181 characters in practice — comfortably inside QR capacity. The `NEXUS1.` prefix identifies the
format and version so a scanner rejects anything else outright; base64url keeps it QR- and
clipboard-safe with no escaping surprises.

It carries **only** what pairing needs. No hostname, user name, address, or hardware detail appears
in something that gets photographed; a test asserts this.

### 19.3 Owner capabilities vs the device surface

| Surface | Actions | Why |
|---|---|---|
| Capabilities (dispatcher) | `device.get_primary`, `pairing.begin`, `pairing.cancel`, `device.unpair_primary` | Owner-facing and already trusted |
| Device endpoints (`DeviceSurface.cs`) | `POST /device/pair`, `POST /device/auth/challenge`, `POST /device/auth/verify` | The phone is **unauthenticated** at these moments and must not arrive as an ordinary trusted caller |

`/pairing/qr.png` renders the active session's payload as a PNG. It is fetched with the session
token rather than set as a plain `<img src>`, because an `<img>` cannot send a header and the
alternative would be dropping auth on an endpoint that serves the live pairing secret.

In v0.3 all of these are served by the loopback transport and still require the local session
token, because the only client that exists is the development simulator on this machine. The future
remote transport will expose the two device operations without the loopback token — a phone
authenticates with its shared secret instead.

## 20. Authentication

HMAC-SHA-256 challenge/response. The permanent secret is never transmitted after pairing.

```
Phone ── POST /device/auth/challenge ──▶ PC: 256-bit random challenge, single-use, 60s
Phone ── HMAC-SHA256(shared_secret, message) ──▶ PC: recompute, constant-time compare
```

The signed message is domain-separated:

```
"nexus-auth-v1" 0x00 <PcInstanceId> 0x00 <DeviceId> 0x00 <challenge>
```

Binding the purpose, the installation, and the device means a proof captured here cannot be
replayed against a different PC Core installation, a different device id, or a future protocol that
reuses the same secret.

**Replay protection:** a challenge is removed on the first verification attempt, success or
failure. A captured proof is worthless once spent, and a wrong guess cannot be retried against the
same challenge. Challenges also expire, and outstanding ones are capped.

Every failure returns the same opaque `authentication_failed`. Telling an unauthenticated caller
*why* it failed would let it probe which device ids and challenges exist.

### 20.1 The exact condition that creates a trusted DEVICE caller

```csharp
AuthenticatedDevice device = authenticator.Verify(challengeId, deviceId, proof);  // throws otherwise
CallerDescriptor caller  = device.ToCaller(transport, connectionId);              // ActorType.Device
```

`AuthenticatedDevice` has **no public constructor**. The only way to obtain one is a successful
`Verify`, and `ToCaller` is the only method that produces `ActorType.Device`. Nothing in a request
body can reach it.

A request claiming `actor_type: "DEVICE"` with a `device_id` has no effect whatsoever — parameters
are bound to a capability's own parameter record and never to the caller identity, which the
transport stamps. A regression test asserts a hostile body still arrives as `LOCAL` / `null`.

Loopback callers remain `ActorType.Local` with `DeviceId = null`, unchanged from v0.2.

### 20.2 The policy seam, still not implemented

`CapabilityDispatcher.DispatchAsync` remains the single choke point where a future policy decision
point belongs, now with all four inputs genuinely available: caller identity, device identity,
requested capability, and current security state. **v0.3 still implements no authorization.**
Identity remains inert metadata.

## 21. Development phone simulator

The Android app does not exist, so the developer test page carries a minimal stand-in: paste or
auto-fill a pairing code, pair as the phone, run an authentication challenge with the correct or a
deliberately wrong secret, and forget the credential.

It holds its credential in a **page-scoped variable only** — never `localStorage`, never a cookie,
never anywhere it could outlive the tab. It is not the Android application and is not a client
design.

## 22. Windows auto-start

**Mechanism:** one value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, named
`PcCore`, whose data is the quoted path of the executable. Nothing else is written anywhere.

Per-user, so it needs no administrator rights and cannot affect anyone else on the machine. It is
also the location Windows surfaces in Task Manager's Startup apps tab and in Settings, so the owner
can see and disable it without going near this application — precisely the property a startup entry
should have.

**Deliberately not:** no service, no elevation, no scheduled task, no watchdog, no re-registration
after the owner removes it, no attempt to survive being killed, and no hiding. PC Core runs as an
ordinary process in the user's session and is visible in Task Manager like anything else.

**Escape hatch:** `PcCore:Startup:Enabled` (default `true`). Set it to `false` while debugging and
PC Core will not touch the startup list — it reports what is registered and leaves it alone. The
flag is the only thing that governs registration.

**Manual control:** `--install-startup`, `--uninstall-startup`, `--startup-status`. Each handles
its command and exits without opening a socket.

## 23. Testing added in v0.3

58 tests, taking the suite from 219 to 277.

- **Security state persistence** — each of the three states surviving a restart; `SECURITY_SESSION`
  specifically not downgraded; `since_utc` preserved; restart publishing no event; revision and
  schema version; wire names on disk; corruption falling back to `ARMED`; quarantine rather than
  deletion; recovery persisted so it does not repeat; an unrecognised state value treated as corrupt.
- **Event refactor** — a subscriber calling back into the service without deadlocking (bounded, so
  the failure mode is a fast failure rather than a hung run); another thread reading and mutating
  state *while a subscriber is running*, which is the direct proof the mutation lock is not held;
  event order still matching commit order under 16-way contention; persisted state agreeing with the
  final state after contention.
- **PC identity** — generated once, stable across restart, distinct per installation, and not
  containing any machine identifier.
- **Primary phone** — initially absent; pairing persisted; the credential still authenticating after
  restart; a second phone unable to overwrite silently; explicit replacement retiring the old
  credential; unpair removing it and being idempotent; the old credential failing afterwards; the
  plaintext secret absent from disk **under real DPAPI**; a blob that cannot be unprotected leaving
  the system unpaired; a corrupt record leaving it unpaired.
- **DPAPI** — round-trip, distinct ciphertext, tampered blob rejected rather than returning garbage.
- **Pairing** — 256-bit secret entropy; unique sessions; payload round-trip and absence of machine
  identifiers; malformed payloads; expiry; cancellation; single use; wrong secret burning the
  session; wrong session id; a new session invalidating the old; 16-way concurrent completion
  pairing exactly one phone.
- **Authentication** — success; wrong secret; a different challenge signed; an altered proof; replay;
  failure also consuming the challenge; expiry; a guessed device id establishing nothing;
  authenticating against an unpaired system; proofs bound to the installation; and that a verified
  `AuthenticatedDevice` is the only route to a `DEVICE` caller.
- **Regression** — a hostile request body asserting `actor_type: DEVICE` still arriving as `LOCAL`
  with a null device id, plus the whole v0.1/v0.2 suite unchanged.

Every test that touches persistence uses a throwaway directory; nothing in the suite reads or writes
the real `%LOCALAPPDATA%\Nexus`. Test secrets are generated per-run and disposable.

## 24. Explicitly NOT in v0.3

Android app; remote transport; Tailscale; LAN or internet listener; NAT traversal, STUN, TURN, ICE,
relays; remote screen streaming; keyboard/mouse control; camera or microphone; Claude Code, Codex,
or any AI bridge; notifications; security screenshots or recording; activity detection;
authorization policy; device PKI or ECDSA.

## 25. v0.1 and v0.2 guarantees, re-verified

Unchanged and confirmed by the full suite plus the manual sequence: allowed-root filesystem
enforcement; loopback-only binding; session token, `Host` and `Origin` checks — including on the new
device endpoints; no elevation; no shell execution; no file write/delete/move; no input injection;
capabilities independent of transport; transports free of Windows-control logic; typed protocol
models; and loopback callers remaining `LOCAL` with a null device id.

The v0.2 security-state semantics are unchanged in every respect except that state now persists:
same three states, same four-edge table, same idempotency, same refusal of invalid transitions.

---

# v0.4 — Authenticated Tailscale Remote Transport

## 26. What v0.4 adds

The paired Primary Phone can now reach PC Core over the owner's Tailscale network, authenticate with the credential established in v0.3, and drive a restricted set of capabilities. The Android application is still not implemented; a protocol-level simulator stands in for it.

```
Primary Phone
     │  Tailscale (WireGuard)
     ▼
PcCore.Transport.Remote          ← binds ONE Tailscale address, nothing else
     │  HMAC-SHA-256 challenge/response (v0.3 authenticator)
     ▼
CallerDescriptor(ActorType.Device, DeviceId)
     │
     ▼
ICapabilityPolicy                ← the seam v0.2 documented, now occupied
     │
     ▼
CapabilityDispatcher             ← unchanged choke point
     │
     ▼
The same capability instances the loopback transport uses
```

No capability is duplicated for remote use, no transport contains Windows-control logic, and `PcCore.Windows` was not touched: network interface enumeration comes from `System.Net.NetworkInformation` in the BCL.

## 27. Network exposure

### 27.1 The invariant

**PC Core must never open the remote port on an ordinary LAN or WAN interface.** This is enforced structurally, not by convention.

`RemoteEndpoint` — the only type that can name a bind address — has an `internal` constructor. In production the only code that calls it is `TailscaleAddressDetector`, after the address passes every check below. There is no configuration setting for a bind address, no wildcard path, and no fallback.

### 27.2 Detection

Two independent facts must agree, or nothing is produced:

1. **Interface identity.** The adapter name or description contains `tailscale` (on Windows: adapter `Tailscale`, description `Tailscale Tunnel`), and it is operationally up.
2. **Address range.** The address is inside `100.64.0.0/10`, evaluated with integer arithmetic on the address bytes — not a `"100."` string prefix. `100.0.0.1` and `100.128.0.1` are ordinary public addresses outside the range and are rejected.

Requiring both makes the check safe in either direction: a LAN adapter someone renamed "Tailscale" still fails the range test, and a machine holding a CGNAT address on a real WAN link (some ISPs do this) still fails the identity test.

The Tailscale CLI is deliberately not invoked: it is not reliably on `PATH`, shelling out on a timer is fragile, and the OS already answers the question directly. No elevation is required.

### 27.3 Determinism and failure

Several qualifying addresses → the numerically lowest is chosen, so the bind address does not change between boots. No candidate → **no endpoint and no listener**, with a diagnostic naming the reason.

```
127.0.0.1     → loopback development transport (port 47811, unchanged)
100.x.x.x     → authenticated remote transport (port 47812)
192.168.x.x   → NOT listening
172.x / WAN   → NOT listening
0.0.0.0       → never, under any configuration
```

### 27.4 Port

**47812**, fixed, adjacent to the loopback transport's 47811. Configurable via `PcCore:RemoteTransport:Port` for the unlikely case of a conflict, but not dynamic — a phone should not have to rediscover the port after every reboot. It requires no router port forwarding: it is reachable only through the bound Tailscale address.

## 28. Transport and why no TLS

WebSocket at `ws://<tailscale-ip>:47812/nexus`, subprotocol `nexus.v1`.

The protocol has three message types and the third — events — cannot be expressed over request/response HTTP. The phone needs a persistent bidirectional connection regardless: it authenticates once per connection rather than per request, and must receive `security.state_changed` without polling. A later screen-streaming milestone wants the same channel. The existing typed envelopes and `ProtocolCodec` are reused verbatim, so both transports share one serialization model; no custom binary framing was introduced.

**No TLS.** Every byte already travels inside a WireGuard tunnel that Tailscale authenticates and encrypts end to end, and PC Core independently proves the peer holds the Primary Phone secret before accepting a command. Adding a certificate would mean generation, trust distribution, renewal, and a pinning story in the Android client — real lifecycle complexity buying a second encryption layer inside the first, and self-signed certificates would make the Android client harder to write, not easier. If this transport were ever exposed outside a tailnet that calculation reverses completely, which is precisely why the listener cannot bind anything but a Tailscale address.

## 29. Authentication

No second credential was created. The v0.3 `IDeviceAuthenticator` and Primary Phone shared secret are used unchanged; the transport contains no competing authentication logic.

```
Phone                                   PC Core
  ── connect ──────────────────────────▶
                    ◀── auth.challenge (event: challenge_id, challenge, expiry)
  ── auth.authenticate ────────────────▶  (challenge_id, device_id, proof)
                                          Verify() → AuthenticatedDevice
                    ◀── response: authenticated, actor_type DEVICE, device_id
  ── system.get_info, files.list, … ───▶  (identity inherited from the session)
```

The signed message is unchanged from v0.3:

```
"nexus-auth-v1" 0x00 <PcInstanceId> 0x00 <DeviceId> 0x00 <challenge>
```

**Replay resistance** comes from the v0.3 challenge lifecycle: challenges are cryptographically random, expire, and are consumed on the first verification attempt whether it succeeds or fails. A captured proof cannot establish a later session.

**An unpaired PC is not short-circuited.** It issues a challenge and fails at verification like any other rejection. Refusing earlier would give a different reply shape and timing, telling anyone on the tailnet whether a phone is paired.

### 29.1 How a trusted DEVICE caller is created

```csharp
AuthenticatedDevice device = _authenticator.Verify(challengeId, deviceId, proof);  // throws otherwise
_caller = device.ToCaller(RemoteTransport.TransportName, _connectionId);           // ActorType.Device
```

`AuthenticatedDevice` has no public constructor (v0.3), so this is the only route to a `DEVICE` identity anywhere in the system. Nothing in a request body can reach it: parameters bind to a capability's own parameter record, never to the caller identity, which the transport stamps. Before the handshake completes, the dispatcher is not invoked at all — the only action a connection can send is `auth.authenticate`.

## 30. Sessions

Authentication happens **once per connection**. Every later request on that connection inherits the identity established at handshake; there is no per-request HMAC.

- A session is a property of an open socket. Closing the connection ends it; reconnecting requires a fresh challenge and a fresh proof.
- Sessions are in-memory and connection-bound. Nothing is persisted or shared between processes.
- An unauthenticated connection is closed after 15 seconds, so a peer that never proves anything cannot hold a socket open indefinitely.
- Re-authenticating on a live session is refused rather than passed to the dispatcher, so it cannot burn a challenge.

**Simultaneous connections: 4** (`PcCore:RemoteTransport:MaxSessions`). Not one: a phone that loses signal leaves a session the PC has not yet noticed is dead, and an exclusive-session rule would lock the owner out until a timeout elapsed. Four absorbs reconnect races and a foreground/background split while bounding memory. At the limit the **oldest** session is closed rather than the newest refused, because the newest connection is the one the owner is actually looking at.

## 31. Capability policy

The first real distinction between callers, evaluated in `CapabilityDispatcher` before parameters are bound and before any capability code runs — so a denied action performs no part of its work.

| Actor | Policy |
|---|---|
| `LOCAL` | Everything. Unchanged from v0.1–v0.3. |
| `DEVICE` | An **allowlist** of eight actions. |
| `AI` | Nothing. No AI adapter exists; failing closed means its access will be a deliberate decision. |

Allowed remotely: `system.get_info`, `system.list_processes`, `files.list`, `files.stat`, `screen.capture`, `security.get_state`, `security.arm`, `security.disarm`.

Denied remotely: `pairing.begin`, `pairing.cancel`, `device.unpair_primary`, `device.get_primary`, and anything added in future milestones until it is named here.

**The phone is a controller for the PC, not the authority that decides which phone is trusted.** Device management stays local-only, so possession of a phone credential can never be escalated into enrolling another device. That asymmetry is why this is an allowlist: a capability added later is denied to the phone by default.

It is a small typed rule set — a `FrozenSet` and a `switch` — not RBAC, not a permission catalogue, not a grant model. There is one owner and one phone.

**Denials** return `not_authorized` with a stable code and no internal detail. The action name is not treated as a secret (pretending it does not exist would make diagnostics worse), the connection is not dropped for one refusal, and the session remains usable.

Policy does **not** widen any existing boundary: `files.list` remotely is still constrained by the allowed roots, verified by test.

## 32. Events

An authenticated session subscribes to the existing `IEventStream`; no second event system was introduced. Delivery is filtered by a small allowlist — event names beginning `security.` — rather than a subscription language. Anything else, present or future, must be named there deliberately rather than reach the network because it happened to be published. Pairing and internal events are not deliverable.

Unauthenticated connections receive no event stream at all: the subscription is created inside the handshake, after verification.

Idempotent state transitions still publish nothing (v0.2 behaviour), so a remote client sees exactly one event per real change.

## 33. Tailscale outage and recovery

`RemoteTransportService` is a hosted service that reconciles on a 30-second timer:

- No Tailscale address → no listener; if one was running, it is stopped. The reason is logged once per distinct diagnostic, not once per tick.
- Address appears → the listener starts automatically. **No PC Core restart is required.**
- Address changes → the listener rebinds.
- A bind failure is caught, logged, and retried on the next tick rather than killing the loop.

One timer and one detector call: a poll for an interface appearing, not a network monitoring subsystem.

## 34. Diagnostics

Startup logs the port, whether a Tailscale address was found, and if not, exactly why and how often it will re-check. `RemoteTransportService.GetStatus()` reports enabled/available/address/port/diagnostic/paired/session-count. The public protocol was not expanded for debugging: no capability was added to expose this.

Logged: transport start/stop, selected address, connection open/close, authentication success with device id, authentication failure with a reason, denied actions, and recoverable errors. **Never logged:** the shared secret, the protected blob, proof values, or challenge material — verified by inspection of every log statement in the transport and by scanning real run logs for the live secret.

## 35. Development simulator

`PcCore.Host.exe --simulate-phone-pair <NEXUS1.…>` completes pairing through the real device endpoint and stores the phone's half of the credential in `dev-phone-simulator.json`, DPAPI-protected — a deliberately separate file from `primary-phone.json`, since conflating the two halves would blur the boundary the design depends on.

`PcCore.Host.exe --simulate-phone ws://<tailscale-ip>:47812/nexus` then runs the full conversation: connect, challenge, authenticate, confirm `DEVICE`, `system.get_info`, `files.list`, `screen.capture`, `security.get_state`, arm, receive `security.state_changed`, disarm, confirm `device.unpair_primary` is denied, disconnect, reconnect and authenticate again.

The protocol client itself (`RemotePhoneClient`) lives in the transport assembly as the reference implementation the Android client will port, so the handshake has one definition that both the server tests and the simulator exercise. It is development-only and is not the Android application.

## 36. Testing added in v0.4

63 tests, taking the suite from 277 to 340.

- **Binding safety** — selects a genuine Tailscale interface; refuses the typical Wi-Fi/Ethernet/Hyper-V machine; refuses a non-CGNAT address even on an interface named "Tailscale"; refuses a CGNAT address on a non-Tailscale interface; refuses a down interface and one with no address; fails closed when enumeration returns nothing; CGNAT boundary arithmetic at `100.63.255.255` / `100.64.0.0` / `100.127.255.255` / `100.128.0.0` and the `100.0.0.1` case a string prefix would wrongly accept; deterministic choice independent of enumeration order; wildcard and broadcast never produced; `RemoteEndpoint` has no public constructor; and a check against this machine's real interfaces asserting the detector never yields an address the host holds on an ordinary interface.
- **Authentication** — the paired phone establishes a DEVICE session; wrong secret; unknown device id; a proof bound to a different installation; a replayed proof on a new connection; a challenge from one connection answered on another; an unpaired system; and a request self-asserting `actor_type: DEVICE` before authenticating.
- **Policy** — each allowed action succeeds; each device-management action returns `not_authorized`; the filesystem boundary still applies remotely; a denial neither closes the connection nor performs any part of the action; denials carry no internal detail; and the allowlist is pinned to exactly the eight documented actions.
- **Sessions** — identity is connection-bound (a second connection gains nothing); disconnect cleans up; reconnect requires fresh authentication; the session limit evicts the oldest; malformed and unknown actions do not drop the session; re-authenticating a live session is refused.
- **Events** — an authenticated device receives `security.state_changed` with the right payload; an idempotent transition produces no duplicate; an unauthenticated connection receives nothing even when a real transition occurs.
- **Recovery** — stays offline with no Tailscale address; comes online by itself when one appears; `Enabled: false` never opens a port; the status readout contains no field a secret could hide in.
- **Regression** — the entire v0.1–v0.3 suite, with the existing dispatcher tests now running under the real policy so LOCAL behaviour is verified unchanged.

## 37. Explicitly NOT in v0.4

Android app; public internet exposure; router port forwarding; Tailscale installation, login, or tailnet/ACL management; Tailscale Serve; STUN, TURN, ICE, custom NAT traversal, relays; remote keyboard or mouse input; live screen streaming; camera; microphone; AI, Claude Code, or Codex; notifications; security evidence recording; activity detection.

---

# v0.5 — First Native Android Client

## 38. What v0.5 adds

The phone. Everything before this milestone was a machine talking to itself: a loopback page, a
protocol-level simulator, a transport with no client. v0.5 is the first time a person picks up a
device and controls the PC with it.

Two things had to be built for that, and only two:

1. **A native Android application** — Kotlin, Jetpack Compose, one connection, five screens.
2. **A way for an unpaired phone to obtain a credential over the network**, because until now the
   only pairing path ran over loopback on the PC itself, which a phone cannot reach.

Everything else is unchanged. No new capability was added, `PcCore.Windows` was not touched, and the
DEVICE policy allowlist is the same eight actions v0.4 defined.

## 39. The bootstrap problem

The remote transport requires an authenticated `DEVICE` identity. That identity requires the Primary
Phone's permanent secret. A phone that has never paired does not have it, and the only place it can
be issued is the PC. Before v0.5 the issuing surface was `POST /device/pair` on the loopback
transport — reachable by the development simulator running on the same machine, and by nothing else.

So exactly one exception exists, and its shape is deliberate:

| Property | Choice | Why |
|---|---|---|
| Where it lives | `pairing.enroll`, handled in `RemoteConnection`'s handshake | The remote transport binds only to a Tailscale address, so the exception inherits that boundary. It is not reachable over LAN or WAN because nothing is. |
| How it is authorised | The one-time pairing secret from `pairing.begin` | An owner-initiated, short-lived, single-use, 256-bit credential. Without an active local session there is nothing to enroll against. |
| How the secret is presented | HMAC-SHA-256 over the PC's challenge | The pairing secret is never transmitted. Its only appearance on any wire was in the QR code the owner photographed. |
| What it grants | A credential, and then the connection closes | Enrollment is not authentication. The socket does not become a `DEVICE` session; the phone reconnects and authenticates like every later time. |
| Whether it is a capability | No | `CapabilityDispatcher` serves identified callers under a policy. An unidentified peer has no place in it, and `pairing.enroll` is not registered, so no caller — local, device or otherwise — can dispatch it. |

The authority to decide *which* phone is trusted never moves. The owner starts pairing on the PC;
replacing an existing Primary Phone still requires `replace_existing` stated locally. A phone holding
a valid credential cannot enroll another device, cannot re-enroll itself, and cannot revoke anything
— `device.*` and `pairing.*` remain denied to `DEVICE` callers by the v0.4 policy.

## 40. Pairing payload v2

`NEXUS2.<base64url(compact JSON)>`.

```
{"v":2,"pc":"<PcInstanceId>","s":"<session>","k":"<pairing secret>",
 "h":"100.x.x.x","p":47812,"pv":1}
```

**Why a version rather than extra fields.** A v1 scanner and a v2 scanner are doing different things.
v1 says "pair with this installation"; v2 says "pair with this installation, at this address,
speaking this protocol version". Growing v1 in place would leave a scanner unable to distinguish a
missing endpoint from an old code. The prefix answers that before a byte is parsed, and each format
is rejected as the other.

**`h` is absent when no Tailscale address is bound.** The endpoint comes from the transport's live
bound address through `IDeviceEndpointProvider`, not from configuration, so a code can never promise
an address that is not being served. A phone that scans such a code says exactly that, and the PC's
local page says it too, before the owner walks to the phone.

**What is not in it:** no user name, no host name, no MAC address, no hardware serial, and no
permanent secret. `PairingSessionTests` asserts the machine and user names are absent from the
encoded bytes.

v1 is retained and still produced as `payload_v1`, used only by the loopback development simulator,
which runs on the PC and needs no endpoint.

## 41. Protocol corrections

Two, both additive, both found by writing a real client:

- **`FileEntry.path`.** A listing returned names only, so a phone navigating into a subdirectory had
  to join the parent path with a name and guess a separator. Path reconstruction is not a client's
  job when the server already knows the answer — and joining is quietly wrong for a root like `D:\`.
  Every path is still re-authorised from scratch; this removes a class of client bug, not a check.
- **`allowed_roots` in the authentication response.** The phone needs somewhere to start browsing.
  This is session bootstrap rather than a capability: it mirrors how the local test page receives the
  same list at composition, discloses nothing a `files.list` response does not already contain, and
  keeps the capability surface at exactly what v0.4 defined.

## 42. The Android client

**Toolchain.** Kotlin 2.2.10, AGP 9.2.1, Gradle 9.4.1, Compose BOM 2026.08.00, JDK 21. AGP 9 compiles
Kotlin itself; the standalone Kotlin Android plugin is incompatible with it, so built-in Kotlin is
used with the Compose and serialization sub-plugins on top.

**minSdk 31** (Android 12). Chosen for the platform behaviour the app relies on rather than for the
lowest number that compiles: modern Keystore semantics and the theming this app is built against.
One owner, one phone; breadth of device support is not a goal. `compileSdk` and `targetSdk` are 37.

**Structure.**

```
protocol/   Pure Kotlin. Envelopes, codec, base64url, HMAC, pairing code, endpoint validation.
data/       Credential storage (Keystore) and the repository.
net/        The connection manager, the enrollment client, the private-network probe.
ui/         Compose screens and their view models.
```

The `protocol` package has no Android dependency at all, which is what lets the cross-language
vectors run as ordinary JVM tests.

## 43. Endpoint validation

The app refuses to open a socket to anything that is not a Tailscale address. This is not defence in
depth; it is the load-bearing half of the "no TLS" decision in section 28. Cleartext is acceptable
*because* the connection cannot leave the tunnel.

`NexusEndpoint` has a private constructor. The only production route to one is `parse`, which
requires:

- a literal IPv4 address — a host name is refused on sight, never resolved, because resolution hands
  the choice of destination to whoever answers the lookup;
- membership of `100.64.0.0/10`, by address arithmetic, not a `"100."` prefix;
- strict dotted-quad spelling — octal, hex, shorthand and padded octets are all refused, since those
  are precisely the forms that denote one address while looking like another;
- `ws://` and the `/nexus` path, with no credentials, query or fragment;
- an unprivileged port.

Android's network security configuration cannot express a CIDR range, so cleartext is permitted at
the manifest level and the real control lives here. `NexusEndpointTest` additionally asserts that no
file under `src/main` calls `forTestsOnly`, the only other way to construct an endpoint.

## 44. Credential storage

An AES-256-GCM key generated in the Android Keystore, non-exportable, with
`setRandomizedEncryptionRequired` on. The sealed file is `[version][ivLength][iv][ciphertext]` in
app-private storage; the framing is plain code with its own tests, and the Keystore round trip is an
instrumented test on a real device.

No biometric gate. The app reconnects while it is open, and a prompt on every reconnect trains the
owner to tap through it; the device lock screen protects the app itself.

The secret never enters Compose state — the UI holds `PairedPc`, which has no field that could carry
it — and `NexusCredential.toString()` is overridden so an accidental log statement cannot print it.
There is no analytics SDK, no crash reporter, and backup and device transfer are excluded outright.

## 45. Connection lifetime

One `PcConnectionManager` owns the socket; no screen opens its own. It connects on `ON_START` and
disconnects on `ON_STOP`: there is no background service, because v0.5 has nothing that needs to
arrive while the app is closed, and a WebSocket held open all day is battery cost with no feature
behind it. Security notifications will need a push channel designed for the purpose.

Reconnection is bounded — 2s doubling to 30s — and stops entirely on `authentication_failed`, which
will not become valid by retrying.

Failures are classified by what the phone itself can observe, because Nexus can fix none of them and
the owner has to know which one applies: no network at all, a phone with no tailnet address of its
own, a PC that did not answer, a rejected credential, or an unrecognised reply.

## 46. Cross-language test vectors

v0.5 is the first milestone where the protocol is implemented twice, in two languages, by two JSON
stacks. The failures that produces are quiet: an HMAC over subtly different bytes, a base64 variant
with padding, a timestamp format one side cannot parse.

`shared/protocol/protocol-vectors.json` is generated from the real PC Core implementations by
`ProtocolVectorTests` and asserted against by both sides. It covers base64url at every padding
residue, both signed messages as hex, both proofs, the NEXUS2 payload and six malformed variants, and
the request, response, error, event and challenge envelopes.

Two findings came out of writing it:

- `System.Text.Json` escapes non-ASCII to `\uXXXX`; kotlinx.serialization emits it raw. Both are
  valid JSON and neither is wrong, so the vectors pin both spellings and each side is tested against
  the other's.
- PC Core writes timestamps as `+00:00`, which `Instant.parse` rejects and `OffsetDateTime.parse`
  accepts. Pinned, because the difference is invisible until a timestamp is actually parsed.

## 47. Testing added in v0.5

**PC Core** — 357 total, up from 340:

| Area | Covers |
|---|---|
| `RemoteEnrollmentTests` | Enrollment end to end over a real socket; no active session; a wrong proof burning the code; a proof bound to another installation; an auth proof reused as an enrollment proof; replay; a paired phone that cannot be replaced from the tailnet; unreachable from a DEVICE session; a response carrying no machine detail |
| `ProtocolVectorTests` | The committed vectors match what PC Core produces; the auth vector verifies against the real authenticator; the PC parses Kotlin's unescaped spelling |
| `PairingSessionTests` | NEXUS2 round trip and its absent fields; a code with no endpoint; v1 still offered; neither format accepted as the other |

**Android** — 55 JVM unit tests, 20 instrumented:

| Suite | Covers |
|---|---|
| `ProtocolVectorsTest` | Every vector above, from the Kotlin side |
| `NexusEndpointTest` | CGNAT boundaries, LAN/loopback/public refusal, host names, lenient IPv4 spellings, URL form, and that production code never builds an unvalidated endpoint |
| `PairingCodeTest` | NEXUS2 parsing, v1 refusal with an explanation, truncated scans, malformed payloads, a code naming a LAN address |
| `PcConnectionManagerTest` | Handshake against a scripted PC that verifies the real HMAC; out-of-order response correlation; events arriving mid-request; structured denials; failure classification; requests with no session |
| `SealedBlobTest` | Credential framing, version and truncation refusal |
| `CredentialStoreTest` (device) | Keystore round trip, nothing readable on disk, a fresh IV per save, tamper and truncation refusal, clear, no secret in `toString` |
| `ConnectionBannerTest` (device) | Every failure state naming its own cause |
| `HomeScreenTest` (device) | The control panel rendered from session state: every action present, unreachable without a session, nothing credential-shaped on screen |

## 48. Explicitly NOT in v0.5

Live screen streaming, remote mouse and keyboard, launching or controlling PC applications, the
microphone and camera bridges, a background notification channel, security-session evidence
recording, AI of any kind, shell execution, file writing, a cloud backend, user accounts, analytics,
and crash reporting.

Tailscale remains the owner's to install and sign into, on both devices. Nexus observes the private
network; it does not manage it.

---

## 49. What v0.6 adds

Nexus becomes a remote-control application rather than a remote-inspection one. The phone can watch
the PC's desktop continuously and drive it — pointer, buttons, scrolling, typing, modifier
combinations — and the PC shows that it is happening.

Three things are new on the PC: a sustained capture pipeline, an input subsystem, and a visible
indicator. Two are new on the phone: a binary frame path and a control surface. Nothing already
working changed shape.

## 50. Screenshot and stream are different things

`screen.capture` is untouched. It remains one PNG on demand, through GDI, with no session and no
sequence — the reliable answer to "what is on the screen right now".

`IDesktopStream` is a separate interface with a separate implementation, and the two share no code.
This is deliberate, and the reason is not tidiness:

- A screenshot must keep working when everything else does not. It is what diagnoses a broken
  stream, and building it on the streaming pipeline would mean a capture fault took both.
- The failure modes are different. A screenshot either succeeds or fails once. A stream has to
  survive a resolution change, a display being recreated, a driver reset, the lock screen, and a
  UAC prompt — while running.
- The performance shapes are different. A GDI `BitBlt` of a 1920x1080 desktop is tens of
  milliseconds and copies everything every time. Desktop Duplication hands over the frame the
  compositor already produced, only when it changed.

A stream built out of repeated screenshots would have been a worse stream *and* a more fragile
screenshot.

## 51. Desktop Duplication

`DxgiDesktopStream` is the only place in Nexus that talks to Direct3D. It enumerates outputs through
DXGI, creates a D3D11 device on the adapter that owns the chosen display, duplicates the output,
copies each acquired frame into a staging texture, and reads it as BGRA.

**The interop is hand-written**, calling through the COM vtable with function pointers rather than
declaring `[ComImport]` interfaces. A COM interface declaration must list every method of every base
interface in order, because the runtime finds a method by counting slots — reaching
`ID3D11DeviceContext::CopyResource` that way means declaring forty methods nobody calls, and one
miscount is an access violation rather than a compile error. Calling through the vtable directly
makes the slot number the visible thing, so each is stated and commented against the header it comes
from. Every slot was validated by a standalone probe against a real adapter before any of it was
used.

**What the stream survives**, and how each maps:

| Condition | DXGI result | Nexus response |
|---|---|---|
| Nothing changed | `DXGI_ERROR_WAIT_TIMEOUT` | Normal. The client keeps its last frame; nothing is sent. |
| Resolution or mode change, display recreated, driver reset | `DXGI_ERROR_ACCESS_LOST`, `INVALID_CALL` | `Lost` — dispose, reopen, carry on |
| Lock screen, UAC prompt, secure desktop | `SESSION_DISCONNECTED`, `ACCESS_DENIED` | `Unavailable` — back off a second, retry |
| Another application already duplicating | `NOT_CURRENTLY_AVAILABLE` | `Unavailable` — retry |
| Protected content | frame arrives with `ProtectedContentMaskedOut` | Frame is valid; parts are black. Logged once at debug. |

None of these end the stream, and none of them can take PC Core down.

**Two findings from the probe shaped the design.** The first `AcquireNextFrame` after
`DuplicateOutput` returns a frame with `AccumulatedFrames == 0` whose texture is blank — so a cold
start on a still desktop would show black until something happened to move. The stream primes its
first frame from a single GDI grab instead, and only that first one. And DXGI reports *physical*
pixels while the output rectangle is DPI-scaled: the probe saw a 1536x864 rectangle for a 1920x1080
duplication because a plain console process is DPI-unaware. PC Core enables per-monitor-v2 awareness
at startup, so the two agree — but the cursor mapping still scales through the ratio rather than
assuming it.

## 52. The cursor

Desktop Duplication does not draw the pointer into the frame. Nexus draws the real cursor into the
captured buffer with `GetCursorInfo` and `DrawIconEx`, offset by the icon's hotspot, and sets
`CursorBakedIn` in the frame header.

Drawing the actual cursor rather than sending coordinates for the phone to draw its own means the
owner sees the I-beam, the resize arrows and the busy spinner they would see sitting at the machine.
The flag is what makes double-rendering impossible: the client is told the pointer is already there.

## 53. Frame encoding

JPEG, behind `IFrameEncoder`.

A video codec was the alternative and was not chosen. H.264 would bring a Media Foundation pipeline,
hardware-encoder capability differences, keyframe and bitrate management, and a decoder on the
Android side with its own surface lifecycle — a milestone's work whose benefit is bandwidth this
link is not short of. Independent frames also mean a dropped frame costs exactly one repaint instead
of corrupting everything until the next keyframe, which is what makes the drop-stale-frames policy
safe rather than merely cheap.

**The defaults were measured, not guessed.** Against a continuously-changing 1920x1080 desktop — the
worst case, since a still desktop produces no frames at all:

```
1600 wide, q70   101 KB/frame   10.7 Mbit/s   12.9 ms encode
1280 wide, q70    73 KB/frame    7.7 Mbit/s    9.6 ms encode
1280 wide, q65    68 KB/frame    7.2 Mbit/s    9.5 ms encode   <- chosen
1024 wide, q65    47 KB/frame    5.0 Mbit/s    8.1 ms encode
```

1280x720 at quality 65, 15 fps. Ten megabits to a phone is more than a tunnel over mobile data
should be asked for, and 1280 is already more pixels than a phone screen shows. Dropping to 1024
saves another two megabits at a visible cost to small text, which is usually what a remote desktop is
being read for.

## 54. Binary frames

Frames travel as binary WebSocket messages on the same authenticated connection. Commands,
responses and events stay exactly as they were, in JSON.

A 70 KB JPEG becomes 90 KB of base64 inside a JSON string, has to be escaped, parsed and unescaped,
and arrives fifteen times a second. That is a steady tax on both sides for nothing.

The header is 64 fixed bytes, little-endian:

```
0   4  magic "NXS1"          28  2  width
4   1  header version        30  2  height
5   1  encoding              32  4  desktop left    (signed)
6   1  flags                 36  4  desktop top     (signed)
7   1  display index         40  4  desktop width
8   4  stream id             44  4  desktop height
12  8  frame sequence        48  4  cursor x
20  8  timestamp (unix ms)   52  4  cursor y
                             56  1  cursor visible
                             57  3  reserved
                             60  4  payload length
64  ..  payload
```

Everything a client needs to interpret one frame fits in it: which stream, which display, how big,
where on the desktop, when, and where the pointer was. A media container would have added framing
rules, a second clock domain, and a parser worth attacking, to carry the same facts.

The layout is pinned by `shared/protocol/protocol-vectors.json` and asserted **by byte offset** on
both sides. A field written at the wrong offset produces a header that parses cleanly on both sides
and means something different on each — a round-trip test cannot see it, because both halves shift
together.

## 55. Backpressure

One rule, applied twice: **latest frame wins**.

```
capture  ->  encode  ->  [one-slot channel]  ->  socket  ->  [one-slot flow]  ->  decode  ->  screen
                          drop oldest                          drop oldest
```

The PC's channel holds one encoded frame and drops the older one when a newer arrives. The phone's
flow does the same on receipt, because the phone can also be the slow part. Neither side ever queues
desktop content: what a slow link misses is a desktop that has already been superseded, and
buffering would spend memory to deliver it late.

**Media never starves control.** Both kinds of message share one send gate, so a frame sender waits
— briefly and boundedly — for any queued control message before taking it. Losing a frame costs one
repaint of content about to be replaced; delaying a response makes the interface feel stuck, and
delaying a security event is worse than that.

## 56. Input

```
DEVICE command -> CapabilityDispatcher -> policy -> InputService -> IInputController -> SendInput
```

`InputService` does the validation, the rate limiting and the held-state tracking.
`WindowsInputController` translates and calls `SendInput`. No P/Invoke appears in a transport, and
no policy appears in the P/Invoke.

**Coordinates are normalised.** The phone sends a position as a fraction of the streamed display, 0
to 1, plus the display index. The PC converts to virtual-desktop pixels and then to the 0..65535
range `SendInput` expects. The phone never has to know about DPI scaling, monitor origins, or
negative coordinates on a display left of the primary — and all the arithmetic that must be right
lives on one side, where it is unit-tested against multi-monitor layouts.

A position outside the display is **refused, not clamped**. Clamping would move the pointer to the
nearest edge and hide the client bug that produced it, while still clicking somewhere the owner did
not touch.

**Keys are a closed enum.** A raw virtual-key code off the network into a Win32 structure is exactly
the validation everyone skips. Everything reachable has a deliberate mapping; anything else cannot be
pressed. Text goes through `input.text` as UTF-16 with `KEYEVENTF_UNICODE`, so an owner typing in
their own language gets what they typed rather than a mangled approximation.

**Rate limiting** is a token bucket — 250/second sustained, 500 burst. Not to stop an attacker, who
by definition is the authenticated Primary Phone, but to stop a bug: a client stuck in a move loop
would otherwise saturate the link and the desktop at once.

## 57. Control is not connection

Being connected is not being in control. Input becomes possible when the owner turns it on, and
stops when they leave the screen, background the app, disconnect, or anything else fails.

The reason is plain: a phone left connected on a desk should not be a keyboard.

Streaming and input are also independent of each other. It is possible to view without input, to
stop the stream while keeping control, and to do either without touching the other — which is what a
future milestone wanting one without the other will need.

**Held state is tracked on both sides.** The PC records every button and key a session put down and
releases exactly those when the session ends for any reason, including a dropped socket. The phone
separately forgets what it believed was latched, because after a reconnect a stale Ctrl would make
the next keystroke a silent shortcut. Neither side trusts the other to have cleaned up.

## 58. Windows will not let this reach everything

`SendInput` from a standard-user process cannot send input to a window running as administrator, to
the UAC prompt, or to the secure desktop. Nexus keeps that restriction.

Getting past it would mean running PC Core elevated, installing a privileged helper, or shipping a
driver — three ways of making a remote-control tool considerably more dangerous than the thing it
controls. When Windows refuses, the call reports `BLOCKED_BY_INTEGRITY` and the phone says so, once,
rather than appearing broken.

The request still *succeeds*: the command was valid and was delivered as far as it could go. Only
`delivered` is false. Conflating "you may not ask" with "Windows did not accept" would have made the
diagnosis harder.

## 59. Gestures

| Gesture | Direct | Touchpad |
|---|---|---|
| Tap | move there, left click | left click where the pointer is |
| Double tap | move there, double click | double click |
| Long press | move there, right click | right click |
| Drag | button down, follow the finger | button down, pointer moves by the delta |
| Two fingers vertically | scroll | scroll |
| Tap on the letterbox | show or hide the controls | — |

Small and mostly what a phone already teaches. There are no three-finger chords or edge swipes to
memorise, because a remote desktop is already unfamiliar to operate and a gesture nobody remembers
is a feature nobody uses. Anything ambiguous — middle click, a right click without waiting — has an
explicit button instead.

`DIRECT` is the default. `TOUCHPAD` exists because precision work on a 1280-wide desktop shown at
phone size is easier with a relative pointer than with a fingertip covering forty pixels of it.

## 60. Modifiers latch

There is no way to hold Ctrl and tap C at once on a touchscreen. Tapping `Ctrl` latches it, the
toolbar shows it lit, and the next key or click is wrapped: modifier down, action, modifier up. The
latch is then spent.

The release is in a `finally`, and only modifiers that actually went down are released. A refused
Ctrl+C that left Ctrl down would make every subsequent keystroke a shortcut, with nothing on the
phone showing why — and that is precisely the bug the tests found in the first version of this code,
where the modifier-down loop sat outside the `try`.

Typing is never wrapped in modifiers. Ctrl plus a paragraph is not a thing anyone means.

## 61. The indicator

The PC always shows something while a phone is watching or driving it.

| Mode | Input live | Watching only |
|---|---|---|
| `STANDARD` (default) | badge + tray | tray |
| `MINIMAL` | tray | tray |
| `ALWAYS_VISIBLE` | badge + tray | badge + tray |

The badge is a small translucent pill in the corner of the work area: layered for per-pixel alpha,
click-through, never activated, out of the taskbar and Alt+Tab. It cannot take focus or intercept a
click.

**There is no mode that shows nothing, and there will not be one.** A machine that can be watched
and typed on with no sign of it is a surveillance tool, and the difference between Nexus and one is
exactly that the owner can always tell. What the modes trade is how much the indication gets in the
way. `MINIMAL` drops the badge for an owner who finds it distracting on their own machine; the tray
entry stays, undisguised.

The setting is **local-only**. `indicator.get` and `indicator.set` are not in the DEVICE allowlist,
so a phone cannot quiet the sign that it is there. A test asserts that no combination of mode and
activity produces an invisible session.

## 62. Security state is not touched

Remote control works in all three states and changes none of them.

`SECURITY_SESSION` plus an authenticated phone driving the machine is **not** `OWNER_PRESENT`. The
system does not get to conclude that whoever is controlling it must be the owner — that is the
inference a "this was me" workflow exists to make explicitly, and it is not implemented here.

## 63. Testing added in v0.6

| Area | What it proves |
|---|---|
| Frame header | The layout by byte offset, and that every malformed shape is refused before an allocation |
| Stream service | Latest-frame-wins under a slow reader; a lost capture reopens; an unavailable desktop is retried; stopping releases everything |
| Input service | Normalised coordinates on multi-monitor layouts including negative origins; refusal rather than clamping; held state released in the right order; rate limiting; per-session budgets |
| Indicator | The full mode/activity table, persistence, and that nothing can hide a live session |
| Transport | Binary frames on a real socket beside JSON responses; input gated on a session; a dropped connection releases held buttons and modifiers |
| Kotlin | Frame parsing and its malformed cases; letterbox mapping at the corners and in both orientations; modifier ordering including the failure paths |
| Cross-language | The frame header byte for byte, every input envelope, and the key set in both directions |

The Direct3D half cannot be faked, so it is verified against the real display by
`--selftest-remote-control`, which measures capture, encoding, input injection and the indicator on
the actual machine.

## 64. Explicitly NOT in v0.6

Live audio. Microphone or camera. Security-session recording or activity detection. Push
notifications. A background Android service holding a socket open. File modification. Shell
execution. Application automation. AI of any kind. H.264 or WebRTC — revisit only against a measured
blocker, not a preference.
