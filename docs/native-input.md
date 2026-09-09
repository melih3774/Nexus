# Native Windows input: what was researched, what was built, and what is blocked

This is the record behind the v0.8 input work. It exists so that the next person to ask "why isn't
this a driver?" gets an answer with measurements in it rather than an opinion.

Everything here was measured on **this machine**: Windows 11 Pro 26100.x (build 26200), x64,
one 1920×1080 display at 125% scale, Synaptics precision touchpad, no touchscreen.

---

## 1. The problem, stated exactly

Windows User Interface Privilege Isolation refuses input sent from a process at a lower integrity
level to a window at a higher one. PC Core runs as a standard user at **medium** integrity. An
elevated application's window is **high** integrity. So `SendInput` returns a short count and
`GetLastError` gives `ERROR_ACCESS_DENIED` (5), and the phone reports `BLOCKED_BY_INTEGRITY`.

That is Windows working correctly. The question was never how to defeat it, but which of the
supported ways around it costs least.

| Option | Cost |
|---|---|
| Run PC Core elevated | Every capability — files, screen, processes — gains administrator reach. Rejected outright. |
| A kernel-mode virtual HID device | Needs a signed driver. See §3. |
| `uiAccess="true"` manifest | Needs an Authenticode signature trusted by the machine **and** installation under `%ProgramFiles%`. See §5. |
| A small elevated broker process | Needs one-time administrator consent. Nothing else changes. **Chosen.** |

---

## 2. What the input architecture was before

Read from the v0.7.1 tree, not from memory:

- **Pointer, both modes** — `WindowsInputController.MovePointerAbsolute/MovePointerRelative` →
  `SendInput` with `MOUSEEVENTF_MOVE`, plus `ABSOLUTE | VIRTUALDESK` for Direct.
- **Direct mode** — the phone sent a position as a fraction of a display; `InputService.MoveAbsolute`
  converted it to virtual-desktop pixels using the display's own rectangle, then to SendInput's
  0–65535 space. Absolute *pointer* movement, not touch.
- **Touchpad mode** — relative deltas, scaled on the phone by the stream's scale factor.
- **Keyboard** — a closed `NexusKey` enum mapped to virtual-key codes, with the extended-key flag on
  the navigation cluster. Ordinary characters went through `input.text` as `KEYEVENTF_UNICODE`, which
  is how Turkish worked.
- **Attribution** — every injected event carried `0x4E585330` in `dwExtraInfo`;
  `WindowsActivityDetector` matched it exactly and reported `ActivitySource.NexusRemote`.
- **Cleanup** — `InputService` tracked held buttons and keys per session and released them on
  `input.end`, `input.release_all` and disconnect.
- **Foreground rules** — none. Nexus never called `SetForegroundWindow`; input went wherever focus was.
- **Elevated windows** — refused, and reported honestly.
- **Secure Desktop** — unreachable by construction, and not explicitly detected.

All of that still works and none of it was rewritten. The pointer path, the key map, the Unicode
path and the cleanup ordering were moved into `PcCore.NativeInput` unchanged so that two processes
could share one copy.

---

## 3. Virtual HID Framework: researched, and blocked

> **Superseded — read [`virtual-hid.md` §1](virtual-hid.md) first.** The conclusion of this section
> is wrong. It rests on the quote below, which is still published on Microsoft's conceptual page and
> is contradicted by their own DDI reference for `VHF_CONFIG`: that structure has a `FileHandle`
> member documented as "Required for user-mode drivers", and VHF supports a **UMDF2 user-mode** HID
> source driver. A user-mode driver is not gated by HVCI or Secure Boot, so the three blockers this
> section records apply to a KMDF kernel driver and not to the design Nexus would actually build.
> The remaining barrier is driver-package signing, which is a price rather than an impossibility.
>
> The measurements below are still accurate and still worth keeping; it is the inference from them
> that was too strong.

Per [Microsoft's VHF documentation][vhf]:

> In this release, VHF supports a HID source driver only in kernel mode.

A Nexus virtual HID device would therefore be a **KMDF kernel driver** linking `vhfkm.lib`, with
`vhf.sys` as a lower filter, declared in an INF. Three separate things block that here:

**The WDK is not installed.** `C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0` has the
SDK headers — `shared\vhf.h` is present, which is misleading — but no `km\` directory and no
`vhfkm.lib`. Only Visual Studio **Build Tools** 2022 is installed, not the WDK.

**Signing.** Since Windows 10 version 1607, Windows will not load a new kernel-mode driver that is
not signed through the Microsoft Hardware Developer Center. Establishing an account requires an EV
code-signing certificate. There is a documented exception when **Secure Boot is off**, and Secure
Boot *is* off on this machine (`UEFISecureBootEnabled = 0`) — but that exception only re-admits
cross-signed drivers whose end-entity certificate was issued **before 29 July 2015**, which cannot
be obtained today.

**Memory Integrity is on.** Measured:

```
VirtualizationBasedSecurityStatus            : 2   (running)
SecurityServicesConfigured                   : {2} (HypervisorEnforcedCodeIntegrity)
SecurityServicesRunning                      : {2} (HVCI is active)
CodeIntegrityPolicyEnforcementStatus         : 2   (enforced)
HKLM\...\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity\Enabled = 1
HKLM\SYSTEM\CurrentControlSet\Control\CI\Config\VulnerableDriverBlocklistEnable = 1
```

HVCI enforces kernel code integrity from the hypervisor and does not honour test-signing. So the
usual development route — `bcdedit /set testsigning on` plus a self-signed certificate — would
require **disabling Memory Integrity**, and the whole point of Nexus is that it does not weaken the
machine it protects. Not done, not recommended, not necessary.

### Could a third-party signed driver have been borrowed?

Looked for, not found. The plausible candidates all fail at least one requirement:

| Candidate | Why not |
|---|---|
| ViGEmBus | Virtual **gamepads** only — no mouse, keyboard or touch. Archived by its author. |
| HidHide | *Hides* HID devices; it does not create one. |
| Interception | Keyboard/mouse filter driver, unmaintained for years, old signature that will not satisfy current kernel CI. |
| VMulti | Virtual multitouch HID, but requires test-signing to install. |

Installing an unmaintained kernel driver of unclear provenance to gain a feature already achievable
without one would be a straightforwardly bad trade.

### What production signing would actually take

If a Nexus HID driver were ever wanted for real:

1. **EV code-signing certificate** from a CA Microsoft recognises — hardware token, identity
   validation, an annual fee. This is the gate; nothing else can start without it.
2. **Partner Center / Hardware Developer Center account**, validated with that certificate.
3. A driver package — driver binary, INF, `.cat` catalog — built against a current WDK.
4. **Attestation signing** for a client-only driver: submit the package, Microsoft returns it signed,
   no HLK run required. Sufficient for Windows 10/11 client. (WHQL, which does require HLK test
   logs, is only needed for the compatibility program and Windows Update distribution.)
5. HVCI compatibility must hold — the HLK *HyperVisor Code Integrity Readiness* test — which for a
   VHF source driver is not onerous but is a real requirement.
6. Re-submission for each driver change.

Nothing was purchased and nothing was submitted.

---

## 4. What was built instead

### Elevated reach: the Nexus Input Broker

One WinExe, one project reference (`PcCore.NativeInput`, which itself has none), started by a logon
task with `HighestAvailable`. PC Core stays exactly what it was.

The whole protocol is ten operations. The pipe is created with an explicit security descriptor:

```
D:(A;;GA;;;<owner SID>)(A;;GA;;;SY)S:(ML;;NW;;;ME)
```

The mandatory label is load-bearing and not obvious: every kernel object a high-integrity process
creates is labelled high with no-write-up, which would make the pipe **unwritable** by a medium
integrity PC Core. `NamedPipeServerStream` cannot express a label, so the pipe is created through
`CreateNamedPipeW` directly.

Both ends verify the other's executable (`GetNamedPipeClientProcessId` /
`GetNamedPipeServerProcessId` → `QueryFullProcessImageNameW`). **Measured:** a PowerShell process
running as the same owner connects to the pipe and is disconnected before it can send a byte —

```
REFUSED a connection from an unexpected executable: powershell.exe
```

### Real touch: `InjectTouchInput`

`InitializeTouchInjection` / `InjectTouchInput` are supported Win32 since Windows 8 and need no
driver. Measured on this machine, injecting a tap into a real window produced:

```
WM_POINTERDOWN     extra=0x00000000
WM_POINTERUP       extra=0x00000000
WM_MOUSEMOVE       extra=0xFF515792   [MI_WP_SIGNATURE, touch]
WM_LBUTTONDOWN     extra=0xFF515792
WM_LBUTTONUP       extra=0xFF515792
```

So an application that handles `WM_POINTER` gets genuine touch, and a legacy application gets the
mouse messages Windows promotes it into. That is what "feels like a touch display" actually requires.

One measurement caused two bugs and is worth recording in full. **Touch injection makes the machine
look like it has a touchscreen**, and it does so system-wide:

```
before InitializeTouchInjection   SM_DIGITIZER = 0x0    SM_MAXIMUMTOUCHES = 0
after  InitializeTouchInjection   SM_DIGITIZER = 0xC1   SM_MAXIMUMTOUCHES = 10
```

The first attempt at handling this sampled `SM_DIGITIZER` early, in a static field. That was wrong
twice over. A class whose statics are only field initialisers is `beforefieldinit`, so the runtime
deferred the sample until after injection had started; adding an explicit static constructor fixed
that, but the metric is *system-wide*, so a freshly started PC Core still read `0xC1` merely because
the broker was already running in another process.

So the question is answered by enumerating raw input devices instead. Enumerating this machine shows
why both exclusions are needed:

```
before injection
  hid  page=0x0D usage=0x05  \?\HID#SYNA32E3&Col02#...     <- precision touchpad, not a touchscreen

after injection
  hid  page=0x0D usage=0x04  \?\VIRTUAL_DIGITIZER          <- Nexus's own injection device
  hid  page=0x0D usage=0x05  \?\HID#SYNA32E3&Col02#...
```

`PhysicalDigitizer.IsPresent()` therefore counts usage `0x01`, `0x02` and `0x04` on usage page
`0x0D`, skips `0x05` (Touch Pad), and skips any device named `VIRTUAL_DIGITIZER`. Verified: it
answers `false` on this machine whether or not a broker is injecting, and
`CreatingAnInjectorDoesNotGiveTheMachineATouchscreen` pins the property.

### Unicode

A HID keyboard carries key *positions*, not characters; it cannot type `ş` unless the layout happens
to have one. `KEYEVENTF_UNICODE` carries UTF-16 code units directly. So even if the driver route had
been open, text would still have gone through the Unicode path — the hybrid the prompt anticipated
is what Nexus already had, and it is unchanged.

**Measured end to end** by typing into a real edit control and reading it back:

```
'Nexus çğışüö ÇĞİŞÜÖ 42 ✓'
```

---

## 5. `uiAccess`: the lower-privilege alternative, documented but not taken

A process with `uiAccess="true"` in its manifest may bypass UIPI and drive higher-privilege windows
with `SendInput`, **without** running elevated. It requires:

1. An Authenticode signature verifiable against the machine's Trusted Root store, and
2. Installation in a directory writable only by administrators (`%ProgramFiles%`).

This is architecturally *better* than the broker — a UIAccess process sits below high integrity, so
its blast radius if compromised is smaller. It was not taken because requirement 1 means either
buying a code-signing certificate, or adding a self-signed root to the machine's trust store, which
is a larger and less reversible trust decision than creating one visible scheduled task.

**If a code-signing certificate ever exists for Nexus, this is the upgrade to make.** The broker
would keep its protocol and its pipe and simply stop needing elevation. Note that a `uiAccess`
manifest on an unsigned binary makes the process fail to start, so it cannot be added speculatively.

---

## 6. Attribution: why the tag was no longer enough

The v0.7 rule was exact: injected events carry `0x4E585330` in `dwExtraInfo`, the detector matches
it, and Nexus's own input can never be mistaken for a person. That rule still holds for `SendInput`
and is still the primary check.

Touch injection breaks it. `POINTER_TOUCH_INFO` has no field for a caller tag, and the mouse
messages Windows promotes a contact into carry `MI_WP_SIGNATURE` (`0xFF515700`) instead.

**Measured:** injected touch produced **zero** `WH_MOUSE_LL` events. The low-level hook never sees
promoted touch, so on this machine the security detector cannot see Nexus's touch at all. That would
be enough — but "measured once, on one build of Windows" is not a boundary, and being wrong means
Nexus raising an alarm about the owner using their own PC.

So there is now a second rule. `INexusInputAttribution` is a ledger: every event Nexus successfully
injects is declared with its kind and count, claims expire after 400 ms, and the total outstanding
per kind is capped. The monitor consults it for anything that still looks local. It can absorb
precisely what Nexus caused and nothing more — `NexusInputAttributionTests` and
`NexusInputIsNotAnIntruderTests` pin both directions, including the case where one observation more
than Nexus caused still raises a session.

The detector also now recognises `MI_WP_SIGNATURE` and reports it as **local** on a machine with a
real digitizer, so that an intruder using a physical touchscreen while the PC is ARMED still raises
an incident. This machine has no digitizer, and PC Core says so at start-up when one is present.

### Known limitation

On a PC **with** a physical touchscreen, a person touching the screen produces promoted mouse
messages that a `WH_MOUSE_LL` hook does not see. Detection of local touch on such a machine
therefore rests on the other signals — keyboard, physical mouse, unlock, logon. Closing that would
mean adding a Raw Input listener for digitizer devices. Not done; there is no touchscreen here, and
it is the right next step if Nexus ever runs on one.

---

## 7. Indirect Display Driver (IddCx): researched, deliberately not implemented

[IddCx][idd] is the model for "monitors that aren't connected to traditional GPU display outputs",
and Microsoft names remote display and virtual monitors as its scenarios. Unlike VHF it is a
**UMDF user-mode** driver running in session 0, so kernel code integrity does not apply to the
driver binary.

It is still a PnP driver package, and installing one requires a signed catalog — WHQL or a
commercial release certificate. Test-signing needs a test certificate and `TESTSIGNING` mode. So the
same gate applies, just for a different reason.

**But the more important finding is that it would not help.** A virtual display is an *additional*
monitor showing *different* content. Nexus's use case is "show me what is on my PC", which is the
primary display. Making a Nexus Display mirror the primary would mean putting Windows into clone
topology, which changes the owner's own desktop arrangement. And a monitor that stays connected
while the phone is away causes exactly the problems the prompt anticipated: windows opening
off-screen, topology churn, surprising resolutions.

**Conclusion: option A.** Keep streaming the primary display. IddCx is worth revisiting only if the
owner wants the phone as a genuine *second* screen — a different feature, with a monitor that
connects when a display session starts and disconnects when it ends. Nothing in v0.8 changes the
screen stream, and it still measures 12.8 fps at 1280×720, JPEG 65 — unchanged from v0.6.

---

## 8. Latency

Measured through the full capability layer, 300 relative moves, on this machine:

| Path | Median | p95 |
|---|---|---|
| PC Core injecting for itself | 62 µs | 122 µs |
| Through the Nexus Input Broker | 100 µs | 339 µs |

The broker costs about **38 µs** per event. The same event has already crossed Tailscale, which is
three to four orders of magnitude more. Nothing is batched, and no event waits for another.

---

## 9. What is still not done

- **The elevated-application test has not been run.** It needs an elevated broker, which needs
  one-time administrator consent that only the owner can give. `--selftest-native-input --foreground`
  reports the focused window's integrity level and whether input reached it, so it is one command
  once the broker is installed.
- **Windows-restart persistence has not been observed**, for the same reason. PC Core restart
  persistence *has* been: PC Core was restarted and reconnected to the running broker with no action.
- **The Secure Desktop has not been observed live.** The gate is enforced and unit-covered, and the
  self-test refuses to run when the input desktop is not `Default`, but no UAC prompt was raised
  during this pass — deliberately, since leaving a consent prompt on an unattended machine blocks it.

[vhf]: https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/virtual-hid-framework--vhf-
[idd]: https://learn.microsoft.com/en-us/windows-hardware/drivers/display/indirect-display-driver-model-overview
