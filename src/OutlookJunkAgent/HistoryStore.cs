using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace OutlookJunkAgent;

/// <summary>
/// Append-only JSON-lines record of every classification the agent has emitted, for use by the
/// Phase A accuracy calculation. Lives at state/history.jsonl in the working directory; this is
/// the only persistent state the agent owns. One line per classification; malformed lines are
/// skipped with a warning rather than failing the run.
///
/// Pruned to a rolling window at startup so the file does not grow unbounded.
/// </summary>
public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private readonly ILogger _log;

    public HistoryStore(string path, ILogger log)
    {
        _path = path;
        _log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public async Task<IReadOnlyList<HistoryEntry>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return Array.Empty<HistoryEntry>();

        var entries = new List<HistoryEntry>();
        var malformed = 0;
        await using var stream = File.OpenRead(_path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<HistoryEntry>(line, JsonOpts);
                if (entry is not null) entries.Add(entry);
            }
            catch (JsonException)
            {
                malformed++;
            }
        }
        if (malformed > 0)
        {
            _log.LogWarning("history.jsonl: skipped {N} malformed line(s).", malformed);
        }
        return entries;
    }

    public async Task AppendAsync(HistoryEntry entry, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(entry, JsonOpts);
        await File.AppendAllTextAsync(_path, json + Environment.NewLine, Encoding.UTF8, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Rewrites history.jsonl in place keeping only entries newer than <paramref name="maxAge"/>.
    /// Atomic swap via temp file so a crash mid-prune cannot truncate history.
    /// </summary>
    public async Task PruneOlderThanAsync(TimeSpan maxAge, CancellationToken ct)
    {
        if (!File.Exists(_path)) return;

        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var entries = await LoadAsync(ct).ConfigureAwait(false);
        var keep = entries.Where(e => e.Timestamp >= cutoff).ToList();
        if (keep.Count == entries.Count) return;

        var tmp = _path + ".tmp";
        await using (var stream = File.Create(tmp))
        await using (var writer = new StreamWriter(stream, Encoding.UTF8))
        {
            foreach (var e in keep)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(e, JsonOpts).AsMemory(), ct).ConfigureAwait(false);
            }
        }
        File.Move(tmp, _path, overwrite: true);
        _log.LogInformation("history.jsonl: pruned {Pruned} entries older than {Days}d (kept {Kept}).",
            entries.Count - keep.Count, (int)maxAge.TotalDays, keep.Count);
    }
}

public sealed record HistoryEntry(
    DateTimeOffset Timestamp,
    string MessageId,
    string AgentDecision,
    string AgentReason,
    string RunToken,
    string Phase,
    string Provider,
    string Model);
