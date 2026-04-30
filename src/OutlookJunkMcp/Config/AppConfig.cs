using OutlookJunkCommon;

namespace OutlookJunkMcp.Config;

public sealed class AppConfig
{
    public required string ClientId { get; init; }
    public required string TriageFolderName { get; init; }
    public required bool AllowDelete { get; init; }

    public static AppConfig Load()
    {
        var clientId = Environment.GetEnvironmentVariable(EnvVars.ClientId);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException(
                $"Required environment variable '{EnvVars.ClientId}' is not set. " +
                "Register a public-client Azure app for personal Microsoft accounts and set this to its client ID.");
        }

        var triageName = Environment.GetEnvironmentVariable(EnvVars.TriageFolder);
        if (string.IsNullOrWhiteSpace(triageName))
        {
            triageName = FolderNames.DefaultTriage;
        }

        var allowDeleteRaw = Environment.GetEnvironmentVariable(EnvVars.AllowDelete);
        var allowDelete = allowDeleteRaw is "1" or "true" or "TRUE" or "True";

        return new AppConfig
        {
            ClientId = clientId,
            TriageFolderName = triageName,
            AllowDelete = allowDelete,
        };
    }
}
