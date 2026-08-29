# PC Core v0.2

A user-level Windows process that exposes a **small, explicit set of capabilities** over a structured request/response/event protocol, on **loopback only**.

- **v0.1 — Foundation:** protocol, capability layer, filesystem boundary, loopback transport, five read-only capabilities.
- **v0.2 — Security State Foundation:** one authoritative owner-presence state, three `security.*` capabilities, a `security.state_changed` event, and typed caller identity. **State only** — no detection, no recording, no alerting, no authorization.

Still deliberately small: no AI, no Android, no remote access. See [SPEC.md](SPEC.md) for the full specification (§12 covers v0.2) and [PLAN.md](PLAN.md) for the implementation plan.

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
info: PC Core 0.2.0 starting on DESKTOP-XXXX (Microsoft Windows 10.0.26200)
info: Filesystem boundary: 3 allowed root(s): C:\Users\you\Desktop, ...
info: Capabilities: files.list, files.stat, screen.capture, security.arm, security.disarm,
      security.get_state, system.get_info, system.list_processes
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

Read-only. There is no `files.read`, no write/delete/move, no input injection, no shell execution.

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
    "Screen": { "Enabled": true, "MaxWidthLimit": 7680 }
  }
}
```

Tokens: `{Desktop}` `{Documents}` `{Downloads}` `{Pictures}` `{Music}` `{Videos}` `{UserProfile}`, or absolute paths. A root that cannot be resolved is logged and dropped, never silently widened.

The bind **address** is not configurable. That is deliberate: an "allowed bind address" setting is exactly how a localhost-only service ends up on the LAN.

## Layout

```
src/
  PcCore.Protocol/            contracts; the only assembly that touches System.Text.Json
  PcCore.Capabilities/        dispatcher, registry, path security, security state, all eight actions
  PcCore.Windows/             the only assembly with P/Invoke or System.Drawing
  PcCore.Transport.Loopback/  Kestrel, /rpc, /ws, token + loopback guards
  PcCore.Host/                composition root, config, logging, test page
tests/
  PcCore.Tests/               219 tests
```

`PcCore.Capabilities` has no reference to `PcCore.Windows`, and `PcCore.Transport.Loopback` has no reference to it either. Those are compiler-enforced boundaries, not conventions: a capability cannot tell how a caller arrived, and a transport cannot control Windows.
