using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Playwright;
using OpcUaExporter.SimServer;
using OpcUaExporter.Web;

namespace OpcUaExporter.E2ETests;

/// <summary>
/// Shared for the whole run: an isolated data directory, one OPC UA simulation
/// server and one Chromium instance. Each test gets its own web app (and so its
/// own singleton services) from <see cref="StartAppAsync"/>.
/// </summary>
public sealed class E2EEnvironment : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "OpcUaExporter.E2ETests", Guid.NewGuid().ToString("N"));
    private IPlaywright? _playwright;

    public SimServerHost Sim { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Keep profiles, settings and the client PKI out of the user's real %LocalAppData%\OpcUaExporter.
        Environment.SetEnvironmentVariable(AppPaths.RootDirectoryOverrideVariable, _dataDirectory);

        Sim = await SimServerHost.StartAsync(GetFreeTcpPort());

        _playwright = await Playwright.CreateAsync();
        try
        {
            Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException(
                "Chromium for Playwright is not installed. Set PLAYWRIGHT_BROWSERS_PATH to an existing install, or run " +
                "'pwsh tests/OpcUaExporter.E2ETests/bin/Debug/net8.0/playwright.ps1 install chromium'.", ex);
        }
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
            await Browser.CloseAsync();
        _playwright?.Dispose();
        if (Sim is not null)
            await Sim.DisposeAsync();

        try { Directory.Delete(_dataDirectory, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Starts a fresh browser host on a free loopback port.</summary>
    public async Task<RunningApp> StartAppAsync()
    {
        var app = OpcUaExporterWebApp.Build(["--urls", "http://127.0.0.1:0"], environmentName: "Development");
        await app.StartAsync();

        // With port 0 Kestrel picks a free port; Urls reports the bound address once started.
        return new RunningApp(app, app.Urls.First());
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

public sealed class RunningApp(WebApplication app, string baseUrl) : IAsyncDisposable
{
    public string BaseUrl { get; } = baseUrl;

    public IServiceProvider Services => app.Services;

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class E2ECollection : ICollectionFixture<E2EEnvironment>
{
    public const string Name = "E2E";
}
