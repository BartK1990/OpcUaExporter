using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using OpcUaExporter.Services;

namespace OpcUaExporter;

/// <summary>
/// WPF application entry point.
/// Builds the DI container and launches the main window.
/// </summary>
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var serviceCollection = new ServiceCollection();

        serviceCollection.AddWpfBlazorWebView();
        serviceCollection.AddLogging();

        // Application services — singletons so Blazor components share state
        serviceCollection.AddSingleton<DiagnosticsLogService>();
        serviceCollection.AddSingleton<PythonBridgeService>();
        serviceCollection.AddSingleton<OpcUaService>();

        Services = serviceCollection.BuildServiceProvider();

        var mainWindow = new MainWindow(Services);
        mainWindow.Show();
    }
}
