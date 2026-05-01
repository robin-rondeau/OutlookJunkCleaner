using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OutlookJunkCommon;

namespace OutlookJunkAgent;

/// <summary>
/// Wraps the MCP client connection: spawns OutlookJunkMcp.exe as a child process over stdio,
/// discovers tool names, and exposes typed wrappers for each MCP tool. The driver no longer
/// dispatches free-form tool calls; the host calls these typed methods directly, which is the
/// structural enforcement of one-message-per-iteration isolation (item 1) and the consequence
/// of moving the LLM to a constrained-output classifier (item 5).
/// </summary>
public sealed class McpClientHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly McpClient _client;
    private readonly ILogger<McpClientHost> _log;

    private McpClientHost(McpClient client, ILogger<McpClientHost> log)
    {
        _client = client;
        _log = log;
    }

    public static async Task<McpClientHost> ConnectAsync(string serverExePath, ILogger<McpClientHost> log, CancellationToken ct)
    {
        if (!File.Exists(serverExePath))
        {
            throw new FileNotFoundException($"MCP server executable not found: {serverExePath}", serverExePath);
        }

        log.LogInformation("Spawning MCP server: {Path}", serverExePath);

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "outlook-junk",
            Command = serverExePath,
            Arguments = [],
        });

        var client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
        return new McpClientHost(client, log);
    }

    public async Task<IReadOnlyList<string>> DiscoverToolNamesAsync(CancellationToken ct)
    {
        var tools = await _client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
        var names = tools.Select(t => t.Name).ToArray();
        _log.LogInformation("Discovered {N} tools from MCP server: {Names}", names.Length, string.Join(", ", names));
        return names;
    }

    public async Task<IReadOnlyList<MessageSummary>> ListJunkAsync(int? limit, int? sinceHours, bool includeRead, CancellationToken ct)
    {
        var args = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (limit is int l) args["limit"] = l;
        if (sinceHours is int s) args["sinceHours"] = s;
        args["includeRead"] = includeRead;
        var json = await CallTextToolAsync(ToolNames.ListJunk, args, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<IReadOnlyList<MessageSummary>>(json, JsonOpts) ?? Array.Empty<MessageSummary>();
    }

    public async Task<IReadOnlyList<MessageSummary>> ListTriageAsync(int? limit, CancellationToken ct)
    {
        var args = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (limit is int l) args["limit"] = l;
        var json = await CallTextToolAsync(ToolNames.ListTriage, args, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<IReadOnlyList<MessageSummary>>(json, JsonOpts) ?? Array.Empty<MessageSummary>();
    }

    public async Task<MessageContent> GetMessageAsync(string id, CancellationToken ct)
    {
        var args = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id };
        var json = await CallTextToolAsync(ToolNames.GetMessage, args, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<MessageContent>(json, JsonOpts)
            ?? throw new InvalidOperationException("get_message returned null content");
    }

    public async Task<MutationOutcome> MarkAsReadAsync(string id, string reason, CancellationToken ct)
    {
        var args = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["reason"] = reason,
        };
        var json = await CallTextToolAsync(ToolNames.MarkAsRead, args, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<MutationOutcome>(json, JsonOpts)
            ?? throw new InvalidOperationException("mark_as_read returned null content");
    }

    public async Task<MutationOutcome> MoveToTriageAsync(string id, string reason, CancellationToken ct)
    {
        var args = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["reason"] = reason,
        };
        var json = await CallTextToolAsync(ToolNames.MoveToTriage, args, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<MutationOutcome>(json, JsonOpts)
            ?? throw new InvalidOperationException("move_to_triage returned null content");
    }

    public async Task<MutationOutcome> DeleteFromJunkAsync(string id, string reason, CancellationToken ct)
    {
        var args = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["reason"] = reason,
        };
        var json = await CallTextToolAsync(ToolNames.DeleteFromJunk, args, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<MutationOutcome>(json, JsonOpts)
            ?? throw new InvalidOperationException("delete_from_junk returned null content");
    }

    public async Task<ServerStatus> GetStatusAsync(CancellationToken ct)
    {
        var json = await CallTextToolAsync(ToolNames.GetStatus, new Dictionary<string, object?>(StringComparer.Ordinal), ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ServerStatus>(json, JsonOpts)
            ?? throw new InvalidOperationException("get_status returned null content");
    }

    public async Task<IReadOnlyDictionary<string, string>> LookupClassificationStatusAsync(
        IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<string, string>(StringComparer.Ordinal);
        var args = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ids"] = ids.ToArray(),
        };
        var json = await CallTextToolAsync(ToolNames.LookupClassificationStatus, args, ct).ConfigureAwait(false);
        var entries = JsonSerializer.Deserialize<IReadOnlyList<ClassificationLookup>>(json, JsonOpts)
            ?? Array.Empty<ClassificationLookup>();
        var result = new Dictionary<string, string>(entries.Count, StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (!string.IsNullOrEmpty(e.Id)) result[e.Id] = e.Location;
        }
        return result;
    }

    private async Task<string> CallTextToolAsync(string name, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var response = await _client.CallToolAsync(name, args, cancellationToken: ct).ConfigureAwait(false);
        var text = JoinText(response.Content);
        if (response.IsError == true)
        {
            throw new McpToolException(name, text);
        }
        return text;
    }

    private static string JoinText(IList<ContentBlock>? content)
    {
        if (content is null || content.Count == 0) return "";
        if (content.Count == 1) return content[0] is TextContentBlock t0 ? t0.Text ?? "" : "";
        var sb = new StringBuilder();
        foreach (var c in content)
        {
            if (c is TextContentBlock t && !string.IsNullOrEmpty(t.Text)) sb.AppendLine(t.Text);
        }
        return sb.ToString().TrimEnd();
    }

    public ValueTask DisposeAsync() => _client is IAsyncDisposable d ? d.DisposeAsync() : ValueTask.CompletedTask;
}

public sealed class McpToolException : Exception
{
    public string ToolName { get; }
    public McpToolException(string toolName, string message) : base($"{toolName}: {message}")
    {
        ToolName = toolName;
    }
}

public sealed record MessageSummary(
    string Id,
    string Sender,
    string Subject,
    DateTimeOffset ReceivedAt,
    bool IsRead,
    bool HasAttachments,
    string BodyPreview,
    string? ListUnsubscribe);

public sealed record MessageContent(
    string Id,
    string Folder,
    string Sender,
    string SenderDomain,
    string Subject,
    DateTimeOffset ReceivedAt,
    bool IsRead,
    bool HasAttachments,
    string Body,
    bool BodyTruncated,
    IReadOnlyList<ImageRef> Images,
    IReadOnlyList<LinkRef> Links,
    string? ListUnsubscribe,
    IReadOnlyList<HeaderRef> RelevantHeaders);

public sealed record ImageRef(string Alt);

public sealed record LinkRef(string VisibleText, string Href, bool HostMismatchHint);

public sealed record HeaderRef(string Name, string Value);

public sealed record MutationOutcome(string Id, string Action, string Outcome, string Reason);

public sealed record ServerStatus(
    int JunkCount,
    int JunkUnreadCount,
    int TriageCount,
    bool DeleteEnabled,
    IReadOnlyList<string> AllowedFolders);

public sealed record ClassificationLookup(string Id, string Location);
