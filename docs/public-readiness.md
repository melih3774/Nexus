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

## Before making the repository public

- [ ] Choose a license, or decide deliberately to publish without one
- [ ] Re-run this audit on the new commits since the date above
- [ ] Decide whether the accepted history items above are acceptable in public, or publish a fresh
      history
- [ ] Replace the pending screenshot, and check every image against the redaction rules again
- [ ] Enable private vulnerability reporting, and review Actions, Dependabot and branch settings
- [ ] Reconsider whether design records that describe this machine's security posture in detail
      (`docs/virtual-hid.md` §13, `docs/native-input.md`) should stay as written
