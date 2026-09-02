using System.IO;
using System.Windows;
using System.Windows.Threading;
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

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var serviceCollection = new ServiceCollection();

        serviceCollection.AddWpfBlazorWebView();
        serviceCollection.AddLogging();

        // Application services — singletons so Blazor components share state
        serviceCollection.AddSingleton<DiagnosticsLogService>();
        serviceCollection.AddSingleton<OpcUaClientService>();
        serviceCollection.AddSingleton<OpcUaService>();
        serviceCollection.AddSingleton<ThemeService>();

        Services = serviceCollection.BuildServiceProvider();

        var mainWindow = new MainWindow(Services);
        mainWindow.Show();
    }

    // Exceptions raised on the UI (dispatcher) thread, e.g. while marshalling a
    // background OPC UA subscription update. Mark handled so one bad update
    // doesn't take the whole app down.
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("UI thread", e.Exception);
        e.Handled = true;
    }

    // Exceptions raised on background threads (e.g. the OPC UA SDK's publish
    // thread). These cannot be marked handled — the process will still
    // terminate — but at least the cause gets logged before it does.
    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogCrash("background thread", e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("unobserved task", e.Exception);
        e.SetObserved();
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpcUaExporter", "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"[{DateTime.Now:O}] Unhandled exception on {source}:{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never itself throw during crash handling.
        }
    }
}
