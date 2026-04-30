using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace OutlookJunkMcp.Auth;

public sealed class MsalAuth
{
    private static readonly string[] Scopes = ["Mail.ReadWrite"];
    private static readonly string RedirectUri = "https://login.microsoftonline.com/common/oauth2/nativeclient";

    private readonly IPublicClientApplication _pca;
    private readonly ILogger<MsalAuth> _log;

    private MsalAuth(IPublicClientApplication pca, ILogger<MsalAuth> log)
    {
        _pca = pca;
        _log = log;
    }

    public static async Task<MsalAuth> CreateAsync(string clientId, ILogger<MsalAuth> log, CancellationToken ct = default)
    {
        var pca = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, AadAuthorityAudience.PersonalMicrosoftAccount)
            .WithRedirectUri(RedirectUri)
            .Build();

        await TokenCacheStorage.RegisterAsync(pca, ct).ConfigureAwait(false);
        return new MsalAuth(pca, log);
    }

    /// <summary>
    /// Acquires a token silently from cache. Throws MsalUiRequiredException if interactive sign-in is needed.
    /// Used by the running MCP server — never falls back to interactive flows.
    /// </summary>
    public async Task<string> AcquireTokenSilentAsync(CancellationToken ct = default)
    {
        var accounts = await _pca.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault();
        if (account is null)
        {
            throw new MsalUiRequiredException(
                "no_account",
                "No cached account. Run with --first-auth to perform device-code sign-in.");
        }

        var result = await _pca.AcquireTokenSilent(Scopes, account)
            .ExecuteAsync(ct)
            .ConfigureAwait(false);

        return result.AccessToken;
    }

    /// <summary>
    /// Performs the interactive device-code flow. Used only by --first-auth mode.
    /// </summary>
    public async Task<string> AcquireTokenInteractiveAsync(Action<DeviceCodeResult> onCode, CancellationToken ct = default)
    {
        var result = await _pca.AcquireTokenWithDeviceCode(Scopes, dcr =>
        {
            onCode(dcr);
            return Task.CompletedTask;
        }).ExecuteAsync(ct).ConfigureAwait(false);

        _log.LogInformation("Acquired token for {Account}, expires {Expires:u}", result.Account.Username, result.ExpiresOn);
        return result.AccessToken;
    }
}
