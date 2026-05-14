using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using OutlookJunkMcp.Config;
using OutlookJunkMcp.Graph;

namespace OutlookJunkMcp.Tools;

[McpServerToolType]
public sealed class JunkTools
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly MailClient _mail;
    private readonly AppConfig _config;

    public JunkTools(MailClient mail, AppConfig config)
    {
        _mail = mail;
        _config = config;
    }

    [McpServerTool(Name = "list_junk")]
    [Description("List items in the Junk folder. Defaults to unread-only so the agent's working set is 'what's new since last run'. Returns sender, subject, receivedAt, isRead, hasAttachments, bodyPreview, and listUnsubscribe header (when present).")]
    public async Task<string> ListJunk(
        [Description("Maximum items to return. Default 50, max 200.")] int? limit = null,
        [Description("Only items received in the last N hours. Optional.")] int? sinceHours = null,
        [Description("If true, include already-read items. Default false.")] bool includeRead = false,
        CancellationToken ct = default)
    {
        var msgs = await _mail.ListJunkAsync(limit ?? 50, sinceHours, includeRead, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(msgs, JsonOpts);
    }

    [McpServerTool(Name = "list_triage")]
    [Description("List items currently in the Triage folder. Useful for reviewing previously-triaged mail or checking what is awaiting human review.")]
    public async Task<string> ListTriage(
        [Description("Maximum items to return. Default 50, max 200.")] int? limit = null,
        CancellationToken ct = default)
    {
        var msgs = await _mail.ListTriageAsync(limit ?? 50, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(msgs, JsonOpts);
    }

    [McpServerTool(Name = "get_message")]
    [Description("Fetch full body and relevant headers for a single message. Refuses unless the message is currently in Junk or Triage.")]
    public async Task<string> GetMessage(
        [Description("Opaque message ID returned by list_junk or list_triage.")] string id,
        CancellationToken ct = default)
    {
        var details = await _mail.GetMessageAsync(id, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(details, JsonOpts);
    }

    [McpServerTool(Name = "mark_as_read")]
    [Description("Mark a Junk message as read in place. The message stays in the Junk folder. Use this for confident junk during Phase A (training); remains available in Phase B as a softer alternative to delete.")]
    public async Task<string> MarkAsRead(
        [Description("Opaque message ID; must be currently in Junk.")] string id,
        [Description("One-line reason recorded in the audit log.")] string reason,
        CancellationToken ct = default)
    {
        var result = await _mail.MarkAsReadAsync(id, reason, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    [McpServerTool(Name = "move_to_triage")]
    [Description("Move a Junk message to the Triage folder for human review. Use this for ambiguous mail in either phase.")]
    public async Task<string> MoveToTriage(
        [Description("Opaque message ID; must be currently in Junk.")] string id,
        [Description("One-line reason explaining why this is borderline; recorded in the audit log.")] string reason,
        CancellationToken ct = default)
    {
        var result = await _mail.MoveToTriageAsync(id, reason, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    [McpServerTool(Name = "get_status")]
    [Description("Return overall counts and the current allowed-folder list. Useful as a first call to confirm the server is healthy and to know how big the working set is.")]
    public async Task<string> GetStatus(CancellationToken ct = default)
    {
        var status = await _mail.GetStatusAsync(_config.AllowDelete, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(status, JsonOpts);
    }

    [McpServerTool(Name = "lookup_classification_status")]
    [Description("Look up the current folder bucket for each supplied message ID. Returns one entry per ID with location set to 'junk', 'triage', 'deleted', 'inbox', 'archive', or 'other'. 'deleted' covers both the Deleted Items folder and the recoverable-items dumpster (a message purged directly from Junk on consumer Outlook returns a Graph 404, which we bucket as deleted because both states mean 'user did not rescue it'). Read-only; intended for the host to compute Phase A classifier accuracy by following up on past classifications. Does not require IDs to be surfaced in the current session.")]
    public async Task<string> LookupClassificationStatus(
        [Description("Message IDs to look up. Maximum 500 per call.")] string[] ids,
        CancellationToken ct = default)
    {
        var results = await _mail.LookupClassificationStatusAsync(ids ?? Array.Empty<string>(), ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(results, JsonOpts);
    }
}
