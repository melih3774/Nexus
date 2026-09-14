# Security monitoring

Arm the PC and walk away. If someone uses it, Nexus opens an incident, records what it can, and sends
one alert to your phone. From the phone you can answer the alert, look at the evidence, and watch or
listen to the room.

- [The states](#the-states)
- [Detection](#detection)
- [Evidence](#evidence)
- [Answering an incident](#answering-an-incident)
- [Getting the alert](#getting-the-alert)
- [Looking at evidence on the phone](#looking-at-evidence-on-the-phone)
- [Live camera and microphone](#live-camera-and-microphone)
- [Checking it without a phone](#checking-it-without-a-phone)

---

## The states

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

One service owns the state and allows four transitions. Everything else is refused with
`invalid_state_transition`, and the state is left as it was, never quietly redirected. The only way
into `SECURITY_SESSION` is internal: no capability exposes it.

**The state survives restarts.** `ARMED` stays `ARMED`, and a security session is never downgraded by
a reboot. Otherwise rebooting would be the cheapest way to clear an incident. If the state file is
corrupt, PC Core starts `ARMED`: an unknown previous state is the one situation in which "the owner is
present" must not be assumed.

**An open incident can only be answered.** `security.arm` is refused while a session is open, and
the phone does not offer Disarm as a shortcut around This was me / Not me.

## Detection

**Nothing is recorded while you are present.** That is not a setting. In `OWNER_PRESENT` the
detector's observations are thrown away before any evidence code can run. Arming is what turns it on.

**There is no keylogger.** Detection reads exactly two facts about a keyboard event: whether it was
injected, and whether Nexus produced it. The key code is never read. The timeline says *keyboard
activity occurred*, never what was typed.

**Your own remote control never triggers it.** Every input Nexus produces is attributed to Nexus
before it reaches Windows:

- Input through the Nexus virtual HID devices is recognised by device identity. The device descends
  from Nexus's own root device in the PnP tree, and Raw Input tells the detector which device an event
  came from.
- Injected input carries a tag the detector matches exactly.
- Nexus declares its input *before* delivering it and withdraws the declaration if Windows refuses
  the input. The declaration used to come after delivery, and the virtual HID install showed why that
  was wrong: a HID report reaches the low-level hook while the submit call is still returning, so an
  ARMED machine raised an incident against its owner for their own keystroke.

So working on the PC from your phone while it is armed raises nothing, and a person at the physical
keyboard or mouse opens exactly one incident. [virtual-hid.md §5 and §13](virtual-hid.md) have the
details. On a machine with its own touchscreen, a person's touch and Nexus's touch produce the same
promoted mouse messages, and PC Core says so at start-up.

## Evidence

| | Default | |
|---|---|---|
| Screen | Timeline + screenshots | at most one every 8 s |
| Camera | Snapshot + short clip | at most one every 2 min |
| Microphone | Short clip | 20 s at the start of the incident |

Continuous modes exist for all three and are off by default. Everything is stored under
`%LOCALAPPDATA%\Nexus\Security\`. It is never written into the repository and never uploaded
anywhere.

- **Integrity.** Each file gets a SHA-256 digest, so any later change to it can be detected.
- **Retention.** Kept for 30 days, 200 incidents or 2 GB, whichever limit is hit first. Open and
  high-alert incidents are never trimmed, and acknowledging an incident does not delete it.
- **Windows controls apply.** Camera and microphone go through the standard Windows APIs, so privacy
  settings and the camera indicator light still work.
- **Destructive changes need Windows Hello at the PC.** Deleting evidence, disabling a camera or
  microphone source, turning detection off and shortening retention all require it. Viewing does
  not. The phone can do none of these things, because a stolen phone must not be able to destroy the
  recording of its own theft.

## Answering an incident

**This was me** asks for your biometric again, even inside an unlocked session. The phone has
already proved it is *the* paired phone. This proves the person holding it right now is you. Without
that step, an unlocked phone left on the desk could stand down an incident about whoever picked it
up.

**Not me** asks for no extra confirmation, because reporting an intruder should never be slowed
down by one. It flags the incident, keeps it open and keeps recording. **It does nothing else.** No
shutdown, no lock, no wipe. You are the one holding the device that can act.

## Getting the alert

Turn on **Alert me on this phone** in the Security screen. It keeps a connection open and shows a
permanent notification while it does. Without a push service, that is the only way Android will
wake an app for a network event, and a push service would mean routing a signal about your home
through a third party. There is no cloud, no account and no server. You can stop it from the
notification at any time.

## Looking at evidence on the phone

An incident holds screenshots, camera stills, camera clips and an audio clip, and all of them open
on the phone:

| | |
|---|---|
| **Camera stills, screenshots** | Shown as images |
| **Camera clips, screen recordings** | Play in the incident view, with scrubbing |
| **Audio clips** | Play with the same controls |
| **Deleted or expired** | Marked as such, separately from an error |

`security.evidence.get` is a ranged read: `offset` and `max_bytes` in, `chunk_bytes` and `end_of_file`
out. The phone appends chunks to a `.part` file, checks the result against the digest the PC recorded,
and only then renames it to a content-addressed name. A cached file is therefore complete by
construction, and opening the same clip twice downloads it once. The cache lives in the app's private
`cacheDir`, is pruned to a size budget, and never touches external storage or the gallery. No
filesystem path crosses the wire in either direction.

Evidence the incident still lists but the disk no longer holds is reported as `MISSING`, not thrown
as `NOT_FOUND`, so the phone can say "deleted or passed its retention period" instead of "something
went wrong".

## Live camera and microphone

The owner can watch and listen to the room the PC is in, from the Security screen:

```
media.live.start { "sensor": "CAMERA" }      →  H.264 over the authenticated connection
media.live.start { "sensor": "MICROPHONE" }  →  16-bit mono PCM
media.live.stop                              →  both, or one
```

The camera reads the device's video stream through `MediaFrameReader` and feeds the same H.264
encoder the desktop uses, with its own quality profile. When the link falls behind, it stops
forwarding frames entirely instead of dropping individual ones. Once the backlog clears, it asks for
one fresh keyframe and resumes from there, so the phone never decodes from a broken reference.

**What stops it.** Any of these, without anyone having to ask:

- the owner session lapsing (checked on a timer, not only at start)
- the phone disconnecting
- leaving the Security screen
- PC Core shutting down
- the owner, from the phone or from the PC's tray

**What it is not.** Nothing is written to disk. The live-capture service has no file path and no
option to add one. Live monitoring and incident evidence are separate subsystems on purpose: one is
watched and discarded, the other is recorded and kept.

**It cannot be hidden.** While a sensor is live, the PC shows a badge reading
`Nexus · Camera LIVE`, turns its tray icon red, and puts the state at the top of the tray menu with a
Stop entry. The indicator mode that can quieten the remote-control badge **cannot** quieten this one.
A setting that could would turn the feature into covert surveillance. Windows' own camera and
microphone privacy settings and hardware indicators are untouched and relied on: if Windows says
no, the stream fails.

Starting a stream does not ask for a fingerprint again. The owner session *is* the authorisation,
and a prompt for every action trains people to approve without reading.

## Checking it without a phone

```bash
PcCore.Host.exe --selftest-security
```

Installs the real hooks, injects real input and proves it is attributed to Nexus rather than to a
person, captures from the real camera and microphone, verifies the digests, and runs both
resolutions. `--prompt-hello` exercises the real Windows Hello prompt.

```bash
PcCore.Host.exe --selftest-live-sensors
```

Captures from both devices for a few seconds and reports frame counts, rates and time to the first
frame. A device that produces nothing looks exactly like a device nobody is watching, so this
separates "the PC cannot capture" from "the phone is not receiving". Its first run found three silent
defects: a microphone returning zero samples, eight channels read as one, and a camera running at
1.2 fps. The v1.1 commit (`1690fe0`) describes all three.
