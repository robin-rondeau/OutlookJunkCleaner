# Outlook Junk Cleaner — Plan

## Context

You receive a high volume of repetitive junk mail in your Outlook **consumer** account (`outlook.com` / `hotmail.com` / `live.com`), much of it from senders that ignore unsubscribe requests. You want an AI agent to triage and clean this folder for you on an **hourly cron** (skipping 00:00–06:00), without giving the agent your full mailbox credentials, and without locking yourself into a single LLM provider.

Trust is built in two phases:

- **Phase A — training.** Confident junk **stays in Junk and is just marked as read**; ambiguous mail is **moved to Triage** for manual review. You get an audit trail directly in your Junk folder: read = agent-classified-as-junk; unread = agent hasn't looked yet (or hasn't run since the message arrived). You evolve the classification rubric based on what you see.
- **Phase B — trusted.** Once Phase A confidence holds, you flip an env-var (no rebuild) that registers a `delete_from_junk` tool. Confident junk graduates from "marked read" to "moved to Deleted Items"; ambiguous still goes to Triage.

Two structural decisions shape the rest of the design:

1. **Microsoft Graph has no folder-level OAuth scopes.** You grant `Mail.ReadWrite` over the *entire* mailbox, or nothing. The folder boundary therefore has to live in application code. A custom **MCP server in C#** is the cleanest way to express that boundary.
2. **You want the option to swap LLMs later** (Claude ↔ OpenAI/Azure OpenAI ↔ etc.). To keep that swap cheap, the cron-driven agent host is also a small **custom C# program**, not Claude Code. Claude Code remains useful as a *secondary* MCP client for ad-hoc interactive review, but it is not on the critical path.

The combination is the **hybrid architecture** below.

## Topology — what runs where

Two machines:

- **This laptop (WSL).** Dev only. Build, iterate, push to git. Sometimes off / offline.
- **Always-on Windows machine.** Already has Claude Code for Windows installed (used for ad-hoc interactive review only). The cron, the MCP server, and the agent host all run here.

Layout on the always-on machine:

```
C:\Tools\OutlookJunkCleaner\
├── .mcp.json                             ← lets Claude Code attach to the MCP server for interactive review
├── rubric.md                             ← classification rubric (plain markdown, you edit this over time)
├── bin\
│   ├── OutlookJunkMcp.exe                ← the MCP server (boundary + Graph + MSAL)
│   └── OutlookJunkAgent.exe              ← the cron-driven agent host (LLM-swappable)
└── logs\                                 ← Task Scheduler stdout/stderr, rotated
```

Token cache (machine-local, not in the project dir):
```
%LOCALAPPDATA%\OutlookJunkMcp\token.cache  ← DPAPI-encrypted MSAL cache
```

## How the cron run works (end-to-end)

1. Windows Task Scheduler fires on the hour, between 06:00 and 23:00.
2. Action: run `bin\OutlookJunkAgent.exe`, working dir `C:\Tools\OutlookJunkCleaner\`.
3. The agent host:
   1. Spawns `bin\OutlookJunkMcp.exe` as a child process and connects to it as an MCP **client** over stdio.
   2. Discovers the available tools (Phase A: 5 tools; Phase B: 6).
   3. Loads `rubric.md` and constructs a system prompt: rubric + behavioral rules + phase-aware instructions.
   4. Runs an agent loop against the configured LLM provider (default: Anthropic Claude). When the model emits a tool call, the host translates it to an MCP `tools/call`, forwards the result back, and continues.
   5. Stops when the model stops calling tools, writes a summary to `logs\YYYY-MM-DD.log`, exits.
4. The MCP server child process exits with the host. No long-running services.
5. **Independently**, when you want to review interactively, you `cd C:\Tools\OutlookJunkCleaner\` and run `claude`. Claude Code reads `.mcp.json`, attaches to the same MCP server, and you can ask "what's in Triage and why?" — same tools, same boundary.

## Component design

### 1. `OutlookJunkMcp` — C# MCP server (the boundary)

- .NET 9 console app, **self-contained `win-x64`** publish so the target machine doesn't need a .NET runtime install.
- Official C# MCP SDK (`ModelContextProtocol` NuGet package), stdio transport.
- Auth: **MSAL.NET** `PublicClientApplication`, `/consumers` authority, scope `Mail.ReadWrite offline_access`.
- First-auth: **device-code flow** (any browser works, doesn't need to be on the target machine).
- Token cache: DPAPI-encrypted file at `%LOCALAPPDATA%\OutlookJunkMcp\token.cache`. Only the user account that ran first-auth can decrypt it.
- Folder allow-list is **compiled in**; the agent cannot pass arbitrary folder names.

### 2. `OutlookJunkAgent` — C# agent host (the cron runner; LLM-swappable)

- .NET 9 console app, also self-contained `win-x64`.
- References the **MCP client** half of the C# MCP SDK to talk to the server child process.
- Has a small `IAgentDriver` abstraction with one method: `RunAsync(IEnumerable<McpTool> tools, string systemPrompt, string userPrompt) → AgentResult`.
- Initial implementation: `AnthropicAgentDriver` using the Anthropic Messages API (community SDK or raw HTTP — ~50 lines of tool-use loop).
- To swap LLMs later, you write a second driver (`OpenAiAgentDriver`, `AzureOpenAiAgentDriver`, …) and choose by env var `OUTLOOK_JUNK_AGENT_PROVIDER=anthropic|openai|azureopenai`. The MCP server, the rubric, the cron, the security model, and most of the host are unchanged.
- **Schema adapter:** MCP tool definitions are JSON Schema; provider tool-call formats are minor variations on JSON Schema. The adapter is a small `McpToolToProviderTool` translator per driver.

### 3. `rubric.md` — the classification rubric (the "training" surface)

- Plain markdown loaded as a string at startup.
- Sections: definite-junk patterns, definite-not-junk patterns, ambiguity policy ("when in doubt, triage with a one-line reason"), phase-aware behavioral rules ("in Phase A the action for confident junk is `mark_as_read`, never delete, even if the tool exists").
- This is what you iterate on as the agent's classifications evolve.
- Same file is used by both the cron host (read at startup) and Claude Code interactive sessions (loaded as context manually or via a tiny project-scoped slash command).

### 4. MCP tool surface (the entire allowed action space)

| Tool | Purpose | Phase A | Phase B |
|---|---|---|---|
| `list_junk(limit?, sinceHours?, includeRead?=false)` | List items in Junk: `{id, sender, subject, receivedAt, isRead, hasAttachments, bodyPreview, listUnsubscribe}`. **Defaults to unread-only** so the agent's working set each hour is "what's new since last run." | yes | yes |
| `get_message(id)` | Full body + headers, **only if the message is currently in Junk or Triage** | yes | yes |
| `mark_as_read(id, reason)` | Mark a Junk message as read in place. The agent's action for confident junk in Phase A; remains available in Phase B. Reason is recorded for auditability. | yes | yes |
| `move_to_triage(id, reason)` | Move from Junk → Triage with a reason. Action for ambiguous mail in both phases. | yes | yes |
| `list_triage(limit?)` | List items currently in Triage so the agent can see what's outstanding | yes | yes |
| `get_status()` | `{ junkCount, junkUnreadCount, triageCount, deleteEnabled, allowedFolders }` | yes | yes |
| `delete_from_junk(id, reason)` | Move from Junk → Deleted Items (recoverable ~30 days) | **not registered** | registered |

Hard invariants (compiled in, not negotiable from the agent side):

- The agent can only ever read Junk and Triage. Inbox, Archive, Sent, and any custom folders are unreachable.
- The only legal mutations are: mark-read in Junk, move `Junk → Triage`, and (Phase B) move `Junk → Deleted Items`. Triage is read-only to the agent except as a *destination*.
- The agent never sees the access token; it only handles opaque Graph message IDs.
- "Delete" means *move to Deleted Items*, never hard-delete. Outlook.com retains Deleted Items ~30 days, giving you a recovery window.
- These invariants apply identically whether the host is `OutlookJunkAgent.exe` or Claude Code or any future MCP-aware client.

### 5. Phase-A → Phase-B graduation

Set `OUTLOOK_JUNK_MCP_ALLOW_DELETE=1` in the Task Scheduler action's environment. The MCP server registers `delete_from_junk` on next run; no rebuild. The change is auditable (lives in the task definition). Update `rubric.md` at the same time to tell the agent it now has delete authority and what bar to meet before using it.

## Step-by-step setup

### One-time, on this laptop (dev)

1. Scaffold the solution: two projects, `OutlookJunkMcp` and `OutlookJunkAgent`, plus a shared `OutlookJunkCommon` for the tool-name constants and shared types.
2. NuGet refs:
   - Both: `ModelContextProtocol` (gives you both server and client sides).
   - `OutlookJunkMcp` only: `Microsoft.Identity.Client`, `Microsoft.Identity.Client.Extensions.Msal`, `Microsoft.Graph`.
   - `OutlookJunkAgent` only: an Anthropic SDK (community `Anthropic.SDK` or raw `HttpClient` calls — both viable).
3. Implement the six MCP tools, the folder allow-list, the MSAL bootstrap (server side), and the agent loop + Anthropic driver (host side).
4. Register an Azure app registration ("Personal Microsoft accounts only", redirect URI `https://login.microsoftonline.com/common/oauth2/nativeclient`, public-client allowed). Note the client ID — not a secret for public clients but cleaner to keep out of source.
5. Build self-contained: `dotnet publish ./OutlookJunkMcp -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true` and same for `OutlookJunkAgent`.
6. Commit + push.

### One-time, on the always-on Windows machine

1. Create `C:\Tools\OutlookJunkCleaner\`.
2. Copy or `git clone` the repo, drop both published `.exe` files into `bin\`.
3. Set env vars (system-wide or in the Task Scheduler action): `OUTLOOK_JUNK_MCP_CLIENT_ID`, `OUTLOOK_JUNK_AGENT_PROVIDER=anthropic`, `ANTHROPIC_API_KEY=…`.
4. **First-auth, interactively, once:** `bin\OutlookJunkMcp.exe --first-auth`. Visit the device-code URL, sign in to your `outlook.com` account, grant `Mail.ReadWrite`. Token cache is now warm.
5. Smoke-test the server alone: `bin\OutlookJunkMcp.exe --self-test` should print "Junk: N (M unread), Triage: K" and exit 0.
6. Smoke-test the host: `bin\OutlookJunkAgent.exe --dry-run` runs the full pipeline but the dry-run flag tells the rubric to list intended actions without actually invoking mutating tools. Confirm the action list is sensible.
7. (Optional) Smoke-test interactive review: from the project dir, run `claude`, ask "what's in Junk right now?", verify it uses the same MCP tools and gets a sensible answer.
8. Install the cron: `scripts\install-task.ps1` (admin PowerShell). Registers a Task Scheduler trigger: hourly between 06:00 and 23:00, action = `bin\OutlookJunkAgent.exe`, working dir = `C:\Tools\OutlookJunkCleaner\`. Run-only-when-user-is-logged-on (DPAPI requirement) — or run-as the same user with stored credentials if the machine auto-logs-in.

### Ongoing

- Phase A: review Triage regularly. Edit `rubric.md` when the agent makes mistakes; commit and re-deploy by copying the file (no rebuild required).
- Phase B graduation: once happy, edit the Task Scheduler action and add `OUTLOOK_JUNK_MCP_ALLOW_DELETE=1`. Update `rubric.md`. Watch one or two cycles before walking away.
- LLM swap (if/when you want one): write a second `IAgentDriver` (e.g. `OpenAiAgentDriver`), set `OUTLOOK_JUNK_AGENT_PROVIDER=openai`, redeploy `OutlookJunkAgent.exe`. The server, rubric, cron, and Phase A/B state are untouched.

## Critical files to be created (in this repo)

Server side:
- `OutlookJunkMcp/OutlookJunkMcp.csproj`, `Program.cs` — entry point, MSAL bootstrap, MCP server start.
- `OutlookJunkMcp/Tools/JunkTools.cs` — the seven tools, MCP-annotated. `delete_from_junk` is conditionally registered based on env var.
- `OutlookJunkMcp/Graph/MailClient.cs` — Graph wrapper enforcing the folder allow-list before every call.
- `OutlookJunkMcp/Auth/TokenCache.cs` — DPAPI-protected MSAL cache via `Microsoft.Identity.Client.Extensions.Msal`.
- `OutlookJunkMcp/Config/AppConfig.cs` — env-var reader (`OUTLOOK_JUNK_MCP_ALLOW_DELETE`, `OUTLOOK_JUNK_MCP_TRIAGE_FOLDER` default `Triage`, `OUTLOOK_JUNK_MCP_CLIENT_ID`).

Agent host:
- `OutlookJunkAgent/OutlookJunkAgent.csproj`, `Program.cs` — entry point, MCP client connection, rubric load, driver dispatch.
- `OutlookJunkAgent/Drivers/IAgentDriver.cs` — provider abstraction.
- `OutlookJunkAgent/Drivers/AnthropicAgentDriver.cs` — initial driver. ~80 LoC: tool-use loop, schema adapter from MCP `inputSchema` to Anthropic `tools[].input_schema`.
- `OutlookJunkAgent/AgentRunSummary.cs` — record per-run actions for the log file.

Shared:
- `OutlookJunkCommon/ToolNames.cs` — string constants for tool names.

Project root:
- `.mcp.json` — registers `outlook-junk` as a stdio MCP server pointing at `bin\OutlookJunkMcp.exe`. Used by Claude Code (interactive review). The agent host has its own way of launching the server (no .mcp.json dependency).
- `rubric.md` — classification rubric and phase-aware behavioral rules.
- `scripts\install-task.ps1` — registers the Task Scheduler job.
- `scripts\first-auth.ps1` — wraps `OutlookJunkMcp.exe --first-auth` with friendlier output.
- `README.md` — quick start, troubleshooting, graduation procedure, how to add a new LLM driver.

## Verification

End-to-end checks before considering this "working":

1. **Server `--self-test`** prints `Junk: N (M unread), Triage: K` and exits 0. Confirms MSAL + scope + folder lookup + folder allow-list initialization.
2. **MCP handshake (interactive).** From the project dir, `claude --debug` lists six tools (Phase A) under `/mcp outlook-junk`. Confirms the server is wired correctly and Claude Code can attach.
3. **Folder boundary test.** Grab a message ID from your Inbox via Outlook on the web. Ask Claude Code interactively to call `get_message` on it. The server must refuse with "message not in allowed folder." Proves the boundary is real, not advisory.
4. **Phase A behavior (cron host).** Seed Junk with a mix of (a) obviously-junk repeat-offender mail and (b) a couple of borderline items. Run `bin\OutlookJunkAgent.exe`. The obvious junk is **marked as read but stays in Junk**; the borderline items are **moved to Triage** with reasons. Inbox is untouched. A second run immediately after is a no-op (nothing unread to classify).
5. **Delete gating.** With `OUTLOOK_JUNK_MCP_ALLOW_DELETE` unset, `delete_from_junk` is not in the tool list (verifiable via `claude --debug` or by logging the tools the host discovers). Set the env var, rerun, confirm the tool now appears.
6. **Cron run.** Force-run the Task Scheduler job. Log file shows clean exit and the moves you expected.
7. **Provider-swap rehearsal (optional but proves the architecture).** Stub a second `IAgentDriver` (e.g. one that just echoes the tool list and exits). Set `OUTLOOK_JUNK_AGENT_PROVIDER=stub` and confirm the host picks it up without any other change. This is the cheap way to prove model-portability before you commit to writing a real second driver.
8. **Graduation rehearsal.** After ~1 week of Phase A with low false-positive rate in Triage reviews, flip the env var, update `rubric.md`, watch one Phase-B cycle interactively (via Claude Code) before letting the cron run unattended.

## Open items (defaults baked in; tell me if you'd rather change)

- **Triage folder name** — default `Triage` (top-level). The server creates it on first run if missing. Configurable via `OUTLOOK_JUNK_MCP_TRIAGE_FOLDER`.
- **Initial LLM provider** — Anthropic Claude (`claude-opus-4-7` or `claude-sonnet-4-6` depending on cost/quality preference). Configurable via `OUTLOOK_JUNK_AGENT_PROVIDER` + provider-specific API key.
- **Anthropic SDK choice for the host** — start with raw `HttpClient` against the Messages API. ~100 LoC including the tool-use loop. Avoids taking a dependency on a community SDK whose lifecycle you don't control. Switch to a community SDK later if you want.
- **MSAL client ID** — read from env var (no client ID committed to source).
- **Hard-delete tool** — not in v1. Adding it later is a small change behind a third env-var; not currently planned.
