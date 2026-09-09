# Third-party notices

Nexus ships no third-party binaries. This file records source that was adapted, and the licence it
came under.

---

## WinUHid — MIT License

**Used as:** reference, and one adapted file section.

**What was adapted.** The decorated-section layout of `src/driver/NexusHid/NexusHid.inf` — the split
between the Windows 10 (`10.0...19041`) and Windows 11 (`10.0...22000`) install sections, and the
`Include`/`Needs` directives that pull in the inbox VHF service (`hidvhf.inf`, `vhfservice`) and the
UMDF reflector upper filter (`wudfrd.inf`, `WUDFRD_UpperFilter`) — is adapted from
`WinUHid Driver/WinUHidDriver.inf`.

**Why that and not more.** That wiring is not Nexus-specific, is not documented in one place, and is
the part of a VHF INF that is genuinely hard to derive: Windows 11 requires the reflector declared as
an upper filter and Windows 10 does not, and a package that gets it wrong installs successfully and
then never starts. Rewriting it from scratch would have produced either the same lines or a bug.

**What was not taken.** No driver source, no client library, no installer code, no binaries, no
certificates. `Driver.c`, `NexusHidProtocol.h` and the whole control protocol are Nexus's own, as are
every identifier in the package: device name, hardware id, service name, provider, interface GUID,
vendor and product ids.

```
MIT License

Copyright (c) 2025 Cameron Gutman

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Source: <https://github.com/cgutman/WinUHid>

---

## LizardByte libvirtualhid — not used

Evaluated and rejected as a backend; see [`virtual-hid.md` §2](virtual-hid.md). No source was taken
and no licence was purchased. Recorded here so that the absence is deliberate and documented rather
than assumed.
