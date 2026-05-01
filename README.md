# OutlookJunkCleaner

A junk-mail triage agent for a single user's Outlook **consumer** account (`outlook.com`,
`hotmail.com`, `live.com`). Runs hourly via Windows Task Scheduler, classifies unread Junk
messages against a user-maintained rubric, and either marks confident junk as read in place
(Phase A) or eventually deletes it (Phase B). Ambiguous mail is moved to a Triage folder
for manual review.

The architecture is designed around three properties:

- **Folder-scoped boundary.** Microsoft Graph has no folder-level OAuth scopes, so the boundary
  is enforced in code by an MCP server that holds the credentials and only exposes a narrow
  tool surface to whatever LLM is driving it.
- **LLM-provider portability.** A small custom C# host drives the cron run against any provider
  (default: Anthropic Claude). To swap providers later, write a second `IAgentDriver` and change
  one env var. No other piece changes.
- **Prompt-injection hardening.** Email content is attacker-controlled. The host classifies one
  message at a time with a fresh LLM context (no cross-message contamination), feeds the LLM
  a server-side-sanitized payload (HTML stripped, unicode-formatting code points removed,
  links/images extracted into structured fields) wrapped in a per-run random delimiter, and
  forces the LLM into an output-constrained tool schema (`{action, confidence, reason}`) so it
  cannot freely call mailbox-mutating tools. The host translates the classification into the
  appropriate MCP call. The MCP server additionally refuses ids it didn't surface in this session.

See `PLAN.md` for the design rationale.

## Architecture

```
[Windows Task Scheduler]
   |  hourly, 06:00-23:00
   v
[OutlookJunkAgent.exe]   <- the cron-driven host (LLM-swappable)
   |--- spawns MCP server as child process (stdio)
   |--- discovers tool list (used to detect Phase A vs Phase B)
   |--- loads rubric.md -> builds stable cached system prompt
   '--- per-message classifier loop:
        for each unread Junk message:
          mcp.get_message -> Spotlighter wraps in random delimiters
          driver.ClassifyAsync (one-shot, fresh LLM context)
          host dispatches mark_as_read | move_to_triage | delete_from_junk
                    |
                    v
[OutlookJunkMcp.exe]   <- the boundary (single owner of credentials)
   |--- MSAL.NET -> Microsoft Graph -> user's mailbox
   |--- sanitizes email content (HTML strip, unicode hygiene, link/image extraction)
   |--- per-session id allow-set (refuses unknown ids)
   '--- exposes 6 (Phase A) or 7 (Phase B) tools, folder-allow-listed
```

Tool surface (Phase A):
- `list_junk(limit?, sinceHours?, includeRead?=false)` - defaults to unread-only
- `list_triage(limit?)`
- `get_message(id)` - refuses messages not currently in Junk or Triage
- `mark_as_read(id, reason)` - confident-junk action in Phase A
- `move_to_triage(id, reason)` - ambiguous-mail action in either phase
- `get_status()`

Phase B adds:
- `delete_from_junk(id, reason)` - moves to Deleted Items (recoverable ~30 days)

## Repository layout

```
OutlookJunkCleaner.sln
src/
  OutlookJunkCommon/   shared constants (tool names, env vars)
  OutlookJunkMcp/      MCP server: Graph + MSAL + folder allow-list
  OutlookJunkAgent/    cron host: MCP client + IAgentDriver + Anthropic driver
rubric.md              classification rubric - edit this as you train
.mcp.json              lets Claude Code attach for ad-hoc interactive review
scripts/
  first-auth.ps1
  install-task.ps1
PLAN.md
```

## Prerequisites

1. **.NET 9 SDK** on the build machine (`dotnet --version` >= 9.0).
2. **Azure app registration** for personal Microsoft accounts:
   - Azure portal -> App registrations -> New registration
   - Supported account types: **Personal Microsoft accounts only**
   - Redirect URI (public client / native): `https://login.microsoftonline.com/common/oauth2/nativeclient`
   - Authentication blade -> "Allow public client flows" -> **Yes**
   - API permissions -> Microsoft Graph -> Delegated -> `Mail.ReadWrite`
   - Note the application (client) ID - you'll set it as `OUTLOOK_JUNK_MCP_CLIENT_ID`.
3. **Anthropic API key** in `ANTHROPIC_API_KEY` (or substitute another provider - see "Swapping LLM providers").
4. **Always-on Windows machine** with the same user account that will run the scheduled task
   (DPAPI token cache is per-user).

## Build

```bash
# from the repo root
dotnet restore
dotnet build -c Release
```

For deployment, publish self-contained `win-x64` so the always-on machine doesn't need a
.NET runtime install:

```bash
dotnet publish ./src/OutlookJunkMcp   -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish/bin
dotnet publish ./src/OutlookJunkAgent -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish/bin
```

This produces `OutlookJunkMcp.exe` and `OutlookJunkAgent.exe` under `publish/bin/`.

## Deploy

On the always-on Windows machine:

1. Pick a stable directory, e.g. `C:\Tools\OutlookJunkCleaner\`.
2. Copy `rubric.md`, `.mcp.json`, and `scripts/` from this repo to that directory.
3. Copy the published `bin/` directory (containing both `.exe` files and dependencies) into it.

   ```
   C:\Tools\OutlookJunkCleaner\
     .mcp.json
     rubric.md
     scripts\
     bin\
       OutlookJunkMcp.exe
       OutlookJunkAgent.exe
   ```

4. Set the per-user environment variables (PowerShell):

   ```powershell
   [Environment]::SetEnvironmentVariable('OUTLOOK_JUNK_MCP_CLIENT_ID','<your-azure-client-id>','User')
   [Environment]::SetEnvironmentVariable('ANTHROPIC_API_KEY','sk-ant-...','User')
   # Optional:
   [Environment]::SetEnvironmentVariable('OUTLOOK_JUNK_MCP_TRIAGE_FOLDER','Triage','User')
   # Default model is claude-haiku-4-5-20251001 (cheapest tier; plenty smart for this task).
   # Override for higher quality at higher cost:
   [Environment]::SetEnvironmentVariable('OUTLOOK_JUNK_AGENT_MODEL','claude-opus-4-7','User')
   ```

   To run a local model instead of calling the Anthropic API, see the **Local LLM via Ollama**
   section below.

   Open a new terminal so they take effect.

5. **First-auth** (one-time, interactive):

   ```powershell
   cd C:\Tools\OutlookJunkCleaner
   .\scripts\first-auth.ps1
   ```

   Follow the device-code prompt, sign in to your Outlook consumer account, grant `Mail.ReadWrite`.

6. **Smoke-test** the server alone:

   ```powershell
   .\bin\OutlookJunkMcp.exe --self-test
   # Expected: prints "Junk: N (M unread)\nTriage: K\nDeleteEnabled: False"

   .\bin\OutlookJunkMcp.exe --test-sanitizer
   # Expected: 22 / 22 sanitizer self-tests passed
   # Runs the in-process EmailSanitizer assertion suite. No auth needed.
   ```

7. **Smoke-test** the agent host (dry-run - classifies but does not mutate the mailbox):

   ```powershell
   .\bin\OutlookJunkAgent.exe --dry-run
   # Inspect the printed summary and logs\YYYY-MM-DD.log.
   ```

8. **Install the scheduled task** (admin PowerShell):

   ```powershell
   .\scripts\install-task.ps1
   ```

   Force a test run from Task Scheduler with `Start-ScheduledTask -TaskName "OutlookJunkCleaner"`
   and inspect `logs\`.

## Phase A -> Phase B graduation

Phase A is the default. The agent's confident-junk action is `mark_as_read` (the message stays
in Junk, just with the read flag set). You audit by looking at Junk: read = agent-classified;
unread = agent hasn't seen it yet. Watch for ~1 week, edit `rubric.md` whenever the agent
makes a mistake.

When you're satisfied, graduate:

```powershell
[Environment]::SetEnvironmentVariable('OUTLOOK_JUNK_MCP_ALLOW_DELETE','1','User')
```

And update `rubric.md`'s opening paragraph to tell the agent it now has delete authority.
The next hourly run will see `delete_from_junk` in its tool list and use it for confident
junk; ambiguous mail still goes to Triage.

To roll back: unset the env var and restart any open terminals.

## Interactive review with Claude Code

`.mcp.json` registers the MCP server for Claude Code. From the project directory on the
Windows machine:

```powershell
cd C:\Tools\OutlookJunkCleaner
claude
```

Then ask things like "what's in Triage and why?" or "show me the last 20 messages the agent
marked as read" - Claude Code uses the same MCP server and the same folder boundary as the
cron host. This is purely for ad-hoc review; the cron does not depend on Claude Code.

## Local LLM via Ollama

If you'd rather run a local model than pay per-message API cost (or you don't want an
Anthropic account at all), the project ships a second `IAgentDriver` for Ollama:

1. Install Ollama on the always-on Windows machine: <https://ollama.com/download>.
2. Pull a model — `llama3.1:8b` is a sensible default for this workload, or try
   `qwen2.5:7b-instruct` / `mistral:7b-instruct` if you want to compare. ~5 GB disk per model.

   ```powershell
   ollama serve         # in one terminal, leave running
   ollama pull llama3.1:8b
   ```
3. Set env vars:

   ```powershell
   [Environment]::SetEnvironmentVariable('OUTLOOK_JUNK_AGENT_PROVIDER','ollama','User')
   # Optional overrides:
   [Environment]::SetEnvironmentVariable('OUTLOOK_JUNK_OLLAMA_MODEL','llama3.1:8b','User')
   [Environment]::SetEnvironmentVariable('OUTLOOK_JUNK_OLLAMA_BASE_URL','http://localhost:11434','User')
   ```

4. Run the agent — the same `OutlookJunkAgent.exe` now drives the local model. The MCP
   server, rubric, cron schedule, sanitization, id scoping, and Phase A/B state are
   unchanged.

The Ollama driver uses Ollama's structured-output mode (`format: <json schema>`, available
in Ollama 0.5+) to enforce the same `{action, confidence, reason}` contract as the Anthropic
driver. Smaller models (8B-class) are more easily fooled by injection attempts hidden in the
body — but the MCP server's defenses (id scoping, sanitization, server-side reason
validation) still apply, so the blast radius of an injection success is unchanged.

Cost: $0 marginal beyond the electricity to run the always-on Windows machine you already have.

## Swapping to another LLM provider

To run against an unsupported provider (OpenAI, Azure OpenAI, Bedrock, vLLM, …):

1. Add a new file under `src/OutlookJunkAgent/Drivers/`, e.g. `OpenAiAgentDriver.cs`,
   implementing `IAgentDriver`. The interface is one method (`ClassifyAsync`); model the
   driver after `AnthropicAgentDriver` or `OllamaAgentDriver`. Translate the inline
   `classify` JSON Schema to the provider's structured-output mechanism (forced tool use,
   `response_format: {type:"json_schema"}`, etc. — they're all JSON Schema variants).
2. Add a case in `DriverFactory.Create` that returns the new driver when
   `OUTLOOK_JUNK_AGENT_PROVIDER` matches.
3. Set the env var (`OUTLOOK_JUNK_AGENT_PROVIDER=openai`) plus whatever credentials the
   driver needs.
4. Rebuild and redeploy `OutlookJunkAgent.exe`. The MCP server, rubric, cron, and Phase A/B
   state are all unchanged.

## Where things live on disk

- **Token cache:** `%LOCALAPPDATA%\OutlookJunkMcp\token.cache` - DPAPI-encrypted, per-user.
- **Per-day audit log:** `<project-dir>\logs\YYYY-MM-DD.log` - what tools the agent called,
  with the reasons it provided.

## Troubleshooting

- **`MsalUiRequiredException: no_account`** on a server run -> you skipped or invalidated
  first-auth. Re-run `scripts\first-auth.ps1`.
- **Task Scheduler reports success but the log shows nothing happening** -> the task may be
  running as a different user than the one that ran first-auth. Check the Principal on the
  task; DPAPI fails silently across users.
- **Agent doesn't see `delete_from_junk` in Phase B** -> you set the env var system-wide
  but the Task Scheduler action inherits a snapshot at registration time. Re-run
  `install-task.ps1 -Force` after setting the env var.
- **Anthropic 4xx errors** -> check `ANTHROPIC_API_KEY` and the model id. The default
  (`claude-haiku-4-5-20251001`) is the cheap-tier 2026 Haiku; override via `OUTLOOK_JUNK_AGENT_MODEL`
  to use Sonnet 4.6 (`claude-sonnet-4-6`) or Opus 4.7 (`claude-opus-4-7`) if you want higher
  classification quality.
- **Anthropic 429 / 529** -> the driver retries automatically (up to 5 attempts, exponential
  backoff with jitter, respects `Retry-After`). If you see persistent failures in the log,
  you've likely hit a daily / monthly quota — flip to a smaller model or wait for the window
  to reset. Each warning logs the Anthropic `request-id` for support tickets.
- **Ollama "connection refused"** -> the local Ollama server isn't running. Start it
  (`ollama serve`) and pre-pull the model (`ollama pull llama3.1:8b` or whatever
  `OUTLOOK_JUNK_OLLAMA_MODEL` is set to).

## Implementation status

The full source has been written but the solution has **not yet been built** - the
development environment that produced it didn't have the .NET SDK available. First
local build is expected to surface a small number of API-shape adjustments,
specifically in three areas:

- **`ModelContextProtocol` C# SDK** (pinned to `0.6.0`). Type names like
  `CallToolResult` / `CallToolResponse`, `Content` / `ContentBlock`, and the exact tool-schema
  property name on `McpClientTool` have shifted across SDK versions. If `dotnet build`
  reports missing types in `src/OutlookJunkAgent/McpClientHost.cs`, those are the lines
  to look at - bump the package version or rename the types. The protocol behavior is
  stable; only the C# names are in flux.
- **`Microsoft.Graph` v5 SDK** request-builder paths (`Me.Messages[id].Move.PostAsync(...)`,
  `MovePostRequestBody`). These are stable from 5.40+ but the namespace import may need
  adjustment depending on the exact patch version.
- **`HtmlAgilityPack`** (pinned to `1.11.65`) — used by `EmailSanitizer` for HTML→text. The
  `HtmlNode` / `HtmlTextNode` / `HtmlEntity` API has been stable for years; mostly a confidence
  check that the package resolves.

Run `dotnet build -c Release` from the repo root after install and report any errors;
the rest of the design (folder allow-list, MSAL flow, classifier dispatch, Phase A/B gating,
sanitizer pipeline) is provider-stable and shouldn't need rework. After build, run
`bin\OutlookJunkMcp.exe --test-sanitizer` to verify the sanitizer transforms before
exercising the live mailbox path.
