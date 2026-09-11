namespace OpcUaExporter.Abstractions;

/// <summary>
/// Native (host-provided) file and message dialogs.
/// <para>
/// A Blazor Hybrid WebView has no browser file picker, so the shell hosting the
/// UI supplies the platform implementation — <c>OpcUaExporter.Wpf</c> uses the
/// WPF <see cref="Microsoft.Win32.FileDialog"/> family. Declaring the contract
/// here keeps the UI project free of any Windows dependency and lets tests
/// substitute a stub.
/// </para>
/// </summary>
public interface IFileDialogService
{
    /// <summary>
    /// Prompts for a file to write to.
    /// </summary>
    /// <param name="filter">Win32-style filter, e.g. <c>"CSV Files|*.csv"</c>.</param>
    /// <returns>The chosen path, or <see langword="null"/> if the user cancelled.</returns>
    Task<string?> ShowSaveFileDialogAsync(string filter);

    /// <summary>
    /// Prompts for an existing file to read.
    /// </summary>
    /// <param name="filter">Win32-style filter, e.g. <c>"JSON Files|*.json"</c>.</param>
    /// <returns>The chosen path, or <see langword="null"/> if the user cancelled.</returns>
    Task<string?> ShowOpenFileDialogAsync(string filter);

    /// <summary>
    /// Asks the user a yes/no question.
    /// </summary>
    /// <returns><see langword="true"/> when the user confirmed.</returns>
    Task<bool> ConfirmAsync(string message);

    /// <summary>
    /// Shows an informational message the user must acknowledge.
    /// </summary>
    Task ShowMessageAsync(string message);
}
