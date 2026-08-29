# PC Core v0.4

A user-level Windows process that exposes a **small, explicit set of capabilities** over a structured request/response/event protocol — locally over loopback, and remotely to one paired phone over Tailscale.

**This is a personal-use system: one owner, one Windows PC, one Android phone.** It is not built for multiple users, organizations, device fleets, or distribution, and narrower designs were chosen over general ones throughout.

- **v0.1 — Foundation:** protocol, capability layer, filesystem boundary, loopback transport, five read-only capabilities.
- **v0.2 — Security State Foundation:** one authoritative owner-presence state, three `security.*` capabilities, a `security.state_changed` event, typed caller identity.
- **v0.3 — Personal Device Pairing, Persistence, and Startup:** security state persists across restarts, a persistent PC identity, pairing with exactly one Primary Phone over a 256-bit shared secret, HMAC-SHA-256 authentication, and per-user Windows auto-start.
- **v0.4 — Authenticated Tailscale Remote Transport:** the Primary Phone can connect over your Tailscale network, authenticate with that credential, and drive a restricted capability set. The listener binds **only** to a detected Tailscale address, never LAN or wildcard.

Still deliberately absent: no Android app, no AI, no activity detection, no recording, no internet exposure. See [SPEC.md](SPEC.md) (§26–37 cover v0.4) and [PLAN.md](PLAN.md).

> **The rule:** external controllers interact with the computer *through capabilities*, never through arbitrary Windows functionality. If PC Core does not expose it, a caller cannot do it.

---

## Requirements

- Windows 10 1809 or later
- .NET SDK 10.0 (`winget install Microsoft.DotNet.SDK.10`)
- **No administrator rights.** PC Core warns loudly if you run it elevated.

## Run

```bash
dotnet run --project src/PcCore.Host
```

It binds `http://127.0.0.1:47811` and prints a per-run session token:

```
info: PC Core 0.4.0 starting on DESKTOP-XXXX (Microsoft Windows 10.0.26200)
info: Filesystem boundary: 3 allowed root(s): C:\Users\you\Desktop, ...
info: Capabilities: device.get_primary, device.unpair_primary, files.list, files.stat,
      pairing.begin, pairing.cancel, screen.capture, security.arm, security.disarm,
      security.get_state, system.get_info, system.list_processes
info: PC instance e2df2a34-...; primary phone: not paired
info: Security state: OwnerPresent
info: Registered auto-start: HKCU\Software\...\Run\PcCore = "...\PcCore.Host.exe"
info: Remote transport port: 47812 (Tailscale only)
info: Listening on http://127.0.0.1:47811 (loopback only)

  Session token: 6f1c...
```

Open <http://127.0.0.1:47811/> for the developer test client. The host injects the token into that page, so nothing needs pasting.

## Build and test

```bash
dotnet build PcCore.slnx -c Release
```

```bash
dotnet run --project tests/PcCore.Tests -c Release
```

> **Note on `dotnet test`.** It does not work with this toolchain combination: the Microsoft.Testing.Platform build bundled with `xunit.v3` 4.0.0 does not expose the `--server` mode that the .NET 10.0.400 SDK's `dotnet test` uses to drive it, so the run reports "Zero tests ran". `dotnet run --project tests/PcCore.Tests` is a first-class xunit v3 execution path and runs the whole suite. `global.json` already carries the `Microsoft.Testing.Platform` opt-in, so `dotnet test` should start working once the packages catch up.

## Calling it

Every request is one envelope. `POST /rpc` for request/response; `GET /ws` for the same plus server-pushed events.

```bash
curl -s http://127.0.0.1:47811/rpc \
  -H "Content-Type: application/json" \
  -H "X-PcCore-Token: <token>" \
  -d '{"protocol_version":1,"id":"1","type":"request","action":"system.get_info"}'
```

The token is also written to `%LOCALAPPDATA%\PcCore\session.token`.

### Capabilities

| Action | Parameters | Returns |
|---|---|---|
| `system.get_info` | — | machine name, OS, versions, uptime, timestamps, elevation state |
| `system.list_processes` | — | `{ count, processes: [{ pid, name, working_set_bytes? }] }` |
| `files.list` | `{ path, include_hidden? }` | one directory's entries, non-recursive |
| `files.stat` | `{ path }` | metadata for one file or directory |
| `screen.capture` | `{ display?, max_width? }` | one base64 PNG |
| `security.get_state` | — | `{ state, previous_state, since_utc }` |
| `security.arm` | — | `OWNER_PRESENT -> ARMED`; `{ changed, state, previous_state, since_utc }` |
| `security.disarm` | — | back to `OWNER_PRESENT` from `ARMED` or `SECURITY_SESSION` |
| `device.get_primary` | — | `{ paired, device_id?, display_name?, paired_at_utc? }` — never the secret |
| `pairing.begin` | `{ replace_existing? }` | `{ session_id, expires_at_utc, pc_instance_id, payload }` |
| `pairing.cancel` | — | `{ cancelled }` |
| `device.unpair_primary` | — | `{ unpaired }`; deletes the stored credential |

Read-only with respect to your machine: there is no `files.read`, no write/delete/move, no input injection, no shell execution. The `pairing.*` and `device.*` actions change only PC Core's own pairing state.

The phone-facing operations are deliberately **not** capabilities, because a phone completing pairing or proving its identity has not authenticated yet:

| Endpoint | Purpose |
|---|---|
| `POST /device/pair` | Complete a pairing session; returns the shared secret exactly once |
| `POST /device/auth/challenge` | Get a single-use random challenge |
| `POST /device/auth/verify` | Present an HMAC proof |
| `GET /pairing/qr.png` | QR image of the active pairing payload |

### Security state (v0.2)

Three states — `OWNER_PRESENT` (start-up default), `ARMED`, `SECURITY_SESSION` — held in memory by one authoritative singleton service. Four permitted transitions:

| From | To | Trigger |
|---|---|---|
| `OWNER_PRESENT` | `ARMED` | `security.arm` |
| `ARMED` | `OWNER_PRESENT` | `security.disarm` |
| `ARMED` | `SECURITY_SESSION` | **internal only** — no capability exposes it |
| `SECURITY_SESSION` | `OWNER_PRESENT` | `security.disarm` |

Anything else is refused with `invalid_state_transition` and the state is left untouched — never silently redirected. `SECURITY_SESSION -> ARMED` is deliberately excluded; the reasoning is in [SPEC.md §12.3](SPEC.md).

Arm and disarm are idempotent: asking for the state you are already in succeeds with `changed: false` and publishes **no** event. A real change publishes `security.state_changed` over the existing WebSocket channel:

```json
{ "protocol_version": 1, "type": "event", "event": "security.state_changed",
  "payload": { "previous_state": "OWNER_PRESENT", "current_state": "ARMED" },
  "timestamp_utc": "..." }
```

**What this state does *not* do in v0.2:** nothing detects activity, nothing records anything, nothing notifies anyone, and nothing changes what a caller is permitted to do. It is the foundation those features will read — see [SPEC.md §12.10](SPEC.md) for the full exclusion list.

## Pairing your phone (v0.3)

Open the test page, click **pairing.begin**, and it shows a QR code and a copyable `NEXUS1.…` code. Either form carries the same short-lived payload:

```
NEXUS1.<base64url({"v":1,"pc":"<PcInstanceId>","s":"<sessionId>","k":"<pairingSecret>"})>
```

The session is single-use, expires in five minutes (configurable), and is consumed on any completion attempt — right or wrong. Completing it mints a random `DeviceId` and a **256-bit shared secret**, returned exactly once for the phone to keep. There is no PIN to type.

There is exactly **one Primary Phone, or none**. `pairing.begin` refuses with `already_paired` if a phone is paired, unless you pass `replace_existing: true`. `device.unpair_primary` deletes the credential and is idempotent; the old secret then authenticates nothing.

Authentication is HMAC-SHA-256 challenge/response — the permanent secret is never transmitted after pairing. The signed message binds the purpose, this installation, and the device id, so a proof cannot be replayed elsewhere. Challenges are single-use and expire.

**A verified proof is the only way to obtain an `ActorType.DEVICE` caller identity**: `AuthenticatedDevice` has no public constructor, so a request body claiming to be a device has no effect at all. Loopback callers stay `LOCAL` with a null device id.

The Android app does not exist yet, so the test page carries a development simulator that pairs and authenticates. Its credential lives in page memory only and is gone on reload.

## Persistent state (v0.3)

Everything that survives a restart lives in `%LOCALAPPDATA%\Nexus`, never in this repository:

| File | Contents |
|---|---|
| `instance.json` | random `PcInstanceId` — not derived from MAC, serial, product id, user name, or hostname |
| `security-state.json` | state, previous state, since-timestamp, revision |
| `primary-phone.json` | device id, display name, paired-at, and the shared secret as a **DPAPI blob** (`CurrentUser` scope) |

Writes are atomic: temp file, flushed to disk, then an atomic replace. A corrupt file is renamed aside rather than deleted.

**Restart behaviour:** `OWNER_PRESENT` → `OWNER_PRESENT`, `ARMED` → `ARMED`, and `SECURITY_SESSION` → `SECURITY_SESSION`. A security session is never downgraded by a reboot — otherwise rebooting would be the cheapest way to clear an incident. It ends when you end it.

**If the state file is corrupt**, PC Core starts at `ARMED`, not `OWNER_PRESENT`: a damaged file means the previous state is unknown, and the one thing that must not be inferred from "unknown" is that the owner is present. It does not fabricate a `SECURITY_SESSION` either. Clear it with `security.disarm`.

## Auto-start (v0.3)

PC Core registers itself to start at sign-in with a single per-user registry value:

```
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
  PcCore = "<full path to PcCore.Host.exe>"
```

No administrator rights, no service, no scheduled task, no watchdog, no hiding. It appears in Task Manager's **Startup apps** tab, where you can disable it without touching this application.

```bash
dotnet run --project src/PcCore.Host -- --startup-status
```

Use `--install-startup` / `--uninstall-startup` to manage it manually. To stop PC Core registering itself while you debug, set `PcCore:Startup:Enabled` to `false` in `appsettings.json` — that flag is the only thing that governs registration.

> If you move or publish the executable, re-run `--install-startup` so the registered path follows it.

## Remote access from your phone (v0.4)

The paired Primary Phone can reach PC Core over your Tailscale network. PC Core does **not** install, configure, or log into Tailscale — install it and sign in yourself on both the PC and the phone; PC Core only uses the private network that already exists.

```
127.0.0.1     → loopback development transport (47811)
100.x.x.x     → authenticated remote transport (47812)
192.168.x.x   → NOT listening
public WAN    → NOT listening
```

**Binding is the safety-critical part.** The remote listener opens on exactly one address, and only when two independent facts agree: the adapter identifies itself as Tailscale *and* the address is inside `100.64.0.0/10` (checked with address arithmetic, not a `"100."` string prefix). If either is missing there is **no listener at all** — there is no configuration setting for a bind address, no wildcard path, and no fallback to LAN. If Tailscale is not running you will see:

```
warn: Remote transport: unavailable (no Tailscale network interface is present...). Re-checking every 30s;
      no remote listener will be opened until a Tailscale address exists.
```

When Tailscale comes up the listener starts on its own — no PC Core restart needed. If the interface goes away, the listener stops.

**Authentication** reuses the v0.3 Primary Phone credential; no second credential exists. On connect the PC sends a random single-use challenge, the phone returns an HMAC-SHA-256 proof, and the connection is bound to that identity for its lifetime — subsequent commands do not re-sign. Closing the connection ends the session; reconnecting requires a fresh challenge. Up to 4 simultaneous sessions are allowed (a reconnect race should never lock you out); at the limit the oldest is dropped.

**No TLS**, deliberately: the traffic is already inside WireGuard, and PC Core separately proves the peer holds the shared secret. Adding certificates would mean generation, distribution, renewal, and pinning in the Android client — complexity buying a second encryption layer inside the first. That trade only holds because the listener cannot bind anything but a Tailscale address.

### What the phone may do

| Allowed remotely | Denied remotely |
|---|---|
| `system.get_info`, `system.list_processes` | `pairing.begin`, `pairing.cancel` |
| `files.list`, `files.stat` | `device.unpair_primary`, `device.get_primary` |
| `screen.capture` | anything added later, until named in the policy |
| `security.get_state`, `security.arm`, `security.disarm` | |

The phone is a **controller for the PC, not the authority that decides which phone is trusted** — device management stays local-only, so a stolen phone credential can never enroll another device. Denials return `not_authorized`, do not drop the connection, and execute no part of the action. Policy does not widen any existing boundary: `files.list` from the phone is still confined to the allowed roots.

### Trying it with the development simulator

The Android app does not exist yet. A protocol-level simulator stands in for it:

```bash
dotnet run --project src/PcCore.Host -- --simulate-phone-pair NEXUS1....
```

```bash
dotnet run --project src/PcCore.Host -- --simulate-phone ws://100.x.x.x:47812/nexus
```

The first pairs it (copy the code from the local test page) and stores the phone's half of the credential DPAPI-protected in `dev-phone-simulator.json`. The second runs the full conversation — authenticate, confirm `DEVICE`, call the allowed capabilities, arm, receive `security.state_changed`, disarm, confirm a device-management call is denied, disconnect, reconnect.

## Security boundary in one paragraph



Nothing on disk is reachable unless it is listed in `PcCore:Files:AllowedRoots` (default: Desktop, Documents, Downloads). Every caller-supplied path is rejected by shape (relative, UNC, device namespace, alternate data stream, wildcards), canonicalised, then **resolved to its real path by the kernel** (`GetFinalPathNameByHandle`) so that junctions, symlinks, mount points and 8.3 short names cannot alias past the boundary — and only then containment-tested **segment by segment**, not by string prefix. Unresolvable means denied. Zero roots means everything is denied, never everything allowed. [SPEC.md §6](SPEC.md) documents the pipeline and, just as importantly, the Windows edge cases that remain (TOCTOU, hardlinks, mount points inside a root).

Loopback is not an authorisation boundary, so `/rpc` and `/ws` also require the session token and validate `Host` and `Origin` to block DNS rebinding. See [SPEC.md §7.3](SPEC.md).

## Configuration

`src/PcCore.Host/appsettings.json`, overridable with `PCCORE_`-prefixed environment variables.

```json
{
  "PcCore": {
    "Transport": { "Port": 47811, "RequireToken": true, "MaxRequestBytes": 65536 },
    "Files": { "AllowedRoots": [ "{Desktop}", "{Documents}", "{Downloads}" ] },
    "Screen": { "Enabled": true, "MaxWidthLimit": 7680 },
    "Pairing": { "SessionTimeoutSeconds": 300, "ChallengeTimeoutSeconds": 60, "DefaultDisplayName": "Primary Phone" },
    "Startup": { "Enabled": true },
    "RemoteTransport": { "Enabled": true, "Port": 47812, "AvailabilityCheckSeconds": 30, "MaxSessions": 4 }
  }
}
```

Tokens: `{Desktop}` `{Documents}` `{Downloads}` `{Pictures}` `{Music}` `{Videos}` `{UserProfile}`, or absolute paths. A root that cannot be resolved is logged and dropped, never silently widened.

The bind **address** is not configurable. That is deliberate: an "allowed bind address" setting is exactly how a localhost-only service ends up on the LAN.

## Layout

```
src/
  PcCore.Protocol/            contracts; the only assembly that touches System.Text.Json
  PcCore.Capabilities/        dispatcher, registry, path security, security state,
                              identity/pairing/auth, state storage, all twelve actions
  PcCore.Windows/             the only assembly with P/Invoke, System.Drawing, DPAPI, or the registry
  PcCore.Transport.Loopback/  Kestrel, /rpc, /ws, token + loopback guards (127.0.0.1 only)
  PcCore.Transport.Remote/    Tailscale-bound WebSocket, device authentication, sessions
  PcCore.Host/                composition root, config, logging, device endpoints, QR, test page
tests/
  PcCore.Tests/               340 tests
```

`PcCore.Capabilities` has no reference to `PcCore.Windows`, and `PcCore.Transport.Loopback` has no reference to it either. Those are compiler-enforced boundaries, not conventions: a capability cannot tell how a caller arrived, and a transport cannot control Windows.
