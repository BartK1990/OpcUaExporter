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

    builder.Configuration
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddJsonFile(Path.Combine("config", "appsettings.Local.json"), optional: true, reloadOnChange: true)
        .AddEnvironmentVariables("OPCUABRIDGE_")
        .AddCommandLine(args);

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

    builder.WebHost.UseUrls(webOptions.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

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
