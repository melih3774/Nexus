# Virtual HID: what was researched, what was built, and what is deferred to one install pass

This is the record behind the v0.9 virtual-HID work. It supersedes the conclusion of
[`native-input.md` §3](native-input.md), which is wrong in one important way — see §1 — and
corrects itself once, in §9, where §3 turned out to be answering the wrong question.

Sections 1–8 are the research and foundation pass. Sections 9–12 are the Nexus-owned backend that
followed from it.

Nothing in either pass installed a driver, registered a task, changed Secure Boot, HVCI, UAC,
certificate stores or driver-signing state, or bought a licence.

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

## 5. Security attribution under virtual HID — designed, not yet implemented

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

**Marked: designed / pending implementation and real installed-device verification.**

## 6. Secure Desktop under virtual HID

Policy is unchanged and must be enforced *before* a report is submitted, in whatever component holds
the device handle — the same reasoning as v0.8, only more so: a HID source is closer to hardware
than `SendInput`, so the gate matters more, not less. `InputDesktop.Current()` already fails closed
on `Unknown`. Nexus does not switch desktops, open the Secure Desktop, automate `consent.exe`, or
touch UAC policy.

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
| `src/driver/NexusHid/NexusHid.inf` | Root-enumerated UMDF2 package; VHF as lower filter; Win10 and Win11 sections |
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
