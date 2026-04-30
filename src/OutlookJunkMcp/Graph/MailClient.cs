using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Me.Messages.Item.Move;
using Microsoft.Graph.Models;
using OutlookJunkCommon;

namespace OutlookJunkMcp.Graph;

/// <summary>
/// Folder-allow-listed wrapper around Microsoft Graph mail operations. Every method that takes
/// a message ID re-validates that the message is currently in an allowed folder before acting.
/// The agent only ever sees opaque IDs returned from these methods; it cannot reach Inbox or
/// any other folder through this surface.
/// </summary>
public sealed class MailClient
{
    private readonly GraphServiceClient _graph;
    private readonly FolderResolver _folders;
    private readonly ILogger<MailClient> _log;

    private const string SelectMessageList = "id,subject,from,receivedDateTime,isRead,hasAttachments,bodyPreview";
    private const string SelectMessageDetails = "id,parentFolderId,subject,from,receivedDateTime,isRead,hasAttachments,body,internetMessageHeaders";

    public MailClient(GraphServiceClient graph, FolderResolver folders, ILogger<MailClient> log)
    {
        _graph = graph;
        _folders = folders;
        _log = log;
    }

    public async Task<IReadOnlyList<JunkMessageInfo>> ListJunkAsync(int limit, int? sinceHours, bool includeRead, CancellationToken ct)
    {
        return await ListInFolderAsync(_folders.JunkFolderId, limit, sinceHours, includeRead, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JunkMessageInfo>> ListTriageAsync(int limit, CancellationToken ct)
    {
        return await ListInFolderAsync(_folders.TriageFolderId, limit, sinceHours: null, includeRead: true, ct).ConfigureAwait(false);
    }

    public async Task<MessageDetails> GetMessageAsync(string id, CancellationToken ct)
    {
        var msg = await FetchAndValidateAsync(id, requireJunk: false, ct).ConfigureAwait(false);
        var folderName = msg.ParentFolderId == _folders.JunkFolderId ? "Junk" : "Triage";

        var listUnsub = ExtractHeader(msg, "List-Unsubscribe");
        var relevant = ExtractRelevantHeaders(msg);
        var sender = msg.From?.EmailAddress?.Address ?? "<unknown>";
        var senderDomain = sender.Contains('@', StringComparison.Ordinal) ? sender[(sender.IndexOf('@') + 1)..] : "<none>";

        return new MessageDetails(
            Id: msg.Id ?? id,
            Folder: folderName,
            Sender: sender,
            SenderDomain: senderDomain,
            Subject: msg.Subject ?? "",
            ReceivedAt: msg.ReceivedDateTime ?? DateTimeOffset.MinValue,
            IsRead: msg.IsRead ?? false,
            HasAttachments: msg.HasAttachments ?? false,
            Body: msg.Body?.Content ?? "",
            ListUnsubscribe: listUnsub,
            RelevantHeaders: relevant);
    }

    public async Task<MutationResult> MarkAsReadAsync(string id, string reason, CancellationToken ct)
    {
        await FetchAndValidateAsync(id, requireJunk: true, ct).ConfigureAwait(false);

        await _graph.Me.Messages[id].PatchAsync(
            new Message { IsRead = true },
            cancellationToken: ct).ConfigureAwait(false);

        _log.LogInformation("AUDIT mark_as_read id={Id} reason={Reason}", id, reason);
        return new MutationResult(id, ToolNames.MarkAsRead, "marked-read-in-junk", reason);
    }

    public async Task<MutationResult> MoveToTriageAsync(string id, string reason, CancellationToken ct)
    {
        await FetchAndValidateAsync(id, requireJunk: true, ct).ConfigureAwait(false);

        await _graph.Me.Messages[id].Move.PostAsync(
            new MovePostRequestBody { DestinationId = _folders.TriageFolderId },
            cancellationToken: ct).ConfigureAwait(false);

        _log.LogInformation("AUDIT move_to_triage id={Id} reason={Reason}", id, reason);
        return new MutationResult(id, ToolNames.MoveToTriage, "moved-junk-to-triage", reason);
    }

    public async Task<MutationResult> DeleteFromJunkAsync(string id, string reason, CancellationToken ct)
    {
        await FetchAndValidateAsync(id, requireJunk: true, ct).ConfigureAwait(false);

        await _graph.Me.Messages[id].Move.PostAsync(
            new MovePostRequestBody { DestinationId = _folders.DeletedItemsFolderId },
            cancellationToken: ct).ConfigureAwait(false);

        _log.LogInformation("AUDIT delete_from_junk id={Id} reason={Reason}", id, reason);
        return new MutationResult(id, ToolNames.DeleteFromJunk, "moved-junk-to-deleted-items", reason);
    }

    public async Task<StatusInfo> GetStatusAsync(bool deleteEnabled, CancellationToken ct)
    {
        var junkFolder = await _graph.Me.MailFolders[_folders.JunkFolderId].GetAsync(cancellationToken: ct).ConfigureAwait(false);
        var triageFolder = await _graph.Me.MailFolders[_folders.TriageFolderId].GetAsync(cancellationToken: ct).ConfigureAwait(false);

        return new StatusInfo(
            JunkCount: junkFolder?.TotalItemCount ?? 0,
            JunkUnreadCount: junkFolder?.UnreadItemCount ?? 0,
            TriageCount: triageFolder?.TotalItemCount ?? 0,
            DeleteEnabled: deleteEnabled,
            AllowedFolders: ["Junk", "Triage"]);
    }

    private async Task<IReadOnlyList<JunkMessageInfo>> ListInFolderAsync(
        string folderId, int limit, int? sinceHours, bool includeRead, CancellationToken ct)
    {
        var filterParts = new List<string>();
        if (!includeRead)
        {
            filterParts.Add("isRead eq false");
        }
        if (sinceHours is > 0)
        {
            var since = DateTimeOffset.UtcNow.AddHours(-sinceHours.Value);
            filterParts.Add($"receivedDateTime ge {since:o}");
        }

        var page = await _graph.Me.MailFolders[folderId].Messages.GetAsync(req =>
        {
            req.QueryParameters.Top = Math.Clamp(limit, 1, 200);
            req.QueryParameters.Select = SelectMessageList.Split(',');
            req.QueryParameters.Orderby = ["receivedDateTime desc"];
            if (filterParts.Count > 0)
            {
                req.QueryParameters.Filter = string.Join(" and ", filterParts);
            }
        }, cancellationToken: ct).ConfigureAwait(false);

        var messages = page?.Value ?? [];
        var results = new List<JunkMessageInfo>(messages.Count);
        foreach (var m in messages)
        {
            var listUnsub = await TryGetListUnsubscribeAsync(m.Id, ct).ConfigureAwait(false);
            results.Add(new JunkMessageInfo(
                Id: m.Id ?? "",
                Sender: m.From?.EmailAddress?.Address ?? "<unknown>",
                Subject: m.Subject ?? "",
                ReceivedAt: m.ReceivedDateTime ?? DateTimeOffset.MinValue,
                IsRead: m.IsRead ?? false,
                HasAttachments: m.HasAttachments ?? false,
                BodyPreview: m.BodyPreview ?? "",
                ListUnsubscribe: listUnsub));
        }
        return results;
    }

    private async Task<string?> TryGetListUnsubscribeAsync(string? id, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            var m = await _graph.Me.Messages[id].GetAsync(req =>
            {
                req.QueryParameters.Select = ["internetMessageHeaders"];
            }, cancellationToken: ct).ConfigureAwait(false);
            return ExtractHeader(m, "List-Unsubscribe");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not fetch List-Unsubscribe for {Id}", id);
            return null;
        }
    }

    private async Task<Message> FetchAndValidateAsync(string id, bool requireJunk, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Message id is required.", nameof(id));
        }

        Message? msg;
        try
        {
            msg = await _graph.Me.Messages[id].GetAsync(req =>
            {
                req.QueryParameters.Select = SelectMessageDetails.Split(',');
            }, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            throw new InvalidOperationException($"Message {id} not found.");
        }

        if (msg is null || string.IsNullOrEmpty(msg.ParentFolderId))
        {
            throw new InvalidOperationException($"Message {id} has no parent folder.");
        }

        if (requireJunk)
        {
            if (!_folders.IsJunk(msg.ParentFolderId))
            {
                throw new UnauthorizedAccessException(
                    $"Refused: message {id} is not in the Junk folder. " +
                    "This server only mutates messages currently in Junk.");
            }
        }
        else if (!_folders.IsAllowedReadFolder(msg.ParentFolderId))
        {
            throw new UnauthorizedAccessException(
                $"Refused: message {id} is not in an allowed folder (Junk or Triage).");
        }

        return msg;
    }

    private static string? ExtractHeader(Message? msg, string name)
    {
        var headers = msg?.InternetMessageHeaders;
        if (headers is null) return null;
        foreach (var h in headers)
        {
            if (string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return h.Value;
            }
        }
        return null;
    }

    private static IReadOnlyList<HeaderEntry> ExtractRelevantHeaders(Message? msg)
    {
        var wanted = new[]
        {
            "List-Unsubscribe", "List-Unsubscribe-Post", "List-Id",
            "Authentication-Results", "Received-SPF", "DKIM-Signature",
            "Return-Path", "Reply-To",
        };
        var headers = msg?.InternetMessageHeaders;
        if (headers is null) return [];
        var result = new List<HeaderEntry>();
        foreach (var h in headers)
        {
            if (h.Name is null) continue;
            foreach (var w in wanted)
            {
                if (string.Equals(h.Name, w, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new HeaderEntry(h.Name, h.Value ?? ""));
                    break;
                }
            }
        }
        return result;
    }
}
