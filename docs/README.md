# Nexus documentation

## Guides

| | |
|---|---|
| [Getting started](getting-started.md) | Build, install, Tailscale, pairing, the optional privileged parts, configuration |
| [Remote control](remote-control.md) | Gestures, the H.264 stream, cursors, input paths, the Secure Desktop |
| [Security monitoring](security-monitoring.md) | Arming, detection, evidence, alerts, live camera and microphone |
| [Files and clipboard](files-and-clipboard.md) | Browsing, ranged downloads, resumable uploads, clipboard |
| [Updating](updating.md) | Publishing a release, how the PC and the phone update, rollback |
| [Security model](security-model.md) | Network exposure, pairing, owner authentication, policy, privileged components |
| [Development](development.md) | Repository layout, building, tests, self-tests, protocol vectors |
| [Release history](history.md) | What each milestone added and what real hardware found |

## Research records

These are longer and written as engineering records. They explain what was tried, what was measured,
and why the result is what it is.

| | |
|---|---|
| [virtual-hid.md](virtual-hid.md) | Why a user-mode VHF driver was possible after all, how it was built and signed for one machine, and the four defects installing it exposed |
| [native-input.md](native-input.md) | UIPI, the elevated broker, `InjectTouchInput`, `uiAccess`, and why a virtual display was not built |
| [third-party-notices.md](third-party-notices.md) | Adapted source and its licence |

## Design history

| | |
|---|---|
| [design/SPEC.md](design/SPEC.md) | The cumulative specification for v0.1–v0.7; code comments cite its sections |
| [design/PLAN.md](design/PLAN.md) | The implementation plans for the same milestones, including what could not be verified at the time |

## Repository

| | |
|---|---|
| [public-readiness.md](public-readiness.md) | What was audited before the first push, what was fixed, and the checklist before any public release |

## Assets

[assets/](assets/) holds the images used by the README and these guides. [assets/README.md](assets/README.md)
lists which screenshots exist, which are still pending, and the redaction rules for adding more.
