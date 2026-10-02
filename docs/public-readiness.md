# Public-readiness audit

The repository is private. This records what was checked before it was first pushed to GitHub, what
was fixed, and what to decide before it could ever become public. Sensitive values are described
here, never quoted.

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

- Commit `368d26d` contains the home path fixed above, which shows the developer's Windows user
  name. That name is already part of the GitHub account name.
- The items accepted on 2026-09-14 are unchanged.

### Local only, never to be pushed

The developer's clone keeps, from before the message rewrite of 2026-09-14, `refs/original/*`,
`refs/backup/*`, and two old session branches with their worktrees, whose commits still carry the
removed trailer. Push only `main` and tags by name; never `git push --all` or `--mirror`.

## Before making the repository public

- [x] Choose a license: Apache 2.0
- [x] Re-run this audit on the new commits since the date above (2026-10-02)
- [ ] Decide whether the history items above are acceptable in public, or publish a fresh history
- [ ] Replace the pending screenshot, and check every image against the redaction rules again
- [ ] Right after going public: enable private vulnerability reporting (SECURITY.md depends on it),
      Dependabot alerts, and branch protection on `main`; review Actions
- [ ] Reconsider whether design records that describe this machine's security posture in detail
      (`docs/virtual-hid.md` §13, `docs/native-input.md`, `src/Assistant/docs/RESEARCH-*.md`)
      should stay as written
