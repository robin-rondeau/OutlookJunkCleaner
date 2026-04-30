namespace OutlookJunkMcp.Session;

/// <summary>
/// Per-process allow-set of message ids that have been surfaced to the client by list_junk
/// or list_triage. Mutating tools (mark_as_read, move_to_triage, delete_from_junk) and
/// get_message refuse ids not in this set, defeating prompt-injection attempts that
/// synthesise message ids the agent never legitimately saw.
///
/// Process-singleton is correct given the spawn-per-client topology: the cron host spawns its
/// own server child, and an interactive Claude Code session spawns its own — no cross-session
/// leakage.
/// </summary>
public sealed class SurfacedIds
{
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void Add(IEnumerable<string> ids)
    {
        if (ids is null) return;
        lock (_gate)
        {
            foreach (var id in ids)
            {
                if (!string.IsNullOrEmpty(id)) _ids.Add(id);
            }
        }
    }

    public void Add(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_gate) _ids.Add(id);
    }

    public bool Contains(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        lock (_gate) return _ids.Contains(id);
    }

    public int Count
    {
        get { lock (_gate) return _ids.Count; }
    }
}
