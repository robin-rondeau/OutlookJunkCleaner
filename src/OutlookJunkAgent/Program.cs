using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OutlookJunkAgent;
using OutlookJunkAgent.Drivers;
using OutlookJunkAgent.Sanitizer;
using OutlookJunkCommon;

var dryRun = args.Contains("--dry-run");
var help = args.Contains("--help") || args.Contains("-h");
var maxMessages = ParseIntArg(args, "--max-messages", 50);

if (help)
{
    Console.Error.WriteLine("OutlookJunkAgent — cron-driven host for the Outlook Junk Cleaner classifier");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  OutlookJunkAgent.exe                          run one triage pass");
    Console.Error.WriteLine("  OutlookJunkAgent.exe --dry-run                classify but do not mutate the mailbox");
    Console.Error.WriteLine("  OutlookJunkAgent.exe --max-messages N         cap messages processed this run (default 50)");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Required env vars (depending on provider):");
    Console.Error.WriteLine($"  {EnvVars.AnthropicApiKey}        Anthropic API key (anthropic provider)");
    Console.Error.WriteLine($"  {EnvVars.GroqApiKey}             Groq API key (groq provider)");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Optional env vars:");
    Console.Error.WriteLine($"  {EnvVars.AgentProvider}        provider name: 'anthropic' (default) | 'ollama' | 'groq'");
    Console.Error.WriteLine($"  {EnvVars.AnthropicModel}        Anthropic model id; default '{DriverFactory.DefaultAnthropicModel}'");
    Console.Error.WriteLine($"  {EnvVars.OllamaBaseUrl}    Ollama base URL; default '{DriverFactory.DefaultOllamaBaseUrl}'");
    Console.Error.WriteLine($"  {EnvVars.OllamaModel}        Ollama model name; default '{DriverFactory.DefaultOllamaModel}'");
    Console.Error.WriteLine($"  {EnvVars.GroqModel}          Groq model name; default '{DriverFactory.DefaultGroqModel}'");
    Console.Error.WriteLine($"  {EnvVars.McpServerPath}        path to OutlookJunkMcp.exe; default 'bin/OutlookJunkMcp.exe' relative to cwd");
    return 0;
}

var workingDir = Directory.GetCurrentDirectory();
var serverPath = Environment.GetEnvironmentVariable(EnvVars.McpServerPath)
    ?? Path.Combine(workingDir, "bin", OperatingSystem.IsWindows() ? "OutlookJunkMcp.exe" : "OutlookJunkMcp");
var (rubricPath, _) = ConfigPaths.ResolveRubric(workingDir);
var (sendersPath, _) = ConfigPaths.ResolveSenders(workingDir);
var logsDir = Path.Combine(workingDir, "logs");
var stateDir = Path.Combine(workingDir, "state");
Directory.CreateDirectory(logsDir);
Directory.CreateDirectory(stateDir);
var logPath = Path.Combine(logsDir, $"{DateTime.Now:yyyy-MM-dd}.log");
var historyPath = Path.Combine(stateDir, "history.jsonl");

using var loggerFactory = LoggerFactory.Create(b =>
{
    b.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    });
    b.SetMinimumLevel(LogLevel.Information);
});
var log = loggerFactory.CreateLogger("agent");

LogRetention.SweepOlderThan(logsDir, TimeSpan.FromDays(30), log);

var driver = DriverFactory.Create(loggerFactory, out var providerName, out var modelName);
var spotlighter = new Spotlighter();
var summary = new RunSummary
{
    Provider = providerName,
    Model = modelName,
    DryRun = dryRun,
    RunToken = spotlighter.RunToken,
};

try
{
    // Must fire before the 10-minute Task Scheduler ExecutionTimeLimit (see install-task.ps1)
    // so the run gets a chance to catch the cancellation, flush the audit log, and exit cleanly
    // rather than being hard-killed mid-mutation.
    var runBudget = TimeSpan.FromMinutes(9);
    var runDeadline = DateTimeOffset.Now + runBudget;
    using var cts = new CancellationTokenSource(runBudget);
    await using var mcp = await McpClientHost.ConnectAsync(serverPath, loggerFactory.CreateLogger<McpClientHost>(), cts.Token);

    var toolNames = await mcp.DiscoverToolNamesAsync(cts.Token);
    var deleteEnabled = toolNames.Contains(ToolNames.DeleteFromJunk);

    var senders = await SendersStore.LoadAsync(sendersPath, log, cts.Token);
    var heuristics = new HeuristicClassifier(senders);
    var systemPrompt = await RubricLoader.BuildSystemPromptAsync(
        rubricPath, senders, deleteEnabled, dryRun, spotlighter.RunToken, cts.Token);

    var rubricHash = ConfigPaths.Sha256Short(rubricPath);
    var sendersHash = ConfigPaths.Sha256Short(sendersPath);
    log.LogInformation(
        "config: rubric={RP} sha256={RH}; senders={SP} sha256={SH}",
        rubricPath, rubricHash, sendersPath, sendersHash);
    summary.RecordConfigSnapshot(
        $"rubric:  {rubricPath}  sha256={rubricHash}\nsenders: {sendersPath}  sha256={sendersHash}");

    var status = await mcp.GetStatusAsync(cts.Token);
    log.LogInformation(
        "Server status: junk={J} unread={U} triage={T} deleteEnabled={DE}",
        status.JunkCount, status.JunkUnreadCount, status.TriageCount, status.DeleteEnabled);

    var history = new HistoryStore(historyPath, log);
    await history.PruneOlderThanAsync(TimeSpan.FromDays(30), cts.Token);
    var pastEntries = await history.LoadAsync(cts.Token);
    var phaseLabel = deleteEnabled ? "B" : "A";

    if (toolNames.Contains(ToolNames.LookupClassificationStatus))
    {
        var accuracy = await AccuracyComputer.ComputeAsync(
            pastEntries,
            minAge: TimeSpan.FromHours(48),
            maxAge: TimeSpan.FromDays(30),
            mcp,
            log,
            cts.Token);
        if (accuracy.HasData)
        {
            var rendered = accuracy.Render();
            foreach (var line in rendered.Split('\n'))
            {
                log.LogInformation("{Line}", line.TrimEnd('\r'));
            }
            summary.RecordAccuracy(rendered);
        }
        else if (accuracy.LookupFailed)
        {
            summary.RecordAccuracy("classification audit: lookup failed this run.");
        }
    }
    else
    {
        log.LogInformation("MCP server does not expose lookup_classification_status; skipping accuracy audit. Rebuild OutlookJunkMcp to enable.");
    }

    var working = await mcp.ListJunkAsync(limit: maxMessages, sinceHours: null, includeRead: false, cts.Token);
    log.LogInformation(
        "Starting per-message classifier (provider={P} model={M} runToken={Rt} dryRun={D} working={N} cap={C})",
        providerName, modelName, spotlighter.RunToken, dryRun, working.Count, maxMessages);

    var processed = 0;
    foreach (var msg in working)
    {
        cts.Token.ThrowIfCancellationRequested();
        if (processed >= maxMessages) break;
        processed++;

        var msgStart = DateTimeOffset.Now;
        var msgStopwatch = Stopwatch.StartNew();
        log.LogInformation("[{Id}] processing started at {Start:HH:mm:ss.fff}", msg.Id, msgStart);

        try
        {
            var details = await mcp.GetMessageAsync(msg.Id, cts.Token);

            ClassificationResult decision;
            string classifierKind;
            string classifierLabel;
            var heuristic = heuristics.Classify(details);
            if (heuristic is not null)
            {
                decision = new ClassificationResult(heuristic.Action, 1.0, heuristic.Reason, RawText: null);
                classifierKind = "heuristic";
                classifierLabel = heuristic.HeuristicId;
                summary.RecordHeuristicTurn();
            }
            else
            {
                var spotlighted = spotlighter.Wrap(details);
                decision = await driver.ClassifyAsync(
                    new ClassificationRequest(systemPrompt, spotlighted, runDeadline), cts.Token);
                classifierKind = "llm";
                classifierLabel = $"llm conf={decision.Confidence:F2}";
                summary.RecordLlmTurn();
            }

            var cleanedReason = ReasonHygiene.Clean(decision.Reason);
            log.LogInformation(
                "[{Id}] {Action} ({Label}) — {Reason}",
                msg.Id, decision.Action, classifierLabel, cleanedReason);

            var auditReason = $"agent-asserted: {cleanedReason}";
            var targetTool = ActionToTool(decision.Action, deleteEnabled);

            if (dryRun)
            {
                summary.RecordToolCall($"would:{targetTool}", auditReason);
                continue;
            }

            switch (decision.Action)
            {
                case ClassificationAction.ConfidentJunk when deleteEnabled:
                    await mcp.DeleteFromJunkAsync(msg.Id, cleanedReason, cts.Token);
                    summary.RecordToolCall(ToolNames.DeleteFromJunk, auditReason);
                    break;
                case ClassificationAction.ConfidentJunk:
                    await mcp.MarkAsReadAsync(msg.Id, cleanedReason, cts.Token);
                    summary.RecordToolCall(ToolNames.MarkAsRead, auditReason);
                    break;
                case ClassificationAction.Ambiguous:
                case ClassificationAction.NotJunk:
                    await mcp.MoveToTriageAsync(msg.Id, cleanedReason, cts.Token);
                    summary.RecordToolCall(ToolNames.MoveToTriage, auditReason);
                    break;
            }

            // Record only after a successful mutation, so the history reflects what the user
            // could actually have observed in their mailbox. Dry-run classifications never
            // change folder state and would falsely register as "user agreed" in the lookup.
            await history.AppendAsync(new HistoryEntry(
                Timestamp: DateTimeOffset.UtcNow,
                MessageId: msg.Id,
                AgentDecision: ActionToHistoryString(decision.Action),
                AgentReason: cleanedReason,
                RunToken: spotlighter.RunToken,
                Phase: phaseLabel,
                Provider: providerName,
                Model: modelName,
                ClassifierKind: classifierKind), cts.Token);

            msgStopwatch.Stop();
            log.LogInformation(
                "[{Id}] processing finished at {End:HH:mm:ss.fff} (elapsed={Elapsed:F3}s)",
                msg.Id, DateTimeOffset.Now, msgStopwatch.Elapsed.TotalSeconds);
        }
        catch (GroqRateLimitException rle)
        {
            msgStopwatch.Stop();
            log.LogError(
                "[{Id}] rate-limited at {End:HH:mm:ss.fff} after {Elapsed:F3}s; server asks {RetryAfter:F0}s, exceeds remaining run budget. Aborting run (request-id={Rid}).",
                msg.Id, DateTimeOffset.Now, msgStopwatch.Elapsed.TotalSeconds, rle.RetryAfter.TotalSeconds, rle.RequestId ?? "?");
            summary.RecordToolCall(
                "rate-limit-abort",
                $"id={msg.Id} retry-after={rle.RetryAfter.TotalSeconds:F0}s request-id={rle.RequestId ?? "?"}");
            summary.RecordError(rle);
            summary.RecordFinal($"aborted at {processed} of {working.Count} messages due to rate limit");
            break;
        }
        catch (OperationCanceledException)
        {
            msgStopwatch.Stop();
            log.LogWarning(
                "[{Id}] canceled at {End:HH:mm:ss.fff} after {Elapsed:F3}s",
                msg.Id, DateTimeOffset.Now, msgStopwatch.Elapsed.TotalSeconds);
            throw;
        }
        catch (Exception ex)
        {
            msgStopwatch.Stop();
            log.LogWarning(ex,
                "[{Id}] classification failed at {End:HH:mm:ss.fff} after {Elapsed:F3}s",
                msg.Id, DateTimeOffset.Now, msgStopwatch.Elapsed.TotalSeconds);
            summary.RecordToolCall("error", $"id={msg.Id} {ex.GetType().Name}: {ex.Message}");
        }
    }

    if (summary.Error is null)
    {
        summary.RecordFinal($"processed {processed} of {working.Count} messages");
    }
}
catch (Exception ex)
{
    log.LogError(ex, "Agent run failed");
    summary.RecordError(ex);
}
finally
{
    var rendered = summary.Render();
    Console.Out.WriteLine(rendered);
    await File.AppendAllTextAsync(logPath, rendered + Environment.NewLine);
}

return summary.Error is null ? 0 : 1;

static string ActionToTool(ClassificationAction action, bool deleteEnabled) => action switch
{
    ClassificationAction.ConfidentJunk => deleteEnabled ? ToolNames.DeleteFromJunk : ToolNames.MarkAsRead,
    ClassificationAction.Ambiguous => ToolNames.MoveToTriage,
    ClassificationAction.NotJunk => ToolNames.MoveToTriage,
    _ => "?",
};

static string ActionToHistoryString(ClassificationAction action) => action switch
{
    ClassificationAction.ConfidentJunk => "confident_junk",
    ClassificationAction.Ambiguous => "ambiguous",
    ClassificationAction.NotJunk => "not_junk",
    _ => "unknown",
};

static int ParseIntArg(string[] args, string flag, int defaultValue)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == flag && int.TryParse(args[i + 1], out var v) && v > 0) return v;
    }
    return defaultValue;
}
