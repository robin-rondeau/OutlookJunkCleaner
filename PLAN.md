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

- **Always-on Windows machine.** The cron, the MCP server, and the agent host all run here.
- **LLM API**. Choose from Anthropic API, Groq API (free subscription may be adequate), or local ollama. I chose Groq to start and added support for the others but they haven't been tested yet

Suggested layout on the always-on machine:

```
C:\Tools\OutlookJunkCleaner\
├── .mcp.json                             ← lets Claude Code attach to the MCP server for interactive review
├── rubric.md                             ← narrative classification rubric (you edit this over time)
├── senders.json                          ← structured trusted/junk sender lists (heuristic pre-filter input)
├── bin\
│   ├── OutlookJunkMcp.exe                ← the MCP server (boundary + Graph + MSAL)
│   └── OutlookJunkAgent.exe              ← the cron-driven agent host (LLM-swappable)
├── logs\                                 ← daily run summaries; auto-pruned to 30 days
└── state\
    └── history.jsonl                     ← one line per past classification; rolling 30-day window
```

Per-user, machine-local (not in the project dir):
```
%LOCALAPPDATA%\OutlookJunkMcp\token.cache       ← DPAPI-encrypted MSAL cache
%LOCALAPPDATA%\OutlookJunkAgent\rubric.md       ← optional: tighter-ACL location for the rubric
%LOCALAPPDATA%\OutlookJunkAgent\senders.json    ← optional: tighter-ACL location for the lists
```

The agent prefers the LocalAppData copies of `rubric.md` and `senders.json` and falls back to
the project directory if absent. The SHA-256 of whichever copy it actually loaded is logged
on every run, so silent tampering is visible in `logs\YYYY-MM-DD.log`.

## How the cron run works (end-to-end)

1. Windows Task Scheduler fires on the hour, between 06:00 and 23:00.
2. Action: run `bin\OutlookJunkAgent.exe`, working dir `C:\Tools\OutlookJunkCleaner\`.
3. The agent host:
   1. Sweeps `logs\*.log` older than 30 days (best-effort; failures warn but don't abort).
   2. Spawns `bin\OutlookJunkMcp.exe` as a child process and connects to it as an MCP **client** over stdio.
   3. Discovers the available tool list (Phase A: 7 tools; Phase B: 8) — used to detect Phase A vs B and to feature-detect the Phase-A accuracy lookup tool.
   4. Loads `rubric.md` (narrative) and `senders.json` (structured trusted/junk lists), and constructs a stable, cache-friendly system prompt: behavioral rules, sender lists, narrative rubric, spotlighting trust contract, phase-aware instructions. SHA-256 of each loaded file is recorded in the run summary.
   5. Loads `state\history.jsonl`, prunes entries older than 30 days, and (if the lookup tool is available) calls `mcp.lookup_classification_status` on the IDs of past classifications 48h-30d old. Aggregates rescue rate (false-positive floor for Phase B promotion) and missed-junk rate (triage decisions the user ended up deleting); renders both into the run summary's `classification audit` block.
   6. Calls `mcp.list_junk` to get the working set, then **per-message** (cap `--max-messages`, default 50):
      - Calls `mcp.get_message` (server-side sanitized).
      - `HeuristicClassifier` checks the sender domain against trusted/junk lists. On match, the host emits a deterministic decision (no LLM call) — saving cost and producing reproducible audits.
      - Otherwise: `Spotlighter` wraps the payload in random per-run delimiters; the driver runs **once** as a constrained classifier (forced single-tool `classify` schema producing `{action, confidence, reason}`). Each LLM call gets a fresh context — no cross-message contamination.
      - Host translates the action into the appropriate MCP tool call (`mark_as_read`, `move_to_triage`, or `delete_from_junk`).
      - Host appends a `HistoryEntry` (timestamp, message-id, agent decision, reason, run-token, phase, provider, model, classifier-kind: `llm` or `heuristic`) to `state\history.jsonl`.
   7. Writes the run summary (provider/model, llm-turn count, heuristic-turn count, tool-call count, duration, config snapshot, accuracy block, action list) to `logs\YYYY-MM-DD.log`, exits.
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

### 3. The "training" surface: `rubric.md` and `senders.json`

The user-iterable inputs to the classifier are deliberately split:

- **`senders.json`** — structured trusted/junk sender domain lists. Consumed deterministically by `HeuristicClassifier` *before* the LLM is called: a trusted-list match short-circuits to `not_junk` (routed to Triage), a junk-list match short-circuits to `confident_junk`. Subdomain matching is supported (`mail.linkedin.com` matches `linkedin.com`). Same lists are also concatenated into the LLM system prompt as defense-in-depth — if the heuristic ever fails to fire, the LLM has the same data.
- **`rubric.md`** — narrative classification guidance for the residual: definite-junk patterns, definite-not-junk patterns, ambiguity policy ("when in doubt, triage with a one-line reason"), prompt-injection signals, phase-aware behavioral rules ("in Phase A the action for confident junk is `mark_as_read`, never delete, even if the tool exists"). This is what you iterate on as the agent's classifications evolve.

Both files are loaded at startup. SHA-256 of each is logged with each run so silent edits show up in the audit trail. Both also serve Claude Code interactive sessions (loaded as context manually or via a project-scoped slash command).

### 4. MCP tool surface (the entire allowed action space)

| Tool | Purpose | Phase A | Phase B |
|---|---|---|---|
| `list_junk(limit?, sinceHours?, includeRead?=false)` | List items in Junk: `{id, sender, subject, receivedAt, isRead, hasAttachments, bodyPreview, listUnsubscribe}`. **Defaults to unread-only** so the agent's working set each hour is "what's new since last run." | yes | yes |
| `get_message(id)` | Full body + headers, **only if the message is currently in Junk or Triage** | yes | yes |
| `mark_as_read(id, reason)` | Mark a Junk message as read in place. The agent's action for confident junk in Phase A; remains available in Phase B. Reason is recorded for auditability. | yes | yes |
| `move_to_triage(id, reason)` | Move from Junk → Triage with a reason. Action for ambiguous mail in both phases. The Triage folder lives under Inbox (`Inbox\Triage`) so Outlook clients badge its unread count. | yes | yes |
| `list_triage(limit?)` | List items currently in Triage so the agent can see what's outstanding | yes | yes |
| `get_status()` | `{ junkCount, junkUnreadCount, triageCount, deleteEnabled, allowedFolders }` | yes | yes |
| `lookup_classification_status(ids[])` | For each id, return the current bucket: `junk` / `triage` / `deleted` / `inbox` / `archive` / `other`. Read-only; bypasses the per-session id allow-set so the host can follow up on ids surfaced in *previous* runs. Powers the Phase-A accuracy block. `deleted` collapses Deleted Items and Graph-404 (the recoverable-items dumpster a Junk-direct delete falls into on consumer Outlook): both mean "user didn't rescue it," which is the signal the metric needs. | yes | yes |
| `delete_from_junk(id, reason)` | Move from Junk → Deleted Items (recoverable ~30 days) | **not registered** | registered |

Hard invariants (compiled in, not negotiable from the agent side):

- The agent can only ever read Junk and Triage. Inbox, Archive, Sent, and any custom folders are unreachable.
- The only legal mutations are: mark-read in Junk, move `Junk → Triage`, and (Phase B) move `Junk → Deleted Items`. Triage is read-only to the agent except as a *destination*.
- The agent never sees the access token; it only handles opaque Graph message IDs.
- "Delete" means *move to Deleted Items*, never hard-delete. Outlook.com retains Deleted Items ~30 days, giving you a recovery window.
- These invariants apply identically whether the host is `OutlookJunkAgent.exe` or Claude Code or any future MCP-aware client.

### 5. Phase-A → Phase-B graduation

Phase-A graduation is data-driven via the `classification audit` block printed in every run summary. The block is computed by looking up each past classification's current location via `lookup_classification_status` and aggregating:

- **Confident-junk rescue rate** (`(in inbox + in archive) / total`) is the false-positive floor that will carry forward into Phase B if you flip the env var today. It's the metric that should govern graduation.
- **Triage missed-junk rate** (`deleted / total`) is the rate at which the agent routed something to Triage that you ended up deleting — i.e. should have been `confident_junk`. Useful for tuning the rubric and the trusted/junk lists. `deleted` here is the collapsed bucket: it covers both Deleted Items and the recoverable-items dumpster (a Graph 404 on lookup) so it captures the user's "remove this" signal regardless of whether the message went through Deleted Items first or was hard-deleted from Junk.

When you're satisfied, set `OUTLOOK_JUNK_MCP_ALLOW_DELETE=1` in the Task Scheduler action's environment. The MCP server registers `delete_from_junk` on next run; no rebuild. The host detects this at startup (the discovered tool list contains `delete_from_junk`) and routes `confident_junk` decisions to the delete tool instead of `mark_as_read`. The change is auditable (lives in the task definition). Update `rubric.md` at the same time to tell yourself what bar the classifier must meet before deletion is appropriate.

### 6. Prompt-injection hardening

Email content is attacker-controlled and the agent has tool authority over the mailbox. The folder allow-list eliminates the worst attacks (no inbox-write, no send, no exfiltration), but four narrower threats remain: hide-the-phishing (agent marks attacker mail as read so it blends into reviewed junk), cross-message action (one email tricks the agent into acting on other junk), audit-log poisoning, and Phase-B early deletion. Six measures ship together against these:

1. **One-message-per-iteration isolation.** The "agent loop" is now host-driven; each LLM call sees exactly one email with a fresh `messages` array. Message N's content cannot influence message N+1's classification.
2. **Server-side id scoping.** The MCP server keeps a per-process `SurfacedIds` set populated by `list_junk` / `list_triage`. `get_message` and all mutating tools refuse ids not in the set, defeating prompt-injection attempts that synthesise message ids the agent never legitimately saw.
3. **Spotlighting.** Each run generates a 16-hex random delimiter token. Sanitized email payloads are wrapped in `EMAIL_BEGIN-{token}` / `EMAIL_END-{token}` markers; the system prompt drills into the LLM that anything inside those markers is data, never instructions, and that instructions inside the body are evidence of phishing. Inner literal `EMAIL_BEGIN-` / `EMAIL_END-` substrings are escaped to `[delim]` defensively.
4. **Server-side email sanitization.** `EmailSanitizer` (using HtmlAgilityPack) converts HTML bodies to plain text, drops `<script>` / `<style>` / `<head>` / hidden-CSS subtrees, extracts `<img alt>` and `<a>` into structured `Images` / `Links` fields (with a host-mismatch hint when visible text mentions a different domain than the href), strips zero-width / bidi-override / unicode-tag-block / format / private-use / control characters, and caps the body at 8000 chars. Sanitization lives at the MCP boundary so interactive Claude Code review benefits too.
5. **Output-constrained classifier.** The LLM emits `{action: confident_junk|ambiguous|not_junk, confidence: 0..1, reason: <=200 char string}` and nothing else. Forced via Anthropic `tool_choice: {type: "tool", name: "classify"}` with an inline JSON Schema. Removes the entire free-form tool-call attack surface.
6. **Reason-field hygiene + audit-log prefix.** Both the host (`ReasonHygiene`, ASCII-only) and the server (`ReasonValidator`, UTF-8-preserving) clean the reason string: cap length, strip control / format / private-use code points, collapse whitespace. Audit log lines record the cleaned reason as `agent-asserted: <text>`, visually distinguishing LLM-asserted text from any human-authored audit text.

Verification: `bin\OutlookJunkMcp.exe --test-sanitizer` runs an in-process suite (22 cases) covering each transform; no auth required. `dotnet test` from the repo root runs the broader xUnit suite (51 cases) under `tests/OutlookJunkTests/` covering EmailSanitizer, Spotlighter, ReasonHygiene, and ReasonValidator together. The xUnit suite is the security-boundary regression net; gate any change to those components on it.

## Step-by-step setup

### One-time, on this laptop (dev)

1. Scaffold the solution: two projects, `OutlookJunkMcp` and `OutlookJunkAgent`, plus a shared `OutlookJunkCommon` for the tool-name constants and shared types. Add a fourth project `tests/OutlookJunkTests` (xUnit) for the security-boundary regression suite.
2. NuGet refs:
   - Both: `ModelContextProtocol 1.0.0` (gives you both server and client sides).
   - `OutlookJunkMcp` only: `Microsoft.Identity.Client`, `Microsoft.Identity.Client.Extensions.Msal`, `Microsoft.Graph`, `HtmlAgilityPack`.
   - `OutlookJunkAgent` only: raw `HttpClient` for both Anthropic and Ollama drivers (no SDK dependency — keeps the dependency surface small and easier to audit).
   - Tests: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`.
3. Implement the seven MCP tools, the folder allow-list, the per-session id allow-set, the MSAL bootstrap, the email sanitizer (server side); the heuristic pre-filter, the spotlighter, the per-message classifier loop, the history store, the accuracy computer, and the Anthropic + Ollama drivers (host side).
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

## File inventory

Server side (`src/OutlookJunkMcp/`):
- `OutlookJunkMcp.csproj`, `Program.cs` — entry point, modes (`--first-auth`, `--self-test`, `--test-sanitizer`, default = MCP server over stdio), MSAL bootstrap, DI wiring.
- `Tools/JunkTools.cs` — the always-registered tools (list/get/mark/move/status/lookup), MCP-annotated.
- `Tools/DeleteTool.cs` — Phase-B `delete_from_junk`, conditionally registered based on `OUTLOOK_JUNK_MCP_ALLOW_DELETE`.
- `Tools/ReasonValidator.cs` — server-side reason cleaner (UTF-8-preserving; strips control / format / private-use code points; caps length).
- `Graph/MailClient.cs` — Graph wrapper enforcing folder allow-list and id allow-set before every mutation; also implements `LookupClassificationStatusAsync` for the accuracy lookup tool.
- `Graph/FolderResolver.cs` — resolves Junk, Triage (under Inbox), Deleted Items, Inbox, Archive folder IDs at startup. Creates `Inbox\Triage` if missing; warns on a stale top-level Triage with messages.
- `Graph/Models.cs` — wire records (`JunkMessageInfo`, `MessageDetails`, `MutationResult`, `StatusInfo`, `ClassificationLookupEntry`).
- `Auth/MsalAuth.cs`, `Auth/TokenCacheStorage.cs` — MSAL public-client + DPAPI / keychain / keyring token cache via `Microsoft.Identity.Client.Extensions.Msal`.
- `Config/AppConfig.cs` — env-var reader (`OUTLOOK_JUNK_MCP_ALLOW_DELETE`, `OUTLOOK_JUNK_MCP_TRIAGE_FOLDER` default `Triage`, `OUTLOOK_JUNK_MCP_CLIENT_ID`).
- `Sanitizer/EmailSanitizer.cs` — HTML→text, hidden-CSS subtree drop, image/link extraction with host-mismatch hint, length cap.
- `Sanitizer/UnicodeFilter.cs` — code-point hygiene shared with `ReasonValidator`.
- `Sanitizer/SanitizerSelfTest.cs` — in-process assertion suite (`--test-sanitizer`).
- `Session/SurfacedIds.cs` — per-process id allow-set populated by `list_*` tools, checked by mutating + read tools (not by `lookup_classification_status`, which intentionally bypasses the set so it can follow up on ids surfaced in previous runs).

Agent host (`src/OutlookJunkAgent/`):
- `OutlookJunkAgent.csproj`, `Program.cs` — entry point, log-retention sweep, MCP client connect, config load + hash, accuracy compute, per-message classifier loop.
- `McpClientHost.cs` — typed wrappers over the MCP client (`ListJunkAsync`, `GetMessageAsync`, `MarkAsReadAsync`, `MoveToTriageAsync`, `DeleteFromJunkAsync`, `GetStatusAsync`, `LookupClassificationStatusAsync`). The driver never sees the MCP tool surface.
- `Drivers/IAgentDriver.cs` — provider abstraction (`ClassifyAsync(ClassificationRequest) → ClassificationResult`).
- `Drivers/AnthropicAgentDriver.cs` — Anthropic Messages API via raw `HttpClient`. Forced single-tool `classify` schema; retry/backoff on 429/529/5xx with jitter and `Retry-After` honouring; request-id logging; usage logging at Debug level.
- `Drivers/OllamaAgentDriver.cs` — Ollama HTTP API. Structured-output mode (`format: <json schema>`) enforces the same `{action, confidence, reason}` contract. Lower temperature for determinism.
- `Drivers/DriverFactory.cs` — selects driver by `OUTLOOK_JUNK_AGENT_PROVIDER`.
- `HeuristicClassifier.cs` — deterministic pre-filter; trusted-domain → `not_junk`, junk-domain → `confident_junk`, otherwise null (LLM runs).
- `SendersStore.cs` — loads + validates `senders.json`; tolerant of malformed entries (warn-and-skip).
- `RubricLoader.cs` — composes the system prompt: trust contract, decision spec, sender lists, rubric.
- `Sanitizer/Spotlighter.cs` — per-run delimiter token + payload wrap with delimiter escape (case-insensitive).
- `ReasonHygiene.cs` — host-side ASCII-only reason cleaner.
- `HistoryStore.cs` — append-only `state/history.jsonl`; rolling 30-day prune.
- `AccuracyComputer.cs` — reads history, calls `lookup_classification_status`, aggregates rescue rate / missed-junk rate by decision class.
- `ConfigPaths.cs` — resolves `rubric.md` / `senders.json` (LocalAppData preferred, working dir fallback) and computes SHA-256 prefixes for the audit trail.
- `LogRetention.cs` — deletes `logs/*.log` older than 30 days at startup.
- `RunSummary.cs` — per-run record (provider/model/llm-turns/heuristic-turns/tool-calls/duration/config snapshot/accuracy block/actions/error), written to the daily log file and stdout.

Shared (`src/OutlookJunkCommon/`):
- `ToolNames.cs` — string constants for tool names + `EnvVars` + `FolderNames`.

Tests (`tests/OutlookJunkTests/`):
- `EmailSanitizerTests.cs`, `SpotlighterTests.cs`, `ReasonHygieneTests.cs` — xUnit suite (51 cases) covering the prompt-injection security boundary. Runs via `dotnet test`; no auth or Graph required.

Project root:
- `.mcp.json` — registers `outlook-junk` as a stdio MCP server pointing at `bin\OutlookJunkMcp.exe`. Used by Claude Code (interactive review). The agent host has its own way of launching the server (no .mcp.json dependency).
- `rubric.md` — narrative classification rubric and phase-aware behavioral rules.
- `senders.json` — structured trusted/junk sender lists consumed by both the heuristic pre-filter and the LLM system prompt.
- `scripts\install-task.ps1` — registers the Task Scheduler job.
- `scripts\first-auth.ps1` — wraps `OutlookJunkMcp.exe --first-auth` with friendlier output.
- `README.md` — quick start, troubleshooting, graduation procedure, how to add a new LLM driver.

## Verification

End-to-end checks before considering this "working":

1. **Server `--self-test`** prints `Junk: N (M unread), Triage: K` and exits 0. Confirms MSAL + scope + folder lookup + folder allow-list initialization.
2. **Sanitizer `--test-sanitizer`** prints `22 / 22 sanitizer self-tests passed`. Confirms the EmailSanitizer pipeline (HTML drop tags, hidden-CSS subtree drop, unicode-class stripping, image/link extraction with host-mismatch detection, length cap). No auth required. **`dotnet test`** from the repo root runs the broader xUnit suite (51 cases) covering EmailSanitizer + Spotlighter + ReasonHygiene + ReasonValidator together; gate on it for any change to those components.
3. **MCP handshake (interactive).** From the project dir, `claude --debug` lists six tools (Phase A) under `/mcp outlook-junk`. Confirms the server is wired correctly and Claude Code can attach.
4. **Boundary tests, both layers.** Grab a message ID from your Inbox via Outlook on the web. Ask Claude Code interactively to call `get_message` on it directly (without listing first). The server must refuse with `id_not_surfaced` (the per-session allow-set check). Then call `list_triage` first and ask `get_message` on an Inbox id again — must now refuse with "not in an allowed folder" (the folder-boundary check). Both layers prove the security model is real, not advisory.
5. **Phase A behavior (cron host).** Seed Junk with a mix of (a) obviously-junk repeat-offender mail and (b) a couple of borderline items. Run `bin\OutlookJunkAgent.exe`. The obvious junk is **marked as read but stays in Junk**; the borderline items are **moved to Triage** with reasons. Inbox is untouched. A second run immediately after is a no-op (nothing unread to classify).
6. **Delete gating.** With `OUTLOOK_JUNK_MCP_ALLOW_DELETE` unset, `delete_from_junk` is not in the tool list (verifiable via `claude --debug` or by logging the tools the host discovers). Set the env var, rerun, confirm the tool now appears.
7. **Cron run.** Force-run the Task Scheduler job. Log file shows clean exit and the moves you expected.
8. **Provider-swap rehearsal (optional but proves the architecture).** Stub a second `IAgentDriver` returning fixed `ClassificationResult`s. Set `OUTLOOK_JUNK_AGENT_PROVIDER=stub` and confirm the host picks it up without any other change. This is the cheap way to prove model-portability before you commit to writing a real second driver.
9. **Graduation rehearsal.** After ~1 week of Phase A with low false-positive rate in Triage reviews, flip the env var, update `rubric.md`, watch one Phase-B cycle interactively (via Claude Code) before letting the cron run unattended.

## Open items (defaults baked in; tell me if you'd rather change)

- **Triage folder location** — `Inbox\Triage`, display name configurable via `OUTLOOK_JUNK_MCP_TRIAGE_FOLDER` (default `Triage`). Created on first run if missing. Living under Inbox is what gets it badged as unread by Outlook clients; a top-level folder doesn't get badged on most clients.
- **Default LLM provider/model** — Anthropic Claude Haiku 4.5 (`claude-haiku-4-5-20251001`). Bump to Sonnet 4.6 or Opus 4.7 via `OUTLOOK_JUNK_AGENT_MODEL`, or switch to a local model entirely with `OUTLOOK_JUNK_AGENT_PROVIDER=ollama`.
- **Anthropic SDK choice for the host** — raw `HttpClient` against the Messages API. ~170 LoC for the forced-tool classifier driver. Avoids taking a dependency on a community SDK whose lifecycle you don't control. Switch to a community SDK later if you want.
- **MSAL client ID** — read from env var (no client ID committed to source).
- **History retention** — 30 days for both `state\history.jsonl` and `logs\*.log`, hardcoded. Long enough to span any Phase-A evaluation window; short enough that the files stay small. Pruned at startup of every run.
- **Hard-delete tool** — not in v1. Adding it later is a small change behind a third env-var; not currently planned.
