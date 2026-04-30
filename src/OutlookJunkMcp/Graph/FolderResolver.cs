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

    public async Task ResolveAsync(CancellationToken ct = default)
    {
        var junk = await _graph.Me.MailFolders["junkemail"].GetAsync(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Could not resolve well-known JunkEmail folder.");
        JunkFolderId = junk.Id ?? throw new InvalidOperationException("JunkEmail folder has no ID.");

        var deleted = await _graph.Me.MailFolders["deleteditems"].GetAsync(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Could not resolve well-known DeletedItems folder.");
        DeletedItemsFolderId = deleted.Id ?? throw new InvalidOperationException("DeletedItems folder has no ID.");

        TriageFolderId = await ResolveOrCreateTriageAsync(ct).ConfigureAwait(false);

        _log.LogInformation(
            "Folder allow-list resolved: Junk={JunkId} Triage='{TriageName}'={TriageId} DeletedItems={DeletedId}",
            JunkFolderId, _triageFolderName, TriageFolderId, DeletedItemsFolderId);
    }

    private async Task<string> ResolveOrCreateTriageAsync(CancellationToken ct)
    {
        var existing = await _graph.Me.MailFolders.GetAsync(req =>
        {
            req.QueryParameters.Filter = $"displayName eq '{_triageFolderName.Replace("'", "''")}'";
            req.QueryParameters.Top = 1;
        }, cancellationToken: ct).ConfigureAwait(false);

        var match = existing?.Value?.FirstOrDefault();
        if (match?.Id is { Length: > 0 } id)
        {
            return id;
        }

        _log.LogInformation("Triage folder '{Name}' not found; creating.", _triageFolderName);
        var created = await _graph.Me.MailFolders.PostAsync(new MailFolder
        {
            DisplayName = _triageFolderName,
            IsHidden = false,
        }, cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Failed to create Triage folder '{_triageFolderName}'.");

        return created.Id ?? throw new InvalidOperationException("Created Triage folder has no ID.");
    }

    public bool IsAllowedReadFolder(string folderId) =>
        folderId == JunkFolderId || folderId == TriageFolderId;

    public bool IsJunk(string folderId) => folderId == JunkFolderId;
}
