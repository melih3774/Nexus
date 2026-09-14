# Practical PC Assistant implementation pass

Starting point: local v0.1, commit 77e3dd6. Baseline build passes (8 existing platform
warnings); 173 non-Claude tests pass. Existing WPF application launched and inspected
through Windows UI Automation. Existing owner history is preserved.

## Decisions and acceptance

Keep the subscription-authenticated Claude CLI, task/session persistence, job ownership,
and approval broker. Add an official C# MCP SDK stdio server with a separate Windows
library. No listener, API billing, private Claude Desktop IPC, remote, or global install.
Every task gets the same computer tools. Native tools ask the existing broker directly;
the outer CLI hook delegates only our known MCP calls to that gate. A missing broker
fails closed. Unrecognized external MCP tools retain the conservative hook gate.

Use official Chrome integration where supported. Detect installed extension separately
from an actual successful browser call. Browser connection is not inferred from MCP
initialization. Windows control uses bounded Win32/UIA APIs and screenshot image blocks.
Visual input fallback is targeted, finite, cancellable and never crosses UAC boundaries.

AUTO allows observation, normal launch/open/window management and clipboard operations;
ASK gates mutation. Opaque UI input and browser actions whose consequence cannot be
classified require review, with narrowly described in-memory task grants where safe.
Never grant all shell commands. Owner-file reading should work in General tasks without
opening all filesystem mutation permissions. Keep higher impact operations gated.

Stream partial text into the transcript, persist coalesced text, deduplicate final text,
and preserve useful partial output on cancellation. Raw logs remain secondary. Add
capability diagnostics and an obvious stop shortcut. Optional startup remains off.

## Verification

Preserve baseline tests; add focused policy, argument, protocol and streaming regressions.
Exercise the actual stdio server, native fixture windows, clipboard restore, screenshot
image decoding, UIA/input/move, approval denial and cancellation. Exercise real Claude
mixed tasks using disposable files. Browser tests depend on an actual extension connection;
record blocked cases honestly. Finish with build, tests, local commits/tag, no remote.

Do not ship an elevated helper or audio stack at the expense of the core perception/action
loop. Document remaining daily-use limitations and next steps in the final report.

---

# v0.9 pass — observing, verifying, recovering (2026-09-07)

Starting point: local v0.8, commit c8a6e70. Baseline: 314 tests pass, 30 skipped.

## The problem this pass is about

PC Assistant could call tools and report their return values. It could not tell whether
anything actually happened. Two specific gaps made that unavoidable rather than a matter of
prompting:

1. Screen capture read visible desktop pixels, so observing an application meant first
   disturbing the owner's desktop, and observing an obscured one was impossible.
2. Coordinates carried no evidence of the observation they came from, so a click could land on
   whatever happened to be at a pixel several seconds later.

Everything else follows from fixing those two.

## Decisions

**Windows Graphics Capture as the primary window-capture mechanism**, with PrintWindow and
then a visible-region crop behind it, and the mechanism named in every result. The alternative
— always raising the target first — was rejected because it makes observation destructive.
Requires the Windows SDK projections, so `PcAssistant.Windows`, `PcAssistant.PcTools` and the
test project target `net10.0-windows10.0.26100.0`; the WPF app deliberately does not, and
references the tool server as payload only.

**Capture-anchored coordinates.** A capture returns a short-lived id recording the desktop
rectangle its image covers; input takes that id plus image coordinates and resolves them
against live geometry immediately before injecting. Resized, closed, minimized or
process-recycled targets are refused; a moved one is followed. Raw desktop pixels remain
available and remain the weaker claim.

**Consequence-based browser classification**, against a per-task record of tabs the task
opened and the labels on the page it has read — both from the task's own tool results. Page
labels are untrusted and can only cause an action to ask. Unknown targets ask.

**Measured capability reporting.** `--probe` in the tool server does the smallest real version
of each capability and reports Ready / Limited / Unavailable. "Ready when PC tools are ready"
was a statement about the build.

**No notification tool.** Measured three implementations, none rendered on this Windows build;
the reliable route needs persistent OS state that is the owner's decision. Documented rather
than shipped hopefully.

## Verification

Both mixed workflows passed against the real CLI: a WPF app edited, built, run, captured while
occluded, verified visually, driven through UI Automation and verified again; and a web project
edited, served, opened in Chrome through the official integration, and confirmed from the
rendered page. Three real everyday scenarios passed, including describing a window while
another window covered it, with the foreground provably unchanged.

Occlusion is asserted with real pixels — a red window under a blue one — so a silent
degradation to screen reading fails the suite rather than passing quietly.

## What the runs changed

Stopping a background command the task started was classified as an unrecognized tool and cost
an approval; it is ordinary work in AUTO now. One run reached for a machine-wide `taskkill` to
stop its own server, which the policy caught and the skill now forbids explicitly.
