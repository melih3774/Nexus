# Repository assets

Images used by the top-level README and the guides. Everything here is either a real capture of
Nexus, redacted as described below, or a drawing that is clearly not a screenshot (the banner and the
`*.pending.svg` placeholders).

## What is here

| File | What it is | Source |
|---|---|---|
| `nexus-banner.svg` | README banner: wordmark, phone, private link, PC | drawn; colours and glyph from the app's theme and launcher icon |
| `screenshots/android-home.png` | Home screen, connected | real capture, Nexus 1.4.0 on the owner's phone; machine name, device id, credential fingerprint, installation id and Tailscale address redacted |
| `screenshots/android-files.png` | Files, drives and places | real capture, 1.4.0; Windows user name redacted in every path |
| `screenshots/android-security.png` | Security, owner present | real capture, 1.4.0; nothing identifying on screen |
| `screenshots/windows-control-panel.png` | Local control panel on the PC, update status | real capture of the loopback page, 1.4.0; no token, pairing or device data visible |
| `screenshots/android-remote-control.pending.svg` | Placeholder | not a screenshot; replace with the capture below |

Phone captures are 540 px wide, with the status bar and navigation bar cropped. Redactions are solid
`#2A343F` blocks, so a redaction is visibly a redaction rather than an empty field.

## Still wanted

In order of value. One strong image beats several weak ones.

1. **`android-remote-control.png`** (replaces the placeholder in the README). The phone showing the
   PC desktop mid-control, ideally with a **UAC consent prompt** on screen being answered through
   Nexus. Use a clean desktop: no personal windows, files, browser tabs or notifications.
2. **`demo.gif`** (optional, under ~5 MB, 10–20 s). Phone → desktop control → UAC prompt answered →
   Files or Security. Record the phone screen (`adb shell screenrecord`), trim it, and export at
   around 360 px wide and 10–12 fps so the README stays fast to load.
3. **`android-incident.png`**. An incident with its timeline and an evidence item, from a **staged**
   test incident: arm the PC, touch the physical mouse yourself, and let a camera pointed at a blank
   wall record. Never use a real incident.
4. **`android-stream-quality.png`**. The quality panel of the remote screen (profile, resolution,
   frame rate, bitrate).
5. **`windows-tray.png`**. The tray menu, cropped to the menu itself so no other tray icons or
   taskbar content show.

## Redaction rules

Before committing any capture, check for and remove:

- machine name, Windows user name, and any personal path segment
- device id, installation id, credential fingerprint, pairing codes or QR codes
- Tailscale addresses and tailnet names
- evidence images, camera frames of real rooms, audio waveforms of real recordings
- file and folder names that are not generic
- notifications, the status bar, and other apps' windows
- log output, tokens, certificate thumbprints

Cover values rather than editing them into different values: an obviously redacted field is honest,
while a made-up hostname is a small fabrication. Strip metadata by re-encoding (PNG saved through an
image library carries none), and look at the final file at full size before committing it.

When a pending capture arrives, add it next to the placeholder, update the image reference in the
README, and delete the `.pending.svg`.
