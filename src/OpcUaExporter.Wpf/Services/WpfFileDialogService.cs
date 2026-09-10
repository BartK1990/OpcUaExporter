using System.Windows;
using Microsoft.Win32;
using OpcUaExporter.Abstractions;

namespace OpcUaExporter.Services;

/// <summary>
/// <see cref="IFileDialogService"/> backed by the native WPF dialogs.
/// <para>
/// Blazor Hybrid runs inside a WebView with no browser file picker, so the
/// shell supplies the pickers. Every dialog is marshalled onto the UI thread
/// because Win32 common dialogs must be shown from an STA thread.
/// </para>
/// </summary>
public sealed class WpfFileDialogService : IFileDialogService
{
    private const string DialogCaption = "OPC UA Exporter";

    public Task<string?> ShowSaveFileDialogAsync(string filter) =>
        OnUiThreadAsync(() =>
        {
            var dialog = new SaveFileDialog
            {
                Filter = filter,
                OverwritePrompt = true,
                AddExtension = true
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        });

    public Task<string?> ShowOpenFileDialogAsync(string filter) =>
        OnUiThreadAsync(() =>
        {
            var dialog = new OpenFileDialog
            {
                Filter = filter,
                CheckFileExists = true,
                Multiselect = false
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        });

    public Task<bool> ConfirmAsync(string message) =>
        OnUiThreadAsync(() => MessageBox.Show(
            message,
            DialogCaption,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes);

    public Task ShowMessageAsync(string message) =>
        OnUiThreadAsync(() => MessageBox.Show(
            message,
            DialogCaption,
            MessageBoxButton.OK,
            MessageBoxImage.Information));

    /// <summary>
    /// Runs <paramref name="show"/> on the WPF dispatcher and returns its result.
    /// Awaiting the dispatcher operation (rather than calling
    /// <c>Dispatcher.Invoke</c>) keeps the calling Blazor handler asynchronous,
    /// so the WebView stays responsive while the modal dialog is open.
    /// </summary>
    private static async Task<T> OnUiThreadAsync<T>(Func<T> show)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return show();

        return await dispatcher.InvokeAsync(show);
    }
}
