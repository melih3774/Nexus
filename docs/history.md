# Release history

Nexus grew one milestone at a time, and each milestone's commit message records what was built, what
was measured and what broke on real hardware. This page is the short version.

| Version | Milestone |
|---|---|
| **1.6** | [The first public release](#16--the-first-public-release) |
| **1.5** | [The AI Assistant](#15--the-ai-assistant) |
| **1.4** | [Files and clipboard](#14--files-and-clipboard) |
| **1.3** | [Nexus updates itself](#13--nexus-updates-itself) |
| **1.2** | [A real video pipeline](#12--a-real-video-pipeline) |
| **1.1** | [Evidence playback, a Windows shell, live monitoring](#11--evidence-playback-a-windows-shell-live-monitoring) |
| **1.0** | [Owner-authenticated sessions, Secure Desktop, real cursors](#10--owner-authenticated-sessions-secure-desktop-real-cursors) |
| **0.9** | [Virtual HID](#09--virtual-hid) |
| **0.8** | [Native Windows input](#08--native-windows-input) |
| **0.7** | [Security detection, evidence, notifications](#07--security-detection-evidence-notifications) |
| **0.6** | [Live screen and remote input](#06--live-screen-and-remote-input) |
| **0.5** | [The Android app](#05--the-android-app) |
| **0.1–0.4** | [PC Core foundations](#0104--pc-core-foundations) |

---

## 1.6 — The first public release

Nexus is published under the Apache License 2.0, for anyone who wants the same thing for their own PC
and phone. Nothing about who it is for changed: one owner, one phone, one PC, built from this
repository on that PC. What changed is that a stranger can now get there. The HID driver plan prints
every command a new PC needs, its own signing key included; there is a complete uninstall; and the
documentation says what is true rather than what was hoped: Windows 11 for the driver, a Tailscale
account, and the AI Assistant as the one part that uses a cloud service.

Since 1.5, PC Assistant moved into this repository and Nexus releases build, install and update it.
The HID driver went from 0.9.2.0 to 0.9.2.4: one power policy owner, which ended a bugcheck on
hibernate (0.9.2.2); a declarative package with the C runtime linked in, so a Storage Sense cleanup
can no longer unregister it (0.9.2.3); and updates that load the new driver at once, proven on the
real PC by a version-only 0.9.2.4. → [getting-started.md](getting-started.md),
[virtual-hid.md](virtual-hid.md)

## 1.5 — The AI Assistant

An owner who has authenticated on their phone can instruct **PC Assistant**, a separate Windows
program that runs Claude Code, and watch it work: tasks, streamed replies, tool steps, AUTO/ASK, and
approval cards answered from the phone. PC Core relays; it still runs nothing itself and has no shell.
Every `assistant.*` action needs the Full Remote session, and the transcript reaches only the
connection that asked for it while it still holds one — locking the phone withdrew that authority
mid-task on the real device, and the phone had to authenticate again before it could read anything.
Administrator approvals are still answered at the PC. → [ai-assistant.md](ai-assistant.md)

## 1.4 — Files and clipboard

Browse every drive on the PC from the phone; open, save and upload files in resumable, hash-checked
ranges; send text between the two clipboards. All of it requires an owner session. Nexus's own
state and install folders stay unreachable. → [files-and-clipboard.md](files-and-clipboard.md)

## 1.3 — Nexus updates itself

One release manifest and one publish command. A separate updater stages only the files that changed
while PC Core keeps running, asks for UAC only for the driver or SecureCapture, and rolls back if the
new PC Core does not answer. The phone downloads its own APK from its PC over the authenticated
session. The first real updates found three updater defects, all fixed. → [updating.md](updating.md)

## 1.2 — A real video pipeline

Screen and camera moved from independent JPEGs to H.264 through Media Foundation (hardware encode
where available), hardware decode on the phone, measured capture-to-render latency, per-target
quality profiles and an adaptive controller. The first real phone session on 1.2.0 stalled at a few
frames a second; the log showed the controller mostly reacting to disruption it had caused itself.
1.2.1 changes bitrate in place, builds new encoders beside the running one, and settles before
judging. → [remote-control.md](remote-control.md#the-video-stream)

## 1.1 — Evidence playback, a Windows shell, live monitoring

Camera clips and audio from an incident play on the phone. The old explanation for why they did
not turned out to be wrong: the phone had been downloading each clip and then discarding it. PC Core
gained a tray icon, a Start Menu entry, a single version source and a log file on disk. The owner can
watch and listen to the room over the authenticated session, with a badge on the PC that no setting
can hide.

- **1.1.1** — a refused phone is explained in the PC's log (no phone paired, a replaced credential,
  or a wrong secret) while the wire still says only `authentication_failed`. Both ends print a
  credential fingerprint to compare.
- **1.1.2** — live monitoring had never worked for any session: an authorisation check rebuilt the
  caller from a connection id and lost the device id in the process. The caller is now passed through
  whole.

## 1.0 — Owner-authenticated sessions, Secure Desktop, real cursors

The app opens locked and unlocks with `BiometricPrompt`, backed by a Keystore key the platform will
not sign with unless the owner has authenticated. Remote control now proves *who* is holding the
phone, not just *which* phone it is, and older clients fail closed. A LocalSystem helper makes the
UAC prompt visible remotely without weakening it, and HID input now reaches the prompt so the owner
can answer it from the phone. The stream composites the real Windows pointer
shape, including the inverting I-beam. → [security-model.md](security-model.md#owner-authentication)

## 0.9 — Virtual HID

A signed UMDF2 source driver publishes a Nexus mouse, keyboard and touch screen through the Virtual
HID Framework. Input arrives from a device, so it reaches elevated windows without an elevated
broker, and Raw Input can still tell those devices apart from the ones on the desk. Installing it exposed four defects no test had seen, including an armed PC raising an incident
against its owner for their own keystroke. → [virtual-hid.md](virtual-hid.md)

## 0.8 — Native Windows input

A small elevated broker with ten operations lets input reach Administrator windows. Direct mode
sends real Windows touch, and the security detector can still tell Nexus's input from a person's.
Now the fallback path. → [native-input.md](native-input.md)

## 0.7 — Security detection, evidence, notifications

Arm the PC. Activity while armed opens an incident, records screenshots, camera and microphone
evidence with digests, and alerts the phone. **This was me** needs a biometric; **Not me** keeps
recording. No keylogging, and destructive actions only at the PC behind Windows Hello.
→ [security-monitoring.md](security-monitoring.md)

- **0.7.1** — the first real phone on a real tailnet found four defects. Moving the pointer held the
  left button down. One enum without a pinned wire name broke incident history, incident details
  and the alert. The fix for that enum would have discarded old incident records until reading was
  made tolerant. And a reflexive Disarm tap could close a live incident. The cross-language protocol
  vectors grew out of this.

## 0.6 — Live screen and remote input

Desktop Duplication streaming, pointer and keyboard injection, gestures that separate moving from
pressing, latched modifiers, and an on-screen indicator whenever it is happening.

## 0.5 — The Android app

A native Kotlin/Compose client. Pair by scanning a QR code, keep the credential sealed by the Android
Keystore, connect only to Tailscale addresses, and see the PC's screen, files, processes and security
state with live updates.

## 0.1–0.4 — PC Core foundations

- **0.1** — the capability protocol, dispatcher, filesystem boundary and loopback transport.
- **0.2** — one authoritative security state with an explicit transition table, and typed caller
  identity.
- **0.3** — persistent state, a random installation id, pairing with exactly one Primary Phone over a
  256-bit secret, HMAC challenge/response, per-user auto-start.
- **0.4** — the remote transport, bound only to a detected Tailscale address, with a restricted policy
  for the phone.

The design reasoning for 0.1–0.7 is preserved in [design/SPEC.md](design/SPEC.md) and
[design/PLAN.md](design/PLAN.md).
