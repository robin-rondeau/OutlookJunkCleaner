using Microsoft.Extensions.Logging;
using OutlookJunkAgent;
using OutlookJunkAgent.Drivers;
using OutlookJunkCommon;

var dryRun = args.Contains("--dry-run");
var help = args.Contains("--help") || args.Contains("-h");

if (help)
{
    Console.Error.WriteLine("OutlookJunkAgent — cron-driven LLM host for the Outlook Junk Cleaner");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  OutlookJunkAgent.exe              run one triage pass against the configured LLM");
    Console.Error.WriteLine("  OutlookJunkAgent.exe --dry-run    do not invoke any mutating tool; report intent only");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Required env vars:");
    Console.Error.WriteLine($"  {EnvVars.AnthropicApiKey}        Anthropic API key (when using anthropic provider)");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Optional env vars:");
    Console.Error.WriteLine($"  {EnvVars.AgentProvider}        provider name; default 'anthropic'");
    Console.Error.WriteLine($"  {EnvVars.AnthropicModel}        model id; default '{DriverFactory.DefaultAnthropicModel}'");
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
var summary = new RunSummary { Provider = providerName, Model = modelName, DryRun = dryRun };

try
{
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    await using var mcp = await McpClientHost.ConnectAsync(serverPath, loggerFactory.CreateLogger<McpClientHost>(), cts.Token);

    var allTools = await mcp.DiscoverToolsAsync(cts.Token);
    var deleteEnabled = allTools.Any(t => t.Name == ToolNames.DeleteFromJunk);

    // In dry-run, physically remove mutating tools from the LLM's tool list. Defense in depth —
    // the system prompt also instructs the model not to call them, but removing them means it
    // physically cannot.
    var mutatingNames = new HashSet<string>(StringComparer.Ordinal)
    {
        ToolNames.MarkAsRead, ToolNames.MoveToTriage, ToolNames.DeleteFromJunk,
    };
    var tools = dryRun
        ? allTools.Where(t => !mutatingNames.Contains(t.Name)).ToArray()
        : allTools.ToArray();

    var systemPrompt = await RubricLoader.BuildSystemPromptAsync(
        rubricPath,
        deleteEnabled,
        dryRun,
        tools.Select(t => t.Name).ToArray(),
        cts.Token);

    log.LogInformation("Starting agent loop (provider={P} model={M} dryRun={D} deleteEnabled={DE} tools={N})",
        providerName, modelName, dryRun, deleteEnabled, tools.Count);

    var driverResult = await driver.RunAsync(new AgentDriverRequest(
        SystemPrompt: systemPrompt,
        UserPrompt: "Begin triage now. Follow the operating procedure exactly.",
        Tools: tools,
        ExecuteTool: (name, input, ct) => mcp.ExecuteToolAsync(name, input, summary, ct)
    ), cts.Token);

    summary.RecordFinal(driverResult.FinalText);
    for (var i = 0; i < driverResult.LlmTurns; i++) summary.RecordLlmTurn();
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
