using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using OutlookJunkAgent.Drivers;

namespace OutlookJunkAgent;

/// <summary>
/// Wraps the MCP client connection: spawns OutlookJunkMcp.exe as a child process over stdio,
/// discovers tools, and exposes a Func suitable for IAgentDriver.ExecuteTool dispatch.
/// </summary>
public sealed class McpClientHost : IAsyncDisposable
{
    private readonly IMcpClient _client;
    private readonly ILogger<McpClientHost> _log;

    private McpClientHost(IMcpClient client, ILogger<McpClientHost> log)
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

        var client = await McpClientFactory.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
        return new McpClientHost(client, log);
    }

    public async Task<IReadOnlyList<AgentTool>> DiscoverToolsAsync(CancellationToken ct)
    {
        var tools = await _client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
        var result = new List<AgentTool>(tools.Count);
        foreach (var t in tools)
        {
            var schema = ParseSchema(t.JsonSchema);
            result.Add(new AgentTool(t.Name, t.Description ?? "", schema));
            _log.LogDebug("Discovered tool: {Name}", t.Name);
        }
        _log.LogInformation("Discovered {N} tools from MCP server", result.Count);
        return result;
    }

    /// <summary>
    /// Dispatches a tool call from the agent driver to the MCP server. Returns the textual
    /// concatenation of all text content blocks the server returned (typically a JSON string
    /// produced by the tool implementation).
    /// </summary>
    public async Task<string> ExecuteToolAsync(string name, JsonElement input, RunSummary summary, CancellationToken ct)
    {
        var args = JsonElementToArguments(input);
        var reason = TryExtractReason(input);
        summary.RecordToolCall(name, reason);

        var response = await _client.CallToolAsync(name, args, cancellationToken: ct).ConfigureAwait(false);
        return RenderResponse(response);
    }

    private static IReadOnlyDictionary<string, object?> JsonElementToArguments(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return new Dictionary<string, object?>();
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in input.EnumerateObject())
        {
            dict[prop.Name] = JsonValueToObject(prop.Value);
        }
        return dict;
    }

    private static object? JsonValueToObject(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.TryGetInt64(out var i) ? i : v.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => v.GetRawText(),
    };

    private static string? TryExtractReason(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        return input.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()
            : null;
    }

    private static JsonElement ParseSchema(JsonElement schema)
    {
        using var doc = JsonDocument.Parse(schema.GetRawText());
        return doc.RootElement.Clone();
    }

    private static string RenderResponse(CallToolResponse response)
    {
        if (response.IsError == true)
        {
            // Server-side errors come back as content blocks too — surface them as JSON so the
            // model can reason about them rather than crashing.
            var sb = new StringBuilder("{\"error\":");
            sb.Append(JsonSerializer.Serialize(JoinText(response.Content)));
            sb.Append('}');
            return sb.ToString();
        }
        return JoinText(response.Content);
    }

    private static string JoinText(IReadOnlyList<Content>? content)
    {
        if (content is null || content.Count == 0) return "";
        if (content.Count == 1) return content[0].Text ?? "";
        var sb = new StringBuilder();
        foreach (var c in content)
        {
            if (!string.IsNullOrEmpty(c.Text)) sb.AppendLine(c.Text);
        }
        return sb.ToString().TrimEnd();
    }

    public ValueTask DisposeAsync() => _client is IAsyncDisposable d ? d.DisposeAsync() : ValueTask.CompletedTask;
}
