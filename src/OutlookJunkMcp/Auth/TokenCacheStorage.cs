using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace OutlookJunkMcp.Auth;

public static class TokenCacheStorage
{
    private const string CacheFileName = "token.cache";
    private const string CacheDirName = "OutlookJunkMcp";

    // Linux/macOS keychain identifiers — only used if the binary is ever run off Windows.
    // On Windows, the cache helper transparently uses DPAPI.
    private const string KeyringSchemaName = "com.outlookjunkmcp.tokencache";
    private const string KeyringCollection = "default";
    private const string KeyringSecretLabel = "OutlookJunkMcp Token Cache";
    private const string MacKeyChainServiceName = "OutlookJunkMcp";
    private const string MacKeyChainAccountName = "MSAL";

    public static async Task RegisterAsync(IPublicClientApplication app, CancellationToken ct = default)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cacheDir = Path.Combine(localAppData, CacheDirName);
        Directory.CreateDirectory(cacheDir);

        var props = new StorageCreationPropertiesBuilder(CacheFileName, cacheDir)
            .WithLinuxKeyring(
                schemaName: KeyringSchemaName,
                collection: KeyringCollection,
                secretLabel: KeyringSecretLabel,
                attribute1: new KeyValuePair<string, string>("Version", "1"),
                attribute2: new KeyValuePair<string, string>("Product", CacheDirName))
            .WithMacKeyChain(
                serviceName: MacKeyChainServiceName,
                accountName: MacKeyChainAccountName)
            .Build();

        var helper = await MsalCacheHelper.CreateAsync(props).ConfigureAwait(false);
        helper.RegisterCache(app.UserTokenCache);

        ct.ThrowIfCancellationRequested();
    }

    public static string CacheDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), CacheDirName);
}
