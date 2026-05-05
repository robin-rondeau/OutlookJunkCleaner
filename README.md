# OutlookJunkCleaner

A junk-mail triage agent for a single user's Outlook **consumer** account (`outlook.com`,
`hotmail.com`, `live.com`). Runs hourly via Windows Task Scheduler, classifies unread Junk
messages against user-maintained rubric and sender lists, and either marks confident junk as
read in place (Phase A) or eventually deletes it (Phase B). Ambiguous mail is moved to a
Triage folder under the Inbox so the user's Outlook clients badge its unread count.

The architecture is designed around four properties:

- **Folder-scoped boundary.** Microsoft Graph has no folder-level OAuth scopes, so the boundary
  is enforced in code by an MCP server that holds the credentials and only exposes a narrow
  tool surface to whatever LLM is driving it.
- **LLM-provider portability.** A small custom C# host drives the cron run against any provider
  (default: Anthropic Claude). To swap providers later, write a second `IAgentDriver` and change
  one env var. No other piece changes.
- **Heuristic-first, LLM-second.** Trusted and known-junk sender domains in `senders.json` are
  matched deterministically before the LLM is called, both to cut API cost and to make the
  obvious cases auditable and reproducible. The LLM only classifies messages that don't match
  either list.
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
   |--- log-retention sweep (drops logs/*.log older than 30 days)
   |--- spawns MCP server as child process (stdio)
   |--- discovers tool list (detects Phase A vs B; detects accuracy-lookup support)
   |--- loads rubric.md + senders.json (LocalAppData preferred; logs SHA-256 of each)
   |--- prunes state/history.jsonl to 30 days, computes Phase-A accuracy from recent entries
   '--- per-message classifier loop (cap --max-messages, default 50):
        for each unread Junk message:
          mcp.get_message  ->  HeuristicClassifier (trusted/junk-list match)
                           ->  if matched: skip LLM; reason = list-match
                           ->  else:        Spotlighter wraps in random delimiters,
                                            driver.ClassifyAsync (one-shot, fresh LLM context)
          host dispatches mark_as_read | move_to_triage | delete_from_junk
          host appends a HistoryEntry to state/history.jsonl
                    |
                    v
[OutlookJunkMcp.exe]   <- the boundary (single owner of credentials)
   |--- MSAL.NET -> Microsoft Graph -> user's mailbox
   |--- sanitizes email content (HTML strip, unicode hygiene, link/image extraction)
   |--- per-session id allow-set (refuses unknown ids on mutating + get_message)
   '--- exposes 7 (Phase A) or 8 (Phase B) tools, folder-allow-listed
```

Tool surface (Phase A):
- `list_junk(limit?, sinceHours?, includeRead?=false)` - defaults to unread-only
- `list_triage(limit?)`
- `get_message(id)` - refuses messages not currently in Junk or Triage
- `mark_as_read(id, reason)` - confident-junk action in Phase A
- `move_to_triage(id, reason)` - ambiguous-mail action in either phase
- `get_status()`
- `lookup_classification_status(ids[])` - bucket each id into junk/triage/deleted/inbox/archive/other/not_found.
  Used by the host to compute Phase-A accuracy from past classifications. Read-only; bypasses the
  per-session id allow-set so the host can follow up on ids surfaced in *previous* runs.

Phase B adds:
- `delete_from_junk(id, reason)` - moves to Deleted Items (recoverable ~30 days)

## Repository layout

```
OutlookJunkCleaner.sln
src/
  OutlookJunkCommon/   shared constants (tool names, env vars)
  OutlookJunkMcp/      MCP server: Graph + MSAL + folder allow-list + sanitizer
  OutlookJunkAgent/    cron host: MCP client + IAgentDriver + heuristics + history + accuracy
tests/
  OutlookJunkTests/    xUnit suite for the prompt-injection security boundary
rubric.md              narrative classification rubric - edit this as you train
senders.json           structured trusted/junk sender lists - edit alongside rubric
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
dotnet test                    # runs the xUnit suite under tests/OutlookJunkTests
```

The test suite covers the prompt-injection security boundary (EmailSanitizer, Spotlighter,
ReasonHygiene, ReasonValidator). It runs in-process and needs no auth or Graph access — gate
on it for any change to those components.

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
2. Copy `rubric.md`, `senders.json`, `.mcp.json`, and `scripts/` from this repo to that directory.
3. Copy the published `bin/` directory (containing both `.exe` files and dependencies) into it.

   ```
   C:\Tools\OutlookJunkCleaner\
     .mcp.json
     rubric.md
     senders.json
     scripts\
     bin\
       OutlookJunkMcp.exe
       OutlookJunkAgent.exe
   ```

   For tighter ACLs on the prompt-shaping inputs, optionally move `rubric.md` and
   `senders.json` to `%LocalAppData%\OutlookJunkAgent\` instead. The agent prefers that path
   and falls back to the working directory when absent. Either way, the SHA-256 of each file
   is logged on every run so a silent edit is visible in `logs\YYYY-MM-DD.log`.

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
   # Side-effect on first run: creates Inbox\Triage if missing.

   .\bin\OutlookJunkMcp.exe --test-sanitizer
   # Expected: 22 / 22 sanitizer self-tests passed
   # Runs the in-process EmailSanitizer assertion suite. No auth needed.
   ```

   For the cross-component test suite (51 cases spanning EmailSanitizer, Spotlighter,
   ReasonHygiene, and ReasonValidator), run `dotnet test` from the repo root. Same coverage,
   integrated with `dotnet`'s tooling so it can be wired into CI.

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
unread = agent hasn't seen it yet. Watch for ~1 week, edit `rubric.md` (narrative guidance) and
`senders.json` (trusted/junk domain lists) whenever the agent makes a mistake.

The run summary in `logs\YYYY-MM-DD.log` includes a `classification audit` block computed from
`state\history.jsonl`: of the messages the agent called `confident_junk` 48h-30d ago, how many
were rescued by the user to Inbox/Archive (your false-positive rate, the actual gate for Phase
B promotion), how many were deleted, how many are still in Junk. The block also reports the
missed-junk rate on triage decisions (messages the agent routed to Triage that you ended up
deleting). Graduate when the rescue rate is acceptably low for *your* tolerance.

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

## Free-tier cloud LLM via Groq

If you don't want to pay an Anthropic bill *and* don't want to leave a 70B model loaded on
your always-on machine, the project ships a third `IAgentDriver` for [Groq Cloud](https://console.groq.com).
Groq's free tier hosts Llama 3.3 70B at hundreds of tokens/sec and supports `response_format:
json_schema` (strict), so the same `{action, confidence, reason}` contract the other drivers
enforce still applies.

> **Privacy caveat — read this first.** Groq's free tier reserves the right to use inputs
> and outputs to improve their service. The agent will be sending your sanitized junk-mail
> bodies through their API. If that's not acceptable, stay on Anthropic (paid, no training)
> or run Ollama locally. Free isn't free of trade-offs.

1. Sign up at <https://console.groq.com> and create an API key.
2. Set env vars (PowerShell):

   ```powershell
   [Environment]::SetEnvironmentVariable('OUTLOOK_JUNK_AGENT_PROVIDER','groq','User')
   [Environment]::SetEnvironmentVariable('GROQ_API_KEY','gsk_...','User')
   # Optional override; default is llama-3.3-70b-versatile.
   [Environment]::SetEnvironmentVariable('OUTLOOK_JUNK_GROQ_MODEL','llama-3.3-70b-versatile','User')
   ```

3. Run the agent — same `OutlookJunkAgent.exe`, same MCP server, rubric, cron schedule,
   sanitization, id scoping, and Phase A/B state.

Free-tier limits at the time of writing are 30 RPM and ~14,400 RPD on `llama-3.3-70b-versatile`,
plus a per-day token cap. Plenty of headroom for hourly mailbox triage; if you hit a 429 the
driver retries with exponential backoff and respects `Retry-After`. If a different model is
substituted via `OUTLOOK_JUNK_GROQ_MODEL`, pick one that supports `response_format: json_schema`
— otherwise the driver will fall back to Ambiguous@0.0 on every call.

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
  with the reasons it provided. Auto-pruned on each run: anything older than 30 days is
  deleted at startup.
- **Classification history:** `<project-dir>\state\history.jsonl` - one line per past
  classification, used by the next run to look up where the user moved each message and
  compute the Phase-A accuracy block. Rolling 30-day window; older entries are pruned at
  startup.
- **Prompt-shaping inputs (preferred path):** `%LOCALAPPDATA%\OutlookJunkAgent\rubric.md` and
  `%LOCALAPPDATA%\OutlookJunkAgent\senders.json`. Tighter default ACLs than the project
  directory. The agent falls back to the project directory if these aren't present, and logs
  the SHA-256 of whichever copy it actually read so silent edits show up in the audit trail.

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

## Build verification

The solution builds clean against `ModelContextProtocol 1.0.0`, `Microsoft.Graph 5.66`,
`Microsoft.Identity.Client 4.66.2`, and `HtmlAgilityPack 1.11.65`. From the repo root:

```bash
dotnet build -c Release       # 0 warnings, 0 errors
dotnet test                   # 51 tests across the prompt-injection security boundary
```

The xUnit suite under `tests/OutlookJunkTests/` covers `EmailSanitizer` (HTML drop tags,
hidden-CSS subtree drop, image and link extraction, host-mismatch detection, unicode hygiene,
length cap, ZWSP-in-domain, base64 data: URIs), `Spotlighter` (run-token shape, marker
emission, body/subject scrubbing of foreign / actual / mixed-case markers), and both reason
cleaners (`ReasonHygiene` ASCII-strict, `ReasonValidator` UTF-8-preserving — control-char
strip, CRLF neutralisation, length cap, whitespace collapse). Run `dotnet test` after any
change to those files.
