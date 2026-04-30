using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using OutlookJunkMcp.Auth;
using OutlookJunkMcp.Config;
using OutlookJunkMcp.Graph;
using OutlookJunkMcp.Sanitizer;
using OutlookJunkMcp.Session;
using OutlookJunkMcp.Tools;

var mode = ParseMode(args);

if (mode == Mode.TestSanitizer)
{
    return SanitizerSelfTest.Run();
}

var config = AppConfig.Load();

return mode switch
{
    Mode.FirstAuth => await RunFirstAuthAsync(config),
    Mode.SelfTest => await RunSelfTestAsync(config),
    Mode.Server => await RunServerAsync(config, args),
    _ => PrintUsage(),
};

static Mode ParseMode(string[] args)
{
    foreach (var a in args)
    {
        switch (a)
        {
            case "--first-auth": return Mode.FirstAuth;
            case "--self-test": return Mode.SelfTest;
            case "--test-sanitizer": return Mode.TestSanitizer;
            case "--help" or "-h": return Mode.Help;
        }
    }
    return Mode.Server;
}

static int PrintUsage()
{
    Console.Error.WriteLine("OutlookJunkMcp — MCP server for Outlook consumer Junk/Triage operations");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  OutlookJunkMcp.exe                  run as MCP server over stdio (default)");
    Console.Error.WriteLine("  OutlookJunkMcp.exe --first-auth     interactive device-code sign-in to warm token cache");
    Console.Error.WriteLine("  OutlookJunkMcp.exe --self-test      print Junk/Triage counts and exit");
    Console.Error.WriteLine("  OutlookJunkMcp.exe --test-sanitizer run EmailSanitizer in-process self-test (no auth)");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Required env vars:");
    Console.Error.WriteLine("  OUTLOOK_JUNK_MCP_CLIENT_ID      Azure app registration (public client) ID");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Optional env vars:");
    Console.Error.WriteLine("  OUTLOOK_JUNK_MCP_TRIAGE_FOLDER  display name (default: 'Triage')");
    Console.Error.WriteLine("  OUTLOOK_JUNK_MCP_ALLOW_DELETE   '1' to register delete_from_junk (Phase B)");
    return 0;
}

static async Task<int> RunFirstAuthAsync(AppConfig config)
{
    using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    }));
    var log = loggerFactory.CreateLogger("first-auth");

    var auth = await MsalAuth.CreateAsync(config.ClientId, loggerFactory.CreateLogger<MsalAuth>());
    log.LogInformation("Starting device-code sign-in. Visit the URL printed below and enter the code.");

    var token = await auth.AcquireTokenInteractiveAsync(dcr =>
    {
        Console.WriteLine();
        Console.WriteLine(dcr.Message);
        Console.WriteLine();
    });

    log.LogInformation("First-auth complete. Token cached at {Dir}.", TokenCacheStorage.CacheDirectory);
    _ = token;
    return 0;
}

static async Task<int> RunSelfTestAsync(AppConfig config)
{
    using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    }));
    var log = loggerFactory.CreateLogger("self-test");

    var (mail, _) = await BuildMailClientAsync(config, new EmailSanitizer(), new SurfacedIds(), loggerFactory);
    var status = await mail.GetStatusAsync(config.AllowDelete, CancellationToken.None);

    Console.WriteLine($"Junk: {status.JunkCount} ({status.JunkUnreadCount} unread)");
    Console.WriteLine($"Triage: {status.TriageCount}");
    Console.WriteLine($"DeleteEnabled: {status.DeleteEnabled}");
    log.LogInformation("Self-test ok.");
    return 0;
}

static async Task<int> RunServerAsync(AppConfig config, string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    // The MCP protocol speaks over stdout — every log must go to stderr.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.SetMinimumLevel(LogLevel.Information);

    builder.Services.AddSingleton(config);

    var sanitizer = new EmailSanitizer();
    var surfaced = new SurfacedIds();
    builder.Services.AddSingleton(sanitizer);
    builder.Services.AddSingleton(surfaced);

    // Build MailClient eagerly so a missing token fails the server start, not the first tool call.
    var (mail, folders) = await BuildMailClientAsync(config, sanitizer, surfaced, LoggerFactory.Create(b =>
    {
        b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    }));
    builder.Services.AddSingleton(mail);
    builder.Services.AddSingleton(folders);

    var mcp = builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<JunkTools>();

    if (config.AllowDelete)
    {
        mcp.WithTools<DeleteTool>();
    }

    await builder.Build().RunAsync();
    return 0;
}

static async Task<(MailClient Mail, FolderResolver Folders)> BuildMailClientAsync(
    AppConfig config,
    EmailSanitizer sanitizer,
    SurfacedIds surfaced,
    ILoggerFactory loggerFactory)
{
    var auth = await MsalAuth.CreateAsync(config.ClientId, loggerFactory.CreateLogger<MsalAuth>());

    var graphAuthProvider = new BearerTokenAuthProvider(auth);
    var graph = new GraphServiceClient(new HttpClient(), graphAuthProvider);

    var folders = new FolderResolver(graph, config.TriageFolderName, loggerFactory.CreateLogger<FolderResolver>());
    await folders.ResolveAsync();

    var mail = new MailClient(graph, folders, sanitizer, surfaced, loggerFactory.CreateLogger<MailClient>());
    return (mail, folders);
}

internal sealed class BearerTokenAuthProvider : IAuthenticationProvider
{
    private readonly MsalAuth _auth;

    public BearerTokenAuthProvider(MsalAuth auth)
    {
        _auth = auth;
    }

    public async Task AuthenticateRequestAsync(
        Microsoft.Kiota.Abstractions.RequestInformation request,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        var token = await _auth.AcquireTokenSilentAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Remove("Authorization");
        request.Headers.Add("Authorization", "Bearer " + token);
    }
}

internal enum Mode { Server, FirstAuth, SelfTest, TestSanitizer, Help }
