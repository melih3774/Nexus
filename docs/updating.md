# Updating Nexus

```
change code  ->  .\tools\publish-release.ps1 -Install  ->  PC restarts on the new build
                                                        ->  phone: "Nexus x.y.z available  [Update]"
```

Everything stays on the PC. There is no update server: the PC that builds a release is the one that
installs it and the one the phone downloads it from.

- [One-time setup](#one-time-setup)
- [Publishing](#publishing)
- [How the PC updates](#how-the-pc-updates)
- [How the phone updates](#how-the-phone-updates)
- [One state folder, whoever starts Nexus](#one-state-folder-whoever-starts-nexus)

---

## One-time setup

```bash
powershell -ExecutionPolicy Bypass -File tools/setup-android-signing.ps1
```

Android upgrades an app in place, keeping its data, Keystore keys and pairing, only when the new APK
has the same signer. The script sets up a stable local signing key in
`%USERPROFILE%\.nexus\release-signing\`, with a new random password and an ACL that allows only the
current user. It never lives in the repository. By default it adopts the certificate the installed
debug builds already carry. **Back that directory up**: without it, no future build will install
over the app on the phone. `-NewKey` creates a fresh key instead, at the cost of one reinstall and
re-pair.

An app installed before Nexus 1.3 has no updater, so the first updater-capable APK has to reach it
once by hand (`adb install -r <apk>`). A build from this repository already has one.

## Publishing

```bash
powershell -ExecutionPolicy Bypass -File tools/publish-release.ps1 -Install
```

The script:

- builds PC Core, the input broker, the updater and SecureCapture
- builds PC Assistant from a clean copy of `src/Assistant` only if its app sources changed since the
  last release; otherwise the previous release's PC Assistant is carried forward byte for byte
- builds the signed Android release APK only if `android/Nexus`, `shared/protocol` or
  `Directory.Build.props` changed since the last release; otherwise the previous APK is carried
  forward, so the phone is not offered an update that changes nothing
- hashes everything and writes `manifest.json`
- moves the release to `%LOCALAPPDATA%\Nexus\Releases\<release>`, and only then points `latest.json`
  at it

The newest three releases are kept, and a publish in which nothing changed publishes nothing.
`-Install` then updates this PC. Without it, the tray offers the update.

The manifest records:

- the product version (`Directory.Build.props`)
- a release number that goes up on every publish and doubles as the Android `versionCode`
- the SecureCapture version (its own `csproj`)
- the HID driver version (`DriverVer` of the signed package)
- the PC Assistant version (`src/Assistant/Directory.Build.props`)
- SHA-256 and size of every file
- the APK's signer certificate
- `NexusMinAndroidVersionCode`

The script refuses a changed SecureCapture, driver or PC Assistant whose version number did not
change, and an APK signed by a different key.

## How the PC updates

The tray (`Update available … → Install update`), the control panel's **updates** box and `-Install`
all do the same thing: PC Core copies the release's `PcCore.Updater.exe` to a runner folder and starts
it. The updater then:

1. compares the release with the install directory (PC Core by file hash, SecureCapture and the
   driver by version) and stages only what differs, verifying every file against the manifest
   **while the old PC Core keeps serving**;
2. asks for UAC **only** if SecureCapture or the driver changed, and only for that step. The elevated
   half copies the files into `%ProgramFiles%\Nexus\Updates`, re-hashes them there, and runs
   `PcCore.SecureCapture.exe --install` or `pnputil /add-driver … /install`. It creates no new device
   and changes no certificate. Declining the prompt changes nothing;

   a driver update stops PC Core first - after approval, once the elevated half has verified its
   payloads and asks for the device - because PC Core holds the Nexus HID device open, and an open
   handle vetoes the device restart that loads the new driver. It then removes the older Nexus packages
   from the Driver Store (`pnputil /delete-driver`, without `/uninstall` or `/force`) and only then
   checks the device: bound to the new version, **running** it (the driver's own answer to
   `IOCTL_NEXUS_HID_GET_VERSION`), no problem code, stack exactly `WUDFRd -> vhf -> PnpManager`, and
   the UMDF registration pointing at a DLL with the release manifest's hash. If cleanup damaged the
   registration, the active package is reapplied through `UpdateDriverForPlugAndPlayDevices`; if the
   device is still not healthy, the driver installed before is put back from the release store - the
   release that carries it is staged and hashed with the new one - and the update fails. A driver
   update is skipped when no stored release carries the installed driver. If something other than
   Nexus held the device, Windows can only load the new driver after a restart: the update then ends
   **restart required** (below), not succeeded. See docs/virtual-hid.md sections 13.10 and 13.11;
3. stops PC Core through its stop event (Ctrl+C for builds that predate it, and a kill as a last
   resort), unless the driver step already did;
4. moves each changed file aside and copies the new one in, recording each step in a journal;
5. starts PC Core and waits for `system.get_info` over loopback to report the new version and keep
   running.

A release whose PC Core files did not change but whose driver did still stops PC Core for the driver,
and starts the same PC Core again afterwards.

**Restart required.** When Windows bound the new driver but kept the previous one running - something
held the device open - the update records `restart_required` instead of `succeeded`: the updater exits
3010, `status.json` says what keeps running until the restart, the tray shows *Restart Windows to
finish the Nexus update*, and `-Install` prints it in yellow. Whether the restart is still outstanding
is read from Windows (`DN_NEED_RESTART` on the Nexus device), so after a restart the same update shows
as succeeded. The driver itself has then not been checked running; PC Core's log shows which version
it connects to (`Virtual HID: Ready — Nexus HID 9.2.x`).

If step 5 fails, the new process is ended, the moved-aside files are put back, the previous PC Core
is started again, and the tray says the update was rolled back. A journal left by an updater that
died mid-swap is undone on its next run. The updater's own log is
`%LOCALAPPDATA%\Nexus\update\updater.log`.

### PC Assistant

A release carries PC Assistant too. The updater installs it at
`%LOCALAPPDATA%\Programs\Nexus\Assistant\` the first time a release includes it, and replaces it only
when its version changes, so a rebuild of the same source never closes it. When it does change:

1. a PC Assistant running from that folder is **asked** to close — before any UAC prompt, before
   anything is changed. It agrees only when no task is running and no approval is waiting; otherwise
   the update ends there with nothing changed, and it is never killed. A copy running from anywhere
   else, such as a development build, is not the one Nexus installs and is left alone;
2. its changed files are swapped with a journal of their own, while PC Core keeps running;
3. if it was open, it is started again and must stay up; if it does not, the previous files are put
   back and the previous PC Assistant is started again. A PC Core rollback afterwards does not undo it.

PC Assistant's own state — tasks, `pcassistant.db`, `settings.json` and logs under
`%LOCALAPPDATA%\PcAssistant` — and Claude Code's sign-in are never touched. Neither is trust: PC Core
connects only to the path in `PcCore:Assistant:ServerExecutable`, and PC Assistant's administrator
helper serves only the copy it was installed from. See
[ai-assistant.md](ai-assistant.md#moving-from-a-development-build).

**Never touched:** `%LOCALAPPDATA%\Nexus` state (pairing, owner keys, settings, logs, evidence), the
certificate stores, and any file in the install directory that no manifest listed. An edited
`appsettings.json` is kept, and `appsettings.Local.json` beside it is loaded after it and never
shipped.

The first real updates of an installed Nexus found three updater defects: a console handle left dead
by Ctrl+C, a mutex released from the wrong thread, and a rolled-back release that could be resumed
by mistake. See commits `d59460e`, `b0cefb5` and `9947faf`.

## How the phone updates

The phone reports its `versionCode` when it authenticates and calls `update.check` once per session.
When the PC's release has a newer APK, the home screen shows **Nexus x.y.z available → Update**. So
does the lock screen, since updating does not require unlocking.

The app then:

1. downloads the APK with `update.apk.get` in 1 MB ranges into a `.part` file that resumes after an
   interruption;
2. checks the whole file's SHA-256, package name, versionCode and signer against the installed app;
3. hands it to Android's `PackageInstaller`, which requires the user to confirm.

Android shows its own confirmation, and the first time it also asks whether Nexus may install apps.
On the next start the app says **Updated to Nexus x.y.z**.

`update.check` and `update.apk.get` are the only additions to what the phone may call, and
`update.install` is local-only. A phone whose app is older than `NexusMinAndroidVersionCode` still
authenticates, but it is told `update_required` and every other action is refused with that code: it
can fetch the update and nothing else. A phone that reports no version is treated as build 14, the
last one before the updater existed.

## One state folder, whoever starts Nexus

A terminal running inside a packaged (MSIX) desktop app can be shown a private copy of
`%LOCALAPPDATA%\Nexus` under `%LOCALAPPDATA%\Packages\<package>\LocalCache\Local\Nexus`. Windows
creates that copy when a process started from the app creates the folder before it exists. From then
on, Nexus started from that app and Nexus started from the Start Menu read different pairings,
release stores and update records, and a restart from the Start Menu comes up as if the last update
never happened.

PC Core, the updater and `publish-release.ps1` therefore refuse to run against such a copy: the
updater and PC Core exit with code 5 and say which copy they found. PC Core started normally logs a
warning while one exists. Move what you need from the copy into `%LOCALAPPDATA%\Nexus` and delete
it.
