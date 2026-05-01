using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace OutlookJunkAgent.Drivers;

/// <summary>
/// Ollama HTTP API driver. Talks to a local (or LAN) Ollama server, default
/// http://localhost:11434. Uses Ollama's structured-output mode (`format: <json schema>`,
/// available in Ollama 0.5+) to enforce the same {action, confidence, reason} contract as the
/// Anthropic driver — Ollama validates the schema server-side and the model's output is
/// constrained to fit. Smaller models (8B-class) are more easily fooled by injection attempts
/// in the body, but the MCP server's defenses (id scoping, sanitization, server-side reason
/// validation) still apply regardless of which provider drives classification.
/// </summary>
public sealed class OllamaAgentDriver : IAgentDriver
{
    private const int MaxAttempts = 3;
    private const int NumPredict = 256;

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly Uri _endpoint;
    private readonly ILogger<OllamaAgentDriver> _log;

    public OllamaAgentDriver(HttpClient http, string baseUrl, string model, ILogger<OllamaAgentDriver> log)
    {
        _http = http;
        _model = model;
        _endpoint = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "api/chat");
        _log = log;
    }

    public async Task<ClassificationResult> ClassifyAsync(ClassificationRequest req, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["stream"] = false,
            ["format"] = BuildFormatSchema(),
            ["options"] = new JsonObject
            {
                ["temperature"] = 0.1,
                ["num_predict"] = NumPredict,
            },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = req.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = req.SpotlightedEmail },
            },
        };

        var raw = await SendWithRetryAsync(body, ct).ConfigureAwait(false);
        return ParseResponse(raw);
    }

    private async Task<string> SendWithRetryAsync(JsonObject body, CancellationToken ct)
    {
        var rand = Random.Shared;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            HttpResponseMessage? resp = null;
            try
            {
                resp = await _http.PostAsJsonAsync(_endpoint, body, ct).ConfigureAwait(false);
                var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    LogUsage(raw);
                    return raw;
                }

                if (!IsTransient(resp.StatusCode) || attempt == MaxAttempts)
                {
                    throw new InvalidOperationException(
                        $"Ollama API error {(int)resp.StatusCode} after {attempt} attempt(s) " +
                        $"at {_endpoint}: {Truncate(raw, 1000)}");
                }

                var delay = ComputeBackoff(attempt, rand);
                _log.LogWarning(
                    "Ollama {Status} on attempt {N}/{Max}; retrying in {Delay}s",
                    (int)resp.StatusCode, attempt, MaxAttempts, delay.TotalSeconds);
                resp.Dispose();
                resp = null;
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                if (attempt == MaxAttempts)
                {
                    throw new InvalidOperationException(
                        $"Ollama unreachable after {MaxAttempts} attempts at {_endpoint}: {ex.Message}", ex);
                }
                var delay = ComputeBackoff(attempt, rand);
                _log.LogWarning(ex,
                    "Ollama network error on attempt {N}/{Max}; retrying in {Delay}s",
                    attempt, MaxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                if (attempt == MaxAttempts)
                {
                    throw new InvalidOperationException(
                        $"Ollama request timed out after {MaxAttempts} attempts: {ex.Message}", ex);
                }
                var delay = ComputeBackoff(attempt, rand);
                _log.LogWarning(ex,
                    "Ollama timeout on attempt {N}/{Max}; retrying in {Delay}s",
                    attempt, MaxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            finally
            {
                resp?.Dispose();
            }
        }

        throw new InvalidOperationException("Ollama retry loop exited unexpectedly.");
    }

    private static bool IsTransient(HttpStatusCode code)
    {
        var c = (int)code;
        return c >= 500 && c <= 599;
    }

    private static TimeSpan ComputeBackoff(int attempt, Random rand)
    {
        var seconds = Math.Min(30.0, Math.Pow(2, attempt - 1));
        var ms = TimeSpan.FromSeconds(seconds).TotalMilliseconds;
        var jitter = ms * (rand.NextDouble() * 0.5 - 0.25);
        return TimeSpan.FromMilliseconds(Math.Max(50, ms + jitter));
    }

    private void LogUsage(string raw)
    {
        if (!_log.IsEnabled(LogLevel.Debug)) return;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var promptEval = ReadInt(root, "prompt_eval_count");
            var evalCount = ReadInt(root, "eval_count");
            var totalNs = ReadLong(root, "total_duration");
            _log.LogDebug(
                "Ollama usage: prompt_eval={PE} eval={E} duration={D:F2}s",
                promptEval, evalCount, totalNs / 1_000_000_000.0);
        }
        catch
        {
            // Don't let usage logging break the success path.
        }
    }

    private static int ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i : 0;

    private static long ReadLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)
            ? l : 0;

    private ClassificationResult ParseResponse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (!root.TryGetProperty("message", out var msg)
            || !msg.TryGetProperty("content", out var contentEl))
        {
            return Fallback("missing-message-content", raw);
        }
        var content = contentEl.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(content))
        {
            return Fallback("empty-content", raw);
        }

        // Ollama's structured-output mode constrains the model's content to valid JSON
        // matching the schema. Smaller models occasionally still emit text-around-JSON;
        // try to recover a JSON object substring before giving up.
        try
        {
            using var inner = JsonDocument.Parse(content);
            return ParseClassifyInput(inner.RootElement, content);
        }
        catch (JsonException)
        {
            var maybe = ExtractJsonObject(content);
            if (maybe is not null)
            {
                try
                {
                    using var inner = JsonDocument.Parse(maybe);
                    return ParseClassifyInput(inner.RootElement, content);
                }
                catch (JsonException)
                {
                    // fall through
                }
            }
            return Fallback("non-json-content", content);
        }
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

    private static string? ExtractJsonObject(string s)
    {
        var first = s.IndexOf('{');
        var last = s.LastIndexOf('}');
        if (first < 0 || last <= first) return null;
        return s[first..(last + 1)];
    }

    private static ClassificationResult Fallback(string detail, string raw) =>
        new(
            ClassificationAction.Ambiguous,
            Confidence: 0.0,
            Reason: $"unparseable classifier output ({detail})",
            RawText: Truncate(raw, 500));

    private static JsonObject BuildFormatSchema() => new()
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
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
