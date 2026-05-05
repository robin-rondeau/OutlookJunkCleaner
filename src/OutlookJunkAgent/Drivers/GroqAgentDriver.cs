using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace OutlookJunkAgent.Drivers;

/// <summary>
/// Groq Cloud driver. Uses Groq's OpenAI-compatible chat-completions endpoint at
/// https://api.groq.com/openai/v1/chat/completions and pins the output to the same
/// {action, confidence, reason} contract via response_format: json_schema (strict). On the
/// default model (llama-3.3-70b-versatile) Groq validates the schema server-side, so the
/// model's content is constrained to fit. The MCP server's defenses (id scoping, sanitization,
/// server-side reason validation) still apply regardless of which provider drives classification.
///
/// Resilience: retries on 429 / 5xx / network failures up to MaxAttempts with exponential
/// backoff (±25% jitter), respecting the server's Retry-After header when present. Logs the
/// Groq x-request-id on failure for support tickets, and prompt/completion token counts at
/// Debug level so per-run consumption is observable against the free-tier daily caps.
/// </summary>
public sealed class GroqAgentDriver : IAgentDriver
{
    private const string Endpoint = "https://api.groq.com/openai/v1/chat/completions";
    private const int MaxCompletionTokens = 256;
    private const int MaxAttempts = 5;
    private const string SchemaName = "classification";
    private const string RequestIdHeader = "x-request-id";

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger<GroqAgentDriver> _log;

    public GroqAgentDriver(HttpClient http, string apiKey, string model, ILogger<GroqAgentDriver> log)
    {
        _http = http;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _model = model;
        _log = log;
    }

    public async Task<ClassificationResult> ClassifyAsync(ClassificationRequest req, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["temperature"] = 0.1,
            ["max_completion_tokens"] = MaxCompletionTokens,
            ["response_format"] = BuildResponseFormat(),
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
                resp = await _http.PostAsJsonAsync(Endpoint, body, ct).ConfigureAwait(false);
                var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var requestId = TryGetHeader(resp, RequestIdHeader);

                if (resp.IsSuccessStatusCode)
                {
                    LogUsage(raw, requestId);
                    return raw;
                }

                if (!IsTransient(resp.StatusCode) || attempt == MaxAttempts)
                {
                    throw new InvalidOperationException(
                        $"Groq API error {(int)resp.StatusCode} after {attempt} attempt(s) " +
                        $"(request-id={requestId ?? "?"}): {Truncate(raw, 1000)}");
                }

                var delay = ComputeRetryDelay(attempt, resp.Headers, rand);
                _log.LogWarning(
                    "Groq {Status} on attempt {N}/{Max} (request-id={Rid}); retrying in {Delay}s",
                    (int)resp.StatusCode, attempt, MaxAttempts, requestId ?? "?", delay.TotalSeconds);
                resp.Dispose();
                resp = null;
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                if (attempt == MaxAttempts)
                {
                    throw new InvalidOperationException(
                        $"Groq network error after {MaxAttempts} attempts: {ex.Message}", ex);
                }
                var delay = ComputeBackoff(attempt, rand);
                _log.LogWarning(ex,
                    "Groq network error on attempt {N}/{Max}; retrying in {Delay}s",
                    attempt, MaxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                if (attempt == MaxAttempts)
                {
                    throw new InvalidOperationException(
                        $"Groq request timed out after {MaxAttempts} attempts: {ex.Message}", ex);
                }
                var delay = ComputeBackoff(attempt, rand);
                _log.LogWarning(ex,
                    "Groq timeout on attempt {N}/{Max}; retrying in {Delay}s",
                    attempt, MaxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            finally
            {
                resp?.Dispose();
            }
        }

        throw new InvalidOperationException("Groq retry loop exited unexpectedly.");
    }

    private static bool IsTransient(HttpStatusCode code)
    {
        var c = (int)code;
        return c == 429 || (c >= 500 && c <= 599);
    }

    private static TimeSpan ComputeRetryDelay(int attempt, HttpResponseHeaders headers, Random rand)
    {
        if (headers.RetryAfter is { } ra)
        {
            if (ra.Delta is TimeSpan delta && delta > TimeSpan.Zero)
            {
                return AddJitter(delta, rand);
            }
            if (ra.Date is DateTimeOffset when)
            {
                var d = when - DateTimeOffset.UtcNow;
                if (d > TimeSpan.Zero) return AddJitter(d, rand);
            }
        }
        return ComputeBackoff(attempt, rand);
    }

    private static TimeSpan ComputeBackoff(int attempt, Random rand)
    {
        var seconds = Math.Min(60.0, Math.Pow(2, attempt - 1));
        return AddJitter(TimeSpan.FromSeconds(seconds), rand);
    }

    private static TimeSpan AddJitter(TimeSpan baseDelay, Random rand)
    {
        var ms = baseDelay.TotalMilliseconds;
        var jitter = ms * (rand.NextDouble() * 0.5 - 0.25);
        return TimeSpan.FromMilliseconds(Math.Max(50, ms + jitter));
    }

    private static string? TryGetHeader(HttpResponseMessage resp, string name)
    {
        if (resp.Headers.TryGetValues(name, out var values))
        {
            return values.FirstOrDefault();
        }
        return null;
    }

    private void LogUsage(string raw, string? requestId)
    {
        if (!_log.IsEnabled(LogLevel.Debug)) return;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("usage", out var usage)) return;
            var prompt = ReadInt(usage, "prompt_tokens");
            var completion = ReadInt(usage, "completion_tokens");
            _log.LogDebug(
                "Groq usage: prompt={P} completion={C} request-id={Rid}",
                prompt, completion, requestId ?? "?");
        }
        catch
        {
            // Never let usage logging break the success path.
        }
    }

    private static int ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : 0;

    private ClassificationResult ParseResponse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return Fallback("missing-choices", raw);
        }

        var first = choices[0];
        if (!first.TryGetProperty("message", out var msg)
            || !msg.TryGetProperty("content", out var contentEl))
        {
            return Fallback("missing-message-content", raw);
        }

        var content = contentEl.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(content))
        {
            return Fallback("empty-content", raw);
        }

        // response_format: json_schema constrains content to valid schema-conformant JSON.
        // The recovery path mirrors the Ollama driver: if a routed model occasionally emits
        // text-around-JSON, try to extract a JSON object substring before giving up.
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

    private static JsonObject BuildResponseFormat() => new()
    {
        ["type"] = "json_schema",
        ["json_schema"] = new JsonObject
        {
            ["name"] = SchemaName,
            ["strict"] = true,
            ["schema"] = new JsonObject
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
        },
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
