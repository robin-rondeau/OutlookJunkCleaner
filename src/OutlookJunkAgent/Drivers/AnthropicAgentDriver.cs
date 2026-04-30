using System.Net;
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
///
/// Resilience: retries on 429 / 529 / 5xx / network failures up to MaxAttempts with exponential
/// backoff (±25% jitter), respecting the server's Retry-After header when present. Logs the
/// Anthropic request-id on failure for support tickets, and usage tokens (incl. cache_read /
/// cache_create) at Debug level so prompt-cache effectiveness is observable across a run.
/// </summary>
public sealed class AnthropicAgentDriver : IAgentDriver
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";
    private const int MaxTokens = 256;
    private const int MaxAttempts = 5;
    private const string ClassifyToolName = "classify";
    private const string RequestIdHeader = "request-id";
    private const string AnthropicRequestIdHeader = "anthropic-request-id";

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
                var requestId = TryGetHeader(resp, RequestIdHeader)
                    ?? TryGetHeader(resp, AnthropicRequestIdHeader);

                if (resp.IsSuccessStatusCode)
                {
                    LogUsage(raw, requestId);
                    return raw;
                }

                if (!IsTransient(resp.StatusCode) || attempt == MaxAttempts)
                {
                    throw new InvalidOperationException(
                        $"Anthropic API error {(int)resp.StatusCode} after {attempt} attempt(s) " +
                        $"(request-id={requestId ?? "?"}): {Truncate(raw, 1000)}");
                }

                var delay = ComputeRetryDelay(attempt, resp.Headers, rand);
                _log.LogWarning(
                    "Anthropic {Status} on attempt {N}/{Max} (request-id={Rid}); retrying in {Delay}s",
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
                        $"Anthropic network error after {MaxAttempts} attempts: {ex.Message}", ex);
                }
                var delay = ComputeBackoff(attempt, rand);
                _log.LogWarning(ex,
                    "Anthropic network error on attempt {N}/{Max}; retrying in {Delay}s",
                    attempt, MaxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // HttpClient.Timeout fired — distinct from user cancellation.
                if (attempt == MaxAttempts)
                {
                    throw new InvalidOperationException(
                        $"Anthropic request timed out after {MaxAttempts} attempts: {ex.Message}", ex);
                }
                var delay = ComputeBackoff(attempt, rand);
                _log.LogWarning(ex,
                    "Anthropic timeout on attempt {N}/{Max}; retrying in {Delay}s",
                    attempt, MaxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            finally
            {
                resp?.Dispose();
            }
        }

        // Unreachable: the loop either returns on success or throws on the final attempt.
        throw new InvalidOperationException("Anthropic retry loop exited unexpectedly.");
    }

    private static bool IsTransient(HttpStatusCode code)
    {
        var c = (int)code;
        return c == 429        // rate / quota
            || c == 529        // overloaded
            || (c >= 500 && c <= 599);
    }

    private static TimeSpan ComputeRetryDelay(int attempt, HttpResponseHeaders headers, Random rand)
    {
        // If the server tells us when to retry, honour it as the floor (still add jitter).
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
        // 1s, 2s, 4s, 8s, 16s — capped at 60s — each with ±25% jitter.
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
            var input = ReadInt(usage, "input_tokens");
            var output = ReadInt(usage, "output_tokens");
            var cacheRead = ReadInt(usage, "cache_read_input_tokens");
            var cacheCreate = ReadInt(usage, "cache_creation_input_tokens");
            _log.LogDebug(
                "Anthropic usage: input={I} output={O} cache_read={CR} cache_create={CC} request-id={Rid}",
                input, output, cacheRead, cacheCreate, requestId ?? "?");
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
