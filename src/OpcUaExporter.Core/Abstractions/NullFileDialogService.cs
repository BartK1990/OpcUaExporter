namespace OpcUaExporter.Abstractions;

/// <summary>
/// <see cref="IFileDialogService"/> implementation that cancels every prompt.
/// Used as the fallback when the UI is rendered outside a shell that can show
/// native dialogs (component tests, design-time previews).
/// </summary>
public sealed class NullFileDialogService : IFileDialogService
{
    public Task<string?> ShowSaveFileDialogAsync(string filter) => Task.FromResult<string?>(null);

    public Task<string?> ShowOpenFileDialogAsync(string filter) => Task.FromResult<string?>(null);

    public Task<bool> ConfirmAsync(string message) => Task.FromResult(false);

    public Task ShowMessageAsync(string message) => Task.CompletedTask;
}
