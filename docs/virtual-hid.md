# Virtual HID: what was researched, what was built, and what is deferred to one install pass

This is the record behind the v0.9 virtual-HID work. It supersedes the conclusion of
[`native-input.md` §3](native-input.md), which is wrong in one important way — see §1 — and
corrects itself once, in §9, where §3 turned out to be answering the wrong question.

Sections 1–8 are the research and foundation pass. Sections 9–12 are the Nexus-owned backend that
followed from it. **Section 13 is what happened when it was installed**, and it is the section to
read first: several of the defects it records were invisible to every test until the driver ran.

Sections 1–12 installed nothing. Section 13 did: the driver is installed, signed with a self-signed
certificate the owner created and trusted for this machine. Secure Boot, HVCI, UAC and driver-signing
enforcement were not changed, and no licence was bought.

---

## 1. The correction: VHF is not kernel-mode only

`native-input.md` §3 rejected a Nexus virtual HID device on this reasoning, quoting Microsoft:

> In this release, VHF supports a HID source driver only in kernel mode.

and concluded that a Nexus device would therefore need a **KMDF kernel driver**, which needs
Hardware-Dev-Center signing, which needs an EV certificate, and which HVCI would block in
development. That conclusion is not correct, and the quote is the reason it looked correct.

That sentence is still on the [VHF conceptual page][vhf-concept]. It is contradicted by Microsoft's
own DDI reference for the structure the API actually takes, [`VHF_CONFIG`][vhf-config], which
branches on compilation mode:

```c
#ifdef _KERNEL_MODE
    PDEVICE_OBJECT DeviceObject;
#else
    HANDLE         FileHandle;
#endif
```

and documents the user-mode member explicitly:

> `FileHandle` — **Required for user-mode drivers.** A file handle obtained by calling
> `WdfIoTargetWdmGetTargetFileHandle`. To open a WDFIOTARGET, a user-mode (UMDF) VHF source driver
> should call `WdfIoTargetOpen` with `OpenParams.Type` set to `WdfIoTargetOpenLocalTargetByFile`.

So VHF supports a **UMDF2 user-mode** HID source driver. There is independent evidence: LizardByte
ships one (§2), and Microsoft's driver-samples repository carries a Virtual HID Mini Driver (UMDF2)
sample.

**Why this changes the whole calculus.** A UMDF2 driver is a user-mode DLL in the Driver Store, not
a `.sys` loaded into the kernel. HVCI enforces *kernel*-mode code integrity, so it does not gate it.
Secure Boot does not gate it. The blockers `native-input.md` recorded — "disable Memory Integrity to
develop", "cross-signed certificate issued before 29 July 2015" — are blockers for a **KMDF** driver
and do not apply here.

What remains is ordinary driver-package signing, which is a cost, not a technical impossibility.

## 2. Existing backends evaluated

### LizardByte `libvirtualhid` — rejected for now, on licensing and coverage

Maintained and current: announced 2026-08-16, released through 2026-08-29. Architecture is exactly
the one §1 concludes is available — a UMDF2 control driver backed by VHF, with a broker service
brokering device creation over a named pipe while input reports stay on the direct driver path.

| Property | Finding |
|---|---|
| Architecture | UMDF2 control driver + VHF, broker service for device creation |
| Devices on Windows | Gamepads, keyboards, **Raw Input-visible mice** |
| Touch / digitizer / pen / trackpad on Windows | **None.** Those exist only on the Linux `uinput`/`uhid` backends |
| Licence — library | MIT |
| Licence — driver, broker, entitlement sources, MSI | LizardByte Source-Available License 1.0 (**not** open source) |
| Cost | **$14.99/year or $49.99 lifetime**, via Polar; one key activates up to **5 machines** |
| Signing | Release builds use Azure Trusted Signing for the catalog and MSI |
| Architecture support | AMD64 only (ARM64 would need a Microsoft dashboard signing path) |
| Raw Input visibility | Yes — stated for mice, and keyboards are described as Raw Input-visible |

**Two things disqualify it from being an automatic yes.**

First, **it publishes no Windows touch device.** The original premise for this pass held that it
advertised touchscreen, trackpad and pen. It does — on Linux. On Windows the driver publishes
gamepads, keyboards and mice. So Direct-mode touch would stay on `InjectTouchInput` even after
paying, and item 8 of the goal would be unmet.

Second, **the licensing statements conflict and the conflict matters.** The store page says the paid
licence "applies only to creating gamepads … No Virtual HID Driver license is needed for … Windows
keyboard or mouse input". The repository README says "A license is required to create virtual
gamepads, keyboards, or Raw Input-visible mice with the Windows driver", and the driver
documentation says Raw Input-visible keyboards "require the driver and broker license". The most
coherent reading is that *ordinary* keyboard/mouse input (the `SendInput` path, which Nexus already
has) is free, and **Raw Input-visible HID** keyboard/mouse — the only kind Nexus wants — is licensed.

That reading is the expensive one, and it is the one to assume until the owner confirms it.

**No purchase was made.** Per instruction, this stops here and reports the option.

There is also a design concern worth stating even if the licence is cheap: it introduces a
third-party broker service and a machine-activation entitlement check into the input path of a
security product. A licence server deciding whether the owner's mouse works is a dependency Nexus
does not otherwise have.

### Rejected without further evaluation

Unsigned or abandoned virtual-HID drivers, prebuilt `.sys` files of unclear provenance, anything
requiring Secure Boot off for normal use, and kernel hooks written for anti-cheat evasion. None of
these are acceptable in a product whose entire premise is that it does not weaken the machine.

## 3. Signing: what a custom Nexus driver would actually cost

Current requirements, not remembered ones:

| Route | Requirement | Cost |
|---|---|---|
| **Attestation signing** (Partner Center) | Microsoft countersigns after automated checks; no HLK run. Works on Windows 10/11 desktop; cannot ship via Windows Update to retail | **Free** — but the dashboard account must have **at least one EV certificate** associated |
| **EV code-signing certificate** | Required to hold the Partner Center hardware account | **~$290–$499/year** (Sectigo), **~$409/year** (DigiCert) |
| **Azure Trusted Signing** (now Azure Artifact Signing) | $9.99/month basic, 5,000 signatures | **Does not substitute** for EV in the Hardware Dev Center driver-submission path |
| **Test signing** | Local development only; requires machine-level changes | Not acceptable here |

> **Corrected — this table describes public distribution, not personal installation.** The first
> draft of this section treated an EV certificate as a precondition for a custom Nexus driver at
> all. It is not. Everything above applies to **distributing** a driver package to other people's
> machines through Microsoft. Installing a driver the owner built, on the owner's own PC, is a
> different question with a different answer — see §9.

So the realistic barrier for **publicly distributing** a Nexus UMDF2 VHF driver is an **EV
certificate at roughly $290–$409/year plus a Partner Center hardware account**, after which
attestation signing is free and typically completes within hours. Nexus is a personal-use,
single-owner, single-PC project and does not currently need that.

## 4. What was built

Everything here compiles and is tested. None of it can carry a byte until a backend is installed.

### HID report descriptors — `PcCore.NativeInput/Hid/HidReportDescriptors.cs`

The three descriptors Windows would parse to create the devices. These are the substance of the
feature and they are backend-independent: the same bytes work whether a custom Nexus driver or a
third-party backend submits them.

- **Nexus Mouse** — 7-byte report. Five buttons, **16-bit** relative X/Y, wheel, AC Pan.
  Sixteen bits because an 8-bit axis saturates at 127 counts and a phone flick exceeds that, which
  makes the pointer travel in the wrong direction relative to the finger rather than merely slower.
- **Nexus Keyboard** — 8-byte boot-layout report, plus the LED output report Windows sends whether
  or not a descriptor declares one.
- **Nexus Touch** — 9-byte single-contact touch screen, with the Tip Switch, In Range, Contact
  Identifier, Contact Count, Scan Time and Contact-Count-Maximum **feature** report that Windows
  requires before it will treat a digitizer collection as a touch screen.

`HidReportDescriptorTests` parses each descriptor as Windows would and recomputes the report sizes
from the bytes. This is not ceremony: the first draft of the digitizer was annotated and coded as a
7-byte report and is in fact 9. The comment was wrong, the constant was wrong with it, and only the
walker caught it.

### Report encoders — `Hid/HidReports.cs`

`HidMouseReport`, `HidKeyboardReport`, `HidDigitizerReport`, each with a `Neutral` value and a
`Write(Span<byte>)`. Little-endian, bounds-checked. The keyboard is a six-slot array, not a bitmap,
because that is what a boot keyboard is.

### Coordinate mapping — `Hid/DigitizerSurface.cs`

Normalised phone position to digitizer logical coordinates, handling negative desktop origins, DPI,
resolution changes and edge reachability (rounds rather than truncates, or the far edge is
unreachable). Carries the honest limitation that a HID touch screen is bound by Windows to **one**
display, so Direct-mode touch to a non-associated display lands on the wrong screen — a thing to
report, not to silently offset.

### The backend seam — `Hid/IVirtualHidBackend.cs`

A deliberately narrow port: submit finished reports, report availability, neutralise on the way out.
It knows nothing about Nexus's protocol, displays, or the phone, so swapping implementations cannot
change behaviour that has tests. `VirtualHidDeviceIdentity.DevicePath` is the load-bearing field —
see §5. `NoVirtualHidBackend` is the current implementation: it publishes nothing and refuses
everything, which is the truth on this machine.

`VirtualHidState.Partial` exists because a backend with a mouse and a keyboard and no digitizer is
the realistic outcome of integrating a third-party backend, and Direct mode needs to know it must
fall back while Touchpad mode does not.

### Diagnostics vocabulary — `PcCore.Capabilities/Input/NativeInputStatus.cs`

`InputBackend` replaces the misleading "native input" naming with three values that each say what
they are:

```
VIRTUAL_HID          Windows enumerates a Nexus HID device; reports arrive from it
ELEVATED_INJECTION   SendInput / InjectTouchInput, run by the elevated broker
LOCAL_INJECTION      the same calls, run by PC Core itself
```

`input.status` now also reports **per kind**, because one value becomes a lie the moment virtual HID
is partly installed:

```
Mouse: Elevated injection · Keyboard: Elevated injection · Touch: Elevated injection
— No virtual HID backend is installed.
```

`text_uses_character_injection` is reported and is expected to stay `true` forever: a HID boot
keyboard sends key usages and there is no key usage for an arbitrary character. Turkish text works
because it is injected as characters. The honest position is to report the hybrid, not to hide it.

**Migration is additive.** `path`, `state` and `summary` are unchanged on the wire, so the shipped
Android client keeps decoding `input.status` — and its `Json { ignoreUnknownKeys = true }` is there
for exactly this. The protocol vectors were regenerated and grew by nine lines with no deletions.

## 5. Security attribution under virtual HID — implemented and running

This is the invariant that decides whether virtual HID can ship at all:

```
ARMED + Nexus HID input      → stays ARMED
ARMED + physical mouse/keys  → SECURITY_SESSION
```

**The current mechanism will not survive the change.** v0.7 attribution rests on
`dwExtraInfo == 0x4E585330`, which is a property of a `SendInput` call. A HID report has no such
field: it goes through the real HID stack and emerges indistinguishable from hardware, which is the
entire point and also the problem. The v0.7 ledger (`INexusInputAttribution`, 400 ms claims) would
become the only signal, and a timing window is exactly what the ARMED promise must not rest on.

**The design is device identity, not timing.** This is why the descriptors publish three separate
devices rather than one device with three collections: Raw Input identifies a *device*, so
`VirtualHidDeviceIdentity.DevicePath` — recorded at publish time, when Nexus knows it created the
device — becomes the comparison key. `WM_INPUT` carries the originating device handle, and
`GetRawInputDeviceInfo` resolves it to the same path. The rule becomes "this event came from the
Nexus mouse", which is a fact rather than an inference.

The remaining question, which must be answered before any of this ships, is that the existing
detector is a `WH_MOUSE_LL` / `WH_KEYBOARD_LL` hook and **a low-level hook does not carry device
identity**. So the detector needs a Raw Input listener alongside the hooks, and the two need
correlating. That is real work and it is not done.

**Status: implemented.** `RawInputListener` runs alongside the low-level hooks and resolves every
`WM_INPUT` to a device path; `RawInputAttributionService` correlates the two. Attribution is a hard
gate on routing rather than a preference — `VirtualHidEligibility` refuses to route through HID at
all unless the listener is healthy *and* the security monitor is actually subscribed to the ledger,
because HID input is indistinguishable from a person's at the detector if it is not. Verified on
this machine: with the driver connected, PC Core logs
`Raw Input Mouse from a trusted Nexus device: \\?\HID#HID_DEVICE_SYSTEM_VHF#...`, and the three
published devices resolve to distinct paths recorded at publish time.

Timing is no longer the signal. The 400 ms ledger remains, but as corroboration for the one case
device identity cannot settle — a machine with a real digitizer, where a person's touch produces the
same promoted mouse messages Nexus's own does. PC Core says so at start-up when it applies.

## 6. Secure Desktop under virtual HID

Policy is unchanged and must be enforced *before* a report is submitted, in whatever component holds
the device handle — the same reasoning as v0.8, only more so: a HID source is closer to hardware
than `SendInput`, so the gate matters more, not less. `InputDesktop.Current()` already fails closed
on `Unknown`. Nexus does not switch desktops, open the Secure Desktop, automate `consent.exe`, or
touch UAC policy.

**v1.0 adds visibility, not automation.** A HID device produces reports Windows delivers to whichever
desktop has focus, so an owner-authenticated session can *drive* the Secure Desktop — but until v1.0
it could not *see* it, because Desktop Duplication stops there. `PcCore.SecureCapture`, a LocalSystem
service, starts a capture process in the console session and returns frames; no frame is produced
without a live owner-authenticated session, checked per frame. UAC is not disabled, Secure Desktop is
not disabled, no policy is changed and consent is never automated. Where Windows requires a local
confirmation, that requirement is preserved and reported as a platform limitation. See
[remote-control.md — The Secure Desktop](remote-control.md#the-secure-desktop).

## 7. Virtual display — researched only, and not recommended yet

`VirtualDrivers/Virtual-Display-Driver` uses the Windows Indirect Display Driver model (IddCx) and
ships a signed package. IddCx is a user-mode model, which puts it in the same favourable category as
UMDF2 for HVCI and Secure Boot.

**It is deliberately not being pursued in this pass**, on the product reasoning in item 16 of the
brief rather than on technical grounds. Nexus Live Screen already mirrors the physical desktop over
DXGI and that is what remote control needs. A virtual monitor is a different product — the phone
becoming a second desktop — and it is worth building only once someone wants Extend, not because it
is possible. The existing DXGI stream is untouched.

## 8. First-time setup, for the session where this gets switched on

Nothing below has been run. It is the deliberate one-pass sequence for when the owner decides to
start using Nexus.

1. **Decide the backend** (§2 and §3). Either buy the LizardByte lifetime licence at $49.99 and
   accept no HID touch, or commit to an EV certificate at ~$290–409/year and a custom Nexus UMDF2
   driver. This decision gates everything else.
2. **Elevated injection, if wanted as fallback** — `PcCore.Host --install-native-input`, once, with
   administrator consent. Registers `\Nexus\Nexus Input Broker` as a logon task at
   `HighestAvailable`. Visible and removable in Task Scheduler.
3. **Install the chosen HID backend** and reboot.
4. **Verify enumeration before trusting anything** — `input.status` must report `VIRTUAL_HID` for
   the kinds the backend actually publishes, and Device Manager / Raw Input must list the devices.
   A backend that claims ready without enumerated devices is a failed install, not a working one.
5. **Prove the security invariant** (§5) before using ARMED at all: Nexus HID input while ARMED must
   not open a session, and physical mouse or keyboard while ARMED must.
6. **Then** run the real-hardware pass that has been deferred throughout: elevated-window control
   after a local UAC approval, Secure Desktop refusal, disconnect neutralisation, restart recovery,
   and the background alert round trip.

[vhf-concept]: https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/virtual-hid-framework--vhf-
[vhf-config]: https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/vhf/ns-vhf-_vhf_config

---

# v0.9 pass: the Nexus-owned backend

The product decision is a Nexus-owned UMDF2 + VHF driver. libvirtualhid is not adopted (§2 stands).
Nothing below was installed, built as a driver, signed, or trusted.

## 9. Personal installation vs public distribution

This is the correction §3 needed, and it changes the cost of the whole project.

Microsoft's guidance on UMDF signing is explicit:

> UMDF drivers do not require Code Integrity Signing since they aren't kernel-mode code. However,
> your driver package still needs to be signed so that it can't be tampered with & can be traced
> back to the author.
>
> If your package includes any non-Microsoft kernel-mode drivers in addition to the user-mode driver
> then the Code Integrity Signing rules apply to that too.

The Nexus package contains exactly one binary: `NexusHidDriver.dll`, a UMDF2 user-mode driver. The
kernel-mode components in the stack — `vhf.sys` and `WUDFRd.sys` — are Microsoft's own inbox drivers
and are already signed by Microsoft. Nexus ships no kernel-mode code, so the second paragraph does
not apply.

That splits the question in two:

| | What it needs | Applies to Nexus? |
|---|---|---|
| **A. Personal install** on the owner's own PC | A signed *package* whose certificate the machine trusts: a self-signed catalog with the certificate in LocalMachine `Root` and `TrustedPublisher` | **Yes — this is what the owner needs** |
| **B. Public distribution** to other machines | Partner Center hardware account + EV certificate, then free attestation signing | Not needed |

**What is proven and what is not.** Proven from documentation: UMDF2 is user-mode; HVCI governs
kernel-mode code integrity; the Nexus package has no kernel-mode component; package trust is
established through the certificate stores. Strongly suggested by practice: WinUHid ships a
`RootDevCA` component and a `.cer` for exactly this flow, aimed at ordinary consumer machines.

**Not proven, and not assumed:** that this exact package installs and starts on *this* machine with
Secure Boot ON, HVCI ON and TESTSIGNING OFF. Some published material about self-signed driver
installation conflates kernel and user-mode rules, and the only way past that ambiguity is to try
it. It is listed as the first item of the future installation pass (§11) and is deliberately not
answered here.

## 10. What was built

| File | What it is |
|---|---|
| `src/driver/NexusHid/NexusHidProtocol.h` | The control ABI. Six operations, fixed-size structs, `C_ASSERT` on every size |
| `src/driver/NexusHid/NexusHid.inf` | Root-enumerated UMDF2 package; VHF as the function driver under the WUDFRd upper filter (see §13.9); Win10 and Win11 sections |
| `src/driver/NexusHid/Driver.c` | The source driver: VHF create/start/submit/delete, IOCTL validation, neutralisation |
| `src/PcCore.NativeInput/Hid/NexusHidProtocol.cs` | The managed mirror of the ABI |
| `src/PcCore.NativeInput/Hid/InputSourceAttribution.cs` | Raw Input device-identity attribution |

**The control protocol is six operations and no seventh**: version, create, submit, status,
neutralize, delete. There is no passthrough opcode, no caller-supplied pointer, no unbounded length,
and no way to name a file, a process or an address. Every request is refused unless it carries
exactly the struct size its opcode expects, names a known device, and — for a report — matches the
length the device's own descriptor implies.

That last check matters more than it looks. The driver walks the descriptor the caller supplied and
computes the report length itself rather than trusting a field, because a caller that could declare
a 7-byte mouse and then submit 64 bytes into it would be writing past what the device agreed to. A
report of the wrong length is refused rather than padded: padding produces input, and producing
input nobody asked for is the one failure a virtual input device must never have.

**Neutralisation is a release, never a generation.** `IOCTL_NEXUS_HID_NEUTRALIZE` and the file-handle
cleanup path both send an all-zero report to every published device, which is buttons up, keys up,
tip switch clear and in range clear across all three descriptors — a property the descriptors were
written to have. The control handle closing is how the driver learns PC Core is gone, whether it
exited, crashed or was killed. There is deliberately no timer that decides on its own to press or
move anything; that would be a driver producing input nobody requested.

**Not compiled.** The WDK is absent (§12), so `Driver.c` has never been through a compiler. It is
written to be correct and validated as far as text allows. "It builds" is not among the things
currently proven about it.

## 11. The future installation pass — prepared, not executed

Nothing here has been run. No certificate exists, no store was touched, no package was installed.

1. **Install the WDK** for 10.0.26100 (owner approval required; see §12 for exactly what is missing).
2. **Build** `NexusHidDriver.dll` and stamp the INF.
3. **Generate a Nexus-owned code-signing certificate**, self-signed, kept local.
4. **Create and sign the catalog** — `NexusHid.cat`, signed with that certificate.
5. **Trust it, deliberately and visibly** — import the certificate into LocalMachine `Root` and
   `TrustedPublisher`. This is the one step that changes machine trust state; the setup flow must
   name the certificate, show its thumbprint, and require explicit confirmation. It must never
   happen silently as a side effect of an install.
6. **Install the package** with `pnputil /add-driver NexusHid.inf /install`.
7. **Verify the security posture did not move** — Secure Boot still ON, HVCI/Memory Integrity still
   ON, TESTSIGNING still OFF. If any of the three changed, the install is wrong and must be reverted;
   the entire premise is that Nexus does not weaken the machine.
8. **Verify enumeration before trusting anything** — Device Manager and Raw Input must list
   `Nexus Mouse`, `Nexus Keyboard`, `Nexus Touch`. `input.status` reporting `VIRTUAL_HID` without
   enumerated devices is a failed install, not a working one.
9. **Prove the security invariant** — ARMED plus Nexus HID input must stay ARMED; ARMED plus
   physical mouse or keyboard must open a session. Virtual HID does not ship if this is unreliable.

Uninstall is `pnputil /delete-driver NexusHid.inf /uninstall` plus removing the certificate from both
stores — and the certificate removal has to be part of uninstall, or the machine keeps trusting a key
after the thing it was for is gone.

## 12. What is missing to build the driver

The Windows SDK 10.0.26100.0 is installed and `shared/vhf.h` is present — including, checked
directly on this machine, the `#ifdef _KERNEL_MODE` / `HANDLE FileHandle` branch that §1 is about.
`signtool.exe`, `makecat.exe` and `certmgr.exe` are present under `bin/10.0.26100.0/x64`.

**The WDK is not installed.** Specifically missing:

- UMDF2 headers and libraries — `wdf.h`, `wdfdriver.h`, the UMDF import libraries. Nothing under the
  kit provides them, so `Driver.c` cannot compile.
- The VHF import library for user mode.
- `stampinf.exe`, `inf2cat.exe`, `infverif.exe` — so the INF cannot be stamped or verified locally,
  and `NexusHidPackageTests` checks it as text instead.

It was not installed, per instruction. It is the first step of the pass in §11.

---

# 13. The install pass: what actually happened

Everything in §§9–12 built, packaged and validated cleanly, and then the driver was installed. Four
things were wrong. None of them could have been found without installing.

## 13.1 Nothing called `Probe()`

The backend, the lifecycle state machine, the eligibility rules, the router and the device registry
were all written, wired and passing their own tests. `NexusHidBackend.Probe()` had six call sites,
all of them in the test project. PC Core reported *"the Nexus HID driver is not connected"* on a
machine where the driver was installed and started, and it was correct: it had never looked.

Fixed by `VirtualHidProbeService`, a hosted service that probes once during startup and then on a
5-second tick, skips a connected backend entirely — re-probing tears devices down and republishes
them — and releases the devices explicitly during shutdown rather than leaving it to the driver
noticing a closed handle.

The diagnostic that hid this now distinguishes *nobody asked*, *asked and found nothing*, and *found
it and lost it*. They call for different things from the owner, and the old wording collapsed all
three into "not connected".

## 13.2 The driver never wrote the identity it reported

`slot->InstanceId` was declared, copied into the create reply, and assigned nowhere. Every create
returned `Ready` with an empty identity, and PC Core refused three devices Windows had happily
enumerated. `VhfCreate` and `VhfStart` had worked on the first attempt, for all three devices,
including the digitizer.

## 13.3 Handle cleanup neutralised but never deleted

The slot state lives in the control device's context, which belongs to the `WDFDEVICE` and outlives
every client handle. `EvtFileCleanup` only neutralised, so the devices from a dead PC Core stayed
`Ready` for the rest of the boot and every later PC Core was refused `STATUS_INVALID_DEVICE_REQUEST`
by the already-exists guard. **A driver that cannot let go of a dead client's devices serves one
client per boot.**

Slots are now owned by the file object that created them. Cleanup releases exactly that owner's
devices — neutralise, then `VhfDelete` — and ownership also gates submit, delete and neutralise, so
a second handle cannot drive or destroy devices it did not create.

## 13.4 `VHF_CONFIG.InstanceID` and `HardwareIDs` are undocumented, and using them breaks enumeration

The obvious fix for §13.2 was to have the driver *choose* each device's identity through those
members and report what it chose. The result: `VhfCreate` and `VhfStart` both returned
`STATUS_SUCCESS`, and VHF enumerated **no device at all** — no devnode over five create cycles, and
no entry in `setupapi.dev.log`, because Windows was never asked to install anything.

The VHF_CONFIG reference lists `InstanceID`, `InstanceIDLength`, `HardwareIDs` and
`HardwareIDsLength` with **empty descriptions**, while describing every other member. They are not
documented, and they do not work. The driver now sets only `ContainerID`, which is documented and
which tells Windows the three devices are one piece of hardware.

## 13.5 Identity, resolved the right way

Identity is not asserted by the driver at all. It is established from the device tree:

- every device Nexus publishes descends from `ROOT\DEVGEN\NEXUSHID`, and nothing else can;
- ancestry is walked with `CM_Locate_DevNode` / `CM_Get_Parent`, from the PnP instance behind each
  Raw Input device path;
- kind comes from Raw Input's own classification — mouse, keyboard, generic HID;
- anything unresolvable or ambiguous stays physical and untrusted.

This is stronger than the hardware-id match it replaced. A vendor id, a hardware id or a friendly
name are all things a device *claims*, and anything able to install a driver can claim them; the
device tree is Windows' own record of what created what.

**The touch screen is the exception, and it is a Windows behaviour worth knowing.** Raw Input reports
a digitizer as `\\?\VIRTUAL_DIGITIZER` — one synthetic name shared by the whole touch stack,
belonging to no device and resolving to no PnP instance. So the mouse and keyboard are resolved
through Raw Input, whose strings are exactly what attribution will later match, and the touch screen
is found by enumerating HID interfaces under our devnode and taking the one no already-resolved
device is using.

## 13.6 Attribution declared Nexus's own input too late

The worst of the four, and it only appears once input is real hardware. Every event Nexus delivers is
declared to the attribution ledger so the security detector can recognise it. The declaration was
made **after** the delivery, on the sound reasoning that input Windows refused produced nothing to
see.

On the injection path that was harmless: a `SendInput` event carries Nexus's tag in `dwExtraInfo`, so
the signature rule answers first and no timing is involved. On the virtual HID path there is no
signature — that is the entire point of it — and the report reaches Windows *inside* the controller
call. The low-level hook fires while the call is still returning, the detector reaches its verdict,
and the claim is created afterwards.

**An ARMED machine raised a security incident against its owner for their own keystroke.**

Declarations are now made before the input and withdrawn if Windows refuses it, which keeps the
invariant that mattered without depending on winning a race.

## 13.7 Verified on this machine

| | Result |
|---|---|
| Three VHF children enumerate | keyboard.inf, msmouse.inf, input.inf — all Started |
| Mouse / Keyboard / Touch | all `VIRTUAL_HID` |
| Pointer, click, scroll | delivered, cursor observed to move |
| Keyboard | `GetAsyncKeyState` confirms Windows sees the key held, then released |
| Text | typed and read back from a real edit control |
| Direct touch | down / move / up all delivered |
| ARMED + Nexus HID input | **no incident**, stays ARMED |
| ARMED + input Nexus did not produce | **exactly one incident** |
| Secure Desktop | `BLOCKED_BY_SECURE_DESKTOP` while a UAC prompt is up |
| Elevated window | accepts HID key events without a broker |
| Kill PC Core with things held | nothing stuck; the three devices disappear with it |
| Restart | exactly one clean set returns, all three `VIRTUAL_HID` |

## 13.8 What is still true

`input.text` uses character injection and always will: a HID keyboard sends key usages, and the same
usage produces a different character on a different keyboard layout. Turkish text works because it is
injected as characters, and routing it through HID would silently corrupt it. The visible consequence
is that `input.key` reaches an elevated window and `input.text` does not — Windows drops a
medium-integrity `SendInput` at an elevated window, and there is no device to route around it.
`input.status` reports this as `text_uses_character_injection`.

## 13.9 vhf was loaded twice, and every hibernate, restart and shutdown bugchecked

Package 0.9.2.0 loaded `vhf.sys` twice. `Needs = vhfservice.NT.Services` already makes vhf the
device's function driver and sets `VhfMode=2` ("operate as a function driver instead of a filter
driver"); the INF *also* wrote `LowerFilters=vhf`, the classic VHF layout for a UMDF function driver.
The live stack was `WUDFRd, vhf, vhf, PnpManager`, and both vhf instances read the same `VhfMode=2`,
so both were power policy owners.

Nothing showed while the machine ran. On any system power transition the lower instance requested
D3, the D-IRP went back up through WUDFRd, and the upper instance received a device power IRP it had
not requested: `WDF_VIOLATION 0x10D`, Arg1 `0xD`. The WDF in-flight recorder in each minidump shows
exactly that sequence. Modern Standby sends no system power IRP, which is why it looked intermittent —
display off and lid close were fine; hibernate (including Windows' own "Standby Battery Budget
Exceeded" escalation), restart and Fast Startup shutdown all crashed, nine times between install and
diagnosis.

The INF no longer writes the lower filter, and a `DelReg` removes the stale value from machines that
installed 0.9.2.0. The stack is now `WUDFRd, vhf, PnpManager`, the three children
still enumerate, and the note that "without LowerFilters no HID collection ever appears" turned out to
be wrong for this layout. The cost is InfVerif: `DelReg` is not declarative, so `/w` and `/u` report
error 1308 where 0.9.2.0 passed; `/v` passes with a warning. That does not affect a local install and
would matter for a Partner Center submission.

**It was not the only power policy owner.** With the kernel bugcheck gone, the first hibernate showed
the next one: `NexusHidDriver.dll` never gives up power policy ownership, so the UMDF framework also
receives vhf's D-IRP unrequested. In user mode that is a `WUDFVerifierFailure` at
`FxPkgFdo::DispatchDeviceSetPower` rather than a bugcheck — WUDFHost is terminated, the device is
offline after resume, and any other driver sharing that host goes down with it. The same report
followed every one of the nine bugchecks; it had been masked by them.

**0.9.2.2 carries both fixes.** The INF correction above, and `NexusHidEvtDeviceAdd` calls
`WdfDeviceInitSetPowerPolicyOwnership(DeviceInit, FALSE)` before creating the device, leaving vhf the only
power policy owner. It deliberately does not call `WdfFdoInitSetFilter`, which would also start
forwarding create and close to vhf. (0.9.2.1, the INF change alone, was installed only on the
development machine while the second owner was found; it was never released.) Verified on this
machine: restart, a full shutdown and two hibernates - both Windows' own battery-budget escalation from
Modern Standby - each with no bugcheck, no `WUDFVerifierFailure`, no dump, the three devices back and
PC Core reporting Virtual HID ready; the Intel Smart Sound device that used to share the killed host
stays healthy. A Fast Startup shutdown was not cycled separately: it hibernates the kernel session through
the same S4 path the two hibernates covered.

## 13.10 The DelReg cost more than InfVerif: an old package's removal deleted the driver's registration

From the 2026-09-23 cold boot the device started as `vhf, PnpManager` - no reflector, no driver, no
problem code - and PC Core reported Virtual HID NotInstalled. Driver 0.9.2.2 and its files were intact.
The boot-time `219 WUDFRd failed to load, 0xC0000365` events were not it: every WUDFRd device logs
them at every cold boot, with DriverFrameworks 10118 ("could not connect to the SCM, expected during
boot, will retry"), and recovers.

What had changed: `WUDF\Services\NexusHidDriver` (ImagePath, WdfMajorVersion, WdfMinorVersion,
WdfExtensions) was empty. Storage Sense (`cleanmgr /autocleanstoragesense`) had deleted the outranked
0.9.2.0 package on 2026-09-19, and its unconfigure step deleted those values. The reflector, finding no
driver to load, detached without a problem code; the running session was unaffected until the next boot.

Why deleting an *old* package could take the *active* driver's registration: the Driver Store
reference-counts a package's global registry per source, but only for packages it can configure.
0.9.0.0 to 0.9.2.0 were "configurable" (SetupAPI: `Driver package 'NexusHid.inf' is configurable`),
and removing two of them logged `still in use by 2 sources`, then `by 1 source`. 0.9.2.1 and 0.9.2.2
were `NOT configurable` - the `DelReg` of 13.9 is not a declarative directive - so their registration
was written by `WUDFCoinstaller` and counted for nothing. When the last counted package, 0.9.2.0, was
deleted, the count reached zero and Windows deleted the values the active driver needed.

**0.9.2.3 is the same driver in a declarative package again.** No `DelReg` and no lower filter;
InfVerif `/w`, `/u` and `/v` pass (0.9.2.2 fails `/w` with 1308). The service name, `UmdfService`,
`ServiceBinary = %13%` and the includes are unchanged - the configurable model needs none of them
changed. `NexusHidPackageTests` hold the INF to no non-declarative directive and no `LowerFilters`.

**The updater no longer leaves old packages for Storage Sense.** When the driver version changes, the
elevated half (`HidDriverLifecycle`): installs the new package, checks the device is bound to it and
healthy, deletes every other Nexus package matching `Root\NexusHid` (`pnputil /delete-driver`, no
`/uninstall`, no `/force`), and checks again - bound version, no problem code, exactly
`WUDFRd -> vhf -> PnpManager`, and the registered DLL hashing to the release manifest's. If cleanup
damaged the registration it reapplies the active package with `UpdateDriverForPlugAndPlayDevices`
(Windows' own install rewrites it; nothing writes the key by hand). If the device still is not healthy,
it binds the previous driver from the release store - staged and hashed beside the new one, selected
as the newest stored release carrying the installed driver - and cleans up after that the same way. A
driver update is skipped when no stored release carries the installed driver.

Proven in Windows Sandbox (Windows 11 26100) before touching this PC, with the real updater and the
real packages (release 24's 0.9.2.0, release 26's 0.9.2.2, 0.9.2.3):

| Path | Result |
|---|---|
| 0.9.2.0 -> 0.9.2.3 | Stack `WUDFRd, vhf, vhf` became `WUDFRd, vhf`; `LowerFilters=vhf` gone without `DelReg` (Kernel-PnP 410: `Lower Filters:` empty); 0.9.2.0 deleted, `still in use by 1 source`; one package left |
| this PC's history | 0.9.2.0 + 0.9.2.2, deleting 0.9.2.0 emptied the registration exactly as Storage Sense did; `devcon updateni` restored it |
| 0.9.2.2 (repaired) -> 0.9.2.3 | healthy; deleting the non-configurable 0.9.2.2 left the registration alone (`still in use by 1 source`); no repair needed |
| 0.9.2.0 + 0.9.2.2 -> 0.9.2.3 | both removed, registration intact, one package left |
| forced damage (non-configurable 0.9.2.2 over 0.9.2.0) | cleanup emptied the registration; the check caught it; reapplying repaired it |
| rollback 0.9.2.3 -> 0.9.2.2 from release 26's payload | bound by force, healthy; deleting 0.9.2.3 emptied the registration, reapplying repaired it; one package left |

The sandbox also showed something this PC hides: `NexusHidDriver.dll` imported `VCRUNTIME140.dll` (for
`memset`, `memcpy` and `__C_specific_handler`). Without the Visual C++ redistributable the UMDF host could
not load it (`0x8007007E`, DriverFrameworks event 2007) and the device came up as `vhf, PnpManager`, the
same silent symptom; it worked here only because the redistributable is installed. The project builds
with plain v143, whose default is the DLL runtime (`/MD`), where the WDK's own driver build forces the
static one for user-mode drivers (`SetHybridCrtCl` in `WindowsDriver.Common.targets`).

**0.9.2.3 links it in.** Release uses the WDK's hybrid CRT: vcruntime static (`/MT`), the Universal CRT
from Windows' own `ucrtbase.dll` through the `api-ms-win-crt-*` sets, inbox since Windows 10. Its
imports are VhfUm, ntdll, KERNEL32 and those sets; the DLL is 166 KB. (Linking the UCRT statically as well
made it 677 KB for nothing.) Debug keeps both static, because `ucrtbased.dll` exists only where the SDK
does. `tools/validate-driver-package.ps1` lists the imports after the build and refuses anything else,
and `NexusHidPackageTests` hold the project to the setting. In a sandbox with no redistributable,
0.9.2.3 loads (events 2004, 2010) with the stack `WUDFRd, vhf, PnpManager`, and 0.9.2.0 upgrades to it
healthy; the lifecycle paths in the table above give the same results with this build.
