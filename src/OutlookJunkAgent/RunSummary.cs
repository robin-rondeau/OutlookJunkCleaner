using System.Text;

namespace OutlookJunkAgent;

/// <summary>
/// Per-run record of what the agent did. Written to the daily log file at end-of-run so the user
/// has an at-a-glance audit trail without having to read raw API traffic.
/// </summary>
public sealed class RunSummary
{
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly List<string> _events = new();
    private int _toolCalls;
    private int _llmTurns;

    public string Provider { get; init; } = "anthropic";
    public string Model { get; init; } = "claude-opus-4-7";
    public bool DryRun { get; init; }
    public string RunToken { get; init; } = "";
    public string? FinalText { get; private set; }
    public string? Error { get; private set; }
    public string? AccuracyBlock { get; private set; }

    public void RecordToolCall(string name, string? reason)
    {
        _toolCalls++;
        _events.Add(reason is { Length: > 0 }
            ? $"  - {name}: {reason}"
            : $"  - {name}");
    }

    public void RecordLlmTurn() => _llmTurns++;

    public void RecordFinal(string text) => FinalText = text;

    public void RecordError(Exception ex) => Error = $"{ex.GetType().Name}: {ex.Message}";

    public void RecordAccuracy(string rendered) => AccuracyBlock = rendered;

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== run @ {_startedAt:yyyy-MM-dd HH:mm:ss zzz} ===");
        sb.AppendLine($"provider={Provider} model={Model} dryRun={DryRun} runToken={RunToken}");
        sb.AppendLine($"llmTurns={_llmTurns} toolCalls={_toolCalls} duration={(DateTimeOffset.Now - _startedAt).TotalSeconds:F1}s");
        if (_events.Count > 0)
        {
            sb.AppendLine("actions:");
            foreach (var e in _events) sb.AppendLine(e);
        }
        if (AccuracyBlock is { Length: > 0 })
        {
            foreach (var line in AccuracyBlock.Split('\n'))
            {
                sb.AppendLine(line.TrimEnd('\r'));
            }
        }
        if (FinalText is { Length: > 0 })
        {
            sb.AppendLine("final:");
            foreach (var line in FinalText.Split('\n'))
            {
                sb.AppendLine($"  {line.TrimEnd('\r')}");
            }
        }
        if (Error is { Length: > 0 })
        {
            sb.AppendLine($"error: {Error}");
        }
        return sb.ToString();
    }
}
