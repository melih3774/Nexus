# Browser and Claude runtime research

Verified 2026-09-06 on the owner's Windows PC. This document distinguishes supported interfaces, empirical checks, and remaining setup. No credentials, cookies, browser history databases, private authentication IPC, or Claude Desktop internals were inspected.

## Decision

Keep the existing C# Claude Code process bridge. Add the official `--chrome` integration to the same task and use a local PC Assistant MCP server for Windows tools. This preserves the working task/resume/approval architecture and subscription login. Migrating to a Python or TypeScript SDK would add another runtime without solving the Windows native-control gap.

The installed official Chrome integration **works in non-interactive print mode**, including partial output and session resume. Initially disconnected, it connected after the owner completed reconnection, and the controlled browser end-to-end test below **passed**. Browser connection must remain a separate diagnostic from CLI authentication and MCP server startup. Do not label a browser ready merely because an extension manifest exists or the stream init event says the MCP server is connected.

## Installed environment

The initial project documentation mentioned 2.1.247, but the installed executable now reports **2.1.260 (Claude Code)**:

```text
C:\Users\Melih\AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\claude-code\2.1.260\claude.exe
```

`claude` is not on the shell PATH. The old `%APPDATA%\Claude\claude-code` path does not exist outside the packaged-app redirection. The repository's existing locator correctly accounts for the MSIX bundle location and should continue selecting the newest installed bundle.

Read-only `auth status` returned `loggedIn: true`, `authMethod: claude.ai`, and `apiProvider: firstParty`. The limited fields inspected did not establish a plan name. Successful probe output reported `apiKeySource: none`; API and gateway environment variables were removed only from probe child environments. No login credentials were read or changed.

Chrome extension manifest inspection found the official extension ID `fcoeoabgfenejglbffodgkkbkcdhcgfn`:

| Browser/profile | Manifest versions present | Interpretation |
| --- | --- | --- |
| Chrome / Default | 1.0.85, 1.0.90, 1.0.91 | Installed files; enabled state and login not proven |
| Chrome / Profile 2 | 1.0.81 | Installed files; enabled state and login not proven |
| Chrome / Profile 1 | None found | Extension installation not detected |
| Edge / Default | None found | Extension installation not detected |

Chrome and Edge processes were running during the checks. Anthropic native messaging host registry entries were present for both browsers. Registry presence alone does not prove an active Code-to-extension connection.

## Official browser interface

Anthropic documents browser automation through Claude in Chrome, including Chrome and Edge, visible browser actions, existing login state, page/DOM inspection, console/network information, navigation, forms, and file uploads. It requires the extension and a direct Anthropic account signed in through `/login`; API keys and long-lived `setup-token` credentials do not enable this integration. The extension owns site permissions. First-time native messaging setup can require a browser restart. WSL is not supported. [Official Chrome integration documentation](https://code.claude.com/docs/en/chrome)

The current installed help explicitly exposes `--chrome`, `--no-chrome`, `--include-partial-messages`, `--input-format stream-json`, `--output-format stream-json`, `--session-id`, `--resume`, and `--strict-mcp-config`. The executable's supported permission-mode spelling includes `manual`; using `default` as an explicit CLI value is no longer appropriate for this build. `--bare` requires API authentication and skips hooks, so it is unsuitable here. [CLI reference](https://code.claude.com/docs/en/cli-reference)

## Live experiment results

Each probe ran directly with `ProcessStartInfo.ArgumentList`, redirected streams, `UseShellExecute=false`, and `CreateNoWindow=true`. Each had a 45-second timeout that could terminate only its owned child tree. No timeout occurred. Browser probes allowed only the requested observational tool, disabled built-in tools, and used `--permission-prompts none` to deny unexpected prompts. These diagnostic restrictions are not a recommendation to replace PC Assistant's approval broker.

| Experiment | Actual result |
| --- | --- |
| `-p --chrome --verbose --output-format stream-json --include-partial-messages`, strict empty MCP configuration, `tabs_context_mcp(createIfEmpty:false)` | Accepted; Chrome tools loaded; actual tool returned **Browser extension is not connected**; process exited 0 after about 15 seconds |
| Same browser integration with text on stdin, explicit `--session-id`, `--permission-mode manual`, and `--include-hook-events`; `list_connected_browsers` | Accepted; **0 connected browsers**; process exited 0 |
| Follow-up using `--resume` with the same ID, `--input-format stream-json`, and `--chrome` | Accepted; returned the exact previous-turn marker `LARCH-417`; process exited 0 |
| `--no-chrome` control | No Chrome tools advertised; ordinary streamed response succeeded |

The initial system event advertised 22 Chrome tools and `mcp_servers: [{name: "claude-in-chrome", status: "connected"}]`. Nevertheless, the first actual browser call failed to connect. This is a confirmed distinction between **MCP transport initialized** and **browser available**.

The tool names observed were:

```text
mcp__claude-in-chrome__browser_batch
mcp__claude-in-chrome__computer
mcp__claude-in-chrome__file_upload
mcp__claude-in-chrome__find
mcp__claude-in-chrome__form_input
mcp__claude-in-chrome__get_page_text
mcp__claude-in-chrome__gif_creator
mcp__claude-in-chrome__javascript_tool
mcp__claude-in-chrome__list_connected_browsers
mcp__claude-in-chrome__navigate
mcp__claude-in-chrome__read_console_messages
mcp__claude-in-chrome__read_network_requests
mcp__claude-in-chrome__read_page
mcp__claude-in-chrome__resize_window
mcp__claude-in-chrome__select_browser
mcp__claude-in-chrome__shortcuts_execute
mcp__claude-in-chrome__shortcuts_list
mcp__claude-in-chrome__switch_browser
mcp__claude-in-chrome__tabs_close_mcp
mcp__claude-in-chrome__tabs_context_mcp
mcp__claude-in-chrome__tabs_create_mcp
mcp__claude-in-chrome__upload_image
```

Raw output from the three session/resume/control probes and the reproducible probe script remain in ignored `artifacts/browser-research/`. The standalone initial connection probe was inspected in the tool output. These initial probes created only their own Claude conversations/logs and ordinary official Chrome integration initialization state. They did not navigate, click, type, close browsers, install extensions, or approve browser access.

### End-to-end verification after owner reconnection: PASS

The owner then reconnected the extension and authorized proceeding. A fresh print-mode session found one connected local Windows Chrome browser. An initial diagnostic retry denied `select_browser` and `browser_batch` because those tools were accidentally omitted from the probe's explicit allowlist. The denied navigation was not performed; the newly created test tab was closed. That diagnostic configuration error was corrected for a fresh session using the already authorized local test scope.

The successful run used Claude Code 2.1.260 with `-p --chrome --verbose --output-format stream-json --include-partial-messages --include-hook-events --permission-mode manual`, a fresh session ID, an explicit browser-tool allowlist, text stdin, and the same direct process/stream architecture as PC Assistant. `--permission-prompts none` remained set, so an unhandled prompt would have failed the test. No permission denials occurred.

The loopback server listened only on `127.0.0.1:56631` and served a disposable HTML form. Actual tool results confirmed:

1. `tabs_context_mcp(createIfEmpty:true)` created a new task group with its own blank tab, ID `2076214235`.
2. `browser_batch` navigated that tab to the loopback page and returned an actual JPEG screenshot content block.
3. `get_page_text` returned the page title, form label, and initial status. `find` located the textbox as `ref_5`.
4. A second batch clicked the textbox, typed `BIRCH-927`, clicked Verify, and returned a second screenshot.
5. Claude reported `Verified: BIRCH-927` and the banner `VIOLET PINE 582`. Independent inspection of the returned image confirmed both.
6. `tabs_close_mcp` confirmed the test tab closed and its empty group was removed. The owned loopback server process exited. No pre-existing tab was used for navigation or mutation.

Evidence in ignored `artifacts/browser-research/`: `reconnected-e2e.jsonl` contains the actual events; `e2e-tool-evidence.txt` contains tool text without image payloads; `verified-page.jpg` is the returned final screenshot; `e2e.ps1` and `local-page.cjs` reproduce the loopback experiment. `partial-sanitized.jsonl` contains a smaller actual streaming sample, including thinking block 0, tool/text block 1, signature deltas, assembled assistant messages, and the final result.

This proves navigation, page reading, finding a control, typing, clicking, screenshot vision, and owned-tab cleanup through the official browser integration in headless **CLI process mode** with a visible real Chrome browser. It does not independently prove the application's policy hooks, all authenticated sites, uploads, or every browser feature; those remain integration/feature-specific checks.

### Viable launch contract

Production launch can retain text stdin and one process per task turn:

```text
claude.exe -p --verbose --output-format stream-json
  --include-partial-messages --include-hook-events
  --permission-mode manual
  --settings <task-hook-settings.json>
  --mcp-config <pc-tools-mcp.json>
  --chrome
  --session-id <new-task-session-guid>
```

Replace `--session-id` with `--resume` for a follow-up. Pass each value as a separate argument, not shell-joined text. The experiment also proved Chrome tools survive `--strict-mcp-config` with an explicit empty MCP configuration. The application's own MCP configuration should be tested with its normal hooks before calling the mixed workflow verified.

For diagnosis, the following added restrictions were accepted:

```text
--tools ""
--allowedTools mcp__claude-in-chrome__list_connected_browsers
--permission-prompts none
--strict-mcp-config --mcp-config {"mcpServers":{}}
--setting-sources "" --max-turns 2
```

The `--max-turns` option was accepted by this installed build even though its top-level help did not display it. It should not be needed in the ordinary production task launch.

For streaming input, a newline-delimited user message was accepted:

```json
{"type":"user","message":{"role":"user","content":"Without using tools, return the marker from the previous turn."}}
```

### Partial output contract

Observed order: `system/init`, `stream_event` message start, content block starts/deltas, an assembled `assistant` message, content/message stop, and a final `result`. Tool calls similarly arrive as partial input JSON and then assembled `assistant` tool-use blocks. Render `text_delta` incrementally; do not execute partial tool input. Deduplicate the assembled assistant text and final result so the UI does not show the same answer three times. Preserve raw unknown events and tolerate malformed lines. Child/subagent events carry `parent_tool_use_id` and should not be mistaken for the top-level answer.

### Approval implications

Classify browser tool **arguments**, not only names. A batch can contain mutations. `tabs_context_mcp` may create a tab with `createIfEmpty:true`; console/network reads may clear buffers; screenshots may save files. `computer` can represent either observation or input. JavaScript can mutate page state or send network requests and must not be considered generically read-only. Uploads expose local file contents. These are application policy decisions inferred from the documented capabilities and observed tool surface.

Keep ordinary observation and navigation convenient in AUTO. Require a concrete owner approval for purchases, submission/publication, account/security actions, uploads with unclear intent, and similarly consequential interactions. A tool's generic click or typing operation does not reliably identify consequence, so the task's behavioral instructions must complement the hook classifier. Do not claim that name-based rules can prove every website click safe.

## Browser setup and recovery

The owner completed reconnection during this implementation pass; the local browser test above now passes. These instructions remain recovery steps for a missing/disconnected extension, not an outstanding requirement after that successful run.

1. Open Chrome in the profile where the official extension is installed. Confirm the extension is enabled and signed into the same Claude account as the CLI.
2. Start the executable above interactively with `--chrome`, then run `/chrome`. Complete the official setup, use reconnect, and choose the intended browser/profile if requested.
3. If the official setup requests a browser restart, save work and restart Chrome manually.
4. Repeat `list_connected_browsers` / `tabs_context_mcp(createIfEmpty:false)` from PC Assistant.
5. After successful connection, verify a harmless public or localhost page end-to-end: create a task tab, navigate, read title/content, take a screenshot, interact with a local form, and exercise the app's approval policy.

The setup page is [Claude in Chrome](https://claude.ai/chrome). Installing an extension or enabling a browser permission is an owner step, not something to force via registry editing. Until a tool call succeeds, status should say **Extension installed; connection unverified**, not Ready. The successful run verifies the local form and screenshots; no authenticated-page or upload success is claimed.

`BrowserIntegrationStatus.Inspect()` deliberately provides an installation snapshot only. Its separate optional-root diagnostic check compiled and passed cases for missing manifests, installed extension detection with numeric version ordering, malformed-manifest degradation, and an honest unverified summary. The actual snapshot found Chrome Default 1.0.91 and Profile 2 1.0.81. A persisted installation check must not turn into a stale promise that a browser connection remains live.

## Claude Desktop computer use and the SDK

Anthropic supports native computer use inside the Windows Claude Desktop app. Its documented CLI counterpart is currently **macOS-only** and explicitly requires an interactive session; `-p` is excluded. This rules it out as a supported Windows tool provider for PC Assistant's current bridge. [CLI computer-use documentation](https://code.claude.com/docs/en/computer-use), [Desktop documentation](https://code.claude.com/docs/en/desktop)

Desktop's supported `claude://code/new` and `claude://cowork/new` deep links prefill a composer and require review of supplied folders. They do not provide an execution/result API for embedding native computer use. No supported Windows Desktop embedding interface was found in the reviewed public documentation. This is a documented-interface finding, not a claim about hidden implementation details. [Desktop deep-link reference](https://support.claude.com/en/articles/14729294-open-claude-desktop-with-a-link)

The Agent SDK provides Claude Code's agent loop in Python and TypeScript, including tools, sessions, hooks, permissions, and streaming. It would be reasonable for a new application, but does not justify replacing this functioning C# bridge or expose the missing Windows Desktop capability. [Agent SDK overview](https://code.claude.com/docs/en/agent-sdk/overview)

Anthropic's current help article says the announced June subscription billing changes were paused: Agent SDK, `claude -p`, and third-party app usage still draw from subscription limits. The older credit-policy text below that update is explicitly retained only as historical material. Recheck this source before future billing changes; do not interpret a CLI result's `total_cost_usd` accounting estimate as evidence of an extra charge. [Current subscription notice](https://support.claude.com/en/articles/15036540-use-the-claude-agent-sdk-with-your-claude-plan)

The Claude API has a separate computer-use tool interface, but the client still implements the actual capture/input environment. It is a future paid-engine option, not a way to call the installed Desktop's Windows controls. [API computer-use documentation](https://platform.claude.com/docs/en/agents-and-tools/tool-use/computer-use-tool)

## Mature fallback choices

| Option | Fit and cost to this application | Decision |
| --- | --- | --- |
| Official Claude in Chrome | Reuses the owner browser session and existing Claude engine; installed and locally verified after owner reconnection | Primary integration |
| Microsoft Playwright MCP | Structured accessibility snapshots, Chrome/Edge, managed profiles or official extension attachment; adds Node/MCP dependency | Preferred optional general browser fallback if official connection remains unreliable |
| Chrome DevTools MCP | Strong DOM, console, network and performance investigation; supports owned profiles and explicit connection to running Chrome | Good optional development diagnostics fallback |
| Custom CDP/browser engine | Recreates navigation, lifecycle, snapshots, auth attachment and compatibility maintenance | Do not build |
| Windows UIA/screenshot fallback | Useful address-bar/current-window observation; limited page semantics compared with browser tools | Keep as an explicit limited fallback |

Playwright MCP supports `--browser chrome` or `--browser msedge`, `--isolated`, and `--extension` for an existing running browser with its extension installed. A managed profile is separate from the owner's everyday profile; do not copy cookies or storage to simulate authentication. Its maintainers distinguish MCP's persistent introspection loop from the more token-efficient CLI/skills path. PC Assistant's existing MCP host integration makes optional MCP the lower-friction fallback here. [Microsoft Playwright MCP](https://github.com/microsoft/playwright-mcp)

Chrome DevTools MCP supports `--autoConnect` to a running Chrome from version 144 after the owner enables the browser's remote debugging permission at `chrome://inspect/#remote-debugging`. This requires an explicit browser allow dialog. Its default managed profile is separate; `--isolated` avoids cross-task profile conflicts. Traditional debugging ports require a non-default data directory and should not be enabled silently. [DevTools MCP connection documentation](https://github.com/ChromeDevTools/chrome-devtools-mcp/blob/main/docs/advanced-usage.md)

If adding DevTools MCP, use `--no-usage-statistics` and `--no-performance-crux` unless the owner deliberately wants those network features. Its maintainers officially support Google Chrome and Chrome for Testing; Edge compatibility is not promised. Do not enable both fallback engines unconditionally and inflate every task's context. Pin and test a specific package version before integrating it; this research did not install or run either fallback. [Chrome DevTools MCP](https://github.com/ChromeDevTools/chrome-devtools-mcp)
