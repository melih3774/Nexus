# Security model

Nexus can move the pointer on a Windows PC, read its files, show its UAC prompts and turn on its
camera. This document explains who is allowed to do that, how the PC knows, and what Nexus
deliberately refuses to do.

- [Scope](#scope)
- [The rule](#the-rule)
- [Network exposure](#network-exposure)
- [Pairing](#pairing)
- [Device authentication](#device-authentication)
- [Owner authentication](#owner-authentication)
- [What each caller may do](#what-each-caller-may-do)
- [PC Assistant](#pc-assistant)
- [The filesystem boundary](#the-filesystem-boundary)
- [Privileged components](#privileged-components)
- [Visibility](#visibility)
- [Data at rest](#data-at-rest)
- [What Nexus will not do](#what-nexus-will-not-do)
- [Known limits](#known-limits)

For reporting a vulnerability, see [SECURITY.md](../SECURITY.md).

---

## Scope

Nexus is built for **one owner, one Windows PC and one Android phone**. It is not built for multiple
users, organisations, device fleets or unattended access by anyone else, and narrower designs were
chosen over general ones throughout.

The threats it is designed around:

- **someone else on the network**, whether LAN or internet, trying to reach PC Core;
- **a lost or stolen phone** that still holds a valid pairing credential;
- **a person physically at the PC** while the owner is away;
- **other software running as the owner** trying to squat on Nexus's local channels.

Out of scope: malware that already runs with administrator rights on the PC, and someone who has
the phone *and* can pass its biometric or screen-lock prompt. Nexus accepts the device credential as
a fallback, so the phone's own PIN is part of the owner check. Setting a strong one matters.

## The rule

> Callers interact with the computer *through capabilities*, never through arbitrary Windows
> functionality. If PC Core does not expose it, a caller cannot do it.

Every request is a named action with typed parameters, handled by one dispatcher and checked against
one policy. There is no shell execution, no process launch, no arbitrary Win32 call and no
file delete.

**One thing that rule does not cover, stated plainly.** An owner who has authenticated on the phone can
instruct **PC Assistant**, a separate program on the PC that runs Claude Code, and PC Assistant can run
commands and use tools under its own AUTO/ASK policy and approval cards. PC Core still has no shell: no
`assistant.*` action names a command or a process, and PC Core relays requests without running
anything. See [PC Assistant](#pc-assistant) below and [ai-assistant.md](ai-assistant.md).

`PcCore.Capabilities` has no project reference to `PcCore.Windows`, and the loopback
transport has none either. The compiler enforces this: a capability cannot tell how its caller
arrived, and a transport cannot control Windows.

## Network exposure

```
127.0.0.1     → local control panel and developer API (47811), token required
100.x.x.x     → authenticated remote transport (47812), Tailscale address only
192.168.x.x   → not listening
public WAN    → not listening
```

**The remote listener binds exactly one address, and only when two independent checks agree:** the
network adapter identifies itself as Tailscale, *and* the address lies inside `100.64.0.0/10`,
checked with address arithmetic rather than a `"100."` string prefix. If either check fails, there
is no listener at all. There is no bind-address setting, no wildcard path and no fallback to the LAN,
because a bind-address setting is exactly how a localhost-only service ends up on the LAN. Nexus does
not install, configure or log into Tailscale. It only uses the private network the owner already
set up.

**No TLS inside the tunnel, deliberately.** The traffic is already inside WireGuard, which Tailscale
has authenticated and encrypted, and PC Core separately proves the peer holds the shared secret.
Adding certificates would mean generating, distributing, renewing and pinning them on the phone:
complexity that buys a second encryption layer inside the first. That argument holds only while
traffic really stays inside the tunnel, so the phone enforces it too:

```
100.64.0.0/10  → the only addresses Nexus will dial
192.168.x.x    → refused
a host name    → refused, and never resolved
wss:// or another path → refused
```

Every endpoint goes through that check, whether scanned, pasted or restored from storage.

**Loopback is not trusted by default either.** `/rpc` and `/ws` require a per-run session token and
validate `Host` and `Origin` to block DNS rebinding. [design/SPEC.md §7.3](design/SPEC.md) explains
why serving the token to the same-origin control panel is safe.

## Pairing

Pairing starts at the PC and nowhere else. The local control panel shows a QR code (and the same
text to copy) carrying a short-lived pairing payload. The phone scans it and proves it holds the
pairing secret rather than sending it. It then receives a random `DeviceId` and a **256-bit shared
secret**, exactly once.

- The pairing session is single-use, expires after five minutes, and is used up by any completion
  attempt, right or wrong.
- There is exactly **one Primary Phone, or none**. Replacing it is an explicit choice at the PC.
- Unpairing deletes the credential, and the old secret then authenticates nothing.
- **The phone is a controller, not the authority over which phone is trusted.** Pairing and device
  management are local-only, so a stolen phone credential can never enrol another device.

## Device authentication

On every connection the PC sends a random single-use challenge, and the phone answers with an
HMAC-SHA-256 proof. The permanent secret is never sent after pairing. The signed message binds a
purpose string, this PC installation and the device id, so a proof cannot be replayed anywhere else.
The connection is then bound to that identity for its lifetime.

- A verified proof is the **only** way to become a `DEVICE` caller. The type that represents one has
  no public constructor, so a request that merely claims to be a device has no effect.
- Every failure gets one opaque answer, `authentication_failed`, so the response cannot be used to
  discover device ids. The PC's own log names which of the three failure reasons applied (no phone
  paired, a replaced credential, or a wrong secret), using identifiers only.
- Both ends can print an eight-character credential fingerprint: a domain-separated SHA-256 of the
  secret, truncated. It answers "which credential is this?" and is not enough to forge a proof.

## Owner authentication

Pairing proves *which phone* is calling. A stored secret sits on the phone whoever is holding it, so
since v1.0 Nexus also asks **whether the owner is holding it right now**.

The app opens locked. Unlocking runs Android's own `BiometricPrompt` (`BIOMETRIC_STRONG`, with the
device credential as fallback). Nexus asks a yes-or-no question and gets a yes or a no. It never sees
a fingerprint, a face or a PIN.

What gives that answer weight on the PC is a key, not a flag. The phone holds a hardware-backed EC
key in the Android Keystore, created with `setUserAuthenticationRequired`, so the platform refuses to
sign with it unless the owner authenticated within the last fifteen minutes. PC Core verifies the
signature. A valid signature is therefore evidence that a real system prompt succeeded, produced by
the part of the system that saw it happen.

The key is also created with `setInvalidatedByBiometricEnrollment`, so enrolling a new fingerprint
destroys it. Enrolling a finger is exactly what someone who took the phone and learned its PIN
would do. The replacement key is refused until the owner clears the old one **at the PC**, where a
thief holding the phone is not.

| Trigger | Asks again? |
|---|---|
| App launched, or Android recreated the process | Yes |
| Phone screen turned off | Yes — putting the phone down ends the session |
| In the background longer than 15 minutes | Yes |
| One session older than 12 hours | Yes |
| Reboot | Yes |
| A short trip to another app and back | **No** |

The last row is a security decision, not just a convenience. A prompt forty times a day trains people
to tap through prompts without reading them. The Keystore key enforces the fifteen minutes on its
own, so a bug in the app's timer cannot quietly extend the window.

## What each caller may do

| Caller | May reach |
|---|---|
| **Paired phone, owner not authenticated** | `system.get_info`, `security.get_state`, the `session.*` calls needed to authenticate, and `update.check` / `update.apk.get` |
| **Paired phone with `FullRemote` authority** | screen and camera streams, input, files and clipboard, incidents and evidence, live sensors, stream quality, PC Assistant (`assistant.*`) |
| **Local only (at the PC)** | pairing and unpairing, device management, installing updates, indicator mode, deleting evidence and weakening monitoring (behind Windows Hello) |

Anything not named in the policy is denied. Denials return `not_authorized`, run no part of the
action, and do not drop the connection. **The upgrade path fails closed**: an older app that never
calls `session.authorize` never gets `FullRemote` and is refused everything else. Authority is
checked on every request. Long-running work re-checks it while it runs: Secure Desktop capture on
every frame, live sensors on a timer. A lapsed session stops them without the phone having to ask.

## PC Assistant

PC Assistant is a separate program with its own security model, reached over a local named pipe. What
Nexus holds itself responsible for:

- **Who may instruct it.** Every `assistant.*` action needs Full Remote. A paired phone whose owner has
  not authenticated is refused before PC Assistant is contacted. AI callers (`ActorType.Ai`) are still
  granted nothing.
- **Who may watch it.** PC Assistant's events carry the owner's conversations, so the remote transport
  does not deliver them by name as it does `security.*`. Each one is delivered only to a connection
  that subscribed and still holds Full Remote at the moment it is sent. That check does not slide the
  idle window. A connection found without the session loses its subscription, so a phone that locks and
  unlocks has to ask again. The phone also drops the transcript from memory when it locks.
- **Which program it is talking to.** The pipe must belong to the current user, be served from this
  session, and be served by exactly the executable configured as `PcCore:Assistant:ServerExecutable`,
  or nothing is written to it. PC Assistant makes the matching check on PC Core from its side.
- **What reaches the phone.** Parameters are validated before relaying. PC Assistant's replies are
  re-serialised into named fields rather than passed through.

What stays with PC Assistant: Claude Code, AUTO/ASK, which actions need an approval, and the approvals
themselves. Administrator approvals cannot be answered from the phone at all. PC Assistant refuses
them on its bridge, and they are answered in its own card at the PC. The bridge is off on both sides by
default. Nexus never starts PC Assistant on its own: an update that replaces it reopens it only if it
was already running.

Security monitoring treats PC Assistant's clicks and typing like any other injected input: set aside,
never counted as a person. That was measured, not assumed; see
[ai-assistant.md](ai-assistant.md#security).

## The filesystem boundary

Nothing on disk is reachable unless it is inside `PcCore:Files:AllowedRoots`. The default since v1.4
is `{Drives}`, every local drive, and Nexus's own state and install folders are refused regardless.
Every caller-supplied path is:

1. rejected by shape: relative, UNC, device namespace, alternate data stream, wildcards;
2. canonicalised;
3. **resolved to its real path by the kernel** (`GetFinalPathNameByHandle`), so junctions, symlinks,
   mount points and 8.3 short names cannot alias past the boundary;
4. only then checked for containment **segment by segment**, not by string prefix.

A path that cannot be resolved is denied. Zero roots means everything is denied, never everything
allowed. PC Core runs as the owner and never elevated, so Windows permissions still apply on top.
[design/SPEC.md §6](design/SPEC.md) documents the pipeline and the Windows edge cases that remain,
such as TOCTOU, hard links, and mount points inside a root.

## Privileged components

PC Core is a standard-user process and logs a warning if it is started elevated. The parts that
need more are small, separate and installed deliberately by the owner:

| Component | Runs as | What it can do | Installed by |
|---|---|---|---|
| **Nexus HID driver** | UMDF2 user-mode driver, on Windows' inbox VHF | Publish a mouse, keyboard and touch screen for the handle that created them; six fixed control operations | The owner, following the plan `tools/nexus-hid-install.ps1` prints |
| **SecureCapture** | LocalSystem service | Start a capture worker in the console session; return frames to a verified PC Core pipe | `PcCore.SecureCapture.exe --install`, from `%ProgramFiles%` |
| **Input broker** (optional fallback) | Elevated, in the owner's session | Ten input operations over an owner-only pipe with executable checks on both ends | `PcCore.Host.exe --install-native-input` |
| **Updater** | Standard user; elevates only one step | Swap PC Core files; elevate only to reinstall the driver or SecureCapture | Started by PC Core |
| **PC Assistant's privileged helper** (`PcAssistantElevation`) | LocalSystem service that acts with the owner's own elevated token, in the owner's session, never as SYSTEM | Carry out the administrator operations PC Assistant asks for, each one approved in PC Assistant at the PC, with no further UAC prompt | The owner, in PC Assistant (**Capabilities → Install Privileged Helper**, one UAC prompt) |

The driver package contains no kernel-mode code of its own. It is signed with a certificate the owner
created and chose to trust on this machine; its private key cannot be exported. The install plan
shows the certificate and its thumbprint before the step that trusts it. It captures Secure Boot, Memory Integrity (HVCI) and test
signing before installing and compares them afterwards: installing Nexus must change none of them.
It installs with Memory Integrity on and test signing off. The uninstall steps include removing the
certificate from both stores, so the machine does not keep trusting a key after the driver is gone.
See [virtual-hid.md](virtual-hid.md).

## Visibility

Nexus has no stealth mode.

- A **tray icon** is shown whenever PC Core is running.
- An **on-screen badge** appears while the phone is controlling the PC (see the
  [indicator modes](remote-control.md#what-the-pc-shows-while-it-happens)).
- A **`Camera LIVE` / `Microphone LIVE` badge** and a red tray icon appear while a sensor is streaming,
  and no setting hides them.
- **Auto-start** is a single per-user `Run` value, visible and removable in Task Manager's Startup
  apps. The input broker is a visible task in Task Scheduler. Nothing is hidden.
- Windows' own camera light and privacy settings keep working, because Nexus captures through the
  standard APIs.

## Data at rest

| Where | What | Protection |
|---|---|---|
| PC, `%LOCALAPPDATA%\Nexus` | installation id, security state, pairing, owner key record, settings, logs, evidence | Primary Phone secret is a DPAPI blob (`CurrentUser`); writes are atomic; corrupt files are renamed aside, not deleted |
| Phone, app-private storage | the shared secret, sealed by a non-exportable AES-256-GCM Keystore key | excluded from cloud backup and device transfer |
| Phone, Keystore | the owner-session EC key | hardware-backed, requires user authentication, invalidated by new biometric enrolment |

Logs contain identifiers, never credentials. There is no analytics SDK, no crash reporter and no
cloud component of Nexus's own; the optional PC Assistant uses Claude Code, which does
([ai-assistant.md](ai-assistant.md#what-leaves-the-pc)). Evidence never leaves the PC except when the owner fetches it over the
authenticated session.

## What Nexus will not do

- Disable or weaken UAC, the Secure Desktop, Memory Integrity, Secure Boot or driver signing
- Approve a UAC prompt, capture a credential, or automate consent
- Run hidden, or record camera or microphone without an indicator
- Log keystrokes (security detection never reads key codes)
- Listen on the LAN or the internet, or run a relay or cloud service of its own. Tailscale may carry
  the encrypted connection through its relays, and PC Assistant's Claude Code talks to Anthropic
  ([ai-assistant.md](ai-assistant.md#what-leaves-the-pc))
- Let the phone pair devices, delete evidence, or change monitoring settings. Disarming from the
  phone needs the owner session and leaves recorded evidence alone
- Take a destructive action on its own (no remote wipe, lock or shutdown in response to an incident)

## Known limits

- **Whoever passes the phone's unlock is the owner.** The owner session accepts the phone's screen
  lock as well as its biometric. Someone who has the phone and knows its PIN gets full remote
  authority, desktop included, and through the desktop can do anything the owner's account can,
  such as deleting files. Deleting evidence through Nexus still needs Windows Hello at the PC, but
  evidence files are ordinary files of the owner's account.

- **Elevated reach is real power.** Anything that lets a standard-user process produce input in an
  elevated window is, by construction, a way for user-level code to drive administrator UI. Nexus
  narrows that to its own authenticated session, its own driver handle or broker pipe, and a small
  fixed set of operations. If you do not want that, do not install the driver or the broker, and
  elevated windows stay out of reach.
- **Typed text does not reach elevated windows.** `input.text` uses character injection, so that
  every keyboard layout produces the right characters.
- **A real touchscreen blurs attribution.** On a PC with its own touchscreen, a person's touch and
  Nexus's touch look the same to the low-level hooks, and PC Core says so at start-up.
- **Filesystem races.** TOCTOU between check and open, hard links, and mount points created inside
  a root are documented in [design/SPEC.md §6.4](design/SPEC.md).
