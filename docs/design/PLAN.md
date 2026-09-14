# PC Core — Implementation Plan

> **Historical record.** The step-by-step plans for v0.1 to v0.7, including what could and could not
> be verified at the time. Later milestones are recorded in their commits and in the topic guides —
> see the [documentation index](../README.md).

## v0.1 — Foundation (shipped)

Derived from [SPEC.md](SPEC.md). Each step ends in a compiling, testable state.

## Repository layout

```
Nexus/
  SPEC.md
  PLAN.md
  README.md
  PcCore.sln
  global.json                    # pin SDK major
  Directory.Build.props          # shared TFM, nullable, warnings-as-errors, version
  .gitignore
  .editorconfig
  src/
    PcCore.Protocol/             # net10.0        — contracts + the only JSON code
      ProtocolConstants.cs
      MessageType.cs
      RequestEnvelope.cs
      ResponseEnvelope.cs
      EventEnvelope.cs
      ProtocolError.cs
      ErrorCodes.cs
      ProtocolCodec.cs
      DecodeResult.cs
    PcCore.Capabilities/         # net10.0        — platform-neutral
      Abstractions/
        ICapabilityAction.cs
        CapabilityAction.cs
        ICapabilityRegistry.cs
        CapabilityRegistry.cs
        ICapabilityDispatcher.cs
        CapabilityDispatcher.cs
        CapabilityContext.cs
        CallerDescriptor.cs
        CapabilityException.cs
        IEventPublisher.cs
        EventPublisher.cs
      Security/
        AllowedRootsOptions.cs
        IRealPathResolver.cs
        PortableRealPathResolver.cs
        AllowedRoots.cs
        PathAuthorizer.cs
        PathAuthorizationResult.cs
      System/
        ISystemInfoProvider.cs
        IProcessLister.cs
        GetSystemInfoAction.cs
        ListProcessesAction.cs
        Models.cs
      Files/
        ListFilesAction.cs
        StatFileAction.cs
        Models.cs
      Screen/
        IScreenCapture.cs
        CaptureScreenAction.cs
        Models.cs
    PcCore.Windows/              # net10.0-windows — the only P/Invoke
      Win32RealPathResolver.cs
      WindowsSystemInfoProvider.cs
      WindowsProcessLister.cs
      GdiScreenCapture.cs
      DpiAwareness.cs
      Interop/NativeMethods.cs
    PcCore.Transport.Loopback/   # net10.0        — Kestrel, no Windows logic
      LoopbackTransportOptions.cs
      SessionToken.cs
      LoopbackTransport.cs
      RpcEndpoints.cs
      WebSocketConnection.cs
      LoopbackGuard.cs
    PcCore.Host/                 # net10.0-windows — composition root
      Program.cs
      PcCoreOptions.cs
      CapabilityRegistration.cs
      appsettings.json
      wwwroot/index.html
  tests/
    PcCore.Tests/                # net10.0-windows
      Protocol/
      Security/
      Dispatch/
      Capabilities/
      TestSupport/
```

## Steps

| # | Step | Done when |
|---|---|---|
| 1 | Solution skeleton, `Directory.Build.props`, `global.json`, six projects wired with the reference rules from SPEC §3.1 | `dotnet build` succeeds on empty projects |
| 2 | `PcCore.Protocol`: envelopes, error codes, `ProtocolCodec` (snake_case, decode/encode, version check) | Protocol tests pass |
| 3 | `PcCore.Capabilities/Abstractions`: `CapabilityAction<TParams,TResult>`, registry, dispatcher, `CapabilityException`, event publisher | Dispatcher tests pass with fake actions |
| 4 | `PcCore.Capabilities/Security` + `PcCore.Windows/Win32RealPathResolver`: allowed roots, path authorizer | Path-security suite passes, incl. real junction tests |
| 5 | `system.get_info`, `system.list_processes` + Windows providers | System tests pass |
| 6 | `files.list`, `files.stat` | Files tests pass against temp trees |
| 7 | `screen.capture` + `IScreenCapture` + `GdiScreenCapture` + DPI awareness | Fake-capture tests pass; real capture verified manually |
| 8 | `PcCore.Transport.Loopback`: Kestrel, `/rpc`, `/ws`, `/healthz`, token + loopback guard | Endpoints reachable over 127.0.0.1 only |
| 9 | `PcCore.Host`: DI wiring, config, logging, test page | `dotnet run` starts and serves the page |
| 10 | Full `dotnet build` + `dotnet test`, manual verification of all five capabilities | Green build, green tests, screenshots seen in the page |

## Key Windows decisions to settle during implementation

1. **Real-path resolution** — `CreateFileW` + `GetFinalPathNameByHandle(VOLUME_NAME_DOS)`; needs `FILE_FLAG_BACKUP_SEMANTICS` to open directories. Strip the `\\?\` prefix it returns. Fall back to the portable `ResolveLinkTarget` walk if the handle cannot be opened, and treat resolution failure as *deny*, never as *allow*.
2. **DPI awareness** — must be set before any GDI call, or a scaled display returns a downscaled, cropped desktop. Use `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` as the first statement in `Main`, with a graceful no-op if it fails.
3. **Screen capture** — `Graphics.CopyFromScreen` (GDI BitBlt) for v0.1: no admin, no DirectX, works in an ordinary interactive session. Known blind spots to document: DRM/protected windows capture black, hardware-overlay video may not appear, and the cursor is not included unless drawn explicitly. DXGI Desktop Duplication is the replacement when streaming arrives.
4. **Multi-monitor** — virtual-screen bounds may have negative origins on multi-monitor setups; the capture must use the virtual origin rather than assuming `(0,0)`.
5. **`System.Drawing.Common`** — Windows-only since .NET 7; confined to `PcCore.Windows`, which is why that project exists.
6. **Process enumeration** — `Process.GetProcesses()`; reading `WorkingSet64` can throw for protected processes and for processes that exit mid-enumeration. Catch per-process, never per-call, and never request `SeDebugPrivilege`.
7. **Loopback binding** — `ListenLocalhost` covers both `127.0.0.1` and `[::1]`; `ASPNETCORE_URLS` must be neutralised so an environment variable cannot widen the binding.

---

# PC Core v0.2 — Security State Foundation

Derived from [SPEC.md §12](SPEC.md). **Status: complete.**

The v0.1 plan above is kept as history. Nothing in it was undone; v0.2 is purely additive apart from two new fields on `CallerDescriptor`.

## Where the subsystem belongs

`PcCore.Capabilities/Security/`, alongside the path-authorization policy that already lives there. That folder is the capability layer's **policy** home, and it now holds two distinct concerns:

| Concern | Files |
|---|---|
| Filesystem boundary (v0.1) | `AllowedRoots.cs`, `IRealPathResolver.cs`, `PathAuthorizer.cs`, `PathContainment.cs` |
| Owner presence state (v0.2) | `SecurityStateTransitions.cs`, `SecurityStateService.cs`, `SecurityCapabilities.cs` |

They share a namespace and nothing else. Splitting into a third project was rejected: the state service has exactly one consumer group (its own three capabilities) and no independent lifecycle, so a project reference would be ceremony rather than an enforced boundary.

`SecurityState` and `ActorType` went to **`PcCore.Protocol`**, not `PcCore.Capabilities`, because their serialized spellings are contracts callers switch on — the same reason `ErrorCodes` lives there — and because it keeps `System.Text.Json` confined to that assembly (SPEC §4.4).

## Steps as executed

| # | Step | Verified by |
|---|---|---|
| 1 | Confirm green baseline before touching anything | 172/172 passing |
| 2 | `SecurityState` + `ActorType` enums and `SecurityEvents` in `PcCore.Protocol`; `invalid_state_transition` in `ErrorCodes` | build |
| 3 | Extend `CallerDescriptor` with `ActorType` + `DeviceId`; required parameter so the compiler finds every call site | build found 1 site; 172/172 still passing |
| 4 | `SecurityStateTransitions` — the four-edge table as data | table test |
| 5 | `SecurityStateService` — snapshot, transition result, one lock, publish inside it | service + concurrency tests |
| 6 | `security.get_state` / `security.arm` / `security.disarm` | capability + dispatcher tests |
| 7 | Wire `ActorType.Local` in the loopback transport; register the service and three actions in the host | build |
| 8 | Tests: service, idempotency, events, concurrency, dispatcher, serialization, caller identity, WebSocket end-to-end | 219/219 passing |
| 9 | Version 0.1.0 → 0.2.0; test page gains a security section | manual smoke |
| 10 | Docs: SPEC §12, this plan, README | — |

## Decisions worth re-reading before v0.3

1. **`SECURITY_SESSION -> ARMED` is not allowed.** Reasoning in SPEC §12.3. It is pinned by a test so re-enabling it is a deliberate, visible act.
2. **Publish inside the lock.** Guarantees event order matches state order. Creates one standing constraint: an event handler must not synchronously request a transition (SPEC §12.6).
3. **State is in-memory only.** A restart returning to `OWNER_PRESENT` is the safe default while arming has no consequences. Revisit when it does.
4. **No `security.start_session` capability.** Escalation is service-internal so that declaring an incident stays a decision for detection logic, not for any local caller.
5. **Identity is inert.** `ActorType` and `DeviceId` change no behaviour in v0.2. The policy seam is named (SPEC §12.8) but empty.

## Not started

Device pairing, remote transport, Android client, the policy decision point, and every part of security recording. See SPEC §12.10 for the explicit exclusion list.

---

# PC Core v0.3 — Personal Device Pairing, Persistence, and Startup

Derived from [SPEC.md §13–25](SPEC.md). **Status: complete.**

The v0.1 and v0.2 plans above are kept as history. v0.3 changed one v0.2 decision on purpose (the
event-publish location) and made one v0.2 statement obsolete (security state being in-memory).

## Where the new code belongs

| Concern | Location | Why there |
|---|---|---|
| Atomic JSON state store | `PcCore.Capabilities/Storage/` | Platform-neutral file IO; no Windows API needed |
| PC identity, phone registry, pairing, authentication | `PcCore.Capabilities/Identity/` | Policy and protocol logic, testable without Windows |
| DPAPI protection, auto-start registry | `PcCore.Windows/` | The only assembly permitted P/Invoke and Windows APIs |
| Device-facing HTTP endpoints, QR rendering | `PcCore.Host/DeviceSurface.cs` | Composition root; keeps the generic transport free of pairing |
| Phone simulator | `PcCore.Host/wwwroot/index.html` | The existing dev client; no new project for a stand-in |

Two deliberate placements worth re-reading:

- **`ISecretProtector` is an interface in Capabilities, implemented by DPAPI in Windows.** Same
  pattern as `IRealPathResolver` and `IScreenCapture`: the capability layer stays platform-neutral
  and unit-testable, and every Windows call site remains in one auditable assembly.
- **Device endpoints live in the Host, not in `PcCore.Transport.Loopback`.** They are the contract
  the *remote* transport will implement; putting them in the loopback transport would imply they
  belong to it. The loopback transport is unchanged by v0.3.

## The JSON rule, restated

v0.1 said "`System.Text.Json` is referenced by `PcCore.Protocol` only". v0.3 makes that precise:
JSON appears in exactly **two** places — `PcCore.Protocol` (the wire codec) and
`PcCore.Capabilities/Storage` (the disk store). It appears in no capability, no transport, and no
Windows type. The point of the original rule was that raw JSON must not be spread through the
application, and a single dedicated storage layer with typed records does not violate it.

## Steps as executed

| # | Step | Verified by |
|---|---|---|
| 1 | Clean Release build + full suite on untouched v0.2, then `git init`, `.gitignore`, commit, tag `v0.2-security-state` | 219/219 before any change |
| 2 | `AtomicJsonFileStore<T>`, `StateDirectory` | persistence and corruption tests |
| 3 | `ISecretProtector` + `WindowsSecretProtector` (DPAPI) | round-trip and tamper tests |
| 4 | `PcIdentityProvider` | identity tests |
| 5 | SecurityState persistence + the event-publish refactor | persistence and ordering tests |
| 6 | `PrimaryPhoneRegistry`, `PairingService`, `DeviceAuthenticator` | pairing and auth suites |
| 7 | Four owner capabilities; three device endpoints; QR endpoint | manual sequence |
| 8 | `WindowsAutoStart` + `--install/--uninstall/--startup-status` | registry inspection |
| 9 | Phone simulator in the dev page | manual pair + authenticate |
| 10 | Version 0.2.0 → 0.3.0; docs; commit; tag `v0.3-primary-device` | 277/277, clean Release |

## Decisions worth re-reading before v0.4

1. **`SECURITY_SESSION` survives restart.** A reboot must not be a way to clear an incident. The
   "This was me" phone response that ends a session is a future Android milestone.
2. **Corruption falls back to `ARMED`.** Not `OWNER_PRESENT` (which would assert something unknown)
   and not `SECURITY_SESSION` (which would fabricate an incident).
3. **Publish outside the mutation lock, ordered by a queue.** Removes the deadlock surface v0.2
   carried while keeping event order equal to commit order.
4. **One shared secret, not a device PKI.** 256 bits from a CSPRNG, DPAPI-protected at rest. For one
   owner and one phone, a certificate hierarchy would add moving parts without adding security.
5. **`AuthenticatedDevice` has no public constructor.** It is the only route to an
   `ActorType.Device` caller, which is what makes self-asserted device identity structurally
   impossible rather than merely unimplemented.
6. **Pairing sessions are consumed on any attempt**, right or wrong. Single-use in the strong sense,
   and the reason concurrent completions resolve to exactly one winner.
7. **Auto-start is one HKCU `Run` value.** Visible in Task Manager, removable without this app, no
   elevation, no watchdog, no reinstatement.

## Known follow-ups (not started)

- The `/device/*` endpoints still require the loopback session token, because the only client is the
  local simulator. The remote transport will expose them authenticated by the shared secret instead.
- Nothing consumes `ActorType` or `DeviceId` for authorization. The policy seam
  (`CapabilityDispatcher.DispatchAsync`) is still empty by design.
- Secret rotation is not implemented: replacing the credential means re-pairing.

---

# PC Core v0.4 — Authenticated Tailscale Remote Transport

Derived from [SPEC.md §26–37](SPEC.md). **Status: complete, with one live condition noted below.**

## Where the new code belongs

| Concern | Location | Why there |
|---|---|---|
| Tailscale detection, remote listener, sessions, handshake | `src/PcCore.Transport.Remote/` | A transport, parallel to the loopback one; no capability logic, no Windows API |
| Capability policy | `PcCore.Capabilities/Abstractions/CapabilityPolicy.cs` | Policy is a capability-layer decision; both transports get it by going through the dispatcher |
| Reference protocol client | `PcCore.Transport.Remote/RemotePhoneClient.cs` | One definition of the handshake, shared by the server tests and the simulator |
| Simulator CLI | `PcCore.Host/PhoneSimulator.cs` | Composition root; a client, never a listener |

`PcCore.Windows` was **not** modified. Interface enumeration comes from `System.Net.NetworkInformation`, so networking needed no platform-specific code.

## Steps as executed

| # | Step | Verified by |
|---|---|---|
| 1 | Clean Release build + 277/277 on untouched v0.3; confirm clean tree at `v0.3-primary-device` | baseline green |
| 2 | `TailscaleAddressDetector` + `RemoteEndpoint` with an internal constructor | 20 binding-safety tests |
| 3 | `ICapabilityPolicy` + dispatcher integration; existing tests moved onto the real policy | 277/277 still green |
| 4 | `RemoteTransport`, `RemoteConnection`, `RemoteSessionRegistry` | 30 end-to-end tests over a real socket |
| 5 | `RemoteTransportService` reconciliation loop | 4 recovery tests |
| 6 | `RemotePhoneClient` + `--simulate-phone` / `--simulate-phone-pair` | live pairing; live failure reporting |
| 7 | Version 0.3.0 → 0.4.0; docs; commit; tag `v0.4-remote-transport` | 340/340, clean Release |

## Decisions worth re-reading before v0.5

1. **`RemoteEndpoint` has an internal constructor.** The bind address is not a configurable string; the only production caller is the detector. Same structural trick as `AuthenticatedDevice` in v0.3 — make the unsafe thing unconstructible rather than merely unwritten.
2. **Detection requires interface identity AND the CGNAT range.** Either alone is unsafe in one direction; see SPEC §27.2.
3. **No TLS inside the tunnel.** Reasoning in SPEC §28. Revisit only if this transport is ever exposed outside a tailnet, which the binding rule currently makes impossible.
4. **An unpaired PC still issues a challenge.** Refusing earlier would leak pairing state through reply shape and timing.
5. **Policy is an allowlist.** A capability added in v0.5 is denied to the phone until someone names it — the safe default when the capability surface is growing.
6. **Four concurrent sessions, evicting the oldest.** Reconnect races must not lock the owner out of their own machine.
7. **Events are filtered by prefix allowlist,** not a subscription DSL.

## Live condition at completion

Tailscale is **not installed** on this development machine: no CLI, no service, no adapter, no `100.64.0.0/10` address. The detector therefore correctly reports unavailable, PC Core opens **no** remote listener, and port 47812 refuses connections on every interface — which is itself the correct observed behaviour and was verified.

Everything above the socket was exercised end to end against the real `RemoteTransport` over a real WebSocket, with the listener bound to loopback through the internal `RemoteEndpoint` constructor available only to the test assembly. What could not be verified live is the single line `kestrel.Listen(endpoint.Address, endpoint.Port)` receiving an actual tailnet address, and the phone traversing a real WireGuard tunnel.

**To finish live verification** once Tailscale is installed and signed in on both devices:

```
PcCore.Host.exe                                     # logs the detected 100.x address
PcCore.Host.exe --simulate-phone-pair NEXUS1....    # from the local test page
PcCore.Host.exe --simulate-phone ws://100.x.x.x:47812/nexus
```

## Not started

The Android client, security recording, activity detection, the AI bridge, and every deferred item in SPEC §37.

---

# Nexus v0.5 — First Native Android Client

Derived from [SPEC.md §38–48](SPEC.md). **Status: complete, with physical-device verification outstanding.**

## Where the new code belongs

| Concern | Location | Why there |
|---|---|---|
| Pairing payload v1 and v2 | `PcCore.Capabilities/Identity/PairingPayload.cs` | The payload is identity, not transport: the pairing service produces it |
| Where a phone reconnects to | `IDeviceEndpointProvider` in Capabilities, implemented in Transport.Remote | Only the transport knows what address is actually bound; the interface keeps the dependency pointing the right way |
| Remote enrollment | `PcCore.Capabilities/Identity/RemoteEnrollmentService.cs`, dispatched from `RemoteConnection` | The rule is stated in the capability layer; the transport is what confines it to a Tailscale-bound listener |
| Cross-language vectors | `shared/protocol/protocol-vectors.json`, generated by `ProtocolVectorTests` | Neither side owns it, and both are tested against it |
| The Android app | `android/Nexus/` | Same repository, separate build; the existing PC projects were not restructured |

`PcCore.Windows` was **not** modified. No capability was added or removed.

## Steps as executed

| # | Step | Verified by |
|---|---|---|
| 1 | Baseline: clean Release build, 340/340, clean tree at `v0.4-remote-transport`, no remotes | baseline green |
| 2 | `PairingPayloadV2`, `IDeviceEndpointProvider`, `PairingService.CompleteWithProof` | 3 new payload tests; 343/343 |
| 3 | `RemoteEnrollmentService` + `pairing.enroll` in the handshake | 10 `RemoteEnrollmentTests` over a real socket |
| 4 | `FileEntry.path`, `allowed_roots` at authentication | existing file and transport tests |
| 5 | Vector generator; vectors committed | 4 `ProtocolVectorTests` |
| 6 | Android skeleton: Gradle 9.4.1, AGP 9.2.1, minSdk 31 | debug APK built before any app code was written |
| 7 | `protocol` package in Kotlin | 17 `ProtocolVectorsTest` passing against the PC-generated vectors, first run |
| 8 | Credential store, connection manager, repository | 55 JVM tests, 20 on device |
| 9 | Compose UI: pairing, scanner, home, screen, files, system, security | rendered on the emulator in light and dark; `ConnectionBannerTest` and `HomeScreenTest` on device |
| 10 | Version 0.4.0 → 0.5.0; docs; commit; tag `v0.5-android-client` | 357/357 PC, 55 + 20 Android, clean Release |

## Decisions worth re-reading before v0.6

1. **Enrollment ends the connection.** A phone leaves with a credential and reconnects. Enrollment is
   not authentication, and making that literal — rather than promoting the socket — is what keeps
   "proved possession of a one-time code" and "is the trusted Primary Phone" from blurring.
2. **The pairing endpoint comes from the live bound address,** never configuration. A code cannot
   promise an address that is not being served.
3. **`NexusEndpoint` has a private constructor,** and a test asserts production code never uses the
   test-only factory. Same structural move as `RemoteEndpoint` and `AuthenticatedDevice`.
4. **`allowed_roots` is session bootstrap, not a capability.** Resisting a `files.roots` action kept
   the DEVICE allowlist at the eight actions v0.4 defined.
5. **Vectors are generated, never hand-written.** A hand-written vector proves two implementations
   agree with a human's idea of the protocol, which is not the same thing.
6. **No background service.** The connection is foreground-only until there is something that has to
   arrive while the app is closed — which is a push channel, not this socket left running.

## Live condition at completion

Tailscale 1.102.3 is now **installed** on the development machine (winget, as the brief allows) but
**logged out**: `tailscale status` reports `Logged out`, `tailscale ip -4` reports
`NeedsLogin`. Signing in is interactive and account-bound, so it was not attempted. The detector
therefore still refuses, PC Core opens no remote listener, and 47812 refuses connections on every
interface — which remains the correct observed behaviour.

**No physical Android phone is connected to this environment.** The APK is built and both test
suites run — the instrumented ones on an emulator (Pixel 8 AVD, API 36), which is not a physical
device and is not reported as one.

**To finish live verification:**

```
tailscale login                                      # on the PC, in a browser
# install Tailscale on the phone and sign into the same tailnet
PcCore.Host.exe                                      # logs the detected 100.x address
adb install -r android/Nexus/app/build/outputs/apk/debug/app-debug.apk
# begin pairing on http://127.0.0.1:47811/ and scan the QR with Nexus
```

## Not started

Live screen streaming, remote input, the AI bridge, security recording, activity detection, and every
deferred item in SPEC.md §48.

---

# Nexus v0.6 — Live Screen and Remote Input

Derived from [SPEC.md 49-64](SPEC.md). **Status: complete; no physical Android phone was available.**

## Where the new code went

| Concern | Location | Why there |
|---|---|---|
| Desktop Duplication, cursor drawing | `PcCore.Windows/DxgiDesktopStream.cs`, `Interop/DxgiInterop.cs` | The only Direct3D in Nexus, behind `IDesktopStream` |
| JPEG encoding | `PcCore.Windows/JpegFrameEncoder.cs` | Behind `IFrameEncoder`; the codec is the part most likely to be replaced |
| Capture loop, backpressure, lifetime | `PcCore.Capabilities/Screen/DesktopStreamService.cs` | Platform-neutral, so it is testable against a fake capture with no desktop |
| Binary frame layout | `PcCore.Protocol/StreamFrame.cs` | A wire contract, shared with Kotlin and pinned by vectors |
| Input validation, rate limit, held state | `PcCore.Capabilities/Input/` | Policy, not P/Invoke |
| SendInput | `PcCore.Windows/WindowsInputController.cs` | Translation, not policy |
| Indicator decision + persistence | `PcCore.Capabilities/Indicator/` | A pure function of three facts, with its own tests |
| Badge window + tray | `PcCore.Windows/WindowsRemoteIndicator.cs` | Win32 window on its own message loop |
| Frame sink, control priority | `PcCore.Transport.Remote/` | Transport doing transport |
| Frame parse, decode pool, gestures, control surface | `android/.../protocol/StreamFrame.kt`, `remote/` | The phone half |

## Steps as executed

| # | Step | Verified by |
|---|---|---|
| 1 | Baseline: 357/357 PC, 55 Kotlin, 20 instrumented, clean tree at `v0.5-android-client` | all green |
| 2 | Standalone DXGI probe before writing anything on top of it | real frames, clean release, every vtable slot correct |
| 3 | `IDesktopStream` + `DesktopStreamService` + binary header | 33 tests including byte-offset assertions |
| 4 | `IInputController` + `InputService` | 30 tests: coordinates, clamping, held state, rate limits |
| 5 | Indicator service and Win32 badge | 11 tests including "no mode can hide a live session" |
| 6 | Transport: binary sink, control priority, disconnect cleanup | 17 end-to-end tests over a real socket |
| 7 | Measured encoding defaults on this machine | 1280x720 q65 chosen from four measured configurations |
| 8 | Kotlin: frame parsing, letterbox mapping, control session | 48 new Kotlin tests |
| 9 | Cross-language vectors extended | frame header, 10 input envelopes, 67 key names |
| 10 | Version 0.6.0, docs, commit, tag | 486/486 PC, 103 Kotlin, 20 instrumented |

## Decisions worth re-reading before v0.7

1. **Screenshot and stream share no code.** A streaming fault must not break the screenshot that
   diagnoses it. See SPEC 50.
2. **Hand-written COM interop, validated by a probe first.** A wrong vtable slot is an access
   violation, not an exception; the probe existed so that failure happened out of process. SPEC 51.
3. **Normalised coordinates.** The phone sends fractions; the PC owns every pixel conversion. This
   is what keeps DPI scaling and negative monitor origins out of the client entirely. SPEC 56.
4. **Refuse, do not clamp.** An out-of-range position is a client bug; clamping hides it while still
   clicking somewhere the owner did not touch.
5. **Latest frame wins, on both sides.** One rule, applied at the encoder and again at the receiver.
   SPEC 55.
6. **Control is not connection.** Input needs an explicit session, and everything releases on the
   way out. SPEC 57.
7. **No invisible mode, ever.** SPEC 61. A test asserts it rather than a comment claiming it.
8. **UIPI is kept.** Elevated windows stay out of reach; the phone is told why. SPEC 58.

## Live condition at completion

**No physical Android phone was available.** The APK builds and 20 instrumented tests run on an
emulator, which is not the same thing and is not reported as such. Tailscale is still not installed
on this machine, so the tailnet path remains unverified end to end, exactly as at v0.5.

What *was* verified on real hardware is the Windows half: Desktop Duplication against the actual
display for a sustained minute with no losses, JPEG encoding at the measured defaults, `SendInput`
actually moving the pointer, and the indicator appearing in all three modes.

## Not started

Live audio, microphone, camera, security recording, activity detection, push notifications, file
modification, shell execution, application automation, AI, and every deferred item in SPEC 64.

---

# Nexus v0.7 — Security Detection, Evidence, Notifications, and Owner Verification

Derived from [SPEC.md 65-84](SPEC.md). **Status: complete; no physical Android phone was available.**

## Where the new code went

| Concern | Location | Why there |
|---|---|---|
| Activity vocabulary, state gate, noise filter | `Capabilities/Security/Detection/` | Platform-neutral, so the promise "nothing records in OWNER_PRESENT" is testable |
| Low-level hooks, attribution, session events | `PcCore.Windows/WindowsActivityDetector.cs` | The only place that touches input; reads two fields and never the key |
| Session model, store, retention | `Capabilities/Security/Sessions/` | History is data, kept apart from the single-valued state |
| Incident orchestration | `SecurityIncidentService.cs` | One place that creates, records, and resolves — so the promise is checkable in one place |
| Evidence contracts | `Capabilities/Security/Evidence/` | Best-effort by design; a missing camera is not a failed incident |
| Camera, microphone, Windows Hello | `PcCore.Windows/WindowsSecurityEvidence.cs` | Standard Windows APIs, so OS privacy controls and the camera light still apply |
| Incident client, biometric gate, alert service | `android/.../security/` | The phone half |

## Steps as executed

| # | Step | Verified by |
|---|---|---|
| 1 | Baseline: 454/454 PC, 102 Kotlin, 20 instrumented, clean tree at `v0.6-live-control` | all green |
| 2 | WinRT probe before committing to an approach | camera, microphone and Hello all real on this machine |
| 3 | Detection vocabulary and monitor | 12 tests covering the state gate and the noise rules |
| 4 | Sessions, store, retention, containment | 13 tests including path-escape refusals |
| 5 | Evidence sources and incident orchestration | 12 tests including every failure path |
| 6 | Windows detector, media capture, Hello | host self-test against real hardware |
| 7 | Capabilities, policy, host wiring | 491/491 |
| 8 | Android models, client, biometric gate, UI, alert service | 17 new Kotlin tests |
| 9 | Version 0.7.0, docs, commit, tag | see the completion report |

## Decisions worth re-reading before v0.8

1. **The state gate is the whole promise.** `OWNER_PRESENT` discards observations before any
   evidence path is reachable. Do not add a capture path that bypasses the monitor. SPEC 66.
2. **No keystroke ever enters managed memory.** Two fields read by offset. If that file is ever
   refactored, this is the property to preserve. SPEC 69.
3. **The injection tag is 32 bits, and that was measured.** A 64-bit tag is truncated through the
   mouse hook. SPEC 70.
4. **Recordings go to a stream, not a StorageFile.** Windows holds a StorageFile open past
   `StopRecordAsync`. SPEC 74.
5. **Unavailable verification is a refusal.** On both sides. A check that passes when it cannot run
   is not a check. SPEC 78, 80.
6. **`Not me` does nothing destructive, deliberately.** SPEC 79.
7. **Retention never touches an open or flagged incident.** SPEC 76.
8. **A foreground service, not FCM.** The tradeoff is stated rather than hidden. SPEC 77.

## Live condition at completion

**No physical Android phone was available**, so the alert-to-acknowledgement round trip was not
exercised on real hardware. Tailscale is installed but logged out, so the tailnet path remains
unverified end to end, as at v0.5 and v0.6.

What *was* verified on real hardware: the low-level hooks install; 42 injected events were all
attributed to Nexus and none opened an incident; the real camera produced a JPEG and an MP4; the real
microphone produced an M4A; digests are stable and detect tampering; both resolutions behave; and
Windows Hello reports available on this machine.

## Not started

AI, facial or voice recognition, clipboard monitoring, automatic destructive responses, cloud
backends, file writing, shell execution, and every deferred item in SPEC 84.
