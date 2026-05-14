using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace OutlookJunkMcp.Graph;

/// <summary>
/// Resolves the well-known Junk and Deleted Items folder IDs and ensures the user-configured
/// Triage folder exists (creating it if missing). The resolved IDs form the entire allow-list
/// the rest of the server enforces.
/// </summary>
public sealed class FolderResolver
{
    private readonly GraphServiceClient _graph;
    private readonly ILogger<FolderResolver> _log;
    private readonly string _triageFolderName;

    public FolderResolver(GraphServiceClient graph, string triageFolderName, ILogger<FolderResolver> log)
    {
        _graph = graph;
        _triageFolderName = triageFolderName;
        _log = log;
    }

    public string JunkFolderId { get; private set; } = "";
    public string TriageFolderId { get; private set; } = "";
    public string DeletedItemsFolderId { get; private set; } = "";
    public string InboxFolderId { get; private set; } = "";
    public string ArchiveFolderId { get; private set; } = "";

    public async Task ResolveAsync(CancellationToken ct = default)
    {
        var junk = await _graph.Me.MailFolders["junkemail"].GetAsync(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Could not resolve well-known JunkEmail folder.");
        JunkFolderId = junk.Id ?? throw new InvalidOperationException("JunkEmail folder has no ID.");

        var deleted = await _graph.Me.MailFolders["deleteditems"].GetAsync(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Could not resolve well-known DeletedItems folder.");
        DeletedItemsFolderId = deleted.Id ?? throw new InvalidOperationException("DeletedItems folder has no ID.");

        var inbox = await _graph.Me.MailFolders["inbox"].GetAsync(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Could not resolve well-known Inbox folder.");
        InboxFolderId = inbox.Id ?? throw new InvalidOperationException("Inbox folder has no ID.");

        // Archive may not exist on a fresh Outlook.com account that has never archived a message.
        // Treat that as "no archive folder" rather than failing the server start; lookups will
        // simply never bucket anything as 'archive' until the folder appears.
        try
        {
            var archive = await _graph.Me.MailFolders["archive"].GetAsync(cancellationToken: ct).ConfigureAwait(false);
            ArchiveFolderId = archive?.Id ?? "";
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            _log.LogInformation("Archive folder not present on this account; classification lookups will report 'other' for archived items.");
            ArchiveFolderId = "";
        }

        TriageFolderId = await ResolveOrCreateTriageAsync(ct).ConfigureAwait(false);

        _log.LogInformation(
            "Folder allow-list resolved: Junk={JunkId} Triage='{TriageName}'={TriageId} DeletedItems={DeletedId} Inbox={InboxId} Archive={ArchiveId}",
            JunkFolderId, _triageFolderName, TriageFolderId, DeletedItemsFolderId, InboxFolderId,
            string.IsNullOrEmpty(ArchiveFolderId) ? "(none)" : ArchiveFolderId);
    }

    private async Task<string> ResolveOrCreateTriageAsync(CancellationToken ct)
    {
        // Triage lives as a child of Inbox so Outlook clients badge its unread count the same
        // way they badge Inbox sub-folders — that's the only path that gives the user a
        // new-mail-arriving signal when the agent moves something there. A top-level mail
        // folder doesn't get badged on most clients.
        var filter = $"displayName eq '{_triageFolderName.Replace("'", "''")}'";

        var existing = await _graph.Me.MailFolders["inbox"].ChildFolders.GetAsync(req =>
        {
            req.QueryParameters.Filter = filter;
            req.QueryParameters.Top = 1;
        }, cancellationToken: ct).ConfigureAwait(false);

        var match = existing?.Value?.FirstOrDefault();
        if (match?.Id is { Length: > 0 } id)
        {
            return id;
        }

        await WarnIfStaleTopLevelTriageAsync(filter, ct).ConfigureAwait(false);

        _log.LogInformation("Triage folder '{Name}' not found under Inbox; creating.", _triageFolderName);
        var created = await _graph.Me.MailFolders["inbox"].ChildFolders.PostAsync(new MailFolder
        {
            DisplayName = _triageFolderName,
            IsHidden = false,
        }, cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Failed to create Triage folder '{_triageFolderName}' under Inbox.");

        return created.Id ?? throw new InvalidOperationException("Created Triage folder has no ID.");
    }

    /// <summary>
    /// Detects an orphaned top-level mail folder with the same name (left over from before
    /// Triage was moved under Inbox). Logs a one-line migration nudge if it has messages — we
    /// will not auto-move them because that's destructive and the user might have other plans.
    /// </summary>
    private async Task WarnIfStaleTopLevelTriageAsync(string filter, CancellationToken ct)
    {
        try
        {
            var top = await _graph.Me.MailFolders.GetAsync(req =>
            {
                req.QueryParameters.Filter = filter;
                req.QueryParameters.Top = 1;
            }, cancellationToken: ct).ConfigureAwait(false);

            var stale = top?.Value?.FirstOrDefault();
            if (stale is null) return;

            var count = stale.TotalItemCount ?? 0;
            if (count > 0)
            {
                _log.LogWarning(
                    "STALE: a top-level mail folder named '{Name}' exists with {Count} message(s). " +
                    "It is no longer used as Triage (Triage now lives under Inbox so mail clients " +
                    "badge the unread count). Move those message(s) into Inbox\\{Name} and delete " +
                    "the top-level folder to silence this warning and to keep the accuracy audit accurate.",
                    _triageFolderName, count, _triageFolderName);
            }
            else
            {
                _log.LogInformation(
                    "An empty top-level mail folder named '{Name}' exists; harmless, but you can delete it.",
                    _triageFolderName);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Stale top-level Triage check failed; continuing.");
        }
    }

    public bool IsAllowedReadFolder(string folderId) =>
        folderId == JunkFolderId || folderId == TriageFolderId;

    public bool IsJunk(string folderId) => folderId == JunkFolderId;

    /// <summary>
    /// Returns a coarse-grained location bucket for a parent folder ID, used by the classification
    /// status lookup tool to feed Phase A accuracy metrics. Buckets are intentionally fewer than
    /// real folders so the host (and its log output) doesn't have to know about every Outlook
    /// folder layout the user might create.
    ///
    /// "deleted" is used for both the Deleted Items folder *and* the case where the message no
    /// longer has a parent folder Graph can see — i.e. it has been purged out of the regular
    /// folder hierarchy entirely. On consumer Outlook this is the normal terminal state for a
    /// message deleted directly from Junk, because that path skips Deleted Items and goes
    /// straight to the server-side recoverable-items dumpster, which Graph does not expose. From
    /// the accuracy-metric standpoint both cases mean the same thing: "user purged this, did
    /// not rescue it," so collapsing them keeps the audit honest.
    /// </summary>
    public string GetBucket(string? parentFolderId)
    {
        if (string.IsNullOrEmpty(parentFolderId)) return "deleted";
        if (parentFolderId == JunkFolderId) return "junk";
        if (parentFolderId == TriageFolderId) return "triage";
        if (parentFolderId == DeletedItemsFolderId) return "deleted";
        if (parentFolderId == InboxFolderId) return "inbox";
        if (!string.IsNullOrEmpty(ArchiveFolderId) && parentFolderId == ArchiveFolderId) return "archive";
        return "other";
    }
}
