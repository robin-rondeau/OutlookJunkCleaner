using Microsoft.Extensions.Logging;

namespace OutlookJunkAgent;

/// <summary>
/// Deletes daily log files older than a retention window so the logs/ directory does not grow
/// unbounded. The agent's history.jsonl has its own pruning in HistoryStore; this only touches
/// *.log files in the logs directory. Failures (file-in-use, ACLs) are warned and swallowed —
/// log retention is not safety-critical and a busy-file should not abort the run.
/// </summary>
public static class LogRetention
{
    public static void SweepOlderThan(string logsDir, TimeSpan maxAge, ILogger log)
    {
        if (!Directory.Exists(logsDir)) return;

        var cutoff = DateTime.UtcNow - maxAge;
        var deleted = 0;
        var failed = 0;

        foreach (var path in Directory.EnumerateFiles(logsDir, "*.log", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc >= cutoff) continue;
                File.Delete(path);
                deleted++;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Log retention: could not delete {Path}", path);
                failed++;
            }
        }

        if (deleted > 0 || failed > 0)
        {
            log.LogInformation(
                "Log retention sweep: deleted={D} failed={F} (older than {Days}d).",
                deleted, failed, (int)maxAge.TotalDays);
        }
    }
}
