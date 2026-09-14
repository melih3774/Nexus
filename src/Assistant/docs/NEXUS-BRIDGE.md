# Nexus bridge

A local API through which **Nexus PC Core**, and only the Nexus the owner trusted, reaches PC
Assistant's tasks on behalf of the owner's authenticated phone. It lives inside the desktop process,
beside the window, and uses the same `AssistantService`, database and `ApprovalBroker`. A task started
from the phone is an ordinary task here, and an approval has one card however it is answered.

PC Assistant decides nothing differently because a request came through the bridge. AUTO/ASK, the risk
classifier, the hooks and the approval cards all apply exactly as they do to a task typed at the PC. The
bridge adds one rule of its own: administrator approvals are not answerable through it.

- [Turning it on](#turning-it-on)
- [Who is served](#who-is-served)
- [Protocol 1](#protocol-1)
- [What was verified](#what-was-verified)
- [Limits](#limits)

---

## Turning it on

**Capabilities → Let Nexus on this PC use PC Assistant for its owner-authenticated phone.** Off by
default. While it is off there is no pipe.

When Nexus connects for the first time it is refused, and **Capabilities** shows *Nexus at
C:\…\PcCore.Host.exe asked to connect* with **Trust this Nexus**. Trusting records that one executable
path in `settings.json`. **Forget trusted Nexus** removes it and ends any connection admitted under it.
`%LOCALAPPDATA%\PcAssistant\runtime\nexus-bridge.log` records connections and refusals: process ids and
executable paths only, never a prompt, reply or approval.

## Who is served

Three layers, each covering a gap in the one before:

1. **The pipe** `\\.\pipe\PcAssistant.NexusBridge.<owner SID>` has a DACL that allows only the owner's
   SID and denies the NETWORK SID. It has one instance and is created with `FILE_FLAG_FIRST_PIPE_INSTANCE`:
   if something else already holds the name, PC Assistant says so in **Capabilities** instead of sharing
   it, and while Nexus is connected there is no free instance.
2. **The kernel's account of the caller.** `GetNamedPipeClientProcessId` and
   `GetNamedPipeClientSessionId`, then the process's token user and image path. The caller must be in
   this session, run as this user, and be exactly the trusted executable.
3. **The owner's decision.** A `PcCore.Host.exe` that is not trusted yet is refused with
   `client_not_trusted` and offered for trust. Anything else is refused and logged.

A refused caller gets one answer, to its first request, and is disconnected. It never sees an event.

Nexus checks PC Assistant in the other direction: its client requires the pipe to belong to the current
user and verifies the serving process's session and executable before it sends anything.

## Protocol 1

One UTF-8 JSON object per line, both directions. Requests are at most 256 KB.

```
→ {"type":"request","id":"7","method":"tasks.get","params":{"task_id":"…"}}
← {"type":"response","id":"7","ok":true,"result":{…}}
← {"type":"response","id":"7","ok":false,"error":{"code":"not_found","message":"…"}}
← {"type":"event","event":"task.changed","data":{…}}
```

### Methods

| Method | Params | Result |
|---|---|---|
| `hello` | `protocol_version` (must be 1), `client`, `client_version` | `protocol_version`, `server`, `server_version`, `claude` |
| `status` | — | `claude`, `running_tasks`, `pending_approvals` |
| `tasks.list` | `limit` (1–200, default 50) | `tasks`, most recently active first |
| `tasks.get` | `task_id` | `task`, `messages`, `progress`, `pending_approvals`, `older_omitted` |
| `tasks.create` | `kind` (only `general`), `mode` (`auto`/`ask`), `prompt` | `task`; the first turn starts at once |
| `tasks.send` | `task_id`, `text` | `task`; the turn continues the task's Claude session |
| `tasks.stop` | `task_id` | `was_running` |
| `tasks.set_mode` | `task_id`, `mode` | `task` |
| `approvals.resolve` | `approval_id`, `decision` (`allow_once`/`deny`) | `resolved`, `outcome` |

Nothing but `hello` is served before `hello` succeeds. A task's message text is capped at 64,000
characters and marked `truncated`. `tasks.get` returns at most about a million characters of messages,
newest first, and sets `older_omitted` when it left earlier ones out. Prompts are capped at 32,000
characters. A workspace task is described by its folder name, never its path.

`claude` is `{availability, version, detail}`. `availability` is `ready`, `signed_out`, `not_installed`,
`failed` or `unknown`. `detail` gives the reason when it is not ready. It is cached for twenty seconds,
and dropped as soon as a turn changes it.

### Events

Sent after `hello`, to the connected client, for every task:

| Event | Data |
|---|---|
| `task.changed` | `task` |
| `message.appended` | `task_id`, `message`: the **whole** text so far, under the message's id, up to ~16 times a second while it streams |
| `task.progress` | `task_id`, `event`: `kind`, `title`, `detail`, `tool_name` (assistant text is never repeated here) |
| `approval.requested` | the approval: `action`, `target`, `reason`, `category`, `remote_resolvable` |
| `approval.resolved` | `approval_id`, `task_id`, `outcome` (`allowed`, `denied`, `timed_out`, `cancelled`) |

A client that falls four thousand events behind is disconnected rather than allowed to hold up a
running turn.

### Errors

`invalid_request`, `invalid_params`, `unknown_method`, `hello_required`, `unsupported_protocol_version`,
`client_not_trusted`, `not_found`, `task_busy`, `unsupported` (for example a Workspace `tasks.create`),
`requires_local_approval` (an administrator approval), `internal_error`. Messages are for people and
never carry exception text.

## What was verified

Automated, over real pipes against the real task engine: the full method set, streaming, follow-up
session continuity, stop, busy refusal, mode switching, allow and deny, the administrator refusal, the
desktop card resolving from a remote answer, a phone-started task appearing in the list, and the
listener's trust decisions (untrusted, trusted, candidate, squatted name, one client at a time).

Live on the development PC with the feature builds of both programs and a working Claude Code account,
every turn below driven through PC Core rather than through the window:

- An untrusted PC Core was refused, offered in **Capabilities**, and served after **Trust this Nexus**
  was pressed.
- A task created through Nexus ran a real Claude Code turn, and the reply streamed back in order: the
  owner message, *Claude session started*, the assistant text growing from 2 characters to the whole
  sentence, *Done*, then the state change.
- **A follow-up continued the same Claude session.** Asked afterwards what it had said a turn earlier,
  Claude answered from the earlier turn — the `--resume` path, over the bridge.
- **AUTO used a tool without asking.** A task read a file and reported the marker inside it;
  `tool_started` and `tool_completed` streamed, and no card was raised.
- **ASK, allowed.** A file write raised a card, **allow once** was answered through the bridge, and the
  file was on disk afterwards with the right contents.
- **ASK, denied.** The same shape answered **deny**: `tool_failed`, Claude reported the refusal, and the
  file was never created.
- **Stop** from Nexus ended the real Claude process tree ("terminating owned process tree" in the raw
  log) and the task read *Stopped*; no `claude.exe` from that turn was left behind.
- An account that refused every turn with 403 was reported as *not ready* with that reason (see
  [Limits](#limits)), and cleared by itself once the account worked again.
- Closing PC Assistant was pushed to Nexus at once as *not running*; reopening it was pushed within
  seconds; a task from before the restart reopened with its transcript.
- Tasks started through Nexus appeared in the window's list while it was open.

**An approval answered from the phone still lands its click.** There is no desktop click behind such an
approval, so PC Assistant cannot hand the foreground privilege to the PC tools process.
`RemotelyApprovedInputReachesItsTargetWithoutAnyDesktopClick` answers with no handover while another
process holds the foreground. It passed 3 of 3 runs: attaching to the foreground thread's input queue is
enough on its own. It failed while the Quick Settings flyout held the foreground, as a clean foreground
refusal. A zero-length injected mouse move did not change that and was not kept.

Then with real Claude in the loop: a task asked through Nexus typed `FALCON-9931` into a Notepad window
it had found itself. The only answer to the card came over the pipe, nothing was clicked at the PC, and
UI Automation read the text back out of that window afterwards.

**From the owner's actual phone**, over Tailscale, against these two builds: the screen drew correctly,
an AUTO task used a tool and streamed its answer, a follow-up continued the same Claude session, an ASK
card was allowed and the file appeared, another was denied and no file was written, Stop ended a running
task, and closing and reopening PC Assistant was pushed to the phone and recovered. Locking the phone
withdrew Full Remote at PC Core ("the phone locked itself") while a task ran on the PC; the phone had to
authenticate again before it could even list tasks, and it re-subscribed from scratch. A long reply
showed only *Working* for the best part of a minute before Claude Code emitted its first text.

**Nexus security monitoring does not mistake PC Assistant's input for a person.** First with Nexus's real
detector and an ARMED monitor in a throwaway state directory, while PC Assistant's real input path
clicked, pressed a key chord and typed text: 32 keyboard and 8 pointer events, all flagged injected and
set aside, and 0 incidents. Then against the real PC Core, actually `ARMED`, while a task asked through
Nexus clicked a window, typed `OSPREY-5150` and pressed a key: the state was still `ARMED` at the end and
no new security session had been opened.

## Limits

- **A signed-in Claude Code account can still refuse every turn.** `claude auth status` reporting a
  signed-in account is not proof that turns will run — during this work the account answered 403 "Your
  organization has disabled Claude subscription access for Claude Code" while reporting itself signed
  in. PC Assistant reports that as not ready, with that reason, from the first refused turn, and Nexus
  stops offering new work until it clears; it clears by itself when the account works again.
- A system flyout holding the foreground stops desktop input approved from the phone (see above), as
  does a locked PC or a UAC prompt.
- Streaming sends whole-message snapshots. Nexus throttles them for the phone. A short reply starts
  within seconds; a request for something long can sit at *Working* for the best part of a minute,
  because that is when Claude Code emits its first text.
- One client at a time.
