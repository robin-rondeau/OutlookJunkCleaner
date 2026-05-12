using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace OutlookJunkAgent.Drivers;

/// <summary>
/// Groq Cloud driver. Uses Groq's OpenAI-compatible chat-completions endpoint at
/// https://api.groq.com/openai/v1/chat/completions and pins the output to JSON via
/// response_format: json_object (universally supported on Groq, unlike strict json_schema
/// which is gated to a small list of models that does not include llama-3.3-70b-versatile).
/// The system prompt is augmented with an explicit shape directive so the model knows to
/// emit {action, confidence, reason} as the JSON object body. The MCP server's defenses
/// (id scoping, sanitization, server-side reason validation) still apply regardless of which
/// provider drives classification.
///
/// Resilience: retries on 429 / 5xx / network failures up to MaxAttempts with exponential
/// backoff (±25% jitter), respecting the server's Retry-After header when present. Logs the
/// Groq x-request-id on failure for support tickets, and prompt/completion token counts at
/// Debug level so per-run consumption is observable against the free-tier daily caps.
///
/// Multi-model failover: the driver accepts an ordered list of models. Each Groq model has its
/// own free-tier TPD bucket, so when the primary returns a long retry-after (tokens-per-day
/// exhausted) we transparently switch to the next model in the list instead of aborting the
/// LLM pass. The active index is sticky across ClassifyAsync calls so subsequent messages in
/// the same run don't waste a request on a model already known to be daily-exhausted. Only
/// when every model in the list is exhausted does GroqRateLimitException fire.
/// </summary>
public sealed class GroqAgentDriver : IAgentDriver
{
    private const string Endpoint = "https://api.groq.com/openai/v1/chat/completions";
    private const int MaxCompletionTokens = 256;
    private const int MaxAttempts = 3;
    // Fallback cap when the caller doesn't supply a run deadline. Production code always passes
    // one; this exists so tests and ad-hoc usage don't accidentally inherit unbounded waits.
    private const int FallbackMaxRetryAfterSeconds = 30;
    private const string RequestIdHeader = "x-request-id";

    // The shared system prompt (RubricLoader) talks about "the classify tool" because the
    // Anthropic driver routes through forced tool use. Groq's json_object mode just needs
    // any JSON; we append this directive so a 70B model knows the exact target shape.
    // Mentioning "JSON" is also required by Groq's OpenAI-compat layer when json_object is set.
    private const string JsonOutputDirective = """

# Output format (Groq driver)

Emit your decision as a single JSON object and nothing else. No prose, no markdown fence, no
preamble. The object must have exactly these fields:

  {"action": "confident_junk" | "ambiguous" | "not_junk",
   "confidence": <number 0..1>,
   "reason": "<= 200 chars; plain ASCII; one short clause"}
""";

    private readonly HttpClient _http;
    private readonly IReadOnlyList<string> _models;
    private int _activeModelIndex;
    private readonly ILogger<GroqAgentDriver> _log;

    public GroqAgentDriver(HttpClient http, string apiKey, IReadOnlyList<string> models, ILogger<GroqAgentDriver> log)
    {
        if (models.Count == 0) throw new ArgumentException("At least one model is required.", nameof(models));
        _http = http;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _models = models;
        _activeModelIndex = 0;
        _log = log;
    }

    public async Task<ClassificationResult> ClassifyAsync(ClassificationRequest req, CancellationToken ct)
    {
        var systemPrompt = req.SystemPrompt + JsonOutputDirective;
        var body = new JsonObject
        {
            ["temperature"] = 0.1,
            ["max_completion_tokens"] = MaxCompletionTokens,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = req.SpotlightedEmail },
            },
        };

        var raw = await SendWithRetryAsync(body, req.Deadline, ct).ConfigureAwait(false);
        return ParseResponse(raw);
    }

    private async Task<string> SendWithRetryAsync(JsonObject body, DateTimeOffset? deadline, CancellationToken ct)
    {
        var rand = Random.Shared;
        var attempt = 0;

        while (true)
        {
            attempt++;
            ct.ThrowIfCancellationRequested();

            // Body is mutated each attempt so a mid-run failover targets the new model.
            body["model"] = _models[_activeModelIndex];
            var activeModel = _models[_activeModelIndex];

            HttpResponseMessage? resp = null;
            try
            {
                resp = await _http.PostAsJsonAsync(Endpoint, body, ct).ConfigureAwait(false);
                var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var requestId = TryGetHeader(resp, RequestIdHeader);

                if (resp.IsSuccessStatusCode)
                {
                    LogUsage(raw, requestId, activeModel);
                    return raw;
                }

                if (IsTransient(resp.StatusCode)
                    && TryGetServerRetryAfter(resp.Headers, out var serverDelay))
                {
                    var remaining = deadline.HasValue
                        ? deadline.Value - DateTimeOffset.Now
                        : TimeSpan.FromSeconds(FallbackMaxRetryAfterSeconds);
                    if (serverDelay > remaining)
                    {
                        // Retry-after exceeds the remaining run budget — characteristic of a
                        // tokens-per-day exhaustion (minutes-to-hours), not a momentary RPM
                        // throttle (seconds). Each Groq model has its own TPD bucket, so try
                        // the next model in the chain before giving up on the whole run.
                        if (_activeModelIndex + 1 < _models.Count)
                        {
                            var nextModel = _models[_activeModelIndex + 1];
                            _log.LogWarning(
                                "Groq {Status} on {Model}: server asks {Wait:F0}s (exceeds budget {Budget:F0}s); " +
                                "falling over to {Next} (request-id={Rid})",
                                (int)resp.StatusCode, activeModel, serverDelay.TotalSeconds,
                                remaining.TotalSeconds, nextModel, requestId ?? "?");
                            _activeModelIndex++;
                            attempt = 0;
                            resp.Dispose();
                            resp = null;
                            continue;
                        }

                        throw new GroqRateLimitException(
                            serverDelay,
                            requestId,
                            $"Groq {(int)resp.StatusCode}: server asks to wait {serverDelay.TotalSeconds:F0}s, " +
                            $"exceeds remaining run budget ({remaining.TotalSeconds:F0}s); " +
                            $"all {_models.Count} model(s) in fallback chain exhausted. " +
                            $"model={activeModel} request-id={requestId ?? "?"} body={Truncate(raw, 500)}");
                    }
                }

                if (!IsTransient(resp.StatusCode) || attempt >= MaxAttempts)
                {
                    throw new InvalidOperationException(
                        $"Groq API error {(int)resp.StatusCode} after {attempt} attempt(s) on {activeModel} " +
                        $"(request-id={requestId ?? "?"}): {Truncate(raw, 1000)}");
                }

                var delay = ComputeRetryDelay(attempt, resp.Headers, rand);
                _log.LogWarning(
                    "Groq {Status} on {Model} attempt {N}/{Max} (request-id={Rid}); retrying in {Delay:F1}s",
                    (int)resp.StatusCode, activeModel, attempt, MaxAttempts, requestId ?? "?", delay.TotalSeconds);
                resp.Dispose();
                resp = null;
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= MaxAttempts)
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
                if (attempt >= MaxAttempts)
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
    }

    private static bool IsTransient(HttpStatusCode code)
    {
        var c = (int)code;
        return c == 429 || (c >= 500 && c <= 599);
    }

    private static TimeSpan ComputeRetryDelay(int attempt, HttpResponseHeaders headers, Random rand)
    {
        if (TryGetServerRetryAfter(headers, out var serverDelay))
        {
            return AddJitter(serverDelay, rand);
        }
        return ComputeBackoff(attempt, rand);
    }

    private static bool TryGetServerRetryAfter(HttpResponseHeaders headers, out TimeSpan delay)
    {
        if (headers.RetryAfter is { } ra)
        {
            if (ra.Delta is TimeSpan d && d > TimeSpan.Zero)
            {
                delay = d;
                return true;
            }
            if (ra.Date is DateTimeOffset when)
            {
                var diff = when - DateTimeOffset.UtcNow;
                if (diff > TimeSpan.Zero)
                {
                    delay = diff;
                    return true;
                }
            }
        }
        delay = TimeSpan.Zero;
        return false;
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

    private void LogUsage(string raw, string? requestId, string model)
    {
        if (!_log.IsEnabled(LogLevel.Debug)) return;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("usage", out var usage)) return;
            var prompt = ReadInt(usage, "prompt_tokens");
            var completion = ReadInt(usage, "completion_tokens");
            _log.LogDebug(
                "Groq usage: model={M} prompt={P} completion={C} request-id={Rid}",
                model, prompt, completion, requestId ?? "?");
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

        // response_format: json_object guarantees the content parses as JSON, but not the shape.
        // ParseClassifyInput enforces {action, confidence, reason} below; on field-shape misses
        // we fall through to Ambiguous@0.0 (same posture as the Ollama driver).
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

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// Thrown when Groq returns a transient error with a Retry-After value that exceeds the remaining
/// run budget. Signals to the host that the entire run should abort rather than burn time waiting
/// for a quota window that won't close before the outer cancellation fires.
/// </summary>
public sealed class GroqRateLimitException : Exception
{
    public TimeSpan RetryAfter { get; }
    public string? RequestId { get; }

    public GroqRateLimitException(TimeSpan retryAfter, string? requestId, string message)
        : base(message)
    {
        RetryAfter = retryAfter;
        RequestId = requestId;
    }
}
