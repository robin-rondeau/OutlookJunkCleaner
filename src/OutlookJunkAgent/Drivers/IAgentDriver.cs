using System.Text.Json;

namespace OutlookJunkAgent.Drivers;

/// <summary>
/// Provider-agnostic abstraction for the LLM-driven agent loop. To swap providers, implement a
/// new driver and wire it up in DriverFactory. The MCP server, tools, rubric, cron, and Phase A/B
/// state stay unchanged.
/// </summary>
public interface IAgentDriver
{
    Task<AgentDriverResult> RunAsync(AgentDriverRequest request, CancellationToken ct);
}

public sealed record AgentDriverRequest(
    string SystemPrompt,
    string UserPrompt,
    IReadOnlyList<AgentTool> Tools,
    Func<string, JsonElement, CancellationToken, Task<string>> ExecuteTool);

public sealed record AgentTool(
    string Name,
    string Description,
    JsonElement InputSchema);

public sealed record AgentDriverResult(string FinalText, int LlmTurns);
