using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using OpcUaBridge;
using OpcUaBridge.Configuration;
using OpcUaBridge.Host;
using OpcUaBridge.Host.Components;
using Serilog;

// A Windows service starts with its working directory set to C:\Windows\System32.
// Setting it here, before anything else runs, means every relative path in the process --
// configuration files, static assets, Serilog's own sink paths -- resolves beside the
// executable rather than in the system directory.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

Log.Logger = BridgeLogging.CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
        WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
    });

    // Order matters and is pinned by a test; see BridgeConfiguration.
    builder.Configuration.AddBridgeConfiguration(AppContext.BaseDirectory, args);

    // A no-op when the process is not running under the service control manager, so the
    // same executable runs from a console for debugging.
    builder.Host.UseWindowsService(options => options.ServiceName = "OpcUaBridge");

    builder.Host.UseSerilog((_, _, configuration) => BridgeLogging.Configure(configuration));

    builder.Services.AddOpcUaBridge(builder.Configuration);
    builder.Services.AddRazorComponents().AddInteractiveServerComponents();

    // Keep the data-protection keys beside the executable with everything else. The
    // default location is a per-user profile directory, which a service account may not
    // have, and regenerating the keys on every restart drops every open dashboard circuit.
    builder.Services
        .AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(BridgePaths.Default.ConfigDirectory, "keys")))
        .SetApplicationName("OpcUaBridge");

    var webOptions = builder.Configuration
        .GetSection(BridgeOptions.SectionName)
        .Get<BridgeOptions>()?.Web ?? new WebOptions();

    // Bridge:Web:Urls is the single source of truth for the binding, because the options
    // validator's fail-closed check on non-loopback addresses is only meaningful if
    // nothing else can quietly override it. That does mean it wins over ASPNETCORE_URLS
    // and over a launchSettings.json profile, which would otherwise open a browser at an
    // address nothing is listening on -- so say when the two disagree.
    var configuredUrls = webOptions.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    builder.WebHost.UseUrls(configuredUrls);

    var environmentUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
    if (!string.IsNullOrWhiteSpace(environmentUrls) &&
        !WebBinding.AreEquivalent(environmentUrls, webOptions.Urls))
    {
        Log.Warning(
            "ASPNETCORE_URLS is {EnvironmentUrls}, but the dashboard binds {ConfiguredUrls} from " +
            "Bridge:Web:Urls, which takes precedence. Change that setting, or the launchSettings.json " +
            "profile, so the two agree.",
            environmentUrls, webOptions.Urls);
    }

    var app = builder.Build();

    if (await BridgeCommands.TryRunAsync(app.Services, args))
        return 0;

    // Before anything else, so no route -- static asset, Blazor circuit or page -- is
    // reachable without the token when one is configured.
    app.UseMiddleware<AdminTokenMiddleware>();

    app.UseStaticFiles();
    app.UseAntiforgery();
    app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

    Log.Information(
        "OPC UA Bridge starting. Dashboard on {WebUrls} ({Protection}); state in {BaseDirectory}.",
        webOptions.Urls,
        string.IsNullOrWhiteSpace(webOptions.AdminToken) ? "loopback only, no token" : "admin token required",
        BridgePaths.Default.BaseDirectory);

    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "OPC UA Bridge stopped unexpectedly.");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
