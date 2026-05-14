using System.Text;
using Microsoft.Extensions.Logging;

namespace OutlookJunkAgent;

/// <summary>
/// Reads classification history, looks up where each previously-classified message lives now via
/// the lookup_classification_status MCP tool, and aggregates the result into a per-decision-class
/// histogram. The point of the metric is to give the user an evidence-based answer to "is the
/// classifier accurate enough to promote from Phase A (mark-as-read) to Phase B (delete)?" —
/// specifically, the rescue rate on confident_junk decisions is the false-positive floor that
/// would carry forward into Phase B if delete were enabled today.
/// </summary>
public static class AccuracyComputer
{
    public static async Task<AccuracyReport> ComputeAsync(
        IReadOnlyList<HistoryEntry> history,
        TimeSpan minAge,
        TimeSpan maxAge,
        McpClientHost mcp,
        ILogger log,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var eligible = history
            .Where(e => now - e.Timestamp >= minAge && now - e.Timestamp <= maxAge)
            .ToList();
        if (eligible.Count == 0)
        {
            return AccuracyReport.Empty(minAge, maxAge);
        }

        var ids = eligible.Select(e => e.MessageId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        IReadOnlyDictionary<string, string> locations;
        try
        {
            locations = await mcp.LookupClassificationStatusAsync(ids, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Classification status lookup failed; accuracy section will be omitted.");
            return AccuracyReport.Failed(minAge, maxAge);
        }

        var confident = new Dictionary<string, int>(StringComparer.Ordinal);
        var triage = new Dictionary<string, int>(StringComparer.Ordinal);
        var ambiguous = 0;
        var notJunk = 0;

        foreach (var e in eligible)
        {
            // Default to "deleted" rather than "not_found": the server now collapses both
            // "no parent folder" and a Graph 404 into "deleted" (see FolderResolver.GetBucket).
            // If a host-side dict miss still occurs (it shouldn't), treating it the same way
            // keeps the metric internally consistent rather than introducing a phantom bucket.
            var loc = locations.TryGetValue(e.MessageId, out var l) ? l : "deleted";
            switch (e.AgentDecision)
            {
                case "confident_junk":
                    Inc(confident, loc);
                    break;
                case "ambiguous":
                    ambiguous++;
                    Inc(triage, loc);
                    break;
                case "not_junk":
                    notJunk++;
                    Inc(triage, loc);
                    break;
            }
        }

        return new AccuracyReport(
            MinAge: minAge,
            MaxAge: maxAge,
            ConfidentByLocation: confident,
            TriageAmbiguousCount: ambiguous,
            TriageNotJunkCount: notJunk,
            TriageByLocation: triage,
            LookupFailed: false);
    }

    private static void Inc(Dictionary<string, int> d, string key)
    {
        d.TryGetValue(key, out var v);
        d[key] = v + 1;
    }
}

public sealed record AccuracyReport(
    TimeSpan MinAge,
    TimeSpan MaxAge,
    IReadOnlyDictionary<string, int> ConfidentByLocation,
    int TriageAmbiguousCount,
    int TriageNotJunkCount,
    IReadOnlyDictionary<string, int> TriageByLocation,
    bool LookupFailed)
{
    public int ConfidentTotal => Sum(ConfidentByLocation);
    public int ConfidentRescued => Get(ConfidentByLocation, "inbox") + Get(ConfidentByLocation, "archive");
    public int ConfidentDeleted => Get(ConfidentByLocation, "deleted");
    public int ConfidentStillInJunk => Get(ConfidentByLocation, "junk");
    public int TriageTotal => Sum(TriageByLocation);
    // "deleted" covers Deleted Items + Graph-404 (recoverable-items dumpster / hard-delete from
    // Junk). Both mean the user did not rescue the message, which is the signal we care about.
    public int TriageMissedJunk => Get(TriageByLocation, "deleted");

    public bool HasData => !LookupFailed && (ConfidentTotal > 0 || TriageTotal > 0);

    public static AccuracyReport Empty(TimeSpan minAge, TimeSpan maxAge) =>
        new(minAge, maxAge,
            new Dictionary<string, int>(StringComparer.Ordinal), 0, 0,
            new Dictionary<string, int>(StringComparer.Ordinal), false);

    public static AccuracyReport Failed(TimeSpan minAge, TimeSpan maxAge) =>
        new(minAge, maxAge,
            new Dictionary<string, int>(StringComparer.Ordinal), 0, 0,
            new Dictionary<string, int>(StringComparer.Ordinal), true);

    public string Render()
    {
        var window = $"{(int)MinAge.TotalHours}h to {(int)MaxAge.TotalDays}d";
        var sb = new StringBuilder();
        sb.AppendLine($"classification audit (entries {window} old):");

        if (LookupFailed)
        {
            sb.AppendLine("  lookup failed — see warnings above; metric omitted this run.");
            return sb.ToString().TrimEnd();
        }

        if (ConfidentTotal == 0 && TriageTotal == 0)
        {
            sb.AppendLine("  (no eligible history yet — needs entries at least 48h old)");
            return sb.ToString().TrimEnd();
        }

        if (ConfidentTotal > 0)
        {
            var rescuedPct = ConfidentRescued * 100.0 / ConfidentTotal;
            var deletedPct = ConfidentDeleted * 100.0 / ConfidentTotal;
            var stillJunkPct = ConfidentStillInJunk * 100.0 / ConfidentTotal;
            sb.AppendLine($"  confident_junk decisions: N={ConfidentTotal}");
            sb.AppendLine($"    rescued to inbox/archive: {ConfidentRescued} ({rescuedPct:F1}%)  <- Phase B false-positive floor");
            sb.AppendLine($"    user-deleted: {ConfidentDeleted} ({deletedPct:F1}%)  <- user agreed it was junk");
            sb.AppendLine($"    still in junk: {ConfidentStillInJunk} ({stillJunkPct:F1}%)");
            sb.AppendLine($"    locations: {FormatLocations(ConfidentByLocation)}");
        }

        if (TriageTotal > 0)
        {
            var pct = TriageMissedJunk * 100.0 / TriageTotal;
            sb.AppendLine($"  triage decisions: N={TriageTotal} (ambiguous={TriageAmbiguousCount} not_junk={TriageNotJunkCount})");
            sb.AppendLine($"    user-deleted: {TriageMissedJunk} ({pct:F1}%)  <- missed-junk (should have been confident_junk)");
            sb.AppendLine($"    locations: {FormatLocations(TriageByLocation)}");
        }

        return sb.ToString().TrimEnd();
    }

    private static int Sum(IReadOnlyDictionary<string, int> d)
    {
        var total = 0;
        foreach (var v in d.Values) total += v;
        return total;
    }

    private static int Get(IReadOnlyDictionary<string, int> d, string k) =>
        d.TryGetValue(k, out var v) ? v : 0;

    private static string FormatLocations(IReadOnlyDictionary<string, int> d)
    {
        var order = new[] { "junk", "triage", "deleted", "inbox", "archive", "other" };
        var parts = new List<string>(order.Length);
        foreach (var k in order)
        {
            if (d.TryGetValue(k, out var v) && v > 0) parts.Add($"{k}={v}");
        }
        return parts.Count == 0 ? "(none)" : string.Join(" ", parts);
    }
}
