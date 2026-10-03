# PC Assistant — the Nexus AI Assistant component

A personal Windows desktop assistant built around your locally installed Claude Code.
Claude Code does the thinking and the work; PC Assistant is the shell that makes it feel
like an assistant instead of a terminal.

It was developed as its own repository and is now part of Nexus, kept here in the layout it was
written in: its own solution (`PcAssistant.slnx`), its own `Directory.Build.props` and `global.json`,
its own `src/` and `tests/`. Build and test it from this directory
(`cd src/Assistant && dotnet build -c Release`, `dotnet test`). It is still a separate process from
PC Core, reached only over the named pipe described in
[docs/NEXUS-BRIDGE.md](docs/NEXUS-BRIDGE.md); PC Core does not reference its assemblies or start it.
A Nexus release builds it, and the Nexus updater installs it at
`%LOCALAPPDATA%\Programs\Nexus\Assistant\` and keeps it current
([../../docs/updating.md](../../docs/updating.md#pc-assistant)).
The `PcAssistant.*` project and namespace names are unchanged on purpose.

**Version 0.10.1** · Windows only · one owner · no network listener, no telemetry, no API
keys. Claude Code, signed in with your Claude account, sends Anthropic your instructions and the
screenshots and files it works with
([what leaves the PC](../../docs/ai-assistant.md#what-leaves-the-pc)).

Version 0.10.1 adds one thing: before a Nexus update replaces this copy, it asks. PC Assistant closes
only when no task is running and no approval is waiting, and says no otherwise; the update then waits,
and nothing is killed.

Version 0.10 adds one local client: **Nexus**, the owner's own phone link to this PC. With the
option ticked in Capabilities and one Nexus executable trusted, an owner who has authenticated on
their phone can list and open tasks, start General tasks, follow up, stop, switch AUTO/ASK and
answer ordinary approval cards. It is a named pipe for this account, not a network listener, and
nothing decides differently because a request arrived through it — the same policy, the same cards,
the same database. Administrator approvals still have to be answered here.
→ [docs/NEXUS-BRIDGE.md](docs/NEXUS-BRIDGE.md)

Version 0.9 is about the difference between calling a tool and knowing what happened.

PC Assistant can now **observe one window on its own** — Windows Graphics Capture reads a
window's composed surface, so an application sitting behind three others comes back as its own
current content, and nothing is focused, raised or moved to get it. Coordinates derived from
that image are tied to the capture that produced them and re-checked against the window's live
geometry before anything is clicked. Ordinary browsing stopped interrupting you, while the
actions that reach other people still stop every time. The capabilities panel reports what was
measured on this PC rather than what shipped in the build. And there are the everyday controls
an assistant is actually asked for: volume, battery, network, Recycle Bin, and opening an
application, a Settings page or a folder by the name you would say out loud.

Version 0.8 gave administrator work a real path. PC Assistant stays unelevated forever; an
owner-installed LocalSystem helper performs individually approved administrator operations
using **your own account's elevated token, in your own desktop session — never SYSTEM**. UAC
appears once, when you install the helper, and not again for approved operations. Version 0.7
made screen interaction work at all: an approved click now takes the target window's
foreground itself, inside the same operation, instead of losing it to the approval card that
authorised it. Version 0.6 was an interface pass over the same engine: a light/dark/system
theme, a real Create New Project dialog, project-folder wording that says what it does,
inline feedback instead of silent failures, and empty states.

Version 0.5 adds an actual PC perception/action loop to the original task shell. In the
same General or Workspace conversation Claude can inspect screens and windows, use the
official Chrome integration, inspect native controls, operate apps, work with clipboard,
find recent owner files, and inspect processes/system state.

---

## Run it

```powershell
dotnet run --project src\PcAssistant.Desktop
```

Or build once and launch the exe:

```powershell
dotnet build -c Release
.\src\PcAssistant.Desktop\bin\Release\net10.0-windows\PcAssistant.Desktop.exe
```

Requires the .NET 10 SDK (built against 10.0.400) and a locally installed, authenticated
Claude Code. PC Assistant finds `claude.exe` automatically — including the copy bundled
with the Claude desktop app, which is not on PATH.

---

## Using it

**New Task** asks for three things and then your first message:

- **Type** — *General* for thinking, planning, and research (no project needed), or
  *Workspace* for real work in a project directory.
- **Mode** — *Auto* or *Ask*.
- **Workspace** — only for Workspace tasks, chosen from inside an approved projects
  folder. **Add Projects Folder…** approves a folder, **Select Workspace…** picks an
  existing project inside one, and **Create New Project…** makes a new one. Each reports
  what it did on a line under the picker, in green or red; approving a folder never
  silently changes which workspace the task will use.

You never type a title; it is derived from your message.

Then you watch Claude work through a structured progress list, answer its questions in the
same conversation, approve or deny anything that needs it, and send follow-ups. Old tasks
stay in the list and can be reopened.

| Button | What it does |
|---|---|
| **Send** | sends a follow-up in the same task and the same Claude session |
| **Stop** | terminates the Claude process tree this task owns, and nothing else |
| **Raw Log** | the optional diagnostic view of raw provider output |
| **Capabilities** | measures what this PC can actually do — capture, UI Automation, clipboard, audio, power, network, applications — and shows Claude, browser-extension and privileged-helper state. Also where **Start with Windows** lives, and where the privileged helper is installed or removed |
| **Continue as New Task** | branches a fresh task carrying a short handover summary |
| **Theme** | cycles System → Dark → Light; the choice is remembered |

Old tasks pile up, so the list has a **search box**: type part of a title or a workspace name
to narrow it. The conversation you have open stays open and stays selected even when the
search excludes it, and starting a new task clears the search rather than hiding what you just
created.

`Enter` sends, `Shift+Enter` adds a newline. `Ctrl+Alt+Pause` is a global emergency stop
for all PC Assistant tasks; the UI reports if Windows could not register that shortcut.

### Projects folders

PC Assistant has no filesystem scope by default. Use **Manage Projects Folders…** to
approve one or more folders; workspaces can then only be chosen from inside them. Windows
and system directories are always rejected, and a folder name that would traverse out of an
approved folder is rejected too.

### Theme

The title bar button cycles **System → Dark → Light**. *System* follows the Windows
apps-use-light-theme setting and re-checks it whenever PC Assistant is activated, so
changing Windows' theme while the app is open is picked up on the next focus.

---

## AUTO and ASK

**AUTO** — ordinary project and PC work proceeds without interrupting you: reading,
searching, editing workspace files, build/test, observation, normal app/window operations,
clipboard use, and text entry that does not submit. Higher-impact or ambiguous interaction
still asks.

**ASK** — reasoning, reading, searching, and inspecting stay free. Every file change and
every command is put to you first.

### Looking at your PC

**Observing does not disturb anything.** `capture_window` reads a window's own composed
surface through Windows Graphics Capture, so an application behind three other windows comes
back as its current content — nothing is focused, raised, restored or moved to get it. That is
the difference between "look at Discord" and "look at Discord, and by the way your desktop
just rearranged itself".

Where that mechanism cannot be used, PC Assistant falls back to asking the window to render
itself, and only then to reading visible desktop pixels. **The result always says which one it
used** and whether anything is currently on top, so an image that includes another window is
never presented as if it were the application. A minimized, cloaked or capture-protected
window is refused with the reason instead of given a misleading image.

### Screen interaction and sign-in

Acting on a window is different from looking at one: input goes wherever Windows says the
focus is, so an approved click brings the target to the front as part of the same operation.
That one approval covers the whole thing, so approving a click never leaves the target behind
PC Assistant. A window Windows refuses to activate is reported after two attempts and then
left alone rather than asking you again and again.

**Coordinates are tied to the picture they came from.** A capture hands back an id; clicking
uses that id plus a point in the image, and the point is resolved against the window's live
geometry at click time. A window that was resized or replaced refuses the click and asks for a
fresh look; one that merely moved is followed. The failure this removes is the ordinary one —
reasoning about a screenshot for a few seconds and then clicking where something *used to be*.

**Passwords are yours.** Claude may open a sign-in page and fill ordinary fields, but password,
one-time-code and recovery fields are refused by the tools. It pauses, tells you the sign-in is
ready, and continues the task after you have signed in yourself.

### Browsing

Ordinary browsing does not interrupt you in AUTO: navigating, following links, opening tabs,
scrolling, searching, reading a page, and typing into a field.

These always ask, in both modes — sending a message, posting, publishing, submitting a form,
buying, paying, booking, deleting, uploading a file, running script inside a page, changing an
account, and accepting terms. So does any click PC Assistant cannot identify, such as one on
bare coordinates: unknown is treated as consequential.

Tabs the task opened are its own to close. Tabs you had open are not.

You are asked in **both** modes for: paths outside the workspace, system changes and
package installs, administrator elevation, destructive Git (push, commit, reset, clean,
rebase, remote), process control, and bulk deletion or other hard-to-undo commands.

This is enforced by a Claude Code `PreToolUse` hook that PC Assistant answers, not by
asking Claude nicely. PC Assistant never passes `--dangerously-skip-permissions`.

### Everyday controls

Volume (read and set, read back from the device rather than echoed), battery and power source,
network adapters and addresses, Wi-Fi network name, and what is in the Recycle Bin — which PC
Assistant will show you but never empty.

Opening things works by the names you would say: *"open Discord"*, *"open Bluetooth
settings"*, *"open the Downloads folder"*, *"open this folder in VS Code"*. Settings pages use
documented `ms-settings:` addresses rather than guesses about Explorer internals. **A launch is
not a claim**: PC Assistant waits for a real window and tells you which one appeared, or says
plainly that none did. Shells and script hosts are not openable by name; commands go through
the shell tools, which are classified and approved on their own terms.

### Start with Windows

Off unless you turn it on, in **Capabilities**. It is the ordinary per-user startup entry — no
service, no scheduled task, no administrator rights — and you can remove it from there or from
Windows' own **Settings → Apps → Startup**.

---

## Administrator operations

PC Assistant runs unelevated and always will. When work genuinely needs Administrator rights
it says so, shows you exactly what it wants to do, and performs it — after you allow it —
through a small privileged helper you install deliberately.

**What the helper gives Claude is your own account's elevated token, in your own desktop
session. Not SYSTEM.** The ceiling on the whole feature is "what you could have done by
clicking Yes on a UAC prompt".

### Setting it up

**Capabilities → Install Privileged Helper…**. Windows asks for administrator approval on
this PC, once. That installs `PcAssistantElevation`, a LocalSystem service under
`C:\Program Files\PcAssistant\Elevation`, and starts it.

After that, approved administrator operations do **not** prompt again — which is what makes
this usable when you are away from the machine. **Uninstall Privileged Helper** stops and
deletes the service and removes everything it installed.

Nothing is installed or started on your behalf. PC Assistant never enables it just because it
launched.

### What it can do

| | |
|---|---|
| **Open an Administrator terminal** | a visible elevated PowerShell / pwsh / cmd on your desktop |
| **Run a command elevated** | one program plus an argument list; output and exit code come back |
| **Launch a program elevated** | an installer or GUI tool, visible, left running |
| **Manage a Windows service** | start, stop, restart or query one named service |

Arguments are a list, never a shell string, and are never re-split by a shell.

### What you see before it happens

```
ADMINISTRATOR OPERATION — Run a command as Administrator
C:\Windows\System32\whoami.exe /groups

Privilege    Administrator — your own account's elevated token, in your
             desktop session. Not SYSTEM.
Effect       Runs with your administrator token. Output and exit code are
             reported back.
Claude says  I need to confirm the helper really produces an elevated token.
Task         Set up the toolchain

                                          [ Deny ]  [ Allow once ]
```

**You are asked every single time, in both AUTO and ASK.** There is no "Allow for this task"
for administrator operations, and no persistent grant of any kind. A denial means *nothing was
sent to the helper* — not that something was sent and refused.

### What it will not do

- It will not run anything as SYSTEM.
- It will not accept a request from another account, from session 0, from a low-integrity
  process, or from the network.
- It refuses commands that would change UAC or elevation policy. That is a guardrail against
  a mistaken or injected request, not a claim to understand arbitrary PowerShell — make such
  a change yourself if you really mean it.
- It will not stop or restart itself; use Uninstall.
- It will not terminate an installer mid-write. A `run` operation that looks like an install
  is not cancellable, because a half-configured system is worse than a slow one.

### What Claude cannot do afterwards

Two real Windows boundaries, reported rather than worked around:

- **It cannot drive the elevated window it opened.** Measured on this machine against a real
  elevated console: the window can be *enumerated* (handle, title, bounds) and brought to the
  foreground, and it cannot be read or controlled. UI Automation returns the window node and
  nothing inside it — the elevated provider's tree is not readable. Minimizing it fails.
  `SendInput` is accepted by the API but delivery to a higher-integrity window is not something
  the unelevated side can confirm, so it is never reported as typed. Claude can see that the
  window opened; you drive it. Use an elevated `run` when the output has to come back.
- **It cannot see or answer a UAC prompt.** While one is on screen there is no foreground
  window and screen capture fails outright. If the one-time install needs your approval,
  Claude should say *"Administrator approval is waiting on the local PC."*

See [elevation research](docs/RESEARCH-ELEVATION.md) for the architecture, the options that
were rejected, the IPC and identity model, and the measured Secure Desktop behaviour.

An approval card shows the action, the exact command or path, why it is being asked, and
its risk category — you never have to open the raw log to know what you are approving.
No answer within the timeout is treated as a denial. Safe, precisely scoped requests can
offer **Allow for this task**; these in-memory grants end on Stop or app exit and never
grant all shell or generic UI/browser interaction.

---

## Nexus bridge

If you use **Nexus** to reach this PC from your phone, PC Assistant can serve it. Turn it on in
**Capabilities**. Then, when Nexus first connects, press **Trust this Nexus** for the executable shown.
From then on Nexus's owner-authenticated phone can list and open tasks, start General tasks, send
follow-ups, stop tasks, switch AUTO/ASK, and answer ordinary approval cards. Everything runs under the
same policy and cards as at the PC. Administrator approvals stay on the PC.

It is a local named pipe, not a network listener, and it exists only while the option is on. The pipe
admits only your account, and only the one Nexus executable you trusted is served. Protocol, trust model
and what was measured: [docs/NEXUS-BRIDGE.md](docs/NEXUS-BRIDGE.md).

---

## How it is wired

```
Desktop UI → Assistant Core → Claude Code Bridge → claude.exe
                                     ├→ official Claude in Chrome
                                     └→ local stdio MCP → native Windows library
```

The UI never manages a Claude process. Core never sees a CLI flag.

**Integration mechanism** (verified with Claude Code **2.1.260**), one process per turn:

```
claude -p --output-format stream-json --verbose --include-hook-events
       --include-partial-messages --chrome
       --permission-mode manual --settings <generated-hooks.json>
       --strict-mcp-config --mcp-config <generated-pc-tools.json>
       --session-id <uuid>        # first turn
       --resume <uuid>            # every follow-up
       --append-system-prompt <PC Assistant behaviour>
```

with the workspace as the process working directory and the prompt written to stdin.
`stream-json` partial text is coalesced into one live transcript message and persisted in
bounded checkpoints. Assembled/final events are deduplicated; malformed events only reach
the raw log.

The native MCP helper has no listener and cannot operate without the per-task approval
pipe. It exposes bounded structured tools for per-window and monitor capture, windows,
clipboard, process/system state, audio, power, network, Recycle Bin, recent files,
application and Settings discovery/opening, UI Automation search and actions, and a
foreground-verified SendInput fallback whose coordinates are anchored to a capture. Password
controls and UAC secure desktop are excluded. See [Windows research](docs/RESEARCH-WINDOWS.md)
and [browser research](docs/RESEARCH-BROWSER.md).

**Window capture**: `capture_window` uses **Windows Graphics Capture** — a
`GraphicsCaptureItem` created for the HWND, a free-threaded `Direct3D11CaptureFramePool`, and
one frame copied out through `SoftwareBitmap` and the PNG encoder. That reads the window's own
composed surface, which is what makes an occluded window observable without touching the
desktop. Measured here: a WGC frame covers `DWMWA_EXTENDED_FRAME_BOUNDS`, **not**
`GetWindowRect` — 1111x806 at (132,125) versus 1125x813 at (125,125) for the same window — so
that frame rect is what every derived coordinate is resolved against. PrintWindow with
`PW_RENDERFULLCONTENT` and then a visible-region crop sit behind it, and the result names which
one produced the image.

**Coordinate anchoring**: a capture returns a short-lived id recording the desktop rectangle
the image covers. `input` takes that id plus image coordinates and resolves them against the
window's live geometry immediately before injecting, after the foreground has been taken. A
closed, minimized, recycled or resized target is refused; a moved one is followed.

**Enforcement**: a `PreToolUse` hook runs `PcAssistant.HookHost.exe`, which asks PC
Assistant over a per-task named pipe (ACL'd to your user account) and returns the decision.
It holds no policy itself. If it cannot reach PC Assistant it denies, and if the helper is
missing the bridge refuses to start the turn rather than running unguarded.

**Browser policy**: browser calls are classified by consequence rather than by tool name,
against a per-task record of which tabs the task opened and what the current page's elements
are labelled — both fed from the task's own tool results. Page labels are untrusted input and
can only cause an action to ask, never to proceed.

**Capability probe**: `PcAssistant.PcTools.exe --probe` is a read-only self-check that captures
a window, reads a control tree, reads the clipboard and reads the audio endpoint, and reports
Ready / Limited / Unavailable with what it actually tried. The Capabilities panel runs it; a
helper that does not answer is reported as *not measured*, never as working.

**Cancellation**: the Claude process is put in a Windows job object with
`KILL_ON_JOB_CLOSE` before it can spawn anything. Stop kills exactly that tree. A Claude
Code session you started yourself in a terminal is never touched.

**The skill**: `.claude/skills/pc-assistant/SKILL.md` is the single versioned source of PC
Assistant's conduct — concise updates, no narration of routine tool calls, useful
questions, planning kept separate from execution, honest failure reporting, and no
automatic Git commits or pushes. Its body is injected via `--append-system-prompt` on every
turn, because that behaviour has to be always-on and skills are invoked on demand.

---

## Where your data lives

```
%LOCALAPPDATA%\PcAssistant\
├── pcassistant.db     tasks, messages, normalized events
├── settings.json      approved projects folders, last mode, theme, timeouts, Nexus bridge
├── general\           working directory for General tasks
├── rawlogs\           bounded per-task provider logs (8 MB cap, rotates once)
└── runtime\           generated hook settings handed to Claude Code; nexus-bridge.log
```

**No Claude credentials are stored.** The only Claude-related value kept is the session id
used for `--resume`. PC Assistant never asks for an Anthropic API key.

### After a restart

Completed, failed, and cancelled tasks stay available. A task whose Claude process was
running when PC Assistant exited is shown as **Interrupted** — never a false *Running*.
Nothing restarts on its own; send a follow-up and it resumes the same Claude session.

---

## Tests

```powershell
dotnet test
```

The normal suite includes the original task/approval coverage plus streaming, policy,
native Windows validation and a real stdio MCP/screenshot protocol smoke. It does not
launch Claude Code; real-Claude work remains opt-in.

**Window capture is proven with real pixels, not by inspection.** A red window is fully
covered by a maximized blue window; `capture_window` must return red, while a screen-region
capture of the same rectangle must return blue. If the capture ever silently degraded to
reading the screen, that assertion says so. The same suite covers coordinate translation
across a move, refusal after a resize, refusal for a closed, minimized, expired or
process-recycled target, and the refusal to capture PC Assistant's own windows.

The everyday controls run against the real machine, because each is a claim about this PC
and a mock cannot be wrong about it: the real audio endpoint, real power state, real
adapters, the real Recycle Bin, real application discovery, and a real launch that is
verified by a window actually appearing. Anything the owner would notice — changing the
volume — is behind `PCASSISTANT_NATIVE_SMOKE=1`, so an ordinary run never turns the speakers
down.

The capabilities panel is checked by running the shipped helper's own self-check and
asserting every line names a state and the reason for it, and that a missing helper reports
*not measured* rather than anything resembling Ready.

The enforcement path is verified against the **real** `PcAssistant.HookHost.exe` binary,
the one Claude Code actually executes: allow, deny (both `permissionDecision` and exit
code 2), the real classify → policy → broker chain, fail-closed when the broker is
unreachable, staying out of the way when PC Assistant did not launch the process,
malformed payloads, and the generated hook settings file. That covers everything except
Claude itself, and needs no authentication.

The privileged boundary is verified against the **real** helper classes: the boundary suite
hosts the shipped `PrivilegedPipeServer` and `RequestHandler` in-process on a test-only pipe
name and proves the refusals — malformed input, oversize, unknown operations, protocol
mismatch, replay, clock skew, helper-side parameter validation, a forged "already approved"
claim, UAC-weakening refusal, wrong client image, wrong account, missing owner, and the
"cannot elevate" floor. Running it unelevated is the point: without LocalSystem nothing it
accepts could elevate anything, so every assertion is about a refusal.

Approval semantics are asserted against a recording client, so "denied means nothing was
sent" is a statement about what crossed the boundary rather than about intent.

The end-to-end privileged suite needs a helper you have installed, and is opt-in:

```powershell
.\scripts\verify-elevation.ps1              # installs (one UAC prompt), then verifies
.\scripts\verify-elevation.ps1 -SkipInstall  # if it is already installed
.\scripts\verify-elevation.ps1 -Uninstall    # removes it and checks for orphans
```

It proves the elevated token is real (`whoami /groups` showing `S-1-16-12288` and an enabled
`BUILTIN\Administrators`), that the identity is you and not SYSTEM, that the process lands in
your interactive session, that exit codes are reported honestly, that cancelling kills only
that operation's own tree while an unrelated process keeps running, and that timeouts
terminate. It also writes a measured report of exactly what an unelevated process can and
cannot do with an elevated window.

Those privileged requests are made **by PC Assistant itself**, not by the test host, and that
is not a convenience. The installed helper identifies its caller from the pipe connection —
owner SID, integrity level, session, and the image path recorded at install — so it refuses a
test runner before parsing a byte, correctly. Denied is not verified, so the suite starts the
real executable in a verification mode whose probes are a closed set of hard-coded harmless
operations, and reads back what it measured. Nothing can be passed through that mode: no
executable, no argument, no service name, no shell command. The suite still asserts the
refusal directly, from the test host, so both boundaries are covered and neither is mistaken
for the other.

This is what found the two bugs that no amount of in-process testing could: the helper was
doing its privileged work while carrying the *caller's* token, and its readiness check was
answering from a cheaper question than the operations asked. See the
[elevation research](docs/RESEARCH-ELEVATION.md) for both.

The real Claude integration suite is opt-in and runs in a disposable temporary workspace
outside this repository:

```powershell
.\scripts\verify-real-claude.ps1
```

The v0.1 real suite passed 8/8. Version 0.5 additionally passed a real General task where
Claude called `system_info`, `windows`, and `screenshot` through the packaged MCP helper,
reasoned over the live image, and completed with zero approval prompts in AUTO. Official
Chrome testing navigated an owned loopback page, read it, typed, clicked, visually verified
the result, and closed only its own tab.

---

## Known limitations

- Claude Code must be signed in for a *standalone* spawn, which is what PC Assistant does.
  If the header shows **Claude Code authentication required**, run this in a normal
  PowerShell window and restart PC Assistant:

  Run `claude auth login` from a normal PowerShell window, using the executable shown by
  the Capabilities panel if `claude` is not on PATH.

  This uses your existing subscription; it is not an API key. Being signed in to the Claude
  desktop app is **not** sufficient on its own — that app keeps its OAuth tokens to itself
  and shares them only with its own child processes.

- The risk classifier is a denylist scan, not a shell parser. It does not track `cd` inside
  a command, so relative paths after a directory change are not re-scoped. It errs toward
  asking, and unknown tools are treated as high impact.
- `pip install` is classified as a system change, because a virtualenv install cannot be
  reliably distinguished from a global one. It asks once per task.
- Browser readiness is ultimately verified by the next browser tool call. Extension files
  can be present while Chrome is closed or disconnected; reconnect through the official
  extension if a task reports that condition.
- Native UI Automation depends on each application's accessibility support. The coordinate
  fallback is visible, foreground-only, bounded, and cannot control elevated apps or UAC.
- An Administrator terminal PC Assistant opens cannot then be typed into. Use an elevated
  `run` for anything that needs its output back.
- **Privileged helper: installed and verified end to end on this machine.** Approved commands
  run with a real elevated token (`S-1-16-12288`, enabled `BUILTIN\Administrators`) as the
  owner rather than SYSTEM, in the owner's own session, with no UAC prompt after the install.
  Installing or removing it does need one UAC confirmation, and that prompt lives on the Secure
  Desktop, which genuinely cannot be answered over a remote session. Everything else in PC
  Assistant works without the helper installed and nothing waits on it at startup.
- On a machine with Windows 11 **Administrator Protection** enabled there is no linked
  administrator token to elevate to. The helper reports that honestly instead of pretending;
  see the elevation research document.
- Screen capture cannot see a **minimized** window: Windows keeps no current surface for one,
  so it is refused rather than guessed at. Same for a window on another virtual desktop, and
  for one whose application excludes itself from capture. Restoring it is the owner's call.
- Window capture falls back to PrintWindow and then to visible desktop pixels where Graphics
  Capture cannot be used. PrintWindow can render hardware-composited regions — video, some 3D
  content — as blank; a uniformly blank result is rejected rather than returned, but a partly
  blank one is not detectable. The result always names the mechanism it used.
- **Windows notifications are not available.** A tray-balloon implementation was written and
  measured three ways on this machine and Windows 11 26200 rendered nothing in any of them.
  The reliable route needs a registered AppUserModelID, which means installing a Start Menu
  shortcut — persistent OS state, and the owner's decision rather than a side effect. Rather
  than ship a tool that reports "the owner has been notified" when nothing appeared, there is
  no notification tool.
- Browser element labels come from the page and are used only to decide whether to **ask**.
  They can never cause an action to proceed without asking, and a click PC Assistant cannot
  identify — bare coordinates, or an element it has not read — always asks.
- Browser tab ownership is inferred from the tab id in a `tabs_create` result. If that id
  cannot be found, closing the tab asks, which is the safe direction but means a task can be
  asked about a tab it opened itself.
- Native UI Automation depends on each application's accessibility support. The visual
  fallback is bounded, brings the window to the front, and cannot control elevated apps or UAC.
- Wi-Fi network name needs the Windows WLAN service; where it is absent the field is simply
  omitted rather than faked. Tunnels and virtual adapters are recognised generically, by
  adapter type and description, with no product-specific coupling.
- Recent-file discovery is intentionally nonrecursive across Downloads, Desktop and
  Documents. Claude's ordinary file tools handle deeper targeted searches.
- Abrupt app exit may retain the last streaming checkpoint rather than the final sub-500 ms
  fragment; Stop flushes the partial reply before cancellation.
- Single window, single owner, and no networking of PC Assistant's own: Claude Code is what talks to
  Anthropic. The Nexus bridge is a local named pipe for one trusted local client, not a network
  listener.
- **Approvals answered from the phone** have no click at the PC behind them, so PC Assistant cannot
  hand the foreground on. Bringing the target forward still worked with an ordinary application in
  front. It did not work while the Quick Settings flyout held the foreground; that is refused as a
  foreground failure. See [docs/NEXUS-BRIDGE.md](docs/NEXUS-BRIDGE.md#what-was-verified).
- **Signed in is not always usable.** Claude Code can report a logged-in account and still refuse every
  turn, for example with "Your organization has disabled Claude subscription access for Claude Code".
  PC Assistant shows that reason as not ready after the first refused turn.

---

## Still out of scope

- **A GPT advisor.** Later, Claude could call an `AskAdvisor(question)` tool for a short
  independent second opinion. That is one optional tool, not multi-agent orchestration.
  There is no OpenAI dependency and no placeholder for one today.
- **A network client.** PC Assistant has no networking of its own; Claude Code is what talks to
  Anthropic. The one external client is Nexus PC Core on the same PC, over the
  [Nexus bridge](docs/NEXUS-BRIDGE.md).
- **A packaged Claude Code plugin.** The skill and hooks are used directly until the
  architecture is proven.
