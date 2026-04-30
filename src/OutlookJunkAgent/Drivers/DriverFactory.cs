using Microsoft.Extensions.Logging;
using OutlookJunkCommon;

namespace OutlookJunkAgent.Drivers;

public static class DriverFactory
{
    public const string DefaultAnthropicModel = "claude-opus-4-7";

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
            default:
                throw new NotSupportedException(
                    $"Unknown {EnvVars.AgentProvider}='{provider}'. " +
                    "Implement a new IAgentDriver and add it to DriverFactory to support additional providers.");
        }
    }
}
