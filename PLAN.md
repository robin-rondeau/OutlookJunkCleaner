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
   2. Discovers the available tool list (Phase A: 6 tools; Phase B: 7) — used only to detect Phase A vs B.
   3. Loads `rubric.md` and constructs a stable, cache-friendly system prompt: rubric + behavioral rules + phase-aware instructions + the spotlighting trust contract.
   4. Calls `mcp.list_junk` to get the working set, then **per-message**: calls `mcp.get_message` (server-side sanitized), wraps the payload in random per-run delimiters via `Spotlighter`, calls the LLM **once** as a constrained classifier (forced single-tool `classify` schema producing `{action, confidence, reason}`). The host translates the action into the appropriate MCP tool call (`mark_as_read`, `move_to_triage`, or `delete_from_junk`). Each LLM call gets a fresh context — no cross-message contamination.
   5. After processing the working set (cap `--max-messages`, default 50), writes a summary to `logs\YYYY-MM-DD.log`, exits.
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
- References the **MCP client** half of the C# MCP SDK to talk to the server child process. Exposes typed wrappers (`ListJunkAsync`, `GetMessageAsync`, `MarkAsReadAsync`, `MoveToTriageAsync`, `DeleteFromJunkAsync`, `GetStatusAsync`) so the host loop never deals in free-form tool dispatch.
- Has a small `IAgentDriver` abstraction with one method: `ClassifyAsync(ClassificationRequest) → ClassificationResult`. The driver receives a stable system prompt + a per-message spotlighted payload, and returns one of `{ConfidentJunk, Ambiguous, NotJunk}` with a confidence and a short reason. The driver does NOT see the MCP tool surface; the host alone decides which tool to invoke based on the action.
- Two shipped drivers:
  - `AnthropicAgentDriver` — Anthropic Messages API via raw `HttpClient`. Forces tool-use of a single inline `classify` schema (`tool_choice: {"type":"tool","name":"classify"}`); the model physically cannot emit anything else. Includes retry/backoff for 429/529/5xx with `Retry-After` honouring, request-id logging, and Debug-level usage logging (incl. cache_read tokens for verifying prompt-cache effectiveness). Default model: `claude-haiku-4-5-20251001` (cheap-tier 2026 Haiku, single-digit dollars/month at typical volume with prompt caching).
  - `OllamaAgentDriver` — local Ollama HTTP API at `localhost:11434`. Uses Ollama's structured-output mode (`format: <json schema>`) to enforce the same `{action, confidence, reason}` contract. $0 marginal cost. Default model: `llama3.1:8b`. Smaller models are easier to fool with injection at the prompt level, but the MCP-side defenses still apply.
- To swap to another provider (OpenAI, Bedrock, Azure, …), write a third driver and choose by env var `OUTLOOK_JUNK_AGENT_PROVIDER=anthropic|ollama|<new>`. The MCP server, rubric, cron, security model, and host loop stay unchanged. OpenAI's equivalent of forced-tool is `response_format: {"type":"json_schema"}`; same constraint, different payload shape.

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

Set `OUTLOOK_JUNK_MCP_ALLOW_DELETE=1` in the Task Scheduler action's environment. The MCP server registers `delete_from_junk` on next run; no rebuild. The host detects this at startup (the discovered tool list contains `delete_from_junk`) and routes `confident_junk` decisions to the delete tool instead of `mark_as_read`. The change is auditable (lives in the task definition). Update `rubric.md` at the same time to tell yourself what bar the classifier must meet before deletion is appropriate.

### 6. Prompt-injection hardening

Email content is attacker-controlled and the agent has tool authority over the mailbox. The folder allow-list eliminates the worst attacks (no inbox-write, no send, no exfiltration), but four narrower threats remain: hide-the-phishing (agent marks attacker mail as read so it blends into reviewed junk), cross-message action (one email tricks the agent into acting on other junk), audit-log poisoning, and Phase-B early deletion. Six measures ship together against these:

1. **One-message-per-iteration isolation.** The "agent loop" is now host-driven; each LLM call sees exactly one email with a fresh `messages` array. Message N's content cannot influence message N+1's classification.
2. **Server-side id scoping.** The MCP server keeps a per-process `SurfacedIds` set populated by `list_junk` / `list_triage`. `get_message` and all mutating tools refuse ids not in the set, defeating prompt-injection attempts that synthesise message ids the agent never legitimately saw.
3. **Spotlighting.** Each run generates a 16-hex random delimiter token. Sanitized email payloads are wrapped in `EMAIL_BEGIN-{token}` / `EMAIL_END-{token}` markers; the system prompt drills into the LLM that anything inside those markers is data, never instructions, and that instructions inside the body are evidence of phishing. Inner literal `EMAIL_BEGIN-` / `EMAIL_END-` substrings are escaped to `[delim]` defensively.
4. **Server-side email sanitization.** `EmailSanitizer` (using HtmlAgilityPack) converts HTML bodies to plain text, drops `<script>` / `<style>` / `<head>` / hidden-CSS subtrees, extracts `<img alt>` and `<a>` into structured `Images` / `Links` fields (with a host-mismatch hint when visible text mentions a different domain than the href), strips zero-width / bidi-override / unicode-tag-block / format / private-use / control characters, and caps the body at 8000 chars. Sanitization lives at the MCP boundary so interactive Claude Code review benefits too.
5. **Output-constrained classifier.** The LLM emits `{action: confident_junk|ambiguous|not_junk, confidence: 0..1, reason: <=200 char string}` and nothing else. Forced via Anthropic `tool_choice: {type: "tool", name: "classify"}` with an inline JSON Schema. Removes the entire free-form tool-call attack surface.
6. **Reason-field hygiene + audit-log prefix.** Both the host (`ReasonHygiene`, ASCII-only) and the server (`ReasonValidator`, UTF-8-preserving) clean the reason string: cap length, strip control / format / private-use code points, collapse whitespace. Audit log lines record the cleaned reason as `agent-asserted: <text>`, visually distinguishing LLM-asserted text from any human-authored audit text.

Verification: `bin\OutlookJunkMcp.exe --test-sanitizer` runs an in-process suite (22 cases) covering each transform; no auth required.

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
6. Smoke-test the host: `bin\OutlookJunkAgent.exe --dry-run` runs the full pipeline (classifies every working-set message) but the host short-circuits before invoking any mutating MCP tool. The summary records `would:<tool>` entries so you can confirm the action list is sensible.
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
- `OutlookJunkAgent/Drivers/AnthropicAgentDriver.cs` — initial driver. ~170 LoC: single forced-tool call against the inline `classify` schema. No iteration loop, no MCP tool translation.
- `OutlookJunkAgent/Drivers/ClassificationContracts.cs` — `ClassificationRequest` / `ClassificationResult` / `ClassificationAction` records (the provider-agnostic wire format between host and driver).
- `OutlookJunkAgent/Sanitizer/Spotlighter.cs` — per-run delimiter token + payload wrap with delimiter escape.
- `OutlookJunkAgent/ReasonHygiene.cs` — host-side ASCII-only reason cleaner.
- `OutlookJunkAgent/RunSummary.cs` — per-run actions + run token + final classification audit, written to the daily log file.

Server-side hardening (added with the prompt-injection work):
- `OutlookJunkMcp/Sanitizer/EmailSanitizer.cs` — HTML→text, hidden-CSS subtree drop, image/link extraction with host-mismatch hint, length cap.
- `OutlookJunkMcp/Sanitizer/UnicodeFilter.cs` — code-point hygiene shared with `ReasonValidator`.
- `OutlookJunkMcp/Session/SurfacedIds.cs` — per-process id allow-set populated by `list_*` tools, checked by mutating + read tools.
- `OutlookJunkMcp/Tools/ReasonValidator.cs` — server-side reason cleaner (UTF-8-preserving).
- `OutlookJunkMcp/Sanitizer/SanitizerSelfTest.cs` — in-process assertion suite (`--test-sanitizer`).

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
2. **Sanitizer `--test-sanitizer`** prints `22 / 22 sanitizer self-tests passed`. Confirms the EmailSanitizer pipeline (HTML drop tags, hidden-CSS subtree drop, unicode-class stripping, image/link extraction with host-mismatch detection, length cap). No auth required.
3. **MCP handshake (interactive).** From the project dir, `claude --debug` lists six tools (Phase A) under `/mcp outlook-junk`. Confirms the server is wired correctly and Claude Code can attach.
4. **Boundary tests, both layers.** Grab a message ID from your Inbox via Outlook on the web. Ask Claude Code interactively to call `get_message` on it directly (without listing first). The server must refuse with `id_not_surfaced` (the per-session allow-set check). Then call `list_triage` first and ask `get_message` on an Inbox id again — must now refuse with "not in an allowed folder" (the folder-boundary check). Both layers prove the security model is real, not advisory.
5. **Phase A behavior (cron host).** Seed Junk with a mix of (a) obviously-junk repeat-offender mail and (b) a couple of borderline items. Run `bin\OutlookJunkAgent.exe`. The obvious junk is **marked as read but stays in Junk**; the borderline items are **moved to Triage** with reasons. Inbox is untouched. A second run immediately after is a no-op (nothing unread to classify).
6. **Delete gating.** With `OUTLOOK_JUNK_MCP_ALLOW_DELETE` unset, `delete_from_junk` is not in the tool list (verifiable via `claude --debug` or by logging the tools the host discovers). Set the env var, rerun, confirm the tool now appears.
7. **Cron run.** Force-run the Task Scheduler job. Log file shows clean exit and the moves you expected.
8. **Provider-swap rehearsal (optional but proves the architecture).** Stub a second `IAgentDriver` returning fixed `ClassificationResult`s. Set `OUTLOOK_JUNK_AGENT_PROVIDER=stub` and confirm the host picks it up without any other change. This is the cheap way to prove model-portability before you commit to writing a real second driver.
9. **Graduation rehearsal.** After ~1 week of Phase A with low false-positive rate in Triage reviews, flip the env var, update `rubric.md`, watch one Phase-B cycle interactively (via Claude Code) before letting the cron run unattended.

## Open items (defaults baked in; tell me if you'd rather change)

- **Triage folder name** — default `Triage` (top-level). The server creates it on first run if missing. Configurable via `OUTLOOK_JUNK_MCP_TRIAGE_FOLDER`.
- **Default LLM provider/model** — Anthropic Claude Haiku 4.5 (`claude-haiku-4-5-20251001`). Bump to Sonnet 4.6 or Opus 4.7 via `OUTLOOK_JUNK_AGENT_MODEL`, or switch to a local model entirely with `OUTLOOK_JUNK_AGENT_PROVIDER=ollama`.
- **Anthropic SDK choice for the host** — raw `HttpClient` against the Messages API. ~170 LoC for the forced-tool classifier driver. Avoids taking a dependency on a community SDK whose lifecycle you don't control. Switch to a community SDK later if you want.
- **MSAL client ID** — read from env var (no client ID committed to source).
- **Hard-delete tool** — not in v1. Adding it later is a small change behind a third env-var; not currently planned.
