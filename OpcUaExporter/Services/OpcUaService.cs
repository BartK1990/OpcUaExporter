using Microsoft.Extensions.Logging;
using OpcUaExporter.Models;

namespace OpcUaExporter.Services;

/// <summary>
/// High-level OPC UA service used by Blazor components.
/// Wraps PythonBridgeService and manages application state.
/// </summary>
public class OpcUaService
{
    private readonly PythonBridgeService _bridge;
    private readonly ILogger<OpcUaService> _logger;

    public event Action? StateChanged;

    // Connection
    public ConnectionProfile Profile     { get; private set; } = new();
    public bool               IsConnected { get; private set; }

    // Browse tree
    public List<OpcTag> TagTree          { get; private set; } = new();

    // Live readings (after Read)
    public List<TagReading> LastReadings  { get; private set; } = new();

    // Status / busy
    public bool   IsBusy       { get; private set; }
    public string StatusMessage { get; private set; } = "Ready";
    public bool   HasError      { get; private set; }

    public OpcUaService(PythonBridgeService bridge, ILogger<OpcUaService> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Public operations
    // -----------------------------------------------------------------------

    public void SetProfile(ConnectionProfile profile)
    {
        Profile = profile;
        Notify();
    }

    public async Task BrowseAsync(CancellationToken ct = default)
    {
        await RunSafe(async () =>
        {
            SetStatus("Connecting and browsing tags…");
            TagTree      = await _bridge.BrowseAsync(Profile.EndpointUrl, ct);
            SortTreeByNodeId(TagTree);
            IsConnected  = true;
            LastReadings = new();
            SetStatus($"Browsed {FlatCount(TagTree)} variable tags.");
        });
    }

    public async Task ReadSelectedAsync(CancellationToken ct = default)
    {
        var selected = GetSelectedNodeIds();
        if (!selected.Any())
        {
            SetStatus("No tags selected.", isError: true);
            return;
        }

        await RunSafe(async () =>
        {
            SetStatus($"Reading {selected.Count} tag(s)…");
            LastReadings = await _bridge.ReadAsync(Profile.EndpointUrl, selected, ct);
            SetStatus($"Read {LastReadings.Count} tag(s) successfully.");
        });
    }

    public async Task ExportAsync(ExportOptions options, CancellationToken ct = default)
    {
        var selected = GetSelectedNodeIds();
        if (!selected.Any())
        {
            SetStatus("No tags selected for export.", isError: true);
            return;
        }

        await RunSafe(async () =>
        {
            SetStatus($"Exporting {selected.Count} tag(s)…");
            var path = await _bridge.ExportAsync(Profile.EndpointUrl, selected, options, ct);
            SetStatus($"Exported to: {path}");
        });
    }

    public void SelectAll(bool select)
    {
        foreach (var tag in FlattenAll(TagTree))
            tag.IsSelected = select;
        Notify();
    }

    public void SelectInFolder(OpcTag folderTag, bool select)
    {
        foreach (var tag in FlattenAll(folderTag.Children))
            tag.IsSelected = select;

        Notify();
    }

    public void ToggleTag(OpcTag tag)
    {
        tag.IsSelected = !tag.IsSelected;
        Notify();
    }

    public List<string> GetSelectedNodeIds()
        => FlattenAll(TagTree)
           .Where(t => t.IsSelected)
           .Select(t => t.NodeId)
           .ToList();

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task RunSafe(Func<Task> action)
    {
        IsBusy   = true;
        HasError = false;
        Notify();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OPC UA operation failed");
            SetStatus($"Error: {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    private void SetStatus(string msg, bool isError = false)
    {
        StatusMessage = msg;
        HasError      = isError;
        Notify();
    }

    private void Notify() => StateChanged?.Invoke();

    private static IEnumerable<OpcTag> FlattenAll(IEnumerable<OpcTag> tags)
    {
        foreach (var t in tags)
        {
            yield return t;
            foreach (var c in FlattenAll(t.Children))
                yield return c;
        }
    }

    private static int FlatCount(IEnumerable<OpcTag> tags)
        => FlattenAll(tags).Count(t => t.NodeClass == "Variable");

    private static void SortTreeByNodeId(List<OpcTag> tags)
    {
        tags.Sort((a, b) => string.Compare(a.NodeId, b.NodeId, StringComparison.OrdinalIgnoreCase));

        foreach (var tag in tags)
            SortTreeByNodeId(tag.Children);
    }
}
