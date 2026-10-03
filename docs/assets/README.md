# Repository assets

Images used by the top-level README and the guides. Everything here is one of three things, and is
labelled as such wherever it appears:

- **a real capture** of Nexus, redacted as described below;
- **a drawing** that is clearly not a screenshot (the mark and the diagrams);
- **the launch animation**, which recreates the app's screens with their real strings. It looks like
  the product but is not a recording of it, so every place that shows it says so.

## What is here

| File | What it is | Source |
|---|---|---|
| `nexus-mark.svg` | The mark above the README title | drawn; the app's launcher icon (`ic_launcher_background` and `ic_launcher_foreground`) cropped to its visible area |
| `nexus-launch-animation.webp` | README hero: the launch animation, silent, looping | animated WebP, 1280×720 at 15 fps, rendered from the launch animation's source and starting on its first title. One line differs from the launch video: the Assistant task's status reads `Working` / `Done` without a Workspace label, because a task started from the phone is always a General task |
| `nexus-launch-poster.jpg` | The hero for viewers who ask for reduced motion | one frame of the same animation (the phone typing into Notepad) |
| `nexus-attribution.svg` | Diagram: whose input raises an incident on an armed PC | drawn from [security-monitoring.md](../security-monitoring.md#detection) |
| `nexus-architecture.svg` | Diagram: phone, tailnet, PC Core, the privileged parts, the Assistant, and what leaves the PC | drawn from [security-model.md](../security-model.md) and [ai-assistant.md](../ai-assistant.md) |
| `social-preview.png` | 1280×640 card for links to the repository | the animation's end card. Not used in any page: upload it in the repository's **Settings → General → Social preview** |
| `screenshots/android-home.png` | Home screen, connected | real capture, Nexus 1.4.0 on the owner's phone; machine name, device id, credential fingerprint, installation id and Tailscale address redacted |
| `screenshots/android-files.png` | Files, drives and places | real capture, 1.4.0; Windows user name redacted in every path |
| `screenshots/android-security.png` | Security, owner present | real capture, 1.4.0; nothing identifying on screen |
| `screenshots/windows-control-panel.png` | Local control panel on the PC, update status | real capture of the loopback page, 1.4.0; no token, pairing or device data visible |

Phone captures are 540 px wide, with the status bar and navigation bar cropped. Redactions are solid
`#2A343F` blocks, so a redaction is visibly a redaction rather than an empty field.

The diagrams are SVG with their own dark background, so they read the same in GitHub's light and dark
themes. Their text uses the system font stack (Segoe UI, Roboto, Helvetica Neue, Arial) and has room
for the widest of those. When a fact in a guide changes, change the diagram with it: the text is
plain `<text>` elements, and the `aria-label` on the root element carries the same content for screen
readers.

The animation's source, renderer and full-length video with sound are kept out of the repository. The
WebP is silent and has no player controls, because GitHub does not play video files committed to a
repository. A video uploaded through GitHub's own editor does play, if one is wanted later.

## Still wanted

In order of value. One strong image beats several weak ones.

1. **`android-remote-control.png`**, for the README's row of real captures. The phone showing the PC
   desktop mid-control, ideally with a **UAC consent prompt** on screen being answered through
   Nexus. Use a clean desktop: no personal windows, files, browser tabs or notifications.
2. **`android-assistant.png`**. The AI Assistant screen with a finished task that did real work worth
   showing, and names no personal file or folder. A trivial question looks worse than no capture.
3. **`android-incident.png`**. An incident with its timeline and an evidence item, from a **staged**
   test incident: arm the PC, touch the physical mouse yourself, and let a camera pointed at a blank
   wall record. Never use a real incident.
4. **`demo.gif`** or a WebP (optional, under ~5 MB, 10–20 s). Real footage this time: phone → desktop
   control → UAC prompt answered → Files or Security. Record the phone screen (`adb shell
   screenrecord`), trim it, and export at around 360 px wide and 10–12 fps so the README stays fast
   to load.
5. **`android-stream-quality.png`**. The quality panel of the remote screen (profile, resolution,
   frame rate, bitrate).
6. **`windows-tray.png`**. The tray menu, cropped to the menu itself so no other tray icons or
   taskbar content show.

Captures of the current version would also replace the 1.4.0 ones: the Home screen has gained the AI
Assistant card since then.

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

When a wanted capture arrives, add it to the row of real captures in the README, with its version in
the caption.
