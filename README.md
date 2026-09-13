# Nexus v1.4.0

A user-level Windows process that exposes a **small, explicit set of capabilities** over a structured request/response/event protocol — locally over loopback, and remotely to one paired phone over Tailscale.

**This is a personal-use system: one owner, one Windows PC, one Android phone.** It is not built for multiple users, organizations, device fleets, or distribution, and narrower designs were chosen over general ones throughout.

- **v0.1 — Foundation:** protocol, capability layer, filesystem boundary, loopback transport, five read-only capabilities.
- **v0.2 — Security State Foundation:** one authoritative owner-presence state, three `security.*` capabilities, a `security.state_changed` event, typed caller identity.
- **v0.3 — Personal Device Pairing, Persistence, and Startup:** security state persists across restarts, a persistent PC identity, pairing with exactly one Primary Phone over a 256-bit shared secret, HMAC-SHA-256 authentication, and per-user Windows auto-start.
- **v0.4 — Authenticated Tailscale Remote Transport:** the Primary Phone can connect over your Tailscale network, authenticate with that credential, and drive a restricted capability set. The listener binds **only** to a detected Tailscale address, never LAN or wildcard.
- **v0.5 — First Native Android Client:** the phone app. Pair by scanning a QR, store the credential in the Android Keystore, connect over Tailscale, and control the PC — screen, files, system, security — with live state updates.
- **v0.6 — Live Screen and Remote Input:** Desktop Duplication streaming, pointer and keyboard injection, an on-screen indicator while it is happening.
- **v0.7 — Security Detection, Evidence, Notifications, Owner Verification:** arming the PC, noticing a person at it, recording evidence, and answering the alert from the phone.
- **v0.8 — Native Windows Input:** input reaches ordinary Administrator windows, Direct mode sends real Windows touch, and the security detector can still tell Nexus's own input from a person's.
- **v0.9 — Virtual HID:** a signed UMDF2 source driver publishes a Nexus mouse, keyboard and touch screen through the Windows Virtual HID Framework. Input arrives from a device rather than an injection call, so it reaches elevated windows without an elevated broker — and Raw Input can still tell those devices apart from the ones on the desk.
- **v1.0 — Owner-Authenticated Sessions, Secure Desktop, Real Cursors:** the phone app opens locked and unlocks with Android's own `BiometricPrompt`, backed by a hardware Keystore key the platform will not use without a real prompt. Remote control now requires proving *who* is holding the phone, not just *which* phone it is. A LocalSystem helper makes the UAC consent prompt visible remotely without weakening it, and the screen stream composites the actual Windows pointer shape instead of forcing an arrow.
- **v1.1 — Evidence Playback, a Windows Shell, and Live Monitoring:** camera clips and audio from a security incident play on the phone instead of saying they cannot; Nexus has a tray icon, a Start Menu entry and one authoritative version; and the owner can watch or listen to the room their PC is in, over the authenticated session, with the PC saying so on its own screen the whole time.

- **v1.1.1 — Why a phone was refused:** every failed device authentication used to print one sentence, "invalid proof for device X", for three different problems with three different remedies. It now names which one, using identifiers only, in the PC's own log — while the answer sent back over the wire stays a single opaque `authentication_failed`, so the detail cannot be used to enumerate device ids. Repeats are collapsed instead of filling the log, the phone stops retrying a credential the PC has already refused, and both ends print the device id, the installation id and a one-way credential fingerprint, so the three values that make up the signed message can actually be compared. PC Core also keeps a log on disk now (`%LOCALAPPDATA%\Nexus\pccore.log`, one previous generation kept, reachable from the tray) - started from the Start Menu or at sign-in it has no console, so every diagnostic it wrote used to go nowhere.

- **v1.1.2 — Live monitoring could never start:** the host handed the live-sensor service an authorisation predicate that rebuilt a caller identity from a connection id. A rebuilt caller has no device id, and authority is keyed to the device, so it answered "not authorised" for every session that ever asked — while the same phone on the same socket drove remote control and evidence fine. The caller is now passed through whole, and the predicate is a named function the tests exercise instead of a lambda in the composition root.

- **v1.3 — Nexus updates itself:** one release manifest, one publish command, a separate updater that swaps only changed files and rolls back if the new PC Core does not answer, and a phone that downloads its own APK from its PC over the authenticated session. See [Updating Nexus](#updating-nexus-v13).

- **v1.4 — Files and Clipboard:** browse the PC's drives from the phone, open or save a file, upload into any folder Windows lets you write to, with progress, cancel and resume; and text between the two clipboards. All of it behind the owner session. See [Files and Clipboard](#files-and-clipboard-v14).

Still deliberately absent: no AI integration, no virtual display (IDD), no multi-user support, no internet exposure — the remote path is Tailscale only. See [SPEC.md](SPEC.md) and [PLAN.md](PLAN.md).

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

For the Android app:

```bash
cd android/Nexus && ./gradlew :app:testDebugUnitTest
```

```bash
cd android/Nexus && ./gradlew :app:connectedDebugAndroidTest
```

The second needs a device or emulator attached — it covers the Android Keystore, which a JVM stub
could not meaningfully exercise.

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
| `input.touch` | `{ action, x, y, display_index?, contact_id? }` | put a touch contact down, move it, or lift it |
| `input.status` | — | which input path is carrying input, and whether it is healthy |
| `pairing.begin` | `{ replace_existing? }` | `{ session_id, expires_at_utc, pc_instance_id, payload }` |
| `pairing.cancel` | — | `{ cancelled }` |
| `device.unpair_primary` | — | `{ unpaired }`; deletes the stored credential |

That was the v0.1 surface. Later milestones add screen, input, security, update, and (v1.4) file transfer and clipboard capabilities, each described in its own section below; there is still no delete/move and no shell execution. The `pairing.*` and `device.*` actions change only PC Core's own pairing state.

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

The Android app is the real client now (see below). The protocol-level simulator remains for
debugging the transport without a phone:

```bash
dotnet run --project src/PcCore.Host -- --simulate-phone-pair NEXUS1....
```

```bash
dotnet run --project src/PcCore.Host -- --simulate-phone ws://100.x.x.x:47812/nexus
```

There is also `--simulate-phone-enroll NEXUS2....`, which pairs the way a real phone does — over the
Tailscale transport, proving possession of the pairing secret rather than sending it — while
`--simulate-phone-pair` pairs over loopback with the v1 code the local page still offers. Either
stores the phone's half of the credential DPAPI-protected in `dev-phone-simulator.json`. The last
runs the full conversation — authenticate, confirm `DEVICE`, call the allowed capabilities, arm, receive `security.state_changed`, disarm, confirm a device-management call is denied, disconnect, reconnect.

## The Android app (v0.5)

The phone app lives in `android/Nexus/` and is built with the Android Studio toolchain already on
this machine — Gradle 9.4.1, AGP 9.2.1, Kotlin 2.2.10, JDK 21. It targets **Android 12 and later**.

```bash
cd android/Nexus && ./gradlew :app:assembleDebug
```

The APK lands at `android/Nexus/app/build/outputs/apk/debug/app-debug.apk`. Install it with
`adb install -r <path>`, or copy it to the phone. It is not published anywhere: no Play Store, no
app distribution service, no CI. This is one app for one phone.

### Pairing a real phone

1. Install Tailscale on **both** the PC and the phone and sign both into your tailnet. Nexus never
   touches your Tailscale account and cannot set this up for you.
2. Start PC Core. It logs the Tailscale address it bound, or explains why it did not.
3. Open `http://127.0.0.1:47811/` on the PC and click **pairing.begin**. A QR appears, along with the
   same text to copy. It expires in five minutes and works exactly once.
4. Open Nexus on the phone and tap **Scan QR** — or **Paste pairing code** if the camera is
   inconvenient. The camera permission is asked for at that moment and never at launch.
5. The phone proves it holds the pairing secret, receives its permanent credential, and reconnects
   as the Primary Phone.

If the PC has no Tailscale address when you start pairing, the page says so and the code carries no
address. The phone will say the same thing rather than failing vaguely.

### What the app does

| Screen | Uses | Notes |
|---|---|---|
| Home | `system.get_info` | Connection state and which machine answered |
| Screen | `screen.capture` | One screenshot, fitted to the phone. Manual refresh — not streaming |
| Files | `files.*` | Browse, open, save and upload since v1.4 — see [Files and Clipboard](#files-and-clipboard-v14). No rename, move or delete |
| System | `system.get_info`, `system.list_processes` | Filterable process list. Nothing can be started or killed |
| Security | `security.get_state`, `security.arm`, `security.disarm` | Live: `security.state_changed` updates the screen without a refresh |

### Where the credential lives

An AES-256-GCM key generated in the Android Keystore seals the shared secret into app-private
storage. The key cannot be exported, so the file is useless on any other device. The secret never
reaches the UI, never appears in a log, and is excluded from cloud backup and device transfer. There
is no analytics SDK and no crash reporter.

**Forget this PC** on the home screen deletes the phone's copy. The PC keeps its own record until you
unpair there — the phone is a controller, not the authority over which phone is trusted.

### Why plain WebSocket is acceptable here

The connection is `ws://100.x.x.x:47812/nexus` — no TLS — because it runs inside WireGuard, which
Tailscale has already authenticated and encrypted, and because PC Core separately proves the phone
holds the shared secret. That argument only holds while the traffic genuinely stays inside the
tunnel, so the app enforces it:

```
100.64.0.0/10  → the only addresses Nexus will dial
192.168.x.x    → refused
a host name    → refused, and never resolved
wss:// or another path → refused
```

Every endpoint — scanned, pasted, or restored from storage — goes through the same check. A pairing
code that names `example.com` is rejected before a socket is opened.

### Connection behaviour

Nexus connects while the app is open and disconnects when it leaves the foreground. There is no
background service: nothing in v0.5 needs to arrive while the app is closed, and security
notifications will need a push channel designed for the purpose rather than a socket held open all
day. Transient failures retry with a bounded backoff, from two seconds to thirty. A rejected
credential does not retry at all — it will not become valid by trying again.

When it cannot connect, it says which of these it is:

- **No network** — the phone has no connection at all.
- **Tailscale not connected** — the phone has no tailnet address of its own.
- **PC unreachable** — the phone is on the tailnet and the PC did not answer.
- **Authentication failed** — the PC rejected the credential; pair again.

## Live screen and remote control (v0.6)

The Screen action is now the remote-control screen: the PC's desktop, live, with the pointer and
keyboard.

```
┌─────────────────────────┐
│                         │   move            → move the pointer
│      PC SCREEN          │   tap             → left click
│                         │   hold, then move → drag
│                         │   hold, then lift → right click
├─────────────────────────┤   two fingers     → scroll
│ Direct | Touchpad   [•] │   tap a bar       → hide the controls
│ Left Double Right Mid   │
│ Keyboard          Stats │
└─────────────────────────┘
```

**Moving the pointer never presses anything.** Holding still for a moment is what asks for a button,
and what you do next decides which: move and it is a drag, lift and it is a right click. Everything
else has a button on the toolbar.

**Watching is not controlling.** Opening the screen starts the stream; the `Control` switch is what
lets the phone move the pointer and type. Leaving the screen, backgrounding the app, or losing the
connection turns it off again and releases anything left held — a button mid-drag, a latched Ctrl.
Neither side trusts the other to have cleaned up.

**Direct** puts the pointer where your finger is. **Touchpad** moves it by how far your finger
moved, which is easier for precision work when a fingertip covers forty pixels of desktop.

**Modifiers latch,** because you cannot hold Ctrl and tap C at once on a touchscreen. Tap `Ctrl`, it
lights up, and the next key or click is wrapped in it: Ctrl+C, Alt+Tab, Win+R all work. The latch is
spent by the action it modified.

**Typing** goes through the text field and is sent as Unicode, so a keyboard in any language
produces what it should. The buttons beside it are for keys text cannot express — Esc, Tab, Enter,
Backspace, Delete, Home, End, Page Up/Down, and the arrows.

### What the PC shows while this happens

Always something. The three modes trade how much it gets in the way, never whether it exists:

| Mode | Input live | Watching only |
|---|---|---|
| `STANDARD` (default) | badge + tray | tray |
| `MINIMAL` | tray | tray |
| `ALWAYS_VISIBLE` | badge + tray | badge + tray |

The badge is a small translucent pill in the corner, click-through and unable to take focus. Change
the mode on the local test page — it is a decision about this PC, taken at this PC, and the phone
deliberately cannot reach it.

### What it costs

Measured on this machine against a continuously-changing 1920x1080 desktop, which is the worst case
— a still desktop sends nothing at all:

```
773 frames in 60s   12.9 fps   64.8 KB/frame   6.8 Mbit/s   9.7 ms encode   0 capture losses
```

Defaults are 1280x720, JPEG quality 65, 15 fps, in `appsettings.json` under `PcCore:Stream`. Raising
`MaxWidth` sharpens text at proportionally more bandwidth.

### What Windows will not allow

Without native input (below), a standard-user process cannot send input to a window running as
administrator. With it, ordinary elevated application windows become reachable — the UAC prompt and
the lock screen never do, and that is deliberate. See **Native input** and **The Secure Desktop**.

### Checking it works without a phone

```bash
dotnet run --project src/PcCore.Host -- --selftest-remote-control --seconds 15
```

Enumerates displays, duplicates one, measures capture and encoding, moves the pointer four pixels
and back to prove injection reaches Windows, and cycles the indicator through its three modes. Add
`--skip-input` to leave the pointer alone, or `--width`/`--quality`/`--fps` to measure a different
configuration.

## Security workflow (v0.7)

Arm the PC, walk away. If someone uses it, Nexus opens an incident, records what it can, and sends
one alert to your phone.

```
OWNER_PRESENT ──arm──> ARMED ──someone uses the PC──> SECURITY_SESSION
                                                            │
                                              ┌─────────────┴─────────────┐
                                     This was me                       Not me
                                     (biometric)                          │
                                            │                             │
                                    OWNER_PRESENT              SECURITY_SESSION
                                    evidence kept              + HIGH ALERT
```

**Nothing is recorded while you are present.** Not a setting — in `OWNER_PRESENT` the detector's
observations are discarded before any evidence path is reachable. Arming is what turns it on.

**There is no keylogger.** Detection reads two fields of a keyboard event: was it injected, and was
it Nexus. The key code is never read. The timeline says *keyboard activity occurred*, never what was
typed.

**Your own remote control never triggers it.** Every event Nexus injects carries a tag the detector
matches exactly, so working on the PC from your phone while it is armed raises no alarm.

### What gets recorded

| | Default | |
|---|---|---|
| Screen | Timeline + screenshots | at most one every 8s |
| Camera | Snapshot + short clip | at most one every 2 min |
| Microphone | Short clip | 20s at incident start |

Continuous modes exist for all three and are off. Everything lands in
`%LOCALAPPDATA%\Nexus\Security\` — never in this repository, never uploaded anywhere. Each file gets
a SHA-256 so you can tell if it changed. Kept 30 days, 200 incidents, or 2 GB, whichever comes first;
open and high-alert incidents are never trimmed, and acknowledging an incident does not delete it.

Camera and microphone go through the standard Windows APIs, so your privacy settings and the camera
indicator light all still apply.

### Answering an alert

`This was me` asks your phone's biometric first. The phone already proved it is *the* Primary Phone
with its shared secret; this asks whether the person holding it right now is you. Without it, an
unlocked phone on the desk could stand down an incident about whoever picked it up.

`Not me` is not gated — reporting an intruder should never require authenticating first. It flags
the incident, keeps it open, and keeps recording. **It does nothing else.** No shutdown, no lock, no
wipe. You are holding the device that can act.

### Getting the alert

Turn on `Alert me on this phone` in the Security screen. It holds a connection open and shows a
permanent notification while it does — that is the only way Android will wake an app for a network
event without a push service, and a push service would mean routing a signal about your home through
a third party. No cloud, no account, no server. Stop it from the notification any time.

### What needs Windows Hello

Deleting evidence, disabling a camera or microphone source, turning detection off, shortening
retention. Not viewing anything. Your phone cannot do any of them — a stolen phone must not be able
to destroy the recording of the theft.

### Checking it works

```bash
dotnet run --project src/PcCore.Host -- --selftest-security
```

Installs the real hooks, injects real input and proves it is attributed to Nexus rather than to a
person, captures from the real camera and microphone, verifies the digests, and runs both
resolutions. Add `--prompt-hello` to exercise the real Windows Hello prompt.

## Fixes in v0.7.1

Four defects that only a real phone on a real tailnet could show, and one that a real finger could.

**Pointer movement held the left button down.** Any single-finger movement past the touch slop
started a drag, in both Direct and Touchpad, so the pointer could not be moved without dragging
something. PC Core was never involved — `input.pointer` does not touch a button — the phone was
sending `input.button DOWN` on purpose. Moving and pressing are now separate: see the gesture table
above. The decision moved out of the Compose gesture loop into `PointerGestureRecognizer`, where it
is plain Kotlin and tested.

**One unpinned enum broke three security features at once.** `IncidentTrigger` was the only enum in
a session summary without its wire name pinned by attribute, so it inherited the codec's
`snake_case` policy and went out as `pointer_activity` where the phone expected `POINTER_ACTIVITY`.
Because every summary carries a trigger, the phone could not decode `security.sessions.get`,
`security.sessions.list`, or the `security.session_started` event — which showed up as *"Something
went wrong talking to the PC"*, an empty incident history, and a security notification that never
arrived. PC Core logged all three as successes, because from its side they were.

`shared/protocol/protocol-vectors.json` now pins the whole incident shape and every member of every
security enum, generated from the real records and asserted by both languages. The tests that
existed compared `IncidentTrigger.PointerActivity` to itself, which is why none of them noticed.

**Incident history written before the fix still loads.** Pinning the enum made
`System.Text.Json` reject the old spelling, which would have quarantined the existing
`sessions.json` and orphaned the evidence it indexes. State files are now read through
`PersistedEnumConverter`: exact on the way out, indifferent to case and underscores on the way in.
Losing the record of a security incident to fix the display of security incidents is not a trade
worth making.

**Arm could close a live incident.** The Security screen showed one full-width button that read
`Arm` in `OWNER_PRESENT` and `Disarm` in both `ARMED` and `SECURITY_SESSION` — same place, same
size, different meaning. Pressed by reflex during an incident it sent `security.disarm`, which
returns to `OWNER_PRESENT` with no biometric prompt and no record that anyone decided anything. An
open incident is now answered only through `This was me` / `Not me`. `security.arm` was already
refused from `SECURITY_SESSION` by PC Core; the defect was that the phone offered an unlabelled
alternative to answering.

## Native input (v0.8)

Windows refuses input sent from a lower integrity level to a higher one. That is why, before v0.8,
clicking an Administrator window from the phone did nothing: PC Core is a standard-user process and
Windows was doing exactly what it should. The three ways around that are to run PC Core elevated, to
weaken Windows, or to move only the injection into a process that is allowed to do it. v0.8 does the
third.

```
Android Nexus
      |
authenticated PC Core            standard user, as before
      |                          local named pipe, owner-only, both ends verified
Nexus Input Broker               one small elevated process, ten operations
      |
SendInput / InjectTouchInput
      |
Windows input stack
```

**There is no mode to turn on.** If the broker is installed and healthy it carries the input; if it
is not, PC Core injects for itself exactly as it always did. The phone sends the same messages
either way and Direct/Touchpad still means what it always meant.

### Setting it up

Once, from an **elevated** terminal:

```bash
PcCore.Host.exe --install-native-input
```

That registers one visible logon task, `\Nexus\Nexus Input Broker`, which starts the broker in your
session at sign-in with the highest privileges available to you. After that, nothing: the broker
starts with Windows, PC Core finds it, and there is nothing to enable per session.

```bash
PcCore.Host.exe --native-input-status      # what is registered, and whether it is current
PcCore.Host.exe --repair-native-input      # re-register after moving or rebuilding
PcCore.Host.exe --uninstall-native-input   # remove it; Nexus goes back to standard input
```

The phone shows one line — *Native input: ready*, or why not. A broker that started **without**
administrator rights reports `DEGRADED` rather than claiming a reach it does not have.

### What the broker will do, in full

`hello`, `desktop`, `move_abs`, `move_rel`, `button`, `scroll`, `key`, `text`, `touch`,
`release_all`. There is no operation that reads a file, starts a process, takes a window handle or
passes arbitrary arguments to Win32, and adding one means editing `BrokerProtocol.cs`.

The pipe is local, per-user, and admits only the owner's SID — but so would any program the owner
happens to be running, so both ends also check the other's executable. The broker accepts a
connection only from the `PcCore.Host.exe` beside it, and PC Core sends only to the
`PcCore.InputBroker.exe` beside *it*. The second check is not decoration: a program that squatted
the pipe name first would otherwise receive everything the owner typed from their phone.

Every disconnect — clean, crashed or killed — releases every held button, key and touch contact.

**The honest trade-off.** Anything that lets an unelevated process cause input in an elevated window
is, by construction, a way for user-level code to drive administrator UI. Nexus narrows that to one
executable, ten bounded operations and a pipe only the owner can open, and the same is true of every
remote-control tool that can do this at all. If you would rather not have it, do not install it:
everything else works, and elevated windows stay out of reach as they did before.

### Direct mode is touch now

Direct mode sends touch contacts through `input.touch`, and PC Core turns them into real Windows
touch with `InjectTouchInput`. A tap is a tap, a drag drags, and applications that understand touch
get touch rather than a mouse being dragged around. Moving a finger without a drag still moves the
pointer — touch has no equivalent of moving without touching, and the cursor is the only way to see
where you are on the streamed image before committing. Long press stays an immediate right click.

If the PC cannot inject touch, `input.touch` becomes a pointer and a button on the PC side. The
phone never has to know.

### The Secure Desktop

When Windows shows a UAC consent prompt, the lock screen or Ctrl+Alt+Del, it switches to a separate
desktop that the interactive session cannot reach. Nexus **stops there**, and the check lives inside
the injector — below the protocol, so no ordering mistake in the broker can skip it. Nothing here
disables UAC, changes UAC policy, touches `consent.exe`, or automates a credential. The phone shows
*Local administrator confirmation required on the PC*, and normal operation resumes by itself once
you answer the prompt.

### Why not a driver

The obvious "most native" answer is a virtual HID device through the Virtual HID Framework, and it
was the starting point for this work. It does not survive contact with this machine:

- VHF source drivers are **kernel-mode only**, and the WDK is not installed here.
- Since Windows 10 1607, a new kernel driver must be signed through the Microsoft Hardware Dev
  Center, which requires an EV code-signing certificate and a Partner Center account.
- Memory Integrity (HVCI) is **enabled** on this machine, so test-signing is not an option either —
  and turning it off to load Nexus would be weakening the machine to install a security tool.
- No reputable, maintained, currently-signed general-purpose virtual HID mouse/keyboard/touch driver
  exists to borrow instead.

What a HID device would have bought — reaching elevated windows, and real touch — is already
delivered by the broker and by `InjectTouchInput`, both of which are documented, supported Windows
APIs that need no driver and no weakening of anything. See `docs/native-input.md` for the full
signing analysis and what a production driver would actually require.

### Checking it works without a phone

```bash
PcCore.Host.exe --selftest-native-input
```

Creates a window, types into it, reads the text back, injects touch, arms a sandboxed security state
and proves that Nexus's own input does not raise a session. Add `--foreground` to test whatever
window you have focused, which is how you check an Administrator window.

## Owner-authenticated sessions (v1.0)

Pairing proves *which phone* is calling. It is a 256-bit secret stored on the phone, and a stored
secret is on the phone whoever happens to be holding it. v1.0 adds the second question: **is the
owner the one holding it right now?**

The app opens locked. Unlocking runs Android's own `BiometricPrompt`, configured for
`BIOMETRIC_STRONG` with device-credential fallback. Nexus asks a yes-or-no question and receives a
yes or a no — it never sees a fingerprint, a face or a PIN, never stores a template, and has no code
that could.

What makes that mean something on the PC side is a key, not a boolean. The phone generates a
hardware-backed EC key in the Android Keystore with `setUserAuthenticationRequired`, so the platform
refuses to sign with it unless the owner has authenticated within the last fifteen minutes. PC Core
verifies that signature. A signature is therefore evidence that a real system prompt succeeded,
produced by the part of the system that actually saw it happen — not a claim the app is making about
itself.

The key is also created with `setInvalidatedByBiometricEnrollment`, so enrolling a new fingerprint
destroys it. That is deliberate: enrolling a finger is exactly what someone who has taken the phone
and learned its PIN would do. The replacement key is refused until the owner clears the old one
**at the PC**, which is somewhere a thief holding the phone is not.

### When it asks again

| Trigger | Behaviour |
|---|---|
| App launched, or Android recreated the process | Ask |
| Phone screen turned off | Ask — putting the phone down ends the session |
| Backgrounded longer than 15 minutes | Ask |
| One session older than 12 hours | Ask |
| Reboot | Ask (process is new; the Keystore window has lapsed) |
| Short background/foreground flick | **Do not ask** |

The last row is a security decision, not a convenience one. A prompt forty times a day is how you
train someone to tap past prompts without reading them. The Keystore key enforces the same fifteen
minutes independently, so a bug in the app's own timer cannot quietly extend the window.

### Fail closed

Every remote action now requires `RemoteAuthority.FullRemote`. A phone that has authenticated as the
Primary Phone but whose owner has not authenticated gets a deliberately tiny allowlist —
`system.get_info`, `security.get_state`, and the `session.*` calls it needs in order to authenticate.
No screen, no input, no files, no evidence, and no standing down a security incident.

An older Android build never calls `session.authorize`, so it never acquires that authority and is
refused everything else. **The upgrade path fails closed rather than inheriting access.** Existing
pairing data is preserved across the upgrade; the owner does not re-pair.

## The Secure Desktop (v1.0)

Desktop Duplication stops working the instant Windows switches to the Secure Desktop, and it is
supposed to — isolating the consent prompt from everything in the interactive session is the
property that makes UAC worth having. Nexus does not weaken it. UAC is not disabled, Secure Desktop
is not disabled, no security policy is changed, and consent is never automated.

What v1.0 adds is *visibility*, the way every legitimate remote-support product does it: a
LocalSystem service starts a capture process inside the console session, where the Winlogon
desktop's DACL actually admits it, and that process hands back pixels.

```
PC Core (standard user)            creates the frame pipe, chooses its name and a 32-byte nonce
      |                            asks: "start a worker for the console session"
Nexus Secure Desktop Capture       LocalSystem service; DACL admits SYSTEM, Administrators,
      |                            interactive users; rejects remote clients outright
capture worker                     SYSTEM, in the console session, on the Winlogon desktop
      |                            connects outward to PC Core's pipe and proves the nonce
PC Core                            verifies the peer really is SYSTEM before asking for a frame
```

The service accepts exactly one request and returns a yes or a no. It cannot run anything, read a
file, or inject input, and the pixels never pass through it. **No frame is captured off the Secure
Desktop without a live owner-authenticated session**, and that is checked per frame rather than once
when the stream opened — so an authorisation that lapses while a prompt is up stops the picture at
the same moment it stops the input.

Installed once, from an elevated terminal:

```bash
PcCore.SecureCapture.exe --install
```

It installs to `%ProgramFiles%\Nexus\SecureCapture`, not to the build directory. A LocalSystem
service whose image a standard user can replace is a standing offer of SYSTEM to anyone who takes it.

### What remains a platform limitation

Answering a UAC prompt still requires the owner at the machine when Windows demands a local
confirmation. Nexus shows the prompt and, on a machine carrying input through Nexus HID devices, can
deliver input to it — because a HID device produces reports Windows routes to whichever desktop has
focus. It does not and will not bypass a consent requirement, capture credentials, or automate an
approval. Where Windows insists on local presence, that boundary is preserved and reported rather
than worked around.

## Cursors in the screen stream (v1.0)

Desktop Duplication deliberately leaves the pointer out of the frame and reports its shape
separately, so something has to put it back. Until v1.0 that was `DrawIconEx` on whatever
`GetCursorInfo` returned — which draws an arrow correctly and gets the interesting shapes wrong.

The I-beam was the visible failure: it is a **monochrome AND/XOR mask** with no colour of its own,
and one of its four states means *invert what is underneath*. That inversion is exactly how a text
cursor stays readable over both black text and a white page. Rendered as a plain icon it vanished
over text.

All three documented shape types are now composited directly — monochrome, colour, and masked
colour — so arrow, text, hand, resize and wait cursors all render as themselves, at the hotspot
Windows reports. The client is told the pointer is already drawn, so the phone never paints its own
arrow on top of a PC showing a spinner.

## Evidence you can actually look at (v1.1)

An incident records a screenshot, camera stills, camera clips and an audio clip. Until v1.1 the
phone showed the stills and said this about everything else:

> clip playback is not available in this version, the file is on the PC

Which was most of the incident, because the camera records clips and the microphone records audio.

**The cause was not what it looked like.** The obvious theory was that resolving an incident cut off
access to its evidence. Measured against the real store on this machine, that is not what happens:
a resolved `ACKNOWLEDGED_OWNER` incident returns every item on request, bytes included. What the
phone did was fetch the whole clip and then throw it away — the view model checked whether the kind
was an image *after* downloading it, and replaced the result with that sentence. The evidence was
never inaccessible. It was fetched and discarded.

So v1.1 plays it:

| | |
|---|---|
| **Camera stills, screenshots** | Rendered as before |
| **Camera clips, screen recordings** | Play in the incident dialog, with scrubbing |
| **Audio clips** | Play with the same transport controls |
| **Deleted or expired** | Says so, distinctly from an error |

`security.evidence.get` is now a ranged read: `offset` and `max_bytes` in, `chunk_bytes` and
`end_of_file` out. The old 12 MB ceiling was a wall — a file above it was refused outright — and is
now a chunk size. The phone appends chunks to a `.part` file, checks the whole against the digest
the PC recorded, and only then renames it into a content-addressed name, so a file that exists is
complete by construction and opening the same clip twice costs one download. Cached files live in
`cacheDir`, are pruned to a budget, and never touch external storage or the media gallery.

Evidence that the session lists but disk no longer holds is reported as `MISSING` rather than thrown
as `NOT_FOUND`, so the phone can say "deleted or passed its retention period" instead of "something
went wrong". Retention and deletion rules are unchanged.

## Nexus on Windows (v1.1)

`PcCore.Host.exe` is still the whole product. It now also has an ordinary Windows presence:

- **A tray icon** while it runs, showing what is carrying input, the security state, the paired
  phone, and whether it is connected. It turns red while a camera or microphone is live.
- **Open Nexus** on the tray menu launches the control panel — no PowerShell, no port number.
- **A Start Menu entry**, so typing "Nexus" finds it.

Installed once:

```bash
PcCore.Host.exe --install-app
```

That copies the build to `%LOCALAPPDATA%\Programs\Nexus`, points the Start Menu shortcut there, and
**re-points auto-start at the installed copy**. That last part is why the command exists: PC Core
registers its own path at sign-in, and during development that path is `bin\Release` — a directory a
rebuild or a branch switch can empty, leaving a startup entry that silently stops working. Per-user
throughout: no elevation, no service, no `Program Files`. `--shell-status` reports what is where,
and warns if auto-start still points into build output.

The control panel called itself "PC Core v0.4" for six releases. Version now comes from
`ProductInfo`, which reads the assembly attribute MSBuild stamps from `VersionPrefix` — so the page,
the tray and `system.get_info` cannot disagree, and the page is titled from a placeholder rather
than a literal.

## Live camera and microphone (v1.1)

The owner can watch and listen to the room their PC is in, from the Security screen.

```
media.live.start { "sensor": "CAMERA" }   →  JPEG frames over the authenticated connection
media.live.start { "sensor": "MICROPHONE" } →  16-bit mono PCM
media.live.stop                            →  both, or one
```

**What stops it.** All of these, without anybody asking:

- The Full Remote session lapsing — re-checked on a timer, not only at start
- The phone disconnecting
- Leaving the Security screen
- PC Core shutting down
- The owner, from the phone or from the PC's tray

**What it is not.** Nothing is written to disk. There is no file path in the live-capture service
and no option that adds one. Live monitoring and incident evidence are separate subsystems, and that
is deliberate: one is watched and discarded, the other is recorded and kept.

**It cannot be hidden.** While a sensor is live the PC shows a badge on its own screen reading
`Nexus · Camera LIVE`, turns its tray icon red, and puts the state at the top of the tray menu with
a Stop entry. The indicator mode can quieten the badge for ordinary remote control and **cannot**
quieten this — a setting that could would turn the feature into covert surveillance. Windows' own
camera and microphone privacy settings and hardware indicators are untouched and relied upon: this
goes through `MediaCapture` and `AudioGraph` like any application, so if Windows says no, it fails.

Starting a stream does not prompt for a fingerprint again. The Full Remote session *is* the
authorisation, and a prompt per action is how you train someone to approve without reading.

### Checking the devices without a phone

```bash
PcCore.Host.exe --selftest-live-sensors
```

Captures from both devices for a few seconds and reports frame counts, rates and time to first
frame. Worth having because a device that produces nothing looks exactly like a device nobody is
watching — this separates "the PC cannot capture" from "the phone is not receiving".

## Updating Nexus (v1.3)

```
change code  ->  .\tools\publish-release.ps1 -Install  ->  PC restarts on the new build
                                                        ->  phone: "Nexus x.y.z available  [Update]"
```

Everything stays on this PC. There is no update server: the PC that builds a release is the one that
installs it and the one the phone downloads it from.

### One-time setup

```bash
powershell -ExecutionPolicy Bypass -File tools/setup-android-signing.ps1
```

Android upgrades an app in place — keeping its data, Keystore keys and pairing — only when the new APK
has the same signer. The phone app was installed as debug builds signed by this PC's SDK debug key, so
the script adopts that certificate into `%USERPROFILE%\.nexus\release-signing\` with a new random
password (user-only ACL, never in the repository). **Back that directory up**: without it no future
build installs over the app on the phone. `-NewKey` makes a fresh key instead, at the cost of one
reinstall and re-pair.

The very first 1.3 APK has to reach the phone once by hand (`adb install -r <apk>`, in place), because
the app already installed predates the updater.

### Publishing

```bash
powershell -ExecutionPolicy Bypass -File tools/publish-release.ps1 -Install
```

Builds PC Core, the input broker, the updater and SecureCapture; builds the signed Android release APK
only if `android/Nexus`, `shared/protocol` or `Directory.Build.props` changed since the last release
(otherwise the previous APK is carried forward, so the phone is not offered a non-update); hashes
everything; writes `manifest.json`; moves the release to `%LOCALAPPDATA%\Nexus\Releases\<release>` and
only then points `latest.json` at it. The newest three releases are kept. A publish in which nothing
changed publishes nothing. `-Install` then updates this PC. Without it, the tray offers the update.

The manifest records the product version (`Directory.Build.props`), a release number that increases on
every publish and doubles as the Android `versionCode`, the SecureCapture version (its own `csproj`),
the HID driver version (`DriverVer` of the signed package in `artifacts\NexusHidPackage`), SHA-256 and
size of every file, the APK's signer certificate, and `NexusMinAndroidVersionCode`. The script refuses a
changed SecureCapture or driver under an unchanged version, and an APK signed by a different key.

### How the PC updates

The tray (`Update available … → Install update`), the control panel's **updates** box, or `-Install`
all do the same thing: PC Core copies the release's `PcCore.Updater.exe` into
`%LOCALAPPDATA%\Nexus\update\runner` and starts it. The updater:

1. compares the release with the install directory — PC Core by file hash, SecureCapture and the driver
   by version — and stages only what differs, verifying every file against the manifest, **while the old
   PC Core keeps serving**;
2. asks for UAC **only** if SecureCapture or the driver changed, for that step alone; the elevated half
   copies the files into `%ProgramFiles%\Nexus\Updates`, re-hashes them there, and runs
   `PcCore.SecureCapture.exe --install` or `pnputil /add-driver … /install` (no new device, no
   certificate changes). Declining changes nothing;
3. stops PC Core through its stop event (Ctrl+C for builds that predate it, kill as a last resort);
4. moves each changed file aside and copies the new one in, journalled;
5. starts PC Core and waits for `system.get_info` over loopback to report the new version and stay up.

If step 5 fails, the new process is ended, the moved-aside files are moved back, the previous PC Core
is started again and the tray says the update was rolled back. A journal left by an updater that died
mid-swap is undone on its next run. The updater's account is in `%LOCALAPPDATA%\Nexus\update\updater.log`.

**One state folder, whoever starts Nexus.** A terminal inside a packaged (MSIX) app - the Claude
desktop app is one - can be shown a private copy of `%LOCALAPPDATA%\Nexus` under
`%LOCALAPPDATA%\Packages\<package>\LocalCache\Local\Nexus`: Windows creates one when a process started
from that app creates the folder before it exists. From then on Nexus started from the app and Nexus
started from the Start Menu read two different pairings, release stores and update records, and a
restart from the Start Menu comes up as if the last update never happened. PC Core, the updater and
`publish-release.ps1` therefore refuse to run on such a copy (the updater and PC Core exit with 5, and
say which copy), and PC Core started normally logs a warning while one exists. Move what you need from
the copy into `%LOCALAPPDATA%\Nexus` and delete it; where no copy exists, the same terminal writes to the
real folder and everything works from it.

Never touched: `%LOCALAPPDATA%\Nexus` state (pairing, owner keys, settings, logs, evidence), the driver
certificate stores, and any file in the install directory that no manifest listed. An edited
`appsettings.json` is kept; `appsettings.Local.json` beside it is loaded after it and never shipped.

### How the phone updates

The phone reports its `versionCode` when it authenticates and calls `update.check` once per session.
When the PC's release has a newer APK the home screen (and the lock screen — no unlock is needed to
update) shows **Nexus x.y.z available → Update**. The app downloads it with `update.apk.get` in 1 MB
ranges into a `.part` file that resumes after an interruption, checks the whole file's SHA-256,
package name, versionCode and signer against the installed app, and hands it to Android's
`PackageInstaller` with user action required. Android shows its own confirmation — and, once, asks
whether Nexus may install apps. On the next start the app says **Updated to Nexus x.y.z**.

`update.check` and `update.apk.get` are the only additions to the phone's allowlist; `update.install`
is local-only. A phone whose app is below `NexusMinAndroidVersionCode` is still authenticated but told
`update_required`, and every other action is refused with that code — it can fetch the update and
nothing else. A phone that reports no version is treated as build 14, the last one before this.

## Files and Clipboard (v1.4)

The **Files** card now browses the PC itself, not three folders: every local fixed or removable drive,
with Home, Desktop, Documents, Downloads, Pictures and Videos as shortcuts. Tap a folder to go in,
**Up** to go out, **Show hidden** for AppData and friends. A file shows its type, size and modification
time, and offers:

- **Open** — downloads into the app's cache and hands it to whatever Android app opens that type.
- **Save to phone** — downloads into `Download/Nexus` through MediaStore (no storage permission).
- **Upload here** — the system picker, one file or several, into the folder on screen.

Transfers run two at a time with progress and **Cancel**, and keep going when you leave Files.

| Action | What it does |
|---|---|
| `files.roots` | drives (label, free space) and the usual places inside the boundary |
| `files.list` | now also `parent` and `truncated` (a listing stops at 5,000 entries) |
| `files.read` | one range, default 1 MB, max 4 MB, with `expected_size_bytes`/`expected_modified_utc` |
| `files.hash` | SHA-256 of the file as it is on disk now |
| `files.upload.begin` / `.chunk` / `.finish` / `.cancel` | an upload into a folder, ranges of at most 1 MB, strictly in order |
| `clipboard.get` / `clipboard.set` | text only, up to 262,144 characters |

**Who may.** Every one of these is in the phone's allowlist and none is in the pre-owner subset: a paired
phone whose owner has not unlocked Nexus gets `not_authorized`, and so does a session the phone has
locked, mid-transfer. There is no HTTP endpoint for any of it; it is the same authenticated WebSocket and
the same dispatcher as everything else. The remote transport accepts messages up to 2 MB once a device
has authenticated (a 1 MB upload chunk is 1.4 MB of base64); before that the limit is still 64 KB.

**What Windows says still goes.** PC Core runs as you and never elevated, so a folder you cannot open in
Explorer is `access_denied` here too, a file another program holds exclusively is `file_in_use`, and a
full disk is `disk_full` — at the start of an upload, because the space is reserved up front. The
filesystem boundary is unchanged in kind: `{Drives}` is now the default root, and the shape checks,
real-path resolution and segment containment all still apply. Nexus's own folders
(`%LOCALAPPDATA%\Nexus`, the install folder, `%ProgramFiles%\Nexus`) are refused whatever the roots say,
so Files cannot become a way round the rules about evidence and owner keys.

**Downloads** are stateless ranged reads. Each range names the size and modification time the download
started from, and a file that changed is `file_changed` rather than two versions stitched together. At
the end the phone hashes what it holds and compares it with `files.hash`; only a match is kept.

**Uploads** go into a hidden `name.<id>.nexus-upload` file beside the destination — so the new file
inherits that folder's permissions like anything saved there — and are renamed over the real name only
once the whole file has arrived and matches the SHA-256 the phone sends. A name that is already taken is
`already_exists`, and the phone asks before replacing it; replacing is the only change Files can make to
an existing file. There is no delete, rename or move yet.

**When the link drops.** The transfer stops as *Resume*, never as a frozen bar. A download keeps its
partial file on the phone; an upload's partial file stays on the PC, owned by the device rather than the
socket, and beginning the same upload again answers with how much arrived. When the owner session comes
back, interrupted transfers resume once by themselves. An upload that receives nothing for
`PcCore:Files:UploadIdleMinutes` (10) is deleted, PC Core stopping deletes them all, and a small journal
(`uploads.json`) deletes what a crash left at the next start.

**Clipboard** is two buttons on the home screen: **Copy from PC** and **Send to PC** (which opens with the
phone's clipboard in it, to send or edit). There is no automatic sync. Echoes cannot loop: setting text
the PC already has is `changed: false` and not a new version, `clipboard.get` says `origin: "phone"` while
the PC still holds what the phone sent, and the phone does not rewrite its own clipboard with text it
knows is already there. Only hashes are remembered, in memory on both ends.

**Not in this version:** folder downloads, image or file clipboard formats, background transfers after
Nexus leaves the screen (the connection closes then; transfers resume when it is back), drives attached
after PC Core started (restart it), and delete/rename/move.

## Protocol vectors

`shared/protocol/protocol-vectors.json` is generated from PC Core's own implementations and asserted
against by both sides, so the C# and Kotlin halves cannot drift apart quietly. Regenerate it
deliberately:

```bash
PCCORE_WRITE_VECTORS=1 dotnet run --project tests/PcCore.Tests -c Release --filter-class "PcCore.Tests.Protocol.ProtocolVectorTests"
```

Then run the Android tests and expect them to fail until they agree.

## Security boundary in one paragraph



Nothing on disk is reachable unless it is inside `PcCore:Files:AllowedRoots` (default since v1.4: `{Drives}`, every local drive; before that Desktop, Documents, Downloads), and Nexus's own state and install folders are refused regardless. Every caller-supplied path is rejected by shape (relative, UNC, device namespace, alternate data stream, wildcards), canonicalised, then **resolved to its real path by the kernel** (`GetFinalPathNameByHandle`) so that junctions, symlinks, mount points and 8.3 short names cannot alias past the boundary — and only then containment-tested **segment by segment**, not by string prefix. Unresolvable means denied. Zero roots means everything is denied, never everything allowed. [SPEC.md §6](SPEC.md) documents the pipeline and, just as importantly, the Windows edge cases that remain (TOCTOU, hardlinks, mount points inside a root).

Loopback is not an authorisation boundary, so `/rpc` and `/ws` also require the session token and validate `Host` and `Origin` to block DNS rebinding. See [SPEC.md §7.3](SPEC.md).

## Configuration

`src/PcCore.Host/appsettings.json`, overridable with `PCCORE_`-prefixed environment variables.

```json
{
  "PcCore": {
    "Transport": { "Port": 47811, "RequireToken": true, "MaxRequestBytes": 65536 },
    "Files": { "AllowedRoots": [ "{Drives}" ], "UploadIdleMinutes": 10 },
    "Screen": { "Enabled": true, "MaxWidthLimit": 7680 },
    "Pairing": { "SessionTimeoutSeconds": 300, "ChallengeTimeoutSeconds": 60, "DefaultDisplayName": "Primary Phone" },
    "Startup": { "Enabled": true },
    "RemoteTransport": { "Enabled": true, "Port": 47812, "AvailabilityCheckSeconds": 30, "MaxSessions": 4 }
  }
}
```

Tokens: `{Drives}` `{Desktop}` `{Documents}` `{Downloads}` `{Pictures}` `{Music}` `{Videos}` `{UserProfile}`, or absolute paths. A root that cannot be resolved is logged and dropped, never silently widened.

The bind **address** is not configurable. That is deliberate: an "allowed bind address" setting is exactly how a localhost-only service ends up on the LAN.

## Layout

```
src/
  PcCore.Protocol/            contracts; the only assembly that touches System.Text.Json
  PcCore.Capabilities/        dispatcher, registry, path security, security state,
                              identity/pairing/auth, state storage, all twelve actions
  PcCore.NativeInput/         every Windows input call, shared by PC Core and the broker; no dependencies
  PcCore.InputBroker/         the elevated injector; one project reference, ten operations
  PcCore.Windows/             the only assembly with P/Invoke, System.Drawing, DPAPI, or the registry
  PcCore.Transport.Loopback/  Kestrel, /rpc, /ws, token + loopback guards (127.0.0.1 only)
  PcCore.Transport.Remote/    Tailscale-bound WebSocket, device authentication, sessions
  PcCore.Host/                composition root, config, logging, device endpoints, QR, test page
  PcCore.Update/              release manifest, store, plan, staging, file swap and rollback
  PcCore.Updater/             the separate updater process; the only part that ever asks for UAC
tests/
  PcCore.Tests/               586 tests
android/
  Nexus/                      the Android app: protocol/, data/, net/, ui/ (156 unit + device tests)
shared/
  protocol/                   cross-language test vectors, generated by the PC test suite
```

`PcCore.Capabilities` has no reference to `PcCore.Windows`, and `PcCore.Transport.Loopback` has no reference to it either. Those are compiler-enforced boundaries, not conventions: a capability cannot tell how a caller arrived, and a transport cannot control Windows.
