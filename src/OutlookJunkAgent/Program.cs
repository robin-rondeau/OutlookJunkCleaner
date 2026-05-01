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
    Console.Error.WriteLine();
    Console.Error.WriteLine("Optional env vars:");
    Console.Error.WriteLine($"  {EnvVars.AgentProvider}        provider name: 'anthropic' (default) | 'ollama'");
    Console.Error.WriteLine($"  {EnvVars.AnthropicModel}        Anthropic model id; default '{DriverFactory.DefaultAnthropicModel}'");
    Console.Error.WriteLine($"  {EnvVars.OllamaBaseUrl}    Ollama base URL; default '{DriverFactory.DefaultOllamaBaseUrl}'");
    Console.Error.WriteLine($"  {EnvVars.OllamaModel}        Ollama model name; default '{DriverFactory.DefaultOllamaModel}'");
    Console.Error.WriteLine($"  {EnvVars.McpServerPath}        path to OutlookJunkMcp.exe; default 'bin/OutlookJunkMcp.exe' relative to cwd");
    return 0;
}

var workingDir = Directory.GetCurrentDirectory();
var serverPath = Environment.GetEnvironmentVariable(EnvVars.McpServerPath)
    ?? Path.Combine(workingDir, "bin", OperatingSystem.IsWindows() ? "OutlookJunkMcp.exe" : "OutlookJunkMcp");
var rubricPath = Path.Combine(workingDir, "rubric.md");
var logsDir = Path.Combine(workingDir, "logs");
Directory.CreateDirectory(logsDir);
var logPath = Path.Combine(logsDir, $"{DateTime.Now:yyyy-MM-dd}.log");

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
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    await using var mcp = await McpClientHost.ConnectAsync(serverPath, loggerFactory.CreateLogger<McpClientHost>(), cts.Token);

    var toolNames = await mcp.DiscoverToolNamesAsync(cts.Token);
    var deleteEnabled = toolNames.Contains(ToolNames.DeleteFromJunk);

    var systemPrompt = await RubricLoader.BuildSystemPromptAsync(
        rubricPath, deleteEnabled, dryRun, spotlighter.RunToken, cts.Token);

    var status = await mcp.GetStatusAsync(cts.Token);
    log.LogInformation(
        "Server status: junk={J} unread={U} triage={T} deleteEnabled={DE}",
        status.JunkCount, status.JunkUnreadCount, status.TriageCount, status.DeleteEnabled);

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

        try
        {
            var details = await mcp.GetMessageAsync(msg.Id, cts.Token);
            var spotlighted = spotlighter.Wrap(details);
            var decision = await driver.ClassifyAsync(
                new ClassificationRequest(systemPrompt, spotlighted), cts.Token);
            summary.RecordLlmTurn();

            var cleanedReason = ReasonHygiene.Clean(decision.Reason);
            log.LogInformation(
                "[{Id}] {Action} conf={C:F2} — {Reason}",
                msg.Id, decision.Action, decision.Confidence, cleanedReason);

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
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Classification failed for {Id}", msg.Id);
            summary.RecordToolCall("error", $"id={msg.Id} {ex.GetType().Name}: {ex.Message}");
        }
    }

    summary.RecordFinal($"processed {processed} of {working.Count} messages");
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

static int ParseIntArg(string[] args, string flag, int defaultValue)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == flag && int.TryParse(args[i + 1], out var v) && v > 0) return v;
    }
    return defaultValue;
}
