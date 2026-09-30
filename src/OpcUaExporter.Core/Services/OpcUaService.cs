using Microsoft.Extensions.Logging;
using OfficeIMO.Excel;
using OpcUaExporter.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.IO;

namespace OpcUaExporter.Services;

/// <summary>
/// High-level OPC UA service the Blazor components bind to.
/// Wraps <see cref="OpcUaClientService"/>, owns the application state
/// (connection profile, tag tree, readings, recording and trend selection) and
/// raises <see cref="StateChanged"/> so pages can re-render.
/// </summary>
public class OpcUaService
{
    private readonly OpcUaClientService _client;
    private readonly ILogger<OpcUaService> _logger;
    private readonly DiagnosticsLogService _diagnostics;
    private CancellationTokenSource? _browseCancellation;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource _treeCancellation = new();
    private readonly object _subscriptionSync = new();
    private ILiveSubscription? _activeSubscription;

    // Desired subscription state. UI actions only change this set (instantly) and a background sync loop
    // reconciles the live subscription to it, so ticking checkboxes never waits on the server.
    private static readonly TimeSpan SubscriptionSyncDebounce = TimeSpan.FromMilliseconds(150);
    private readonly object _syncGate = new();
    private HashSet<string> _desiredNodeIds = new(StringComparer.OrdinalIgnoreCase);
    private int _desiredVersion;
    private int _appliedVersion;
    private bool _syncLoopRunning;
    private bool _syncDebouncing;
    private CancellationTokenSource? _syncCts;
    private TaskCompletionSource _syncIdle = CompletedSyncIdle();

    public event Action? StateChanged;

    public IReadOnlyList<ConnectionSecurityMode> SecurityModeOptions { get; } =
        Enum.GetValues<ConnectionSecurityMode>();

    public IReadOnlyList<string> SecurityPolicyOptions { get; } =
    [
        "http://opcfoundation.org/UA/SecurityPolicy#None",
        "http://opcfoundation.org/UA/SecurityPolicy#Basic128Rsa15",
        "http://opcfoundation.org/UA/SecurityPolicy#Basic256",
        "http://opcfoundation.org/UA/SecurityPolicy#Basic256Sha256",
        "http://opcfoundation.org/UA/SecurityPolicy#Aes128_Sha256_RsaOaep",
        "http://opcfoundation.org/UA/SecurityPolicy#Aes256_Sha256_RsaPss"
    ];

    public IReadOnlyList<AuthenticationType> AuthenticationOptions { get; } =
        Enum.GetValues<AuthenticationType>();

    // Connection
    public ConnectionProfile Profile     { get; private set; } = new();
    public bool               IsConnected { get; private set; }

    /// <summary>Every connection profile saved to the library (<see cref="AppPaths.ProfilesDirectory"/>), sorted by name. The user switches between servers by picking one of these at runtime instead of editing <see cref="Profile"/> from scratch each time.</summary>
    public List<ConnectionProfile> SavedProfiles { get; private set; } = new();

    // Browse tree
    public List<OpcTag> TagTree          { get; private set; } = new();
    public int BrowsedVariableCount { get; private set; }

    // Live readings (after Read)
    public List<TagReading> LastReadings  { get; private set; } = new();
    public List<PendingCertificateInfo> PendingCertificates { get; private set; } = new();

    // Status / busy
    public bool   IsBusy       { get; private set; }
    public bool   IsBrowsing   { get; private set; }
    public bool   IsSubscribed { get; private set; }

    /// <summary>True while the live subscription is being brought in line with the requested tags.</summary>
    public bool   IsSubscriptionPending { get; private set; }
    public string StatusMessage { get; private set; } = "Ready";
    public bool   HasError      { get; private set; }

    // Node IDs currently covered by the active subscription
    private HashSet<string> _subscribedNodeIds = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> SubscribedNodeIds => _subscribedNodeIds;
    private Dictionary<string, string> _displayNameByNodeId = new(StringComparer.OrdinalIgnoreCase);

    // CSV recording (column-per-tag: one row per live update, latest known value/quality per tag)
    private StreamWriter? _recordingWriter;
    private List<string> _recordingNodeIds = new();
    private HashSet<string> _recordingNodeIdSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TagReading> _recordingLatestByNodeId = new(StringComparer.OrdinalIgnoreCase);
    public bool   IsRecording       { get; private set; }
    public string? RecordingFilePath { get; private set; }
    public int    RecordedRowCount  { get; private set; }

    // Live trend chart (subset of subscribed tags plotted on the chart)
    private readonly List<string> _trendedNodeIds = new();
    private readonly Dictionary<string, string> _trendAxisByNodeId = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> TrendedNodeIds => _trendedNodeIds;
    public bool IsChartVisible { get; private set; }

    /// <summary>Fired for each live update of a currently-trended tag, for the chart to consume.</summary>
    public event Action<TagReading>? TrendUpdate;

    // Server discovery (port scan)
    public string DiscoveryHost { get; set; } = string.Empty;
    public string DiscoveryCustomPorts { get; set; } = string.Empty;
    public bool IsScanningPorts { get; private set; }
    public int ScanProgressCount { get; private set; }
    public int ScanTotalCount { get; private set; }
    public List<DiscoveredServerInfo> DiscoveredServers { get; private set; } = new();

    public OpcUaService(OpcUaClientService client, ILogger<OpcUaService> logger, DiagnosticsLogService diagnostics)
    {
        _client = client;
        _logger = logger;
        _diagnostics = diagnostics;

        LoadSavedProfiles();
        RestoreActiveProfile();
    }

    // -----------------------------------------------------------------------
    // Public operations
    // -----------------------------------------------------------------------

    public void SetProfile(ConnectionProfile profile)
    {
        Profile = profile;
        Notify();
    }

    /// <summary>Switches the active connection to a different saved profile, so the user can move between servers without re-entering settings.</summary>
    public bool SelectProfile(Guid id)
    {
        var match = SavedProfiles.FirstOrDefault(p => p.Id == id);
        if (match is null)
            return false;

        Profile = match;
        PersistActiveProfileId(match.Id);
        Notify();
        return true;
    }

    /// <summary>
    /// Browses the whole address space in the background. The tree is published as soon as its top level is
    /// known and keeps filling in while the deep scan runs; meanwhile the user can expand any node to fetch
    /// its children on demand (<see cref="LoadChildrenAsync"/>). Deliberately not wrapped in <see cref="RunSafe"/>:
    /// browsing must not set <see cref="IsBusy"/>, which would lock the rest of the UI for the whole scan.
    /// </summary>
    public async Task BrowseAsync(CancellationToken ct = default)
    {
        if (IsBrowsing)
            return;

        StopSubscription();

        // Anything still expanding a node of the old tree is moot now.
        _treeCancellation.Cancel();
        _treeCancellation.Dispose();
        _treeCancellation = new CancellationTokenSource();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _browseCancellation = linkedCts;
        IsBrowsing = true;
        BrowsedVariableCount = 0;
        TagTree = [];
        LastReadings = [];
        SetStatus("Connecting and browsing tags…");

        try
        {
            await _client.ResetInteractiveSessionAsync();

            TagTree = await _client.BrowseAsync(
                Profile,
                onTopStructureReady: topTags =>
                {
                    TagTree = topTags;
                    IsConnected = true;
                    var browseMode = Profile.EnableParallelBrowse
                        ? $"parallel (max {Math.Clamp(Profile.ParallelBrowseMaxDegree, 1, 32)})"
                        : "sequential";
                    SetStatus($"Top structure loaded ({topTags.Count} node(s)). Continuing deep scan ({browseMode}) — expand any node to load it now…");
                },
                onVariableCountChanged: variableCount =>
                {
                    BrowsedVariableCount = variableCount;
                    SetStatus($"Browsing tags… {BrowsedVariableCount} variable tag(s) found");
                },
                ct: linkedCts.Token);

            RefreshPendingCertificates();
            IsConnected  = true;
            BrowsedVariableCount = FlatCount(TagTree);
            SetStatus($"Browsed {BrowsedVariableCount} variable tags.");
        }
        catch (OperationCanceledException)
        {
            BrowsedVariableCount = FlatCount(TagTree);
            SetStatus(TagTree.Count > 0
                ? "Browse canceled. Nodes not scanned yet load when you expand them."
                : "Browse canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OPC UA browse failed");
            RefreshPendingCertificates();
            SetStatus($"Error: {ex.Message}", isError: true);
        }
        finally
        {
            IsBrowsing = false;
            if (ReferenceEquals(_browseCancellation, linkedCts))
                _browseCancellation = null;
            Notify();
        }
    }

    /// <summary>
    /// Fetches a node's children from the server right now, independently of (and without waiting for) the
    /// background deep scan. Whichever of the two gets there first publishes the child list; the other reuses it.
    /// </summary>
    public async Task LoadChildrenAsync(OpcTag tag)
    {
        var target = ResolveInTree([tag])[0];
        if (!target.CanLoadChildren || target.IsLoadingChildren)
            return;

        var ct = _treeCancellation.Token;
        target.IsLoadingChildren = true;
        Notify();

        try
        {
            var children = await _client.BrowseChildrenAsync(Profile, target.NodeId, ct);
            target.PublishChildren(children);
            IsConnected = true;
            if (!IsBrowsing)
                BrowsedVariableCount = FlatCount(TagTree);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A new browse replaced the tree this node belonged to.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to browse children of {NodeId}", target.NodeId);
            RefreshPendingCertificates();
            SetStatus($"Could not load children of '{target.DisplayName}': {ex.Message}", isError: true);
        }
        finally
        {
            target.IsLoadingChildren = false;
            Notify();
        }
    }

    public void CancelBrowse()
    {
        if (!IsBrowsing)
            return;

        SetStatus("Canceling browse…");
        _browseCancellation?.Cancel();
    }

    public Task QuickScanAsync(CancellationToken ct = default)
        => RunPortScanAsync(OpcUaClientService.WellKnownOpcUaPorts, "common OPC UA ports", ct);

    public Task FullScanAsync(CancellationToken ct = default)
    {
        var wellKnown = OpcUaClientService.WellKnownOpcUaPorts;
        var rest = Enumerable.Range(1, 65535).Where(p => !wellKnown.Contains(p));
        var ports = wellKnown.Concat(rest).ToList();
        return RunPortScanAsync(ports, "all 65535 ports", ct);
    }

    public Task CustomScanAsync(CancellationToken ct = default)
    {
        List<int> ports;
        try
        {
            ports = OpcUaClientService.ParsePortSpec(DiscoveryCustomPorts);
        }
        catch (FormatException ex)
        {
            SetStatus(ex.Message, isError: true);
            Notify();
            return Task.CompletedTask;
        }

        return RunPortScanAsync(ports, ports.Count == 1 ? $"port {ports[0]}" : $"{ports.Count} custom port(s)", ct);
    }

    public void CancelScan()
    {
        if (!IsScanningPorts)
            return;

        SetStatus("Canceling scan…");
        _scanCancellation?.Cancel();
    }

    private async Task RunPortScanAsync(IReadOnlyList<int> ports, string description, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(DiscoveryHost))
        {
            SetStatus("Enter a host/IP to scan.", isError: true);
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _scanCancellation = linkedCts;
        IsScanningPorts = true;
        DiscoveredServers = [];
        ScanProgressCount = 0;
        ScanTotalCount = ports.Count;
        Notify();

        try
        {
            await RunSafe(async () =>
            {
                SetStatus($"Scanning {description} on {DiscoveryHost}…");

                await _client.ScanForServersAsync(
                    DiscoveryHost,
                    ports,
                    maxDegreeOfParallelism: 100,
                    tcpProbeTimeoutMs: 250,
                    onProgress: (scanned, total) =>
                    {
                        ScanProgressCount = scanned;
                        Notify();
                    },
                    onServerFound: server =>
                    {
                        DiscoveredServers = DiscoveredServers
                            .Append(server)
                            .OrderBy(s => s.Port)
                            .ToList();
                        Notify();
                    },
                    linkedCts.Token);

                SetStatus($"Scan complete. Found {DiscoveredServers.Count} OPC UA server(s) out of {ScanTotalCount} port(s) scanned.");
            }, "Port scan canceled.");
        }
        finally
        {
            IsScanningPorts = false;
            if (ReferenceEquals(_scanCancellation, linkedCts))
                _scanCancellation = null;
            Notify();
        }
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
            LastReadings = await _client.ReadAsync(Profile, selected, ct);
            RefreshPendingCertificates();
            SetStatus($"Read {LastReadings.Count} tag(s) successfully.");
        });
    }

    /// <summary>Whether the user asked for this tag to be subscribed (it may still be pending, see <see cref="IsSubscriptionPending"/>).</summary>
    public bool IsSubscriptionRequested(string nodeId) => _desiredNodeIds.Contains(nodeId);

    /// <summary>Queues a subscription to every selected tag (on top of whatever is already subscribed). Returns immediately.</summary>
    public void SubscribeSelected()
    {
        var selected = GetSelectedNodeIds();
        if (!selected.Any())
        {
            SetStatus("No tags selected.", isError: true);
            return;
        }

        ChangeDesiredSubscription(set => set.UnionWith(selected));
    }

    /// <summary>Queues adding/removing one tag to/from the live subscription. Returns immediately. Unsubscribing
    /// a trended tag also takes it off the trend chart, which has no data without the subscription.</summary>
    public void ToggleTagSubscribe(OpcTag tag)
    {
        if (!tag.IsSelectable)
            return;

        var nodeId = tag.NodeId;
        if (IsSubscriptionRequested(nodeId))
        {
            RemoveTrendEntries([nodeId]);
            ChangeDesiredSubscription(set => set.Remove(nodeId));
        }
        else
        {
            ChangeDesiredSubscription(set => set.Add(nodeId));
        }
    }

    /// <summary>Deselects every tag, which also drops them from the subscription (and the recording/trend that ride on it).</summary>
    public void ClearSelection() => SelectAll(false);

    /// <summary>Queues a full stop of the live subscription (and the recording/trend that ride on it). Returns immediately.</summary>
    public void StopSubscription()
    {
        ClearTrend();
        ChangeDesiredSubscription(set => set.Clear());
    }

    /// <summary>Waits until the live subscription matches the requested tags (or the attempt failed).</summary>
    public Task WaitForSubscriptionSyncAsync(CancellationToken ct = default)
    {
        lock (_syncGate)
            return _syncIdle.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Replaces the requested tag set and makes sure the sync loop is running. The newest request always wins:
    /// a sync still in its debounce window restarts with it, and any in-flight work is canceled when nothing
    /// should be subscribed anymore. Otherwise in-flight work is left to finish — restarting a session that is
    /// still connecting on every click would mean it never finishes while the user keeps ticking boxes, and
    /// interrupting an add/remove of monitored items mid-flight could leave the server's items out of step —
    /// and the loop then goes round again for the newer target.
    /// </summary>
    private void ChangeDesiredSubscription(Action<HashSet<string>> change)
    {
        var startLoop = false;
        lock (_syncGate)
        {
            var next = new HashSet<string>(_desiredNodeIds, StringComparer.OrdinalIgnoreCase);
            change(next);
            if (next.SetEquals(_desiredNodeIds))
                return;

            // Swapped, never mutated, so the renderer can read it without locking.
            _desiredNodeIds = next;
            _desiredVersion++;
            IsSubscriptionPending = true;

            if (_syncDebouncing || next.Count == 0)
                _syncCts?.Cancel();

            if (!_syncLoopRunning)
            {
                _syncLoopRunning = true;
                _syncIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                startLoop = true;
            }
        }

        // Not Task.Run: the loop's continuations resume on the caller's (Blazor renderer) context, so the
        // state it updates is never mutated underneath a render. Every wait inside it is asynchronous.
        if (startLoop)
            _ = RunSubscriptionSyncLoopAsync();

        Notify();
    }

    private async Task RunSubscriptionSyncLoopAsync()
    {
        while (true)
        {
            int version;
            List<string> desired;
            CancellationTokenSource cts;

            lock (_syncGate)
            {
                if (_appliedVersion == _desiredVersion)
                {
                    _syncLoopRunning = false;
                    IsSubscriptionPending = false;
                    _syncIdle.TrySetResult();
                    break;
                }

                version = _desiredVersion;
                desired = _desiredNodeIds.ToList();
                _syncCts = cts = new CancellationTokenSource();
                _syncDebouncing = true;
            }

            try
            {
                // Coalesce a burst of clicks into one server round trip.
                await Task.Delay(SubscriptionSyncDebounce, cts.Token);

                lock (_syncGate)
                {
                    if (version != _desiredVersion)
                        continue;
                    _syncDebouncing = false;
                }

                await ApplyDesiredSubscriptionAsync(desired, cts.Token);

                lock (_syncGate)
                    _appliedVersion = version;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Superseded by a newer request: go round again with the latest target.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Updating the OPC UA subscription failed");
                SetStatus($"Subscription update failed: {ex.Message}", isError: true);

                lock (_syncGate)
                {
                    _appliedVersion = version;

                    // Don't retry forever. Unless the user has already asked for something newer, fall back to
                    // showing what is actually subscribed so no checkbox claims a subscription that isn't there.
                    if (version == _desiredVersion)
                        _desiredNodeIds = new HashSet<string>(_subscribedNodeIds, StringComparer.OrdinalIgnoreCase);
                }

                RemoveTrendEntries(_trendedNodeIds.Where(id => !_desiredNodeIds.Contains(id)).ToList());
                if (IsRecording && !_recordingNodeIds.All(_subscribedNodeIds.Contains))
                    StopRecordingInternal("Recording stopped: the live subscription could not be kept up.");
            }
            finally
            {
                lock (_syncGate)
                {
                    if (ReferenceEquals(_syncCts, cts))
                        _syncCts = null;
                    _syncDebouncing = false;
                }
                cts.Dispose();
            }
        }

        Notify();
    }

    /// <summary>Brings the live subscription to exactly <paramref name="desired"/>: opens it, updates its monitored items in place, or stops it.</summary>
    private async Task ApplyDesiredSubscriptionAsync(List<string> desired, CancellationToken ct)
    {
        if (desired.Count == 0)
        {
            var wasRecording = IsRecording;
            if (await StopSubscriptionInternalAsync())
            {
                SetStatus(wasRecording
                    ? "Subscription stopped. Recording stopped as well — start a new recording when ready."
                    : "Subscription stopped.");
            }
            return;
        }

        List<TagReading> initialReadings;
        ILiveSubscription? active;
        lock (_subscriptionSync)
            active = _activeSubscription;

        if (active is not null)
        {
            try
            {
                initialReadings = await active.UpdateAsync(desired, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Most likely the subscription's session has dropped: rebuild it from scratch below.
                _diagnostics.Add($"Updating the live subscription failed ({ex.Message}); reconnecting.");
                await StopActiveSubscriptionHandleAsync();
                active = null;
                initialReadings = [];
            }
        }
        else
        {
            initialReadings = [];
        }

        if (active is null)
        {
            SetStatus($"Subscribing to {desired.Count} tag(s)…");
            var (handle, readings) = await _client.SubscribeAsync(Profile, desired, ApplySubscriptionUpdate, ct);
            lock (_subscriptionSync)
            {
                _activeSubscription = handle;
                IsSubscribed = true;
            }
            initialReadings = readings;
        }

        var subscribed = new HashSet<string>(desired, StringComparer.OrdinalIgnoreCase);
        _subscribedNodeIds = subscribed;
        _displayNameByNodeId = BuildDisplayNameMap();
        MergeInitialReadings(subscribed, initialReadings);

        var recordingStopped = false;
        if (IsRecording && !_recordingNodeIds.All(subscribed.Contains))
        {
            StopRecordingInternal("Recording stopped: the subscribed tags changed and no longer cover every recorded tag.");
            recordingStopped = true;
        }

        RemoveTrendEntries(_trendedNodeIds.Where(id => !subscribed.Contains(id)).ToList());

        // Monitored-item creation normally delivers a first value, but seed the chart from the initial read too
        // so a constant tag gets a point even if that notification raced ahead of the chart's series.
        foreach (var reading in initialReadings)
        {
            if (_trendedNodeIds.Contains(reading.NodeId, StringComparer.OrdinalIgnoreCase))
                TrendUpdate?.Invoke(reading);
        }

        SetStatus(recordingStopped
            ? $"Subscribed to {subscribed.Count} tag(s). Recording stopped because the subscribed tags changed — start a new recording to include the updated set."
            : $"Subscribed to {subscribed.Count} tag(s). Listening for updates…");
    }

    /// <summary>Drops readings of tags that are no longer subscribed and folds in the first reading of newly subscribed ones.</summary>
    private void MergeInitialReadings(HashSet<string> subscribed, List<TagReading> initialReadings)
    {
        lock (_subscriptionSync)
        {
            var merged = LastReadings.Where(r => subscribed.Contains(r.NodeId)).ToList();
            foreach (var reading in initialReadings)
            {
                var existing = merged.FirstOrDefault(r => string.Equals(r.NodeId, reading.NodeId, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    merged.Add(reading);
                    continue;
                }

                // A live notification got there first; keep its value, but take the resolved name/type.
                existing.DisplayName = string.IsNullOrWhiteSpace(reading.DisplayName) ? existing.DisplayName : reading.DisplayName;
                existing.DataType ??= reading.DataType;
            }

            LastReadings = merged;
        }
    }

    private static TaskCompletionSource CompletedSyncIdle()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    /// <summary>Records every live update of the currently selected tags to a CSV file, subscribing if necessary.</summary>
    public async Task StartRecordingAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            SetStatus("Please choose a CSV output path.", isError: true);
            return;
        }

        var selected = GetSelectedNodeIds();
        if (!selected.Any())
        {
            SetStatus("No tags selected.", isError: true);
            return;
        }

        await RunSafe(async () =>
        {
            // Go through the subscription queue even if everything looks subscribed already: a queued
            // unsubscribe that hasn't been applied yet would otherwise cut the recording short.
            var selectedSet = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
            ChangeDesiredSubscription(set => set.UnionWith(selected));
            if (IsSubscriptionPending)
                SetStatus("Subscribing to the selected tags before recording…");
            await WaitForSubscriptionSyncAsync(ct);

            if (!selectedSet.IsSubsetOf(_subscribedNodeIds))
                throw new InvalidOperationException("Recording not started: the selected tags could not all be subscribed.");

            var writer = new StreamWriter(filePath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            var header = new List<string> { "Timestamp" };
            header.AddRange(selected.Select(id => _displayNameByNodeId.TryGetValue(id, out var dn) ? dn : id));
            writer.WriteLine(string.Join(',', header.Select(EscapeCsv)));

            lock (_subscriptionSync)
            {
                _recordingWriter?.Dispose();
                _recordingWriter = writer;
                RecordingFilePath = filePath;
                RecordedRowCount = 0;
                IsRecording = true;
                _recordingNodeIds = selected.ToList();
                _recordingNodeIdSet = selectedSet;
                _recordingLatestByNodeId.Clear();

                // Seed from the values the live subscription already knows: a tag that rarely
                // changes may not produce another notification for a long time, and its column
                // would otherwise stay a placeholder for the whole file.
                foreach (var reading in LastReadings)
                {
                    if (selectedSet.Contains(reading.NodeId))
                        _recordingLatestByNodeId[reading.NodeId] = reading;
                }
            }

            SetStatus($"Recording selected tags to: {filePath}");
        });
    }

    public Task StopRecordingAsync()
    {
        StopRecordingInternal();
        SetStatus("Recording stopped.");
        return Task.CompletedTask;
    }

    /// <summary>Closes the recording file (if any) and resets recording state. Does not touch the live subscription.</summary>
    private void StopRecordingInternal(string? diagnosticsMessage = null)
    {
        StreamWriter? writer;
        lock (_subscriptionSync)
        {
            writer = _recordingWriter;
            _recordingWriter = null;
            IsRecording = false;
            _recordingNodeIds = new();
            _recordingNodeIdSet = new(StringComparer.OrdinalIgnoreCase);
            _recordingLatestByNodeId.Clear();
        }

        writer?.Dispose();

        if (diagnosticsMessage is not null)
            _diagnostics.Add(diagnosticsMessage);
    }

    /// <summary>Plots the currently selected tags on the live trend chart, queuing a subscription to any that aren't subscribed yet. Returns immediately.</summary>
    public void TrendSelected()
    {
        var selected = GetSelectedNodeIds();
        if (!selected.Any())
        {
            SetStatus("No tags selected.", isError: true);
            return;
        }

        foreach (var id in selected)
            AddTrendEntry(id);

        IsChartVisible = true;
        SetStatus($"Trending {_trendedNodeIds.Count} tag(s) on the live chart.");
        ChangeDesiredSubscription(set => set.UnionWith(selected));
    }

    /// <summary>Adds a single tag to the live trend chart, queuing a subscription to it (alongside any existing ones) if necessary. Returns immediately.</summary>
    public void AddToTrend(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return;

        AddTrendEntry(nodeId);
        IsChartVisible = true;
        SetStatus($"Trending {_trendedNodeIds.Count} tag(s) on the live chart.");
        ChangeDesiredSubscription(set => set.Add(nodeId));
    }

    private void AddTrendEntry(string nodeId)
    {
        var isNew = !_trendedNodeIds.Contains(nodeId, StringComparer.OrdinalIgnoreCase);
        if (isNew)
            _trendedNodeIds.Add(nodeId);
        if (!_trendAxisByNodeId.ContainsKey(nodeId))
            _trendAxisByNodeId[nodeId] = "left";

        // A tag whose value stays constant may never fire another subscription
        // notification once it's already subscribed, so the chart would otherwise
        // never receive a single point for it. Seed it with the current reading.
        if (isNew)
        {
            var reading = LastReadings.FirstOrDefault(r => string.Equals(r.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));
            if (reading is not null)
                TrendUpdate?.Invoke(reading);
        }
    }

    private void RemoveTrendEntries(IReadOnlyCollection<string> nodeIds)
    {
        if (nodeIds.Count == 0)
            return;

        var removed = new HashSet<string>(nodeIds, StringComparer.OrdinalIgnoreCase);
        _trendedNodeIds.RemoveAll(removed.Contains);
        foreach (var id in removed)
            _trendAxisByNodeId.Remove(id);
        if (_trendedNodeIds.Count == 0)
            IsChartVisible = false;
    }

    public void RemoveFromTrend(string nodeId)
    {
        _trendedNodeIds.RemoveAll(id => string.Equals(id, nodeId, StringComparison.OrdinalIgnoreCase));
        _trendAxisByNodeId.Remove(nodeId);
        if (_trendedNodeIds.Count == 0)
            IsChartVisible = false;
        Notify();
    }

    public void ClearTrend()
    {
        _trendedNodeIds.Clear();
        _trendAxisByNodeId.Clear();
        IsChartVisible = false;
        Notify();
    }

    /// <summary>Flips a trended tag's chart y-axis between "left" (default) and "right".</summary>
    public void ToggleTrendAxis(string nodeId)
    {
        var current = _trendAxisByNodeId.TryGetValue(nodeId, out var a) ? a : "left";
        _trendAxisByNodeId[nodeId] = current == "left" ? "right" : "left";
        Notify();
    }

    public List<(string NodeId, string DisplayName, string Axis)> GetTrendedTagInfos()
        => _trendedNodeIds
            .Select(id => (
                NodeId: id,
                DisplayName: _displayNameByNodeId.TryGetValue(id, out var n) ? n : id,
                Axis: _trendAxisByNodeId.TryGetValue(id, out var a) ? a : "left"))
            .ToList();

    private Dictionary<string, string> BuildDisplayNameMap()
        => FlattenAll(TagTree)
            .Where(t => t.IsSelectable)
            .GroupBy(t => t.NodeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DisplayName, StringComparer.OrdinalIgnoreCase);

    public async Task TestConnectionAsync(CancellationToken ct = default)
    {
        await RunSafe(async () =>
        {
            SetStatus("Testing OPC UA connection…");
            await _client.TestConnectionAsync(Profile, ct);
            RefreshPendingCertificates();
            IsConnected = true;
            SetStatus("Connection test successful.");
        });
    }

    public async Task<ServerCapabilitiesInfo?> DiscoverServerCapabilitiesAsync(CancellationToken ct = default)
    {
        ServerCapabilitiesInfo? result = null;

        await RunSafe(async () =>
        {
            SetStatus("Discovering server capabilities…");
            result = await _client.GetServerCapabilitiesAsync(Profile.EndpointUrl, ct);
            SetStatus($"Discovered capabilities for server '{result.ServerName}'.");
        });

        return result;
    }

    public List<string> GetConnectionValidationWarnings()
    {
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(Profile.EndpointUrl))
            warnings.Add("Endpoint URL is required.");

        if (Profile.AuthenticationType == AuthenticationType.UsernamePassword)
        {
            if (string.IsNullOrWhiteSpace(Profile.Username))
                warnings.Add("Username is required for UsernamePassword authentication.");

            if (string.IsNullOrWhiteSpace(Profile.Password))
                warnings.Add("Password is required for UsernamePassword authentication.");
        }

        if (string.IsNullOrWhiteSpace(Profile.SecurityPolicy))
            warnings.Add("Security Policy should be selected.");

        return warnings;
    }

    public async Task TrustCertificateAsync(string thumbprint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
            return;

        await RunSafe(async () =>
        {
            var trusted = await _client.TrustPendingCertificateAsync(thumbprint, ct);
            RefreshPendingCertificates();
            SetStatus(trusted
                ? "Certificate trusted. Retry browse/read."
                : "Certificate was not found in pending list.");
        });
    }

    public void RejectCertificate(string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
            return;

        var rejected = _client.RejectPendingCertificate(thumbprint);
        RefreshPendingCertificates();
        SetStatus(rejected
            ? "Certificate rejected."
            : "Certificate was not found in pending list.");
    }

    public void RefreshPendingCertificates()
    {
        PendingCertificates = _client.GetPendingCertificates();
        Notify();
    }

    /// <summary>Adds a brand-new profile to the library with default settings and makes it active.</summary>
    public async Task AddProfileAsync(string name = "New Server", CancellationToken ct = default)
    {
        await RunSafe(async () =>
        {
            var profile = new ConnectionProfile { Name = string.IsNullOrWhiteSpace(name) ? "New Server" : name };
            await PersistProfileFileAsync(profile, ct);

            SavedProfiles = SavedProfiles.Append(profile).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            Profile = profile;
            PersistActiveProfileId(profile.Id);

            SetStatus($"Created profile '{profile.Name}'.");
        });
    }

    /// <summary>Persists the current in-memory <see cref="Profile"/> (including any edits made to its fields) into the library, adding it if it isn't there yet.</summary>
    public async Task SaveActiveProfileAsync(CancellationToken ct = default)
    {
        await RunSafe(async () =>
        {
            await PersistProfileFileAsync(Profile, ct);

            if (!SavedProfiles.Any(p => p.Id == Profile.Id))
                SavedProfiles.Add(Profile);
            SavedProfiles = SavedProfiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

            PersistActiveProfileId(Profile.Id);
            SetStatus($"Profile '{Profile.Name}' saved.");
        });
    }

    /// <summary>Clones a saved profile under a new name and makes the copy active, so a similar server can be set up without retyping everything.</summary>
    public async Task DuplicateProfileAsync(Guid id, CancellationToken ct = default)
    {
        var source = SavedProfiles.FirstOrDefault(p => p.Id == id);
        if (source is null)
            return;

        await RunSafe(async () =>
        {
            var copy = new ConnectionProfile
            {
                Name = $"{source.Name} (Copy)",
                EndpointUrl = source.EndpointUrl,
                SecurityMode = source.SecurityMode,
                SecurityPolicy = source.SecurityPolicy,
                AuthenticationType = source.AuthenticationType,
                Username = source.Username,
                Password = source.Password,
                EnableParallelBrowse = source.EnableParallelBrowse,
                ParallelBrowseMaxDegree = source.ParallelBrowseMaxDegree
            };

            await PersistProfileFileAsync(copy, ct);
            SavedProfiles = SavedProfiles.Append(copy).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            Profile = copy;
            PersistActiveProfileId(copy.Id);

            SetStatus($"Duplicated profile as '{copy.Name}'.");
        });
    }

    /// <summary>Removes a profile from the library. If it was the active one, another saved profile (or a fresh default) takes its place.</summary>
    public Task DeleteProfileAsync(Guid id)
    {
        var match = SavedProfiles.FirstOrDefault(p => p.Id == id);
        if (match is null)
            return Task.CompletedTask;

        var path = ProfileFilePath(match.Id);
        if (File.Exists(path))
            File.Delete(path);

        SavedProfiles = SavedProfiles.Where(p => p.Id != id).ToList();

        if (Profile.Id == id)
        {
            Profile = SavedProfiles.FirstOrDefault() ?? new ConnectionProfile();
            if (SavedProfiles.Count > 0)
                PersistActiveProfileId(Profile.Id);
            else if (File.Exists(AppPaths.ActiveProfileIdFile))
                File.Delete(AppPaths.ActiveProfileIdFile);
        }

        SetStatus($"Deleted profile '{match.Name}'.");
        Notify();
        return Task.CompletedTask;
    }

    /// <summary>Writes the active profile out to an arbitrary file, e.g. to back it up or hand it to another machine.</summary>
    public async Task ExportProfileAsync(string filePath, CancellationToken ct = default)
    {
        await RunSafe(async () =>
        {
            SetStatus("Exporting server profile…");

            var json = JsonSerializer.Serialize(Profile, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json, ct);

            SetStatus($"Profile exported: {filePath}");
        });
    }

    /// <summary>Reads a profile from an arbitrary file and adds it to the library as a new entry, then makes it active.</summary>
    public async Task ImportProfileAsync(string filePath, CancellationToken ct = default)
    {
        await RunSafe(async () =>
        {
            SetStatus("Importing server profile…");

            var json = await File.ReadAllTextAsync(filePath, ct);
            var loaded = JsonSerializer.Deserialize<ConnectionProfile>(json)
                         ?? throw new InvalidOperationException("Invalid profile file.");

            // Always mint a fresh Id so importing a file exported from this app's own library
            // (or one another machine already imported) can't silently collide with and
            // overwrite an existing entry.
            loaded.Id = Guid.NewGuid();

            await PersistProfileFileAsync(loaded, ct);
            SavedProfiles = SavedProfiles.Append(loaded).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            Profile = loaded;
            PersistActiveProfileId(loaded.Id);

            SetStatus($"Profile imported: {loaded.Name}");
        });
    }

    private static string ProfileFilePath(Guid id) => Path.Combine(AppPaths.ProfilesDirectory, $"{id}.json");

    private static async Task PersistProfileFileAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(ProfileFilePath(profile.Id), json, ct);
    }

    private static void PersistActiveProfileId(Guid id)
        => File.WriteAllText(AppPaths.ActiveProfileIdFile, id.ToString());

    private void LoadSavedProfiles()
    {
        var profiles = new List<ConnectionProfile>();

        string dir;
        try
        {
            dir = AppPaths.ProfilesDirectory;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to access the saved-profiles directory");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var profile = JsonSerializer.Deserialize<ConnectionProfile>(json);
                if (profile is not null)
                    profiles.Add(profile);
            }
            catch (Exception ex)
            {
                // A single corrupt profile file shouldn't block the app from starting.
                _logger.LogError(ex, "Failed to load saved profile from {File}", file);
            }
        }

        SavedProfiles = profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void RestoreActiveProfile()
    {
        if (SavedProfiles.Count == 0)
            return;

        Guid? activeId = null;
        try
        {
            if (File.Exists(AppPaths.ActiveProfileIdFile) &&
                Guid.TryParse(File.ReadAllText(AppPaths.ActiveProfileIdFile).Trim(), out var parsed))
                activeId = parsed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read the last active profile pointer");
        }

        Profile = (activeId is not null ? SavedProfiles.FirstOrDefault(p => p.Id == activeId) : null)
                  ?? SavedProfiles[0];
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

            var selectedSet = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var browsedByNodeId = FlattenAll(TagTree)
                .Where(t => selectedSet.Contains(t.NodeId))
                .GroupBy(t => t.NodeId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var rowsToExport = selected
                .Where(id => browsedByNodeId.ContainsKey(id))
                .Select(id =>
                {
                    var tag = browsedByNodeId[id];
                    return new TagReading
                    {
                        DisplayName = tag.DisplayName,
                        NodeId = tag.NodeId,
                        DataType = tag.DataType
                    };
                })
                .ToList();

            var path = await WriteExportFileAsync(options, rowsToExport, ct);
            SetStatus($"Exported to: {path}");
        });
    }

    public async Task<NodeDetails?> GetNodeDetailsAsync(string nodeId, CancellationToken ct = default)
    {
        NodeDetails? result = null;

        await RunSafe(async () =>
        {
            SetStatus("Reading node properties…");
            result = await _client.GetNodeDetailsAsync(Profile, nodeId, ct);
            SetStatus($"Loaded properties for '{result.DisplayName}'.");
        });

        return result;
    }

    /// <summary>Writes a single value to a node, using its known DataType (if browsed) to convert the raw input.</summary>
    /// <param name="dataTypeOverride">Explicit data type to write as, overriding auto-detection. Pass when the user has picked a type manually — some servers declare a node's DataType as the abstract BaseDataType ("Variant"), so the real type can't always be auto-detected.</param>
    public async Task<TagReading?> WriteValueAsync(string nodeId, string rawValue, string? dataTypeOverride = null, CancellationToken ct = default)
    {
        TagReading? result = null;

        await RunSafe(async () =>
        {
            SetStatus($"Writing value to '{nodeId}'…");

            // Prefer the last-read reading's DataType over the browsed tag's: a node whose
            // static DataType attribute is the abstract BaseDataType ("Variant") only reveals
            // its real type once an actual value has been read/subscribed and refined.
            var dataTypeHint = dataTypeOverride
                ?? LastReadings
                    .FirstOrDefault(r => string.Equals(r.NodeId, nodeId, StringComparison.OrdinalIgnoreCase))
                    ?.DataType
                ?? FlattenAll(TagTree)
                    .FirstOrDefault(t => string.Equals(t.NodeId, nodeId, StringComparison.OrdinalIgnoreCase))
                    ?.DataType;

            result = await _client.WriteAsync(Profile, nodeId, rawValue, dataTypeHint, ct);

            if (result.Error is not null)
                SetStatus($"Write failed: {result.Error}", isError: true);
            else
                SetStatus($"Wrote '{rawValue}' to '{nodeId}'.");
        });

        return result;
    }

    public void SelectAll(bool select)
    {
        foreach (var tag in FlattenAll(TagTree).Where(t => t.IsSelectable))
            tag.IsSelected = select;

        if (!select)
            DropDeselectedFromSubscription();
        Notify();
    }

    /// <summary>Selects/deselects every selectable tag under <paramref name="folderTag"/>, including the tag itself
    /// when it is a Variable (structured variables carry child tags too).</summary>
    public void SelectInFolder(OpcTag folderTag, bool select)
    {
        foreach (var tag in ResolveInTree(FlattenAll([folderTag])).Where(t => t.IsSelectable))
            tag.IsSelected = select;

        if (!select)
            DropDeselectedFromSubscription();
        Notify();
    }

    public void ToggleTag(OpcTag tag)
    {
        var target = ResolveInTree([tag])[0];
        target.IsSelected = !target.IsSelected;

        if (!target.IsSelected)
            DropDeselectedFromSubscription();
        Notify();
    }

    /// <summary>
    /// Maps tags back to their instances in <see cref="TagTree"/>. The tag browser's filtered view shallow-clones
    /// ancestors that don't match the filter themselves, so a clicked node may be a copy; mutating the copy would
    /// be lost on the next render. Tags that are already in the tree (or unknown to it) are returned unchanged.
    /// </summary>
    private List<OpcTag> ResolveInTree(IEnumerable<OpcTag> tags)
    {
        var all = FlattenAll(TagTree).ToList();
        var inTree = new HashSet<OpcTag>(all, ReferenceEqualityComparer.Instance);
        Dictionary<string, OpcTag>? byNodeId = null;

        var result = new List<OpcTag>();
        foreach (var tag in tags)
        {
            if (inTree.Contains(tag))
            {
                result.Add(tag);
                continue;
            }

            byNodeId ??= all.GroupBy(t => t.NodeId).ToDictionary(g => g.Key, g => g.First());
            result.Add(byNodeId.TryGetValue(tag.NodeId, out var original) ? original : tag);
        }
        return result;
    }

    public List<string> GetSelectedNodeIds()
        => FlattenAll(TagTree)
           .Where(t => t.IsSelectable && t.IsSelected)
           .Select(t => t.NodeId)
           .ToList();

    public List<OpcTag> GetSelectedTags()
        => FlattenAll(TagTree)
           .Where(t => t.IsSelectable && t.IsSelected)
           .ToList();

    /// <summary>Subscriptions are managed from the Selected Tags table, so a tag leaving the selection also leaves
    /// the subscription (and the trend chart) rather than staying subscribed where the user can no longer see it.</summary>
    private void DropDeselectedFromSubscription()
    {
        var selected = new HashSet<string>(GetSelectedNodeIds(), StringComparer.OrdinalIgnoreCase);
        var dropped = _desiredNodeIds.Where(id => !selected.Contains(id)).ToList();
        if (dropped.Count == 0)
            return;

        RemoveTrendEntries(dropped);
        ChangeDesiredSubscription(set => set.ExceptWith(dropped));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task RunSafe(Func<Task> action, string? canceledMessage = null)
    {
        IsBusy   = true;
        HasError = false;
        Notify();
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            SetStatus(canceledMessage ?? "Operation canceled.");
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

    private static async Task<string> WriteExportFileAsync(ExportOptions options, List<TagReading> rows, CancellationToken ct)
    {
        switch (options.Format)
        {
            case ExportFormat.Json:
                var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
                await File.WriteAllTextAsync(options.OutputPath, json, ct);
                break;

            case ExportFormat.Xlsx:
                await Task.Run(() => WriteXlsx(options.OutputPath, rows), ct);
                break;

            case ExportFormat.Csv:
            default:
                var csv = BuildCsv(rows);
                await File.WriteAllTextAsync(options.OutputPath, csv, ct);
                break;
        }

        return options.OutputPath;
    }

    private static void WriteXlsx(string path, List<TagReading> rows)
    {
        using var document = ExcelDocument.Create(path, "Tags");
        var sheet = document.Sheets[0];

        sheet.CellValue(1, 1, "Display Name");
        sheet.CellValue(1, 2, "Node ID");
        sheet.CellValue(1, 3, "Data Type");

        for (var i = 0; i < rows.Count; i++)
        {
            var row = i + 2;
            sheet.CellValue(row, 1, rows[i].DisplayName);
            sheet.CellValue(row, 2, rows[i].NodeId);
            sheet.CellValue(row, 3, rows[i].DataType ?? string.Empty);
        }

        document.Save();
    }

    private static string BuildCsv(IEnumerable<TagReading> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Display Name,Node ID,Data Type");

        foreach (var r in rows)
        {
            sb.Append(EscapeCsv(r.DisplayName));
            sb.Append(',');
            sb.Append(EscapeCsv(r.NodeId));
            sb.Append(',');
            sb.Append(EscapeCsv(r.DataType));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            return value;

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private void ApplySubscriptionUpdate(TagReading update)
    {
        lock (_subscriptionSync)
        {
            if (!IsSubscribed)
                return;

            var existing = LastReadings.FirstOrDefault(r => string.Equals(r.NodeId, update.NodeId, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                // Copy-on-write: the renderer enumerates LastReadings without taking this lock.
                LastReadings = [.. LastReadings, update];
            }
            else
            {
                existing.DisplayName = string.IsNullOrWhiteSpace(update.DisplayName) ? existing.DisplayName : update.DisplayName;
                existing.Value = update.Value;
                existing.DataType = string.IsNullOrWhiteSpace(update.DataType) ? existing.DataType : update.DataType;
                existing.Quality = update.Quality;
                existing.Timestamp = update.Timestamp;
                existing.Error = update.Error;
            }

            if (IsRecording && _recordingNodeIdSet.Contains(update.NodeId))
                WriteRecordingRow(update);
        }

        if (_trendedNodeIds.Contains(update.NodeId, StringComparer.OrdinalIgnoreCase))
            TrendUpdate?.Invoke(update);

        Notify();
    }

    /// <summary>Writes one wide-format row: the triggering update's timestamp, plus the latest known value
    /// (or "-" if never received, errored, or of bad quality) for every recorded tag.</summary>
    private void WriteRecordingRow(TagReading update)
    {
        if (_recordingWriter is null)
            return;

        _recordingLatestByNodeId[update.NodeId] = update;

        var fields = new List<string> { EscapeCsv(update.Timestamp ?? DateTime.UtcNow.ToString("o")) };
        foreach (var nodeId in _recordingNodeIds)
        {
            _recordingLatestByNodeId.TryGetValue(nodeId, out var reading);
            fields.Add(EscapeCsv(IsBadQuality(reading) ? "-" : reading!.Value?.ToString() ?? "-"));
        }

        _recordingWriter.WriteLine(string.Join(',', fields));
        RecordedRowCount++;
    }

    private static bool IsBadQuality(TagReading? reading)
        => reading is null
           || reading.Error is not null
           || reading.Quality is null
           || reading.Quality.Contains("Bad", StringComparison.OrdinalIgnoreCase);

    /// <summary>Tears down just the live OPC UA subscription handle, leaving recording/trend state untouched.</summary>
    private async Task<bool> StopActiveSubscriptionHandleAsync()
    {
        ILiveSubscription? handle;

        lock (_subscriptionSync)
        {
            handle = _activeSubscription;
            _activeSubscription = null;
            IsSubscribed = false;
        }

        _subscribedNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (handle is null)
            return false;

        await handle.DisposeAsync();
        return true;
    }

    /// <summary>Full subscription teardown: stops the live feed and anything that depends on it (recording, trend chart).</summary>
    private async Task<bool> StopSubscriptionInternalAsync()
    {
        var handleStopped = await StopActiveSubscriptionHandleAsync();

        if (IsRecording)
            StopRecordingInternal("Recording stopped: the live subscription was stopped.");

        _trendedNodeIds.Clear();
        IsChartVisible = false;

        return handleStopped;
    }
}
