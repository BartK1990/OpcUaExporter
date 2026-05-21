using System.Windows;
using Microsoft.Win32;

namespace OpcUaExporter;

/// <summary>
/// WPF host window for the Blazor Hybrid UI.
/// Exposes a JS-invokable method that opens the native WPF SaveFileDialog.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();

        // Wire up services after XAML is initialised
        // (BlazorWebView.Services is set via XAML binding to App.Services,
        //  but we accept IServiceProvider here for future constructor use.)
    }

    // ──────────────────────────────────────────────────────────────────────
    // JS-invokable: show a native WPF SaveFileDialog.
    // Called from Blazor via:
    //   window.showSaveDialog = (filter) =>
    //       DotNet.invokeMethodAsync('OpcUaExporter', 'ShowSaveDialogAsync', filter);
    // ──────────────────────────────────────────────────────────────────────
    [Microsoft.JSInterop.JSInvokable("ShowSaveDialogAsync")]
    public static Task<string> ShowSaveDialogAsync(string filter)
    {
        string result = string.Empty;

        // WPF dialogs must run on the UI (STA) thread
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var dlg = new SaveFileDialog
            {
                Filter          = filter,
                OverwritePrompt = true,
                AddExtension    = true
            };

            if (dlg.ShowDialog() == true)
                result = dlg.FileName;
        });

        return Task.FromResult(result);
    }
}
