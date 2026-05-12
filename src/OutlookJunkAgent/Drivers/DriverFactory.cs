using Microsoft.Extensions.Logging;
using OutlookJunkCommon;

namespace OutlookJunkAgent.Drivers;

public static class DriverFactory
{
    // Haiku 4.5 is the default: with prompt caching this workload runs to single-digit dollars
    // per month at typical volume, plenty smart for junk classification. Override via
    // OUTLOOK_JUNK_AGENT_MODEL — e.g. "claude-opus-4-7" if you want maximum quality.
    public const string DefaultAnthropicModel = "claude-haiku-4-5-20251001";

    public const string DefaultOllamaBaseUrl = "http://localhost:11434";
    public const string DefaultOllamaModel = "llama3.1:8b";

    // Groq's free tier hosts Llama 3.3 70B at hundreds of tok/s. The driver uses
    // response_format: json_object (universally supported) plus a system-prompt directive that
    // pins the output shape to the same {action, confidence, reason} contract the other
    // drivers produce. Strict json_schema is gated to a small list of Groq models that does
    // not include this one. Free-tier ToS reserves the right to use inputs/outputs for service
    // improvement — see README.
    //
    // Default is a comma-separated fallback chain: when llama-3.3-70b-versatile exhausts its
    // daily token budget, the driver fails over to llama-3.1-8b-instant (separate TPD bucket)
    // so the run can keep classifying instead of aborting. Override via OUTLOOK_JUNK_GROQ_MODEL
    // with either a single name or a comma-separated list.
    public const string DefaultGroqModel = "llama-3.3-70b-versatile,llama-3.1-8b-instant";

    public static IAgentDriver Create(ILoggerFactory loggerFactory, out string providerName, out string model)
    {
        var provider = (Environment.GetEnvironmentVariable(EnvVars.AgentProvider) ?? "anthropic").ToLowerInvariant();
        providerName = provider;

        switch (provider)
        {
            case "anthropic":
            {
                var apiKey = Environment.GetEnvironmentVariable(EnvVars.AnthropicApiKey)
                    ?? throw new InvalidOperationException(
                        $"{EnvVars.AnthropicApiKey} is not set. Required for the Anthropic driver.");
                model = Environment.GetEnvironmentVariable(EnvVars.AnthropicModel) ?? DefaultAnthropicModel;
                var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                return new AnthropicAgentDriver(http, apiKey, model, loggerFactory.CreateLogger<AnthropicAgentDriver>());
            }
            case "ollama":
            {
                var baseUrl = Environment.GetEnvironmentVariable(EnvVars.OllamaBaseUrl) ?? DefaultOllamaBaseUrl;
                model = Environment.GetEnvironmentVariable(EnvVars.OllamaModel) ?? DefaultOllamaModel;
                // Local Ollama needs more headroom for cold-start model loads (first call after
                // boot can take 30s+). Cron's 10-minute outer cancellation token is the real cap.
                var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                return new OllamaAgentDriver(http, baseUrl, model, loggerFactory.CreateLogger<OllamaAgentDriver>());
            }
            case "groq":
            {
                var apiKey = Environment.GetEnvironmentVariable(EnvVars.GroqApiKey)
                    ?? throw new InvalidOperationException(
                        $"{EnvVars.GroqApiKey} is not set. Required for the Groq driver.");
                var modelSpec = Environment.GetEnvironmentVariable(EnvVars.GroqModel) ?? DefaultGroqModel;
                var models = modelSpec
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                if (models.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"{EnvVars.GroqModel} resolved to an empty model list. " +
                        $"Supply one model name or a comma-separated fallback chain.");
                }
                // Report the full chain to the run summary so users can see which models are
                // in scope without having to dig through env vars.
                model = string.Join(",", models);
                // Groq llama-3.3-70b-versatile with 256 max_completion_tokens typically replies in
                // 1-5s. A tight per-attempt HTTP timeout means a single wedged TCP connection can't
                // burn through the 9-minute outer run cap before the retry loop notices and fails over.
                var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                return new GroqAgentDriver(http, apiKey, models, loggerFactory.CreateLogger<GroqAgentDriver>());
            }
            default:
                throw new NotSupportedException(
                    $"Unknown {EnvVars.AgentProvider}='{provider}'. " +
                    "Supported: anthropic, ollama, groq. " +
                    "To add another provider, implement IAgentDriver and add a case to DriverFactory.");
        }
    }
}
