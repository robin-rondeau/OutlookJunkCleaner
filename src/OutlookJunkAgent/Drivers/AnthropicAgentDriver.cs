using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace OutlookJunkAgent.Drivers;

/// <summary>
/// Anthropic Messages API driver. Uses raw HttpClient — no third-party SDK — so the dependency
/// surface stays minimal. Implements the standard tool-use loop: keep iterating while the model
/// returns stop_reason "tool_use", dispatch each tool_use block to the MCP server via
/// AgentDriverRequest.ExecuteTool, append tool_result blocks, repeat.
/// </summary>
public sealed class AnthropicAgentDriver : IAgentDriver
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";
    private const int MaxTokens = 4096;
    private const int MaxIterations = 25;

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger<AnthropicAgentDriver> _log;

    public AnthropicAgentDriver(HttpClient http, string apiKey, string model, ILogger<AnthropicAgentDriver> log)
    {
        _http = http;
        _http.DefaultRequestHeaders.Remove("x-api-key");
        _http.DefaultRequestHeaders.Remove("anthropic-version");
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _model = model;
        _log = log;
    }

    public async Task<AgentDriverResult> RunAsync(AgentDriverRequest req, CancellationToken ct)
    {
        var toolsJson = BuildToolsArray(req.Tools);
        var systemBlocks = BuildSystemBlocks(req.SystemPrompt);

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = req.UserPrompt }
        };

        var turns = 0;
        string? finalText = null;

        for (var iter = 0; iter < MaxIterations; iter++)
        {
            ct.ThrowIfCancellationRequested();

            var body = new JsonObject
            {
                ["model"] = _model,
                ["max_tokens"] = MaxTokens,
                ["system"] = systemBlocks.DeepClone(),
                ["tools"] = toolsJson.DeepClone(),
                ["messages"] = messages.DeepClone(),
            };

            using var resp = await _http.PostAsJsonAsync(Endpoint, body, ct).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Anthropic API error {(int)resp.StatusCode}: {Truncate(raw, 1000)}");
            }
            turns++;

            using var parsed = JsonDocument.Parse(raw);
            var root = parsed.RootElement;
            var stopReason = root.GetProperty("stop_reason").GetString();
            var content = root.GetProperty("content");

            // Echo any text the model produced this turn.
            foreach (var block in content.EnumerateArray())
            {
                if (block.GetProperty("type").GetString() == "text")
                {
                    var t = block.GetProperty("text").GetString();
                    if (!string.IsNullOrWhiteSpace(t))
                    {
                        _log.LogInformation("model: {Text}", t.Trim());
                        finalText = t;
                    }
                }
            }

            if (stopReason != "tool_use")
            {
                break;
            }

            // Append the assistant turn (including tool_use blocks) verbatim, then dispatch each tool.
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = JsonNode.Parse(content.GetRawText())!,
            });

            var toolResults = new JsonArray();
            foreach (var block in content.EnumerateArray())
            {
                if (block.GetProperty("type").GetString() != "tool_use") continue;
                var toolUseId = block.GetProperty("id").GetString()!;
                var toolName = block.GetProperty("name").GetString()!;
                var toolInput = block.GetProperty("input");

                string toolResult;
                try
                {
                    toolResult = await req.ExecuteTool(toolName, toolInput, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Tool '{Tool}' failed", toolName);
                    toolResult = JsonSerializer.Serialize(new { error = ex.Message });
                }

                toolResults.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = toolUseId,
                    ["content"] = toolResult,
                });
            }

            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = toolResults,
            });
        }

        return new AgentDriverResult(finalText ?? "", turns);
    }

    private static JsonArray BuildSystemBlocks(string systemPrompt) => new()
    {
        new JsonObject
        {
            ["type"] = "text",
            ["text"] = systemPrompt,
            ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
        }
    };

    private static JsonArray BuildToolsArray(IReadOnlyList<AgentTool> tools)
    {
        var arr = new JsonArray();
        for (var i = 0; i < tools.Count; i++)
        {
            var t = tools[i];
            var obj = new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = JsonNode.Parse(t.InputSchema.GetRawText())!,
            };
            // Mark the last tool as a cache breakpoint — saves repeated tool-schema tokens
            // across iterations within a single run.
            if (i == tools.Count - 1)
            {
                obj["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            }
            arr.Add(obj);
        }
        return arr;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
