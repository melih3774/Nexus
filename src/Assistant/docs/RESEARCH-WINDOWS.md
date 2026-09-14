# Windows capability implementation and evidence

Reviewed and implemented 2026-09-06 for .NET 10 on Windows. The reusable implementation is `src/PcAssistant.Windows`; the MCP transport and task approval gate are separate callers.

## Choices grounded in existing Windows facilities

| Capability | Evaluated / selected | Reason and source |
|---|---|---|
| Window listing/state/geometry | Small User32 imports | Windows already exposes the required operations. `WM_CLOSE` requests an ordinary application close and preserves the application's unsaved-work handling. [WM_CLOSE](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-close), [SetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow) |
| Screenshot and geometry | Windows Forms monitor enumeration, GDI screen copy and PNG encoder | Uses the framework already installed with this WPF application; returns actual bytes. Thread-scoped per-monitor DPI awareness keeps coordinates in physical pixels. [Graphics.CopyFromScreen](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.graphics.copyfromscreen), [UI Automation and screen scaling](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-and-screen-scaling) |
| Clipboard | Windows Forms Clipboard on one STA worker | Clipboard access requires STA. No replacement clipboard tool or background clipboard manager is installed. [Clipboard](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.clipboard?view=windowsdesktop-10.0) |
| Accessibility | Framework `System.Windows.Automation`, compared with FlaUI | [FlaUI](https://github.com/FlaUI/FlaUI) is a mature wrapper covering richer UIA2/UIA3 use cases. The selected subset needs only the existing managed tree walker and standard patterns, so adding its additional abstraction/dependencies was unnecessary here. UIA calls run on one dedicated MTA worker, away from the application's UI thread. [UIA threading guidance](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-threading-issues), [UIA overview](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-overview) |
| Visible input | Small `SendInput` adapter | Uses Windows' actual foreground input stream, without background window-message tricks or focus-policy bypasses. Integrity restrictions remain enforced by Windows. [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput) |
| Installed applications | App Paths/uninstall registry plus Windows AppsFolder | Registry entries provide ordinary desktop application discovery. AppsFolder also finds registered packaged applications, including Store installations when present. [App Paths registration](https://learn.microsoft.com/en-us/windows/win32/shell/app-registration), [Shell.NameSpace](https://learn.microsoft.com/en-us/windows/win32/shell/shell-namespace) |
| Application launch | Exact discovered executable with no arguments, or typed packaged activation | Package activation uses `IApplicationActivationManager.ActivateApplication` with no arguments. It does not construct commands for PowerShell, cmd, or Explorer. [ActivateApplication](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationactivationmanager-activateapplication) |
| Start Menu shortcuts | Listed by filename, resolved once at launch | Shortcut targets can carry command-line arguments and non-file launch behavior, so the target is read through IShellLink and validated immediately before launch — which is what allows a shortcut pointing at a shell to be refused. Resolution is deliberately not done during discovery: see the v0.9 note below. [Shell links](https://learn.microsoft.com/en-us/windows/win32/shell/links), [GetArguments](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishelllinkw-getarguments) |

No NuGet package, global tool, browser, service, or scheduled task is installed by this library.

## Public contract and boundaries

- `DesktopWindows`: list up to 500 visible titled top-level windows, foreground window, focus/minimize/maximize/restore/move-resize/WM_CLOSE, monitors, and screenshot. Window state changes and close requests are reported as queued; callers should query resulting state. Focus refusal is an error and is never bypassed.
- `ScreenshotResult`: PNG bytes, MIME type and physical `PixelRect` bounds. Default captures the virtual desktop; a supplied region supports monitor/window crops, including negative monitor origins. Maximum 64 million source pixels and 4.5 MB encoded PNG. Larger images produce an explicit smaller-region error rather than silently rescaling coordinates. Screen copy captures visible pixels, not occluded window contents; protected surfaces may appear blank and the pointer is not drawn.
- `ClipboardService`: native format names and bounded Unicode text, or Unicode text replacement. Maximum read/write 100,000 characters. Clipboard writes replace prior contents; this library does not silently create persistent clipboard history.
- `SystemDiscovery`: physical RAM, OS/architecture/core count, local drive capacity/free space and network adapter addresses; process working sets and CPU over a 250 ms sample normalized to all logical cores. Exited/inaccessible processes are skipped, inaccessible CPU measurements remain null. Up to 500 returned processes.
- `RecentFiles(limit, directory, extension)`: metadata only, newest matching files first. Default roots are Desktop, Documents, Downloads and Windows Recent. Each root is top-level only with at most 3,000 examined entries; extension filtering occurs before the result limit. Windows Recent shortcuts are shown as metadata, not executed or resolved. This is bounded discovery, not a full filesystem index.
- `AppLauncher` (replaces the app half of `SystemDiscovery`): one discovery surface combining Start Menu shortcut filenames, App Paths, packaged apps, a curated table of documented `ms-settings:` pages, `shell:` known folders and built-in tools, and installed software from the uninstall registry. The catalogue is cached for one minute; launch references are unpredictable and valid for ten. Shortcuts are resolved through `IShellLink` exactly once, at launch, so a target pointing at a shell or script host is refused immediately before it would run rather than after. Entries of kind `Installed software` answer "what is installed" and carry no launch target. `OpenPathWithAsync` passes exactly one validated existing local path as a single argument-list entry — never a command string. Every launch waits up to eight seconds for a new top-level window and reports the window that appeared, or that none did.
- `OpenUrl`: absolute HTTP/HTTPS without embedded credentials. `OpenDocument`: existing absolute local paths with an explicit document/media extension allowlist; scripts, executables, shortcuts, alternate data streams and UNC/device path syntax are excluded. Standard Windows associations handle the open request.
- `UiAutomationService`: at most 500 nodes, depth 12, five-second traversal budget, eight-second outer wait. Names/automation IDs are limited to 1,024 characters. A fresh snapshot invalidates old references; references also expire after two minutes. Password names are redacted, password values are never read, and password controls have no exposed actions. Supported actions use Invoke, SelectionItem, Value, Toggle and ExpandCollapse patterns, checking visibility, enabled state, process/runtime identity, ancestry and current foreground. `DescribeReference` provides the captured readable target for an approval prompt; execution validates it again.
- `InputService`: explicit foreground window required for every operation. Click/scroll points must hit that target, and drag endpoints must hit it. Held user modifiers/buttons cause rejection. Keyboard input checks the focused UIA control and rejects password fields. Text is limited to 4,000 characters, chords to four named keys, drag to 100 steps, wheel to 20 ticks. Global Windows-key and task-switching chords are excluded. Input remains visible and can fail under UIPI.

UIA/Shell/clipboard workers each use a single bounded queue rather than creating unlimited threads when an external provider hangs. Cancellation is checked before queued work and before a resolved UI action. A native/provider call already in progress cannot be forcibly revoked; after timeout or partial input, inspect state before retrying. Cancellation is not a rollback.

All held-down and release events for a click, chord or drag are submitted in one `SendInput` batch. No asynchronous delay holds a key/button across calls; text is submitted as complete character down/up pairs. A partial insertion attempts to release only this process's pending events. `ReleaseAll()` provides the same in-process fallback; it is not a cross-process emergency hook. Dragging is consequently immediate and can be too fast for applications requiring a dwell; the result explicitly asks the caller to verify. OS focus can still change between verification and event delivery, so UI patterns are preferable where available.

## Verification performed

`dotnet build src/PcAssistant.Windows/PcAssistant.Windows.csproj --nologo` passed without warnings or errors.

`dotnet test tests/PcAssistant.Tests/PcAssistant.Tests.csproj --filter FullyQualifiedName~NativeWindowsTests --nologo` passed 18 cases with one opt-in case skipped on the final normal run. These cover unsafe URL/path rejection, stale references, cancellation before input, extension filtering, PNG signature plus exact pixel dimensions, real system/process/packaged-app discovery, and bounded UIA traversal/password suppression/reference invalidation on a disposable nonactivating WPF window.

The additional explicit native smoke case was run with `PCASSISTANT_NATIVE_SMOKE=1` and passed. It used only a disposable fixture to verify UIA value/invoke, visible click, Ctrl+A, Unicode text input, resize, minimize and restore. The fixture was closed and previous foreground activation restored. No unrelated process was closed or killed, no real user document was edited, and clipboard contents were not changed.

The native tests require an interactive Windows desktop. The input smoke is opt-in because it briefly focuses its fixture. Clipboard replacement, packaged application launching, slow-drag compatibility and elevated/protected application interaction were not exercised against real user applications in this pass.

---

# Independent window capture (2026-09-07)

## The problem

The screenshot path above reads visible desktop pixels. That makes "look at Discord" mean
"first make Discord visible", which rearranges the owner's desktop to answer a question, and
makes an occluded window unobservable in principle rather than merely inconvenient.

## Mechanisms evaluated

| Mechanism | Occlusion-independent | Verdict |
|---|---|---|
| **Windows Graphics Capture** — `GraphicsCaptureItem` for an HWND, `Direct3D11CaptureFramePool` | Yes | **Selected as primary.** Reads the window's own DWM-composed surface. Supported, current, and the mechanism Windows itself uses for window sharing. [Screen capture](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture), [GraphicsCaptureItem](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscaptureitem) |
| **PrintWindow** with `PW_RENDERFULLCONTENT` | Yes | **Selected as fallback.** Asks the window to render itself. Works widely, but hardware-composited regions can render blank. [PrintWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow) |
| **BitBlt / CopyFromScreen** of the window region | No | **Kept as last resort**, and never chosen silently — the result names the mechanism used. |
| `WM_PRINT` / `WM_PAINT` to a memory DC | Partly | Superseded by PrintWindow's full-content flag. |
| DwmGetDxSharedSurface, desktop-duplication of a cropped region | — | Undocumented, or monitor-scoped rather than window-scoped. Rejected. |

## Measured on this machine (Windows 11 26200)

A WGC frame covers **`DWMWA_EXTENDED_FRAME_BOUNDS`**, not `GetWindowRect`. For one Notepad
window, simultaneously:

```
GetWindowRect       125,125   1125x813
ExtendedFrame       132,125   1111x806     <- what the frame actually contains
ClientRect          133,125   1109x805
WGC item/content              1111x806
```

The 7 px horizontal difference is the invisible resize border `GetWindowRect` includes. Every
coordinate derived from a capture is resolved against the frame rect for that reason; using the
window rect would offset every click by that margin.

Occlusion was verified with real pixels rather than by inspection: a red window fully covered
by a maximized blue window captures red, while a screen-region capture of the same rectangle
captures blue. That assertion is in `WindowCaptureTests`.

`GraphicsCaptureSession.IsBorderRequired = false` and `IsCursorCaptureEnabled = false` both
succeed here, so captures carry no highlight border and no cursor. Neither is required: both
are set inside a try/catch because they do not exist on every supported build, and a capture
should not fail over a cosmetic property.

## Honest refusals

Minimized windows have no current surface and are refused rather than approximated — Windows
keeps nothing to read. Cloaked windows (another virtual desktop, suspended packaged apps) and
windows with a non-zero `GetWindowDisplayAffinity` (`WDA_MONITOR` / `WDA_EXCLUDEFROMCAPTURE`)
are refused with the specific reason. An image that is not of the window that was asked for is
worse than no image.

## Coordinate anchoring

A capture returns a short-lived id recording the desktop rectangle the image covers, the image
size, the mechanism and the target's process id. Resolving a point re-reads the window's live
geometry: closed, minimized, resized, or a handle now owned by a different process is refused;
a window that merely moved is followed and the result says so. Ids expire after two minutes,
because a stale coordinate frame is a worse failure than an expired one.

## What was rejected

**Windows notifications.** A `Shell_NotifyIcon` balloon implementation was written and measured
three ways — no message pump, with a dedicated STA pump, and with the icon addition separated
from the balloon by a pump turn — and Windows 11 26200 rendered nothing in any variant,
confirmed by capturing the screen after each. The reliable route is a registered
AppUserModelID, which requires installing a Start Menu shortcut: persistent OS state, and
therefore the owner's decision rather than a side effect of shipping a feature. No notification
tool is exposed. A tool that reports "the owner has been notified" when nothing appeared is
worse than no tool.

**Start Menu shortcut resolution at discovery time.** The first implementation resolved every
`.lnk` through `IShellLink` on each search — roughly a thousand cross-apartment COM round trips
per call on this machine, slow enough to look like a hang. Shortcuts are listed by filename and
resolved exactly once, when one is opened, which is also the only moment the target matters.
That is what makes it possible to refuse a shortcut pointing at a shell, immediately before it
would run rather than after.
