# PC Core — Specification

**Current version: v0.3 (Personal Device Pairing, Persistence, and Startup).**

This document is cumulative. Sections 1–11 describe the v0.1 foundation, section 12 covers v0.2, and **sections 13–25 cover v0.3**. Throughout, what is *implemented* is separated from what is *deliberately postponed* — nothing here describes behaviour that does not exist.

| Milestone | Adds | Status |
|---|---|---|
| v0.1 | Protocol, capability layer, filesystem boundary, loopback transport, five read-only capabilities | shipped |
| v0.2 | Security-state service, three `security.*` capabilities, `security.state_changed`, typed caller identity | shipped (§12) |
| v0.3 | State persistence, event-lock refactor, PC identity, one Primary Phone, HMAC authentication, Windows auto-start | shipped (§13–25) |
| later | Remote transport, Android client, policy decision point, security recording | not started |

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

**v0.2 implements none of it.** No RBAC, no ACLs, no permission catalogue, no interception. The seam is named here so the next milestone does not have to invent one.

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
