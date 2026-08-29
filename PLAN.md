# PC Core — Implementation Plan

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
