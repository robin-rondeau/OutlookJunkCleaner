using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace OutlookJunkAgent.Drivers;

/// <summary>
/// Anthropic Messages API driver as a one-shot classifier. Uses raw HttpClient — no third-party
/// SDK — so the dependency surface stays minimal. Implements item 5 (output-constrained
/// classifier) by registering a single `classify` tool and forcing tool_choice. The model
/// physically cannot emit a free-form tool call or an off-schema decision; on schema violation
/// (rare) the driver falls back to Ambiguous@0.0.
/// </summary>
public sealed class AnthropicAgentDriver : IAgentDriver
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";
    private const int MaxTokens = 256;
    private const string ClassifyToolName = "classify";

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

    public async Task<ClassificationResult> ClassifyAsync(ClassificationRequest req, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = MaxTokens,
            ["system"] = BuildSystemBlocks(req.SystemPrompt),
            ["tools"] = BuildClassifyToolArray(),
            ["tool_choice"] = new JsonObject
            {
                ["type"] = "tool",
                ["name"] = ClassifyToolName,
            },
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = req.SpotlightedEmail,
                }
            },
        };

        using var resp = await _http.PostAsJsonAsync(Endpoint, body, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Anthropic API error {(int)resp.StatusCode}: {Truncate(raw, 1000)}");
        }

        return ParseResponse(raw);
    }

    private ClassificationResult ParseResponse(string raw)
    {
        using var parsed = JsonDocument.Parse(raw);
        var root = parsed.RootElement;
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return Fallback("missing-content", raw);
        }

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var type)) continue;
            if (type.GetString() != "tool_use") continue;
            if (block.TryGetProperty("name", out var n) && n.GetString() != ClassifyToolName) continue;
            if (!block.TryGetProperty("input", out var input)) continue;

            return ParseClassifyInput(input, raw);
        }

        _log.LogWarning("Anthropic response had no classify tool_use block; falling back to ambiguous");
        return Fallback("no-tool-use", raw);
    }

    private ClassificationResult ParseClassifyInput(JsonElement input, string raw)
    {
        if (input.ValueKind != JsonValueKind.Object) return Fallback("input-not-object", raw);

        var actionStr = input.TryGetProperty("action", out var a) ? a.GetString() : null;
        var action = actionStr switch
        {
            "confident_junk" => ClassificationAction.ConfidentJunk,
            "ambiguous" => ClassificationAction.Ambiguous,
            "not_junk" => ClassificationAction.NotJunk,
            _ => (ClassificationAction?)null,
        };
        if (action is null) return Fallback($"bad-action:{actionStr}", raw);

        var confidence = 0.0;
        if (input.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number)
        {
            if (c.TryGetDouble(out var conf)) confidence = Math.Clamp(conf, 0.0, 1.0);
        }

        var reason = "";
        if (input.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
        {
            reason = r.GetString() ?? "";
        }

        return new ClassificationResult(action.Value, confidence, reason, RawText: null);
    }

    private static ClassificationResult Fallback(string detail, string raw) =>
        new(
            ClassificationAction.Ambiguous,
            Confidence: 0.0,
            Reason: $"unparseable classifier output ({detail})",
            RawText: Truncate(raw, 500));

    private static JsonArray BuildSystemBlocks(string systemPrompt) => new()
    {
        new JsonObject
        {
            ["type"] = "text",
            ["text"] = systemPrompt,
            ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
        }
    };

    private static JsonArray BuildClassifyToolArray() => new()
    {
        new JsonObject
        {
            ["name"] = ClassifyToolName,
            ["description"] = "Emit your classification of the email as a confident_junk / ambiguous / not_junk decision with confidence and a short reason.",
            ["input_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "confident_junk", "ambiguous", "not_junk" },
                    },
                    ["confidence"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["minimum"] = 0,
                        ["maximum"] = 1,
                    },
                    ["reason"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["maxLength"] = 200,
                    },
                },
                ["required"] = new JsonArray { "action", "confidence", "reason" },
                ["additionalProperties"] = false,
            },
        }
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
