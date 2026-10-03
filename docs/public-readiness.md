# Public-readiness audit

This records what was checked before the repository was first pushed to GitHub, as a private
repository, and again before it was made public: what was found, what was fixed, and what was
accepted. Sensitive values are described here, never quoted.

## Audit of 2026-09-14

**Scope:** every file in every commit reachable from any branch or tag (62 commits, about 400 paths),
all commit and tag messages, and the README, guides and images added in the documentation pass.

**Searched for:** private keys and certificates, signing material and keystores, APKs and other
binaries, tokens and passwords, pairing codes, device and installation ids, credential
fingerprints, Tailscale addresses and tailnet names, LAN addresses, host names, Windows user names and
home paths, email addresses, phone models and serial numbers, logs, evidence, and runtime state files.

### Clean

- No private key, certificate, keystore, APK, driver binary, log, evidence file or state file was
  ever committed. `.gitignore` has excluded runtime state and signing material from the start.
- No real pairing code, device id, installation id or Tailscale address appears in any file or
  message. The pairing payloads, proofs and `100.64.0.0/10` addresses in tests and protocol vectors
  are synthetic.
- Commit authors and tag authors use a GitHub `noreply` address.
- The screenshots in `docs/assets/` were re-encoded without metadata, and identifying fields are
  covered with visible redaction blocks ([assets/README.md](assets/README.md)).

### Fixed in the current tree

- **Test fixtures** used the development PC's host name, a Windows home path containing the owner's
  user name, a phone display name containing the owner's first name, and the model string of the
  owner's phone. They are now generic values (`DESKTOP-TEST01`, `C:\Users\Example\Desktop`,
  `Owner's Phone`, `Example Phone 1`). Test behaviour is unchanged: the same literal is set and
  asserted.
- **An example path** in the specification used the owner's user name. It now reads `you`.

### Commit messages rewritten before the first push

Every commit message ended with a co-author trailer that the owner chose not to publish. Before
anything was pushed, that one line was removed from all 62 commit messages. Nothing else changed:
every commit keeps its tree, author, committer and dates, the milestone tags point at the rewritten
commits with their messages intact, and the commit hashes cited in these documents were updated to
the new ones. Because no remote existed yet, nobody holds the old hashes.

### Remaining in history, accepted

- **The same low-severity fixture strings** exist in older commits of three test files and in
  `docs/design/SPEC.md` (previously `SPEC.md`). They identify a first name that is already part of
  the GitHub account name, a phone model, and a host name that is not reachable from outside the
  owner's network. Removing them would mean rewriting file contents in most of the history, which is
  not justified by what they reveal. The message-only rewrite above deliberately left trees alone.
- **Commit `fc84307`'s message** records an eight-character credential fingerprint measured while
  debugging. The fingerprint is a domain-separated SHA-256 of the pairing secret truncated to 32
  bits: it cannot be reversed into the secret, is not enough to forge a proof, and belongs to a
  credential that has since been replaced by re-pairing. It is recorded here rather than rewritten.

If the repository is ever made public from a fresh, squashed history instead, both items disappear.
That is the only situation in which rewriting file contents is worth it.

## Audit of 2026-10-02

**Scope:** the 16 commits from the first audit to `44ee57e` (229 files, most of them the PC Assistant
import in `368d26d`), all commit and tag messages, the 14 tags and the refs on the remote, the full
tree, the GitHub settings, and the product as a stranger meets it: a fresh clone built and tested by
following the README and getting-started literally, and every claim the documentation makes checked
against the code.

### Clean

- No key, certificate, token, password, real pairing code, device or installation id, credential
  fingerprint, Tailscale address, tailnet name, host name, phone model or personal email in any new
  commit, message or tag. Authors and taggers use the `noreply` address; no message carries a
  co-author trailer. The four screenshots are unchanged and still carry no metadata.
- A fresh clone builds (`dotnet build`, the driver package, the Android APK), and the .NET (1201) and
  Android (253) suites pass; PC Assistant's passes too, though one test that opens real windows
  needed a second run. No build input depends on the developer's machine.

### Fixed in the current tree

- **No license.** Nexus is now Apache 2.0 (`LICENSE`).
- **A home path** under the developer's Windows user name in `src/Assistant/docs/RESEARCH-BROWSER.md`
  is `%LOCALAPPDATA%` now. The old value remains in commit `368d26d` (see below).
- **SECURITY.md** named no channel that works: GitHub has no private messages. It now names
  private vulnerability reporting, which can only be enabled once the repository is public.
- **A stranger could not install the HID driver.** The guide said to add `-Apply`, which threw; the
  plan it fell back to called tools that are not on PATH and described the certificate steps in
  prose. The plan now prints every command with its tool path, a key of the PC's own included.
- **No uninstall.** getting-started.md now removes every part Nexus can add, in order.
- **Claims that were not true:** Windows 10 (the driver needs UMDF 2.33, which only Windows 11 has);
  "no cloud, no accounts" (Tailscale needs an account and may relay; PC Assistant sends prompts,
  screenshots and files to Anthropic, now disclosed); "only two privileged components" (also the
  input broker, the updater's elevated step and PC Assistant's privileged helper); "the phone cannot
  turn monitoring off" (it cannot change monitoring settings, but an owner session can disarm).
- **Build prerequisites** a stranger had to discover: `JAVA_HOME` and `ANDROID_HOME` for Gradle, the
  .NET SDK 10.0.400 that PC Assistant needs, and the pinned Visual Studio 2022 (v143) and WDK
  10.0.26100 for the driver.

### Remaining in history

- Commits `368d26d` to `6f3761b` (ten of them) contain the home path fixed above, which shows the
  developer's Windows user name. That name is already part of the GitHub account name.
- The items accepted on 2026-09-14 are unchanged.

### Local only, never to be pushed

The developer's clone keeps, from before the message rewrite of 2026-09-14, `refs/original/*`,
`refs/backup/*`, and two old session branches with their worktrees, whose commits still carry the
removed trailer. Push only `main` and tags by name; never `git push --all` or `--mirror`.

## Audit of 2026-10-03

**Scope:** the complete history that would become public, not only the new commits: every commit,
tree and blob reachable from `main` at `f005e91`, `origin/main` and the 14 tags (83 commits, 1177
blobs), every commit and tag message, and every author, committer and tagger. The commits made after
it, before the push, were scanned the same way, with gitleaks and for the same real values, and
added nothing. gitleaks 8.30.1 ran over the same set, and targeted scanners searched every blob, in
UTF-8 and UTF-16, for this PC's and this phone's real values: host and user name, profile path,
Tailscale addresses and tailnet, the phone's serial and model, adapter addresses, saved Wi-Fi names,
and the ids, keys and tokens in PC Core's real state. Every image ever committed, deleted ones
included, was looked at.

### Clean

- No private key, certificate, keystore, token, password or real pairing secret, in any blob or
  message. gitleaks's eight hits are the protocol vectors' synthetic secrets (the bytes 01..20 and
  20..01).
- No log, state file, evidence, crash dump or test result was ever committed. The only executable
  ever committed is the Gradle wrapper jar, whose SHA-256 matches the official 9.4.1 checksum. The
  largest blob is the launch animation (2.0 MB).
- All identities use the GitHub `noreply` address; no message carries a co-author trailer. No image
  carries metadata.

### Fixed in the current tree

- **A real installation id.** `HomeScreenTest.kt` used a real PC Core installation id, since replaced
  on this PC, as its fixture. The audit of 2026-09-14 said none appeared; it now is a synthetic GUID.
- **A second pass over claims:** "no cloud, no account and no server" in security-monitoring.md;
  "local only" and "no networking" in PC Assistant's README (Claude Code talks to Anthropic); "the
  only part that ever asks for UAC" in development.md; wording that implied a published release
  signs the driver (it leaves an unsigned package out); a sentence in updating.md that described the
  maintainer's phone rather than the reader's; and the uninstall steps now include the release
  signing key. The app and the control panel had the same kind of wording ("uses no cloud
  service", "the Android app that does not exist yet"), fixed too.
- **A click on the Secure Desktop where the owner did not aim.** Taking real captures on the phone found it:
  with Direct acting as a mouse, a tap at a UAC prompt is a move, which the Secure Desktop refuses,
  and a click, which the Nexus HID mouse delivered at wherever the pointer rested. A tap on No could
  have answered Yes. An elevated window in front did the same on a PC without the input broker. PC
  Core now withholds that click until a move is delivered
  ([remote-control.md](remote-control.md#the-secure-desktop)).

### Remaining in history, accepted

- **The installation id above** is in every commit up to the one that replaced it. It is not a
  secret, cannot be used without the pairing secret, and belongs to an installation this PC no
  longer has.
- **The developer's first name** is PC Assistant's `<Authors>` and `<Company>` in
  `src/Assistant/Directory.Build.props`, in the current tree too. It is part of the GitHub account
  name and is kept.
- **Machine-local identifiers in tests:** the names of this PC's network adapters and one link-local
  address (`TailscaleDetectionTests.cs`), and three of its PnP device instance paths
  (`NexusHidResolutionTests.cs`, and one in a comment in `NexusHidPnpAncestry.cs`). None is routable
  or a serial number.
- Commit and tag times carry the developer's time zone (+03:00).

The audit's recommendation is to publish the existing history as it is: nothing in it is a
credential, and nothing in it can be used to reach or impersonate the PC or the phone. A fresh,
squashed history would remove the items above, and is the owner's choice, not a requirement.

### A fresh install, followed literally

A fresh clone was built and tested on the development PC, then installed and removed in Windows
Sandbox, following the README and getting-started.md as written. The clone built cleanly, and the
suites passed: PC Core 1201 of 1201 at `f005e91` (1215 with the fixes since), Android 253 of 253,
and PC Assistant 381 of 419, with its 36 opt-in live tests skipped and two failing only because the
sandbox runs elevated (that test passes on the PC, unelevated) and its image opens no Character Map
window.

- **Base install and uninstall** work as written. They left SecureCapture's log in
  `%ProgramData%\Nexus`, and the SecureCapture step failed once because `--uninstall` returns before
  the service process ends. The guide now covers both, and names what Windows itself keeps.
- **The HID driver on a clean machine:** proven, after a fix. The plan as printed stopped at a
  Windows Security prompt because its trust step had silently failed. With the fix it ran in one
  pass: the per-PC key, the signed catalog, the device, the driver answering 0.9.2.4, and Windows
  enumerating the mouse, keyboard and touch screen once PC Core connected. The uninstall plan then
  left no certificate, package or device behind.
- **Not provable in a sandbox:** PC Core adopting those devices (`Virtual HID: Ready`). The sandbox
  has no console session, and Raw Input in its remote session never listed the devices. That, and a
  machine with Secure Boot on, needs a real second PC; Memory Integrity is on on the development PC.

### Known issues, not fixed in 1.6.0

- Starting Nexus while it already runs: the second copy overwrites the control panel's token file,
  briefly creates a second set of virtual devices, then fails to take port 47811 and exits with an
  error report. A single-instance guard has to be designed with the updater's handoff in mind, so it
  is left for a later release; getting-started.md says to start Nexus once.
- `PcCore.SecureCapture.exe --uninstall` returns before the service process has ended (the guide
  waits for it).
- PC Core's console window shows the control panel's session token in its log.
- On a congested link, a click can overtake the move sent before it, because a connection handles up
  to four requests at once; the Secure Desktop guard cannot help then. At a UAC prompt, use
  Touchpad.
- PC Assistant's risk check reads PowerShell's `Format-List` and `Format-Table` as the disk command
  `format`, so ASK mode asks about them as high-impact; and the app shows Claude's Markdown as
  plain text.

## Making the repository public

The repository was made public on 2026-10-03, with the history as it is and the posture records as
written.

- [x] Choose a license: Apache 2.0
- [x] Re-run this audit on the new commits (2026-10-02), then on the complete history (2026-10-03)
- [x] Take the screenshot placeholder off the README (2026-10-03, with the launch README)
- [x] Check every image against the redaction rules again (2026-10-03: every image ever committed)
- [x] Repository description, topics and Dependabot alerts (2026-10-03, while private)
- [x] Keep the history as it is
- [x] Keep the records that describe this machine's security posture as written
- [x] Right after going public (2026-10-03): private vulnerability reporting, secret scanning and
      push protection, and branch protection on `main` (no force-push, no deletion). There are no
      Actions workflows to review
- [ ] Upload `docs/assets/social-preview.png` as the social preview (Settings → General); GitHub has
      no API for it
