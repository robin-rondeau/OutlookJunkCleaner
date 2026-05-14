using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Me.Messages.Item.Move;
using Microsoft.Graph.Models;
using OutlookJunkCommon;
using OutlookJunkMcp.Sanitizer;
using OutlookJunkMcp.Session;
using OutlookJunkMcp.Tools;

namespace OutlookJunkMcp.Graph;

/// <summary>
/// Folder-allow-listed wrapper around Microsoft Graph mail operations. Every method that takes
/// a message ID re-validates that the message is currently in an allowed folder before acting,
/// AND that the id was previously surfaced to the caller via list_junk or list_triage in this
/// session — defeating prompt-injection attempts that synthesise message ids the agent never
/// legitimately saw. The agent only ever sees opaque IDs returned from these methods; it cannot
/// reach Inbox or any other folder through this surface.
/// </summary>
public sealed class MailClient
{
    private readonly GraphServiceClient _graph;
    private readonly FolderResolver _folders;
    private readonly EmailSanitizer _sanitizer;
    private readonly SurfacedIds _surfaced;
    private readonly ILogger<MailClient> _log;

    private const string SelectMessageList = "id,subject,from,receivedDateTime,isRead,hasAttachments,bodyPreview";
    private const string SelectMessageDetails = "id,parentFolderId,subject,from,receivedDateTime,isRead,hasAttachments,body,internetMessageHeaders";

    public MailClient(
        GraphServiceClient graph,
        FolderResolver folders,
        EmailSanitizer sanitizer,
        SurfacedIds surfaced,
        ILogger<MailClient> log)
    {
        _graph = graph;
        _folders = folders;
        _sanitizer = sanitizer;
        _surfaced = surfaced;
        _log = log;
    }

    public async Task<IReadOnlyList<JunkMessageInfo>> ListJunkAsync(int limit, int? sinceHours, bool includeRead, CancellationToken ct)
    {
        var msgs = await ListInFolderAsync(_folders.JunkFolderId, limit, sinceHours, includeRead, ct).ConfigureAwait(false);
        _surfaced.Add(msgs.Select(m => m.Id));
        return msgs;
    }

    public async Task<IReadOnlyList<JunkMessageInfo>> ListTriageAsync(int limit, CancellationToken ct)
    {
        var msgs = await ListInFolderAsync(_folders.TriageFolderId, limit, sinceHours: null, includeRead: true, ct).ConfigureAwait(false);
        _surfaced.Add(msgs.Select(m => m.Id));
        return msgs;
    }

    public async Task<MessageDetails> GetMessageAsync(string id, CancellationToken ct)
    {
        CheckIdSurfaced(id);
        var msg = await FetchAndValidateAsync(id, requireJunk: false, ct).ConfigureAwait(false);
        var folderName = msg.ParentFolderId == _folders.JunkFolderId ? "Junk" : "Triage";

        var listUnsub = ExtractHeader(msg, "List-Unsubscribe");
        var relevant = ExtractRelevantHeaders(msg);
        var rawSender = msg.From?.EmailAddress?.Address ?? "<unknown>";
        var sender = _sanitizer.SanitizeShortText(rawSender, EmailSanitizer.MaxSenderChars);
        var senderDomain = sender.Contains('@', StringComparison.Ordinal)
            ? sender[(sender.IndexOf('@') + 1)..]
            : "<none>";
        var subject = _sanitizer.SanitizeShortText(msg.Subject, EmailSanitizer.MaxSubjectChars);
        var isHtml = msg.Body?.ContentType == BodyType.Html;
        var sanitized = _sanitizer.SanitizeBody(msg.Body?.Content, isHtml);

        return new MessageDetails(
            Id: msg.Id ?? id,
            Folder: folderName,
            Sender: sender,
            SenderDomain: senderDomain,
            Subject: subject,
            ReceivedAt: msg.ReceivedDateTime ?? DateTimeOffset.MinValue,
            IsRead: msg.IsRead ?? false,
            HasAttachments: msg.HasAttachments ?? false,
            Body: sanitized.Body,
            BodyTruncated: sanitized.Truncated,
            Images: sanitized.Images,
            Links: sanitized.Links,
            ListUnsubscribe: listUnsub,
            RelevantHeaders: relevant);
    }

    public async Task<MutationResult> MarkAsReadAsync(string id, string reason, CancellationToken ct)
    {
        CheckIdSurfaced(id);
        await FetchAndValidateAsync(id, requireJunk: true, ct).ConfigureAwait(false);
        var cleanedReason = ReasonValidator.Clean(reason);

        await _graph.Me.Messages[id].PatchAsync(
            new Message { IsRead = true },
            cancellationToken: ct).ConfigureAwait(false);

        _log.LogInformation("AUDIT mark_as_read id={Id} reason=\"agent-asserted: {Reason}\"", id, cleanedReason);
        return new MutationResult(id, ToolNames.MarkAsRead, "marked-read-in-junk", cleanedReason);
    }

    public async Task<MutationResult> MoveToTriageAsync(string id, string reason, CancellationToken ct)
    {
        CheckIdSurfaced(id);
        await FetchAndValidateAsync(id, requireJunk: true, ct).ConfigureAwait(false);
        var cleanedReason = ReasonValidator.Clean(reason);

        await _graph.Me.Messages[id].Move.PostAsync(
            new MovePostRequestBody { DestinationId = _folders.TriageFolderId },
            cancellationToken: ct).ConfigureAwait(false);

        _log.LogInformation("AUDIT move_to_triage id={Id} reason=\"agent-asserted: {Reason}\"", id, cleanedReason);
        return new MutationResult(id, ToolNames.MoveToTriage, "moved-junk-to-triage", cleanedReason);
    }

    public async Task<MutationResult> DeleteFromJunkAsync(string id, string reason, CancellationToken ct)
    {
        CheckIdSurfaced(id);
        await FetchAndValidateAsync(id, requireJunk: true, ct).ConfigureAwait(false);
        var cleanedReason = ReasonValidator.Clean(reason);

        await _graph.Me.Messages[id].Move.PostAsync(
            new MovePostRequestBody { DestinationId = _folders.DeletedItemsFolderId },
            cancellationToken: ct).ConfigureAwait(false);

        _log.LogInformation("AUDIT delete_from_junk id={Id} reason=\"agent-asserted: {Reason}\"", id, cleanedReason);
        return new MutationResult(id, ToolNames.DeleteFromJunk, "moved-junk-to-deleted-items", cleanedReason);
    }

    /// <summary>
    /// Look up the current parent-folder bucket for each supplied message id. Read-only and
    /// intentionally bypasses SurfacedIds enforcement: this tool exists so the host can compute
    /// Phase-A accuracy by following up on past classifications, and those ids were surfaced in
    /// previous (now-terminated) sessions whose SurfacedIds set is gone. The resulting info leak
    /// is bounded to "what folder bucket is this message id in" — no body, sender, subject, or
    /// other content. Mutating tools still require SurfacedIds membership.
    ///
    /// A 404 from Graph (the message no longer exists in any folder Graph exposes) is bucketed
    /// as "deleted" rather than its own state: on consumer Outlook, a message deleted directly
    /// from Junk skips Deleted Items and goes to the server-side recoverable-items dumpster,
    /// which is not reachable via Graph — so the only signal we get back is a 404. Treating
    /// that as "deleted" is the honest accuracy-metric reading. See <see cref="FolderResolver.GetBucket"/>.
    /// </summary>
    public async Task<IReadOnlyList<ClassificationLookupEntry>> LookupClassificationStatusAsync(
        IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids is null || ids.Count == 0) return Array.Empty<ClassificationLookupEntry>();
        if (ids.Count > 500)
        {
            throw new ArgumentException("Too many ids; maximum 500 per call.", nameof(ids));
        }

        var results = new List<ClassificationLookupEntry>(ids.Count);
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(id))
            {
                results.Add(new ClassificationLookupEntry(id ?? "", "deleted"));
                continue;
            }
            try
            {
                var msg = await _graph.Me.Messages[id].GetAsync(req =>
                {
                    req.QueryParameters.Select = ["id", "parentFolderId"];
                }, cancellationToken: ct).ConfigureAwait(false);
                results.Add(new ClassificationLookupEntry(id, _folders.GetBucket(msg?.ParentFolderId)));
            }
            catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (ex.ResponseStatusCode == 404)
            {
                results.Add(new ClassificationLookupEntry(id, "deleted"));
            }
        }
        return results;
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

    private void CheckIdSurfaced(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Message id is required.", nameof(id));
        }
        if (!_surfaced.Contains(id))
        {
            throw new InvalidOperationException(
                "id_not_surfaced: id was not returned by list_junk or list_triage in this session. " +
                "Call list_junk first.");
        }
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
                Sender: _sanitizer.SanitizeShortText(m.From?.EmailAddress?.Address ?? "<unknown>", EmailSanitizer.MaxSenderChars),
                Subject: _sanitizer.SanitizeShortText(m.Subject, EmailSanitizer.MaxSubjectChars),
                ReceivedAt: m.ReceivedDateTime ?? DateTimeOffset.MinValue,
                IsRead: m.IsRead ?? false,
                HasAttachments: m.HasAttachments ?? false,
                BodyPreview: _sanitizer.SanitizeShortText(m.BodyPreview, EmailSanitizer.MaxShortPreviewChars),
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
            "Return-Path", "Reply-To", "Message-ID",
            // Microsoft's upstream spam-confidence score. SCL >= 5 means EOP already
            // classified this as spam server-side; surfacing it lets the rubric weight
            // that verdict against borderline downgrades to ambiguous.
            "X-MS-Exchange-Organization-SCL",
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
