# Security policy

Nexus can control a Windows PC, read its files and turn on its camera, so security reports are taken
seriously even though it is a personal project.

## Reporting a vulnerability

Please **do not open a public issue**. Contact the repository owner privately through GitHub. If the
repository is public and private vulnerability reporting is enabled, use **Security → Report a
vulnerability**.

A useful report says which component is affected (PC Core, the Android app, the HID driver,
SecureCapture, the updater), what an attacker needs beforehand (network position, a paired phone,
local access), and what they gain.

## Supported versions

Only the latest release on the default branch. Nexus has one deployment and there are no maintained
older branches.

## Scope

In scope, especially:

- reaching PC Core from anything other than the paired phone over Tailscale, or from loopback without
  the session token
- acting with owner authority without the owner's biometric (`FullRemote` without a valid owner
  signature)
- escaping the filesystem boundary, or reaching Nexus's own state and install folders through Files
- abusing the HID driver control interface, the SecureCapture service or pipes, the input broker pipe,
  or the updater's elevated step
- capturing the camera or microphone without the on-screen indicator
- anything that makes Nexus weaken Windows security (UAC, Secure Desktop, Memory Integrity, driver
  signing)

Out of scope: attacks that already require administrator rights on the PC, or both the owner's phone
and the ability to pass its biometric or screen-lock prompt.

The design these reports would be measured against is described in
[docs/security-model.md](docs/security-model.md).
