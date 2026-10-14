using Microsoft.JSInterop;
using OpcUaExporter.Abstractions;

namespace OpcUaExporter.Web.Services;

/// <summary>
/// <see cref="IFileDialogService"/> for the browser host, built on the browser's
/// own <c>prompt</c>/<c>confirm</c>/<c>alert</c> dialogs.
/// <para>
/// The OPC UA services read and write files on the machine running this server
/// (not via browser upload/download), so the save/open "dialogs" ask for a
/// path on that machine. That matches the desktop app's behaviour for the
/// local, single-user scenario this host exists for.
/// </para>
/// </summary>
public sealed class BrowserFileDialogService(IJSRuntime js) : IFileDialogService
{
    public Task<string?> ShowSaveFileDialogAsync(string filter) =>
        PromptForPathAsync("Save to file (path on the server machine):", filter);

    public Task<string?> ShowOpenFileDialogAsync(string filter) =>
        PromptForPathAsync("Open file (path on the server machine):", filter);

    public async Task<bool> ConfirmAsync(string message) =>
        await js.InvokeAsync<bool>("confirm", message);

    public async Task ShowMessageAsync(string message) =>
        await js.InvokeVoidAsync("alert", message);

    private async Task<string?> PromptForPathAsync(string title, string filter)
    {
        var path = await js.InvokeAsync<string?>("prompt", $"{title}\n{DescribeFilter(filter)}", SuggestFileName(filter));
        return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
    }

    /// <summary>"CSV Files|*.csv|All|*.*" → "CSV Files (*.csv), All (*.*)".</summary>
    internal static string DescribeFilter(string filter)
    {
        var parts = filter.Split('|');
        var pairs = new List<string>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
            pairs.Add($"{parts[i]} ({parts[i + 1]})");
        return string.Join(", ", pairs);
    }

    /// <summary>Default path for the prompt: "export" with the filter's first concrete extension, in the user's home directory.</summary>
    internal static string SuggestFileName(string filter)
    {
        var extension = filter.Split('|')
            .Skip(1).Where((_, i) => i % 2 == 0)
            .SelectMany(p => p.Split(';'))
            .Select(p => p.Trim().TrimStart('*'))
            .FirstOrDefault(p => p.StartsWith('.') && !p.Contains('*'));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "export" + (extension ?? string.Empty));
    }
}
