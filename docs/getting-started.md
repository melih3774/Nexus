# Getting started

Nexus is a personal system: one owner, one Windows PC, one Android phone. Nothing here is published
to a store or a download page. You build it, install it on your own PC, and put the app on your own
phone.

- [Requirements](#requirements)
- [1. Build and install PC Core](#1-build-and-install-pc-core)
- [2. Tailscale on both devices](#2-tailscale-on-both-devices)
- [3. Install the phone app](#3-install-the-phone-app)
- [4. Pair](#4-pair)
- [5. Optional: the privileged parts](#5-optional-the-privileged-parts)
- [Starting at sign-in](#starting-at-sign-in)
- [Where things live](#where-things-live)
- [Configuration](#configuration)
- [When the phone cannot connect](#when-the-phone-cannot-connect)

---

## Requirements

| | |
|---|---|
| **PC** | Windows 10 1809 or later (developed on Windows 11), .NET SDK 10 |
| **Phone** | Android 12 or later, with a biometric or screen lock |
| **Network** | [Tailscale](https://tailscale.com) signed in on both devices, same tailnet |
| **Android builds** | JDK 21 and the Android SDK (Gradle wrapper included) |
| **Driver builds** | Visual Studio Build Tools and the WDK (only if you build the HID driver yourself) |

PC Core itself needs **no administrator rights** and logs a warning if it is started elevated.

## 1. Build and install PC Core

```bash
dotnet build PcCore.slnx -c Release
```

```bash
src/PcCore.Host/bin/Release/net10.0-windows10.0.17763.0/PcCore.Host.exe --install-app
```

`--install-app` copies the build to `%LOCALAPPDATA%\Programs\Nexus`, adds a Start Menu entry, and
points auto-start at the installed copy rather than at `bin\Release`, a folder that any rebuild or
branch switch can empty. Everything stays per-user: no elevation, no service, no `Program Files`.
`--shell-status` reports what is installed where.

After that, **Nexus** in the Start Menu starts PC Core with a tray icon. **Open Nexus** in the tray
menu opens the local control panel.

<p align="center">
  <img src="assets/screenshots/windows-control-panel.png" alt="The Nexus control panel on the PC, showing update status" width="720">
</p>

Once installed, later builds are best delivered with `tools/publish-release.ps1 -Install`, which
updates in place and rolls back on failure. See [updating.md](updating.md).

## 2. Tailscale on both devices

Install Tailscale on the PC and on the phone, and sign both into the same tailnet. **Nexus never
touches your Tailscale account and cannot set it up for you.**

PC Core logs the Tailscale address it bound to, or explains why it bound nothing. If Tailscale is not
running there is simply no remote listener. It starts on its own once a Tailscale address appears,
with no restart needed, and stops if the interface goes away.

## 3. Install the phone app

For a first install, build a debug APK and install it over USB:

```bash
cd android/Nexus && ./gradlew :app:assembleDebug
```

```bash
adb install -r android/Nexus/app/build/outputs/apk/debug/app-debug.apk
```

For updates that keep pairing intact, set up the stable release signing key once and let the phone
update itself from the PC ([updating.md](updating.md#one-time-setup)).

## 4. Pair

1. Open the control panel on the PC and click **pairing.begin**. A QR code appears, together with the
   same code as text. It expires in five minutes and works exactly once.
2. Open Nexus on the phone and tap **Scan QR**, or **Paste pairing code**. The camera permission is
   requested at that moment, never at launch.
3. The phone proves it holds the pairing secret, receives its permanent credential, and reconnects
   as the Primary Phone.
4. Unlock Nexus with your fingerprint or screen lock. That unlock is what gives the phone remote
   control, files and evidence ([security-model.md](security-model.md#owner-authentication)).

If the PC has no Tailscale address when you start pairing, the control panel says so, and so does
the phone, instead of failing vaguely.

## 5. Optional: the privileged parts

Everything above works without administrator rights. Two optional components add reach, and each is
installed deliberately, once, from an elevated terminal.

**The Nexus HID driver** (mouse, keyboard and touch as real devices, and the only path that reaches
the UAC prompt):

```bash
powershell -ExecutionPolicy Bypass -File tools/validate-driver-package.ps1
```

```bash
powershell -ExecutionPolicy Bypass -File tools/nexus-hid-install.ps1 -Action Install
```

The first script builds and stages the package without installing anything. The second is a dry run
that prints the exact plan. Add `-Apply` to perform it. It names the signing certificate and asks
before trusting it, captures the machine's security posture before and after, and refuses to run
under test signing. [virtual-hid.md](virtual-hid.md) records the whole process, including the
creation of the self-signed certificate.

**SecureCapture** (lets the phone see UAC prompts and the lock screen):

```bash
PcCore.SecureCapture.exe --install
```

A machine with neither still gets remote control, through injection, of everything except elevated
windows and the Secure Desktop. The elevated input broker (`--install-native-input`) is a middle
option. See [remote-control.md](remote-control.md#how-input-reaches-windows).

## Starting at sign-in

PC Core registers itself with one per-user value:

```
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
  PcCore = "<full path to PcCore.Host.exe>"
```

No service, no scheduled task, no watchdog. It appears in Task Manager's **Startup apps**, where you
can disable it. `--startup-status`, `--install-startup` and `--uninstall-startup` manage it by hand.
To stop PC Core registering itself while you debug, set `PcCore:Startup:Enabled` to `false`.

## Where things live

Everything that survives a restart is under `%LOCALAPPDATA%\Nexus`, never in the repository:

| Path | Contents |
|---|---|
| `instance.json` | a random installation id, not derived from any hardware or user identifier |
| `security-state.json` | security state, previous state, timestamp, revision |
| `primary-phone.json` | device id, display name, pairing time, and the shared secret as a DPAPI blob |
| `Security\` | incidents and evidence |
| `Releases\` | published releases, for updates |
| `pccore.log` | PC Core's log (one previous generation kept; **Open log** in the tray) |

Writes are atomic (temporary file, flush, atomic replace). A corrupt file is renamed aside, never
deleted.

## Configuration

`appsettings.json` beside `PcCore.Host.exe`, overridable with `PCCORE_`-prefixed environment
variables, or with an `appsettings.Local.json` next to it that updates never overwrite. Every section
in the shipped file has a comment explaining it. The main ones:

| Section | What it controls |
|---|---|
| `Files` | `AllowedRoots` (tokens `{Drives}` `{Desktop}` `{Documents}` `{Downloads}` `{Pictures}` `{Music}` `{Videos}` `{UserProfile}`, or absolute paths), `UploadIdleMinutes` |
| `RemoteTransport` | `Enabled`, `Port` (47812), session limits. **The bind address is not configurable** |
| `Security` | detection thresholds, evidence intervals, retention |
| `Input` | rate limits and size caps for remote input |
| `Stream` | backlog limits and the JPEG fallback; H.264 quality is chosen from the phone |
| `Pairing` | pairing session and challenge lifetimes |
| `Startup` | whether PC Core registers itself for sign-in |

A root that cannot be resolved is logged and dropped, never quietly widened.

## When the phone cannot connect

The phone says which of these it is:

- **No network**: the phone has no connection at all.
- **Tailscale not connected**: the phone has no tailnet address of its own.
- **PC unreachable**: the phone is on the tailnet and the PC did not answer.
- **Authentication failed**: the PC rejected the credential. The phone stops retrying until you
  press Retry, and both ends show the device id, installation id and credential fingerprint so you
  can compare them before re-pairing.

PC Core's log names the reason for a refused phone, using identifiers only.
