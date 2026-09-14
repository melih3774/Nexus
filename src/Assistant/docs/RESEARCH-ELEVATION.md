# Owner-approved administrator operations: research, decision, evidence

Researched and implemented 2026-09-06/07 against Windows 11 Pro 10.0.26200 and .NET 10.

The problem this pass solves: PC Assistant is useful on the ordinary desktop, but the moment
an operation needs Administrator rights it hits a wall it cannot see past. The goal is not to
get past UAC. It is to give the owner an explicit, auditable, Windows-supported way to say
*yes* to one privileged operation at a time — including when they are away from the machine.

---

## What this machine actually does

Measured first, so the design is not built on an assumption about UAC.

| Setting | Value here | What it means |
|---|---|---|
| `EnableLUA` | 1 | UAC is on. |
| `ConsentPromptBehaviorAdmin` | 5 | Prompt for consent for non-Windows binaries — the default. |
| `PromptOnSecureDesktop` | 1 | Prompts render on the Secure Desktop. |
| `TypeOfAdminApprovalMode` | 1 | **Legacy Admin Approval Mode.** Windows 11 *Administrator Protection* (value 2) is **not** enabled here. |
| `whoami /groups` | `BUILTIN\Administrators … Group used for deny only` | Classic split-token: the interactive token is the filtered one, and a full administrator token is linked to it. |

That last row is the load-bearing fact. It is why a linked-token design works on this machine,
and it is also why the code checks `TokenElevationType` at runtime instead of assuming it —
see [Administrator Protection](#what-would-break-this) below.

---

## Options compared

| Option | Repeat UAC? | Works remotely? | Verdict |
|---|---|---|---|
| `ShellExecute` verb `runas` per operation | **Every time** | **No** — the prompt is on the Secure Desktop, which cannot be captured or clicked by any unelevated process | Rejected as the mechanism. Kept for the one-time install, where a prompt is correct. |
| COM elevation moniker (`CoCreateInstanceAsAdmin`) | **Every time** | No | Rejected. Auto-elevation without a prompt is reserved for Microsoft-signed binaries in `%SystemRoot%\System32` with the right manifest; a third-party app gets a prompt every call. |
| Scheduled task, "run with highest privileges", at logon | No | Yes | **Rejected**, though it would work. The action is fixed at registration, so making it general means passing commands through a file or the registry — a race, and hard to authenticate. It has no request/response channel, weaker lifecycle than a service, and it is the canonical shape of both malware persistence and the classic UAC-bypass technique, which is a bad thing for a security-sensitive component to look like. |
| One long-lived elevated child process | Once per session | Partly | Rejected. Dies at logoff, needs a prompt at the start of every session, and gives no natural place for independent validation. |
| Run PC Assistant itself elevated | Once | Yes | Rejected outright. It would put the entire Claude-driven surface — screen, input, browser, shell — at high integrity permanently. The prompt named this as needing an extraordinarily strong reason; there isn't one. |
| **LocalSystem Windows service + local named pipe + the owner's linked administrator token** | **Once, at install** | **Yes** | **Selected.** |

---

## The selected architecture

```
Claude (in Claude Code)                      unelevated, medium integrity
  │  mcp__pc-assistant__elevated_request
  ▼
PC tools MCP server                          unelevated — carries the request, holds no policy
  │  per-task named pipe (PcElevatedOperation)
  ▼
PC Assistant                                 unelevated — classifies, asks the owner, records
  │  ── owner clicks Allow ──
  │  \\.\pipe\PcAssistant.Elevation          ACL: SYSTEM + Administrators + the one owner SID
  ▼
PcAssistantElevation service                 LocalSystem — validates independently
  │  WTSQueryUserToken(session) → TokenLinkedToken → DuplicateTokenEx
  ▼
CreateProcessAsUser(elevated owner token, lpDesktop = winsta0\default)
  ▼
the operation                                Administrator, as the owner, on the owner's desktop
```

### The one idea that makes it work

`WTSQueryUserToken` gives a LocalSystem service the interactive user's **filtered** token.
`GetTokenInformation(TokenLinkedToken)` on that returns the **full administrator token Windows
already linked to it** — the exact token a UAC consent prompt would have handed over. The
service duplicates it to a primary token and calls `CreateProcessAsUser`.

So the privilege that gets delivered is *not* SYSTEM. It is **the owner's own administrator
token, in the owner's own interactive session**. The ceiling on this whole feature is "what
the owner could have done by clicking Yes on a UAC prompt" — not "what SYSTEM can do". That
distinction is deliberate and is enforced in `SessionElevation`, which refuses to proceed if
the duplicated token does not read back as elevated, or belongs to a different session.

Two documented details that are easy to get wrong and are handled explicitly:

- **`lpDesktop` must be `winsta0\default`.** `CreateProcessAsUser` otherwise places the
  process on a non-interactive window station where it has no visible window and can receive
  no input — the classic "my service started notepad and nothing appeared".
- **Handles cannot be inherited across sessions.** The docs are explicit: with
  `bInheritHandles = TRUE` "you must create the process in the same session as the caller".
  A session-0 service therefore *cannot* simply hand a session-1 child a pipe for its output.
  This is the entire reason the elevated **runner** exists.

### The runner

For `run` (captured output), the service starts a copy of itself in the owner's session with
the owner's elevated token, **suspended**, assigns it to a kill-on-close job object, and only
then resumes it. The runner does an ordinary same-session redirected launch, captures bounded
output, enforces the approved timeout, and reports back over a fresh randomly-named pipe whose
DACL admits only SYSTEM and Administrators — so an unelevated process running as the owner
cannot open it. The service additionally checks that the process which connected is the exact
runner it just created.

The job object is what makes cancellation precise: terminating it can only reach the process
tree this operation created.

`terminal` and `launch` do **not** get a job object. A visible elevated window belongs to the
owner's desktop from the moment it appears; PC Assistant does not get to close the terminal it
was asked to open.

---

## The privileged surface

Four operations. Not dozens of one-off methods, and not "run this string as SYSTEM".

| Operation | What it does | Cancellable |
|---|---|---|
| `terminal` | visible Administrator PowerShell / pwsh / cmd on the owner's desktop | no — it is the owner's window now |
| `run` | one program + argument **vector**, output and exit code captured | yes, unless the executable looks like an installer |
| `launch` | one visible elevated program (installer, GUI tool), returns its pid | no |
| `service` | start / stop / restart / query **one named** Windows service | n/a |

`service` is performed by the service itself through the SCM **while impersonating the owner's
elevated token**, so the Service Control Manager applies the owner's rights rather than
SYSTEM's. A service the owner could not control from an elevated console cannot be controlled
from here either.

### What the validator can and cannot promise

`OperationValidator` runs in three places — the PC tools process (for a fast, useful error),
PC Assistant (before the owner is asked), and the helper (which trusts neither of the other
two). Its honest claim is narrow: **it bounds parameters, not meaning.**

It enforces: protocol equality; absolute local `.exe` paths only (no UNC, no device paths, no
PATH search); at most 64 arguments of 4096 characters; a 16 KB command line; no NUL or newline
in an argument; existing working directories; 1–3600 second timeouts; service names matching
`[A-Za-z0-9_.-]{1,256}` and one of four actions; and a shell allowlist of exactly
powershell / pwsh / cmd.

It also refuses two categories outright:

- **anything inside the helper's own installation directory** — that directory decides what
  SYSTEM runs at the next service start;
- **anything mentioning `EnableLUA`, `ConsentPromptBehaviorAdmin`, `PromptOnSecureDesktop`,
  `FilterAdministratorToken`, `TypeOfAdminApprovalMode`** and friends. This is a **guardrail,
  not a boundary**: it stops a mistaken or injection-suggested request before it reaches the
  owner's approval card. It does not parse PowerShell and it is not claimed to.

What it explicitly does **not** promise: an approved `powershell.exe -Command <script>` is
arbitrary code running with the owner's administrator token. That is the same authority the
owner would have had by typing it into an elevated console. The protection there is that the
owner reads the **literal command** on the approval card, and that nothing reaches the helper
without that approval.

Argument vectors are never re-split by a shell. The helper builds the command line with
`CommandLineToArgvW`'s documented quoting rules, and a test round-trips real arguments through
`CommandLineToArgvW` itself to prove Windows reads back exactly what was meant.

---

## IPC and identity

`\\.\pipe\PcAssistant.Elevation`, created by the service with:

- an explicit DACL naming **SYSTEM (full), Administrators (full), and the single owner SID
  recorded at install time (read/write/synchronize only)**. Not `CreateNewInstance` — so
  nothing running as the owner can add an instance of this pipe name and intercept a later
  connection;
- `PIPE_REJECT_REMOTE_CLIENTS`, so network clients are refused by Windows rather than by a
  hostname check;
- `FILE_FLAG_FIRST_PIPE_INSTANCE` on the first instance, so if anything already owns the name
  the service **fails loudly** instead of quietly sharing it.

Every connection is then identified by Windows, not by the caller's claims. The service
impersonates the client and reads its user SID, session and integrity level, and separately
resolves the client's image path via `GetNamedPipeClientProcessId` +
`QueryFullProcessImageName`. It refuses:

| Refusal | Why |
|---|---|
| a different user SID | this helper belongs to one owner |
| integrity below medium | no low-integrity or sandboxed callers |
| session 0 | privileged operations belong to an interactive desktop |
| a client image path other than the one recorded at install | defence in depth (see limits) |
| a request id already seen | no replay |
| a timestamp more than 2 minutes from the helper's clock | no stale requests |
| a message over 64 KB | discarded whole, never partially parsed |

Deliberately **not** built: a shared secret or PKI. A secret readable by the owner adds
nothing against an attacker who is already running as the owner, and pretending otherwise
would be worse than not having it.

---

## Approval semantics

Elevation is a stronger boundary than any other category, and the policy says so in one place:
`ApprovalPolicy` maps `RiskCategory.Elevation` to **Ask in both AUTO and ASK**, and
`PcToolPolicy.Scope` returns no scope key for it — so *"Allow for this task" is never even
offered* for an administrator operation. There is no persistent privileged grant of any kind.

The card shows: the action, the **literal** command or service, the privilege ("Administrator
— your own account's elevated token, in your desktop session. Not SYSTEM."), the concrete
effect, Claude's stated reason *labelled as Claude's claim*, and which task is asking.

Order matters and is asserted by tests: **the owner's denial means nothing was ever sent.**
Not "sent and refused" — the privileged client never opens the pipe. The same is true for a
timeout, a cancelled task, an unavailable helper, and a request that fails validation.

An unavailable helper is checked **before** the owner is asked, because asking someone to
approve something that cannot run is exactly how approval loops start.

---

## Setup and removal

`Install Privileged Helper…` in the Capabilities panel runs
`PcAssistant.Elevation.Host.exe --install` through an ordinary `runas` UAC prompt. It copies
the payload to `%ProgramFiles%\PcAssistant\Elevation`, records the owner SID and client path,
registers `PcAssistantElevation` as a LocalSystem auto-start service, and starts it.

**Program Files is not cosmetic.** The service runs as LocalSystem; if its binaries or state
lived anywhere the unelevated owner could write, any process running as the owner could
replace them and be SYSTEM at the next service start. The installer refuses to install
anywhere outside Program Files.

`Uninstall Privileged Helper` stops and deletes the service and deletes the directory. It runs
from **PC Assistant's own copy** of the payload, not the installed one, because a process
cannot delete the directory it is running from — an uninstaller that leaves an orphan
directory behind is not an uninstaller.

UAC appears **once, at install** (and once at uninstall). It does not appear again for
approved operations — that is the entire point of the architecture, and it is what makes the
feature usable when the owner is not sitting at the machine.

### Version compatibility

The pipe name is stable across versions on purpose, so a mismatch is reported as a mismatch
rather than looking like "not installed". Both sides compare `PrivilegedProtocol.Version` for
**exact equality**: an older helper refuses a newer client's request shape instead of guessing
at fields it does not know, and the client refuses a reply that arrives with the wrong
protocol even if the helper managed to answer. Diagnostics show
`Installed / version mismatch`, and the fix is the ordinary Install flow, which is honest
about needing elevation.

---

## What would break this

**Windows 11 Administrator Protection.** When enabled (`TypeOfAdminApprovalMode = 2`), the
signed-in administrator's token is a genuine standard-user token with **no linked
administrator token**; elevation instead mints a temporary token from a system-managed shadow
admin account after an explicit Windows Hello or credential confirmation. On such a machine
`TokenLinkedToken` has nothing to return, and this design's silent-elevation property does not
hold.

The code does not assume its way past this. `SessionElevation` reads `TokenElevationType` at
runtime and, when there is no administrator token to reach, returns `NotElevatable` with a
plain explanation rather than falling back to anything. The status surface reports
`Installed / no administrator token on this account`, which is also what a standard-user
account would see. That is the correct behaviour: the design works *with* Windows privilege
separation, so when Windows separates privilege differently, the honest answer is that
elevation needs the owner.

---

## Measured behaviour

### The Secure Desktop, while a UAC prompt is up

Probed from an ordinary unelevated process on `WinSta0\Default`:

| Probe | Result |
|---|---|
| `GetForegroundWindow()` | **0** — there is no foreground window to find |
| `OpenInputDesktop` | **fails, error 1400** — the input desktop is `Winlogon` and is not openable |
| `Graphics.CopyFromScreen` | **throws "the handle is invalid"** — capture returns nothing at all, not even a black frame |
| the prompt itself | displayed, and **auto-cancelled after 122 seconds** unanswered, returning `ERROR_CANCELLED` (1223) |

So PC Assistant cannot see a UAC prompt, cannot find it, and cannot click it. That is the
correct outcome and it is why the one-time install genuinely cannot be completed remotely.
The expected product behaviour is to say so: *"Administrator approval is waiting on the local
PC."*

### Bugs this design's own tests found

Worth recording, because all three were invisible to review and only appeared when the real
classes were exercised:

1. **The client disposed a `StreamWriter` after the server had hung up.** Disposal flushes;
   the flush threw `IOException: Pipe is broken`; the exception replaced the already-computed
   return value. Every privileged request would have failed in production regardless of what
   the helper answered.
2. **The server impersonated its pipe client before reading from the pipe.** Windows does not
   permit that, so client identification raced the client's write and intermittently broke the
   connection. Reading first fixes it, and identification now fails closed to an empty SID,
   which the handler refuses.
3. **The pipe security descriptor named LocalSystem as its owner.** Setting an owner requires
   the privilege to do so and buys nothing — the DACL is what restricts access, and the creator
   owns the object anyway.

Two more came out of reviewing the design against its own claims: listener threads originally
matched the concurrent-operation limit, so four long commands would have occupied every
listener and made `Cancel` unservable exactly when it was needed; and `Start()` claimed the
pipe name and immediately closed the instance, leaving a window in which another process could
claim it.

### What the live install found, that nothing else could

The boundary suite passes against the real server classes, the approval semantics are proven
against a recording client, and none of it caught either of the two bugs below. Both needed an
actually-installed LocalSystem service to appear at all.

**1. The privileged work ran as the caller, not as the service.**

Windows resolves privilege checks against the *thread's* effective token, not the process's.
The service is LocalSystem, but its listener thread was carrying the owner's ordinary
medium-integrity token by the time it reached for the administrator token — so it was asking
Windows for the linked token *as the owner*, which is exactly the caller Windows refuses to
give a primary token to. Two things put it there, and both were doing their job:

- the pipe server impersonates its client in order to identify it, and
- `WindowsIdentity.GetCurrent()` self-impersonates just to obtain a thread token at all, so
  merely *reading* who the interactive user is leaves a token behind.

The symptom was maximally misleading. Every identity check passed; then `DuplicateTokenEx`
failed with `ERROR_BAD_IMPERSONATION_LEVEL`, which points at the duplication rather than at the
identity performing it, and the helper reported `NotElevatable` — "this account has no
administrator token", a true statement about the wrong account. The fix is `RevertToSelf` at
the top of anything privileged, and it is a no-op when there is nothing to revert.

The rule underneath is documented and has no middle ground:
`GetTokenInformation(TokenLinkedToken)` returns the administrator token as a **primary** token
to a caller holding `SeTcbPrivilege`, and as an **identification-level impersonation** token to
everyone else. An identification-level token cannot be duplicated into a primary token at any
impersonation level whatsoever — measured here, all four levels return 1346:

| requested level | `TokenPrimary` | `TokenImpersonation` |
|---|---|---|
| Anonymous | error 1346 | OK |
| Identification | error 1346 | OK |
| Impersonation | error 1346 | error 1346 |
| Delegation | error 1346 | error 1346 |

**2. Readiness was answered by a cheaper check than the operations use.**

`Status` reported **Ready** while every operation failed, because it stopped at "a linked token
exists" rather than "a usable primary administrator token can be produced". It now runs the
identical code path an operation runs, and carries the reason with the answer. A readiness
check that is cheaper than the thing it certifies is not a readiness check.

A third, in the installer: deleting a service tells the SCM to forget it but does not wait for
the service's process to exit, so an upgrade copying over the still-mapped assemblies failed
with a sharing violation — *after* the old service had already been removed. It now waits for
the process and retries the copy.

## Sources

- [WTSQueryUserToken](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsqueryusertoken) — "the calling application must be running within the context of the LocalSystem account and have the SE_TCB_NAME privilege".
- [CreateProcessAsUser](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessasusera) — `winsta0\default` for interactivity; "You cannot inherit handles across sessions"; `CreateEnvironmentBlock` guidance; the `lpApplicationName` quoting warning.
- [TOKEN_INFORMATION_CLASS](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ne-winnt-token_information_class) — `TokenLinkedToken`, `TokenElevation`, `TokenElevationType`, `TokenSessionId`, `TokenIntegrityLevel`.
- [TOKEN_LINKED_TOKEN](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-token_linked_token)
- [GetNamedPipeClientProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid)
- [Launching an interactive process from a Windows Service](https://learn.microsoft.com/en-us/archive/blogs/winsdk/launching-an-interactive-process-from-windows-service-in-windows-vista-and-later) — the documented `WTSQueryUserToken` → `CreateProcessAsUser` pattern.
