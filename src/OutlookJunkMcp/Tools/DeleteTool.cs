using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using OutlookJunkMcp.Graph;

namespace OutlookJunkMcp.Tools;

/// <summary>
/// Phase B only. Registered conditionally based on OUTLOOK_JUNK_MCP_ALLOW_DELETE.
/// When the env var is unset, this tool is never advertised to the agent at all,
/// so the LLM physically cannot call it.
/// </summary>
[McpServerToolType]
public sealed class DeleteTool
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly MailClient _mail;

    public DeleteTool(MailClient mail)
    {
        _mail = mail;
    }

    [McpServerTool(Name = "delete_from_junk")]
    [Description("Move a Junk message to Deleted Items. Recoverable from Deleted Items for ~30 days. Use only for high-confidence junk; ambiguous mail should always go to Triage instead.")]
    public async Task<string> DeleteFromJunk(
        [Description("Opaque message ID; must be currently in Junk.")] string id,
        [Description("One-line reason recorded in the audit log.")] string reason,
        CancellationToken ct = default)
    {
        var result = await _mail.DeleteFromJunkAsync(id, reason, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, JsonOpts);
    }
}
