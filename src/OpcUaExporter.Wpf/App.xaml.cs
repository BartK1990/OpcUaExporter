using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using OpcUaExporter.Abstractions;
using OpcUaExporter.Services;
using OpcUaExporter.UI;

namespace OpcUaExporter;

/// <summary>
/// WPF application entry point: builds the DI container the Blazor UI resolves
/// its services from, installs the global crash handlers, and shows the shell
/// window.
/// </summary>
public partial class App : Application
{
    private IServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _services = BuildServiceProvider();

        new MainWindow(_services).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            (_services as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            // The process is going away regardless; a failed cleanup must not
            // turn a clean exit into a crash dialog.
            LogCrash("shutdown", ex);
        }

        base.OnExit(e);
    }

    private static IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddWpfBlazorWebView();
        services.AddLogging();

        // Platform services first: AddOpcUaExporterUi only fills in the
        // abstractions the host has not already supplied.
        services.AddSingleton<IFileDialogService, WpfFileDialogService>();

        // Application + UI services. All singletons, so every Blazor page
        // observes the same connection, tag tree and subscription.
        services.AddOpcUaExporterUi();

        return services.BuildServiceProvider();
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
            File.AppendAllText(
                AppPaths.CrashLogFile,
                $"[{DateTime.Now:O}] Unhandled exception on {source}:{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never itself throw during crash handling.
        }
    }
}
