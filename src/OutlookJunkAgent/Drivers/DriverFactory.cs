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

    // Groq's free tier hosts Llama 3.3 70B at hundreds of tok/s and supports response_format
    // json_schema (strict), so we get the same {action, confidence, reason} guarantee as the
    // Anthropic and Ollama drivers without paying anything. Free-tier ToS reserves the right
    // to use inputs/outputs for service improvement — see README.
    public const string DefaultGroqModel = "llama-3.3-70b-versatile";

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
                model = Environment.GetEnvironmentVariable(EnvVars.GroqModel) ?? DefaultGroqModel;
                var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                return new GroqAgentDriver(http, apiKey, model, loggerFactory.CreateLogger<GroqAgentDriver>());
            }
            default:
                throw new NotSupportedException(
                    $"Unknown {EnvVars.AgentProvider}='{provider}'. " +
                    "Supported: anthropic, ollama, groq. " +
                    "To add another provider, implement IAgentDriver and add a case to DriverFactory.");
        }
    }
}
