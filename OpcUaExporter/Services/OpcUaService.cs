using Microsoft.Extensions.Logging;
using OpcUaExporter.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.IO;

namespace OpcUaExporter.Services;

/// <summary>
/// High-level OPC UA service used by Blazor components.
/// Wraps PythonBridgeService and manages application state.
/// </summary>
public class OpcUaService
{
    private readonly OpcUaClientService _bridge;
    private readonly ILogger<OpcUaService> _logger;
    private CancellationTokenSource? _browseCancellation;
    private CancellationTokenSource? _scanCancellation;
    private readonly object _subscriptionSync = new();
    private IAsyncDisposable? _activeSubscription;
    private CancellationTokenSource? _subscriptionCts;

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
    public string StatusMessage { get; private set; } = "Ready";
    public bool   HasError      { get; private set; }

    // Node IDs currently covered by the active subscription
    private HashSet<string> _subscribedNodeIds = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> SubscribedNodeIds => _subscribedNodeIds;

    // Server discovery (port scan)
    public string DiscoveryHost { get; set; } = string.Empty;
    public bool IsScanningPorts { get; private set; }
    public int ScanProgressCount { get; private set; }
    public int ScanTotalCount { get; private set; }
    public List<DiscoveredServerInfo> DiscoveredServers { get; private set; } = new();

    public OpcUaService(OpcUaClientService bridge, ILogger<OpcUaService> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    private static string LastProfilePointerPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpcUaExporter");

            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "last-profile.txt");
        }
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
        await StopSubscriptionAsync();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _browseCancellation = linkedCts;
        IsBrowsing = true;
        Notify();

        try
        {
            await RunSafe(async () =>
            {
                SetStatus("Connecting and browsing tags…");
                BrowsedVariableCount = 0;
                TagTree = [];
                Notify();

                TagTree = await _bridge.BrowseAsync(
                    Profile,
                    onTopStructureReady: topTags =>
                    {
                        TagTree = topTags;
                        var browseMode = Profile.EnableParallelBrowse
                            ? $"parallel (max {Math.Clamp(Profile.ParallelBrowseMaxDegree, 1, 32)})"
                            : "sequential";
                        SetStatus($"Top structure loaded ({topTags.Count} node(s)). Continuing deep scan ({browseMode})…");
                    },
                    onVariableCountChanged: variableCount =>
                    {
                        BrowsedVariableCount = variableCount;
                        SetStatus($"Browsing tags… {BrowsedVariableCount} variable tag(s) found");
                    },
                    ct: linkedCts.Token);

                RefreshPendingCertificates();
                SortTreeByNodeId(TagTree);
                IsConnected  = true;
                LastReadings = [];
                BrowsedVariableCount = FlatCount(TagTree);
                SetStatus($"Browsed {BrowsedVariableCount} variable tags.");
            }, "Browse canceled.");
        }
        finally
        {
            IsBrowsing = false;
            if (ReferenceEquals(_browseCancellation, linkedCts))
                _browseCancellation = null;
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

                await _bridge.ScanForServersAsync(
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
        await StopSubscriptionAsync();

        var selected = GetSelectedNodeIds();
        if (!selected.Any())
        {
            SetStatus("No tags selected.", isError: true);
            return;
        }

        await RunSafe(async () =>
        {
            SetStatus($"Reading {selected.Count} tag(s)…");
            LastReadings = await _bridge.ReadAsync(Profile, selected, ct);
            RefreshPendingCertificates();
            SetStatus($"Read {LastReadings.Count} tag(s) successfully.");
        });
    }

    public async Task SubscribeSelectedAsync(CancellationToken ct = default)
    {
        var selected = GetSelectedNodeIds();
        if (!selected.Any())
        {
            SetStatus("No tags selected.", isError: true);
            return;
        }

        await RunSafe(async () =>
        {
            await StopSubscriptionInternalAsync();

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var (handle, initialReadings) = await _bridge.SubscribeAsync(
                Profile,
                selected,
                ApplySubscriptionUpdate,
                linkedCts.Token);

            lock (_subscriptionSync)
            {
                _activeSubscription = handle;
                _subscriptionCts = linkedCts;
                IsSubscribed = true;
            }

            _subscribedNodeIds = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
            LastReadings = initialReadings;
            SetStatus($"Subscribed to {selected.Count} tag(s). Listening for updates…");
        });
    }

    public async Task StopSubscriptionAsync()
    {
        await RunSafe(async () =>
        {
            var stopped = await StopSubscriptionInternalAsync();
            if (stopped)
                SetStatus("Subscription stopped.");
        });
    }

    public async Task TestConnectionAsync(CancellationToken ct = default)
    {
        await RunSafe(async () =>
        {
            SetStatus("Testing OPC UA connection…");
            await _bridge.TestConnectionAsync(Profile, ct);
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
            result = await _bridge.GetServerCapabilitiesAsync(Profile.EndpointUrl, ct);
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
            var trusted = await _bridge.TrustPendingCertificateAsync(thumbprint, ct);
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

        var rejected = _bridge.RejectPendingCertificate(thumbprint);
        RefreshPendingCertificates();
        SetStatus(rejected
            ? "Certificate rejected."
            : "Certificate was not found in pending list.");
    }

    public void RefreshPendingCertificates()
    {
        PendingCertificates = _bridge.GetPendingCertificates();
        Notify();
    }

    public async Task SaveProfileAsync(string filePath, CancellationToken ct = default)
    {
        await RunSafe(async () =>
        {
            SetStatus("Saving server profile…");

            var profile = new ConnectionProfile
            {
                Name = Profile.Name,
                EndpointUrl = Profile.EndpointUrl,
                SecurityMode = Profile.SecurityMode,
                SecurityPolicy = Profile.SecurityPolicy,
                AuthenticationType = Profile.AuthenticationType,
                Username = Profile.Username,
                Password = Profile.Password
            };

            var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(filePath, json, ct);
            SaveLastProfilePath(filePath);

            SetStatus($"Profile saved: {filePath}");
        });
    }

    public async Task LoadProfileAsync(string filePath, CancellationToken ct = default)
    {
        await RunSafe(async () =>
        {
            SetStatus("Loading server profile…");

            var json = await File.ReadAllTextAsync(filePath, ct);
            var loaded = JsonSerializer.Deserialize<ConnectionProfile>(json)
                         ?? throw new InvalidOperationException("Invalid profile file.");

            Profile = loaded;
            SaveLastProfilePath(filePath);

            SetStatus($"Profile loaded: {filePath}");
        });
    }

    public string? GetExistingLastProfilePath()
    {
        if (!File.Exists(LastProfilePointerPath))
            return null;

        var path = File.ReadAllText(LastProfilePointerPath).Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        return path;
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

    public void SelectAll(bool select)
    {
        foreach (var tag in FlattenAll(TagTree).Where(t => t.IsSelectable))
            tag.IsSelected = select;
        Notify();
    }

    public void SelectInFolder(OpcTag folderTag, bool select)
    {
        foreach (var tag in FlattenAll(folderTag.Children).Where(t => t.IsSelectable))
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
           .Where(t => t.IsSelectable && t.IsSelected)
           .Select(t => t.NodeId)
           .ToList();

    public List<OpcTag> GetSelectedTags()
        => FlattenAll(TagTree)
           .Where(t => t.IsSelectable && t.IsSelected)
           .ToList();

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

    private static void SaveLastProfilePath(string filePath)
    {
        File.WriteAllText(LastProfilePointerPath, filePath);
    }

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

            case ExportFormat.Csv:
            default:
                var csv = BuildCsv(rows);
                await File.WriteAllTextAsync(options.OutputPath, csv, ct);
                break;
        }

        return options.OutputPath;
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
                LastReadings.Add(update);
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
        }

        Notify();
    }

    private async Task<bool> StopSubscriptionInternalAsync()
    {
        IAsyncDisposable? handle;
        CancellationTokenSource? cts;

        lock (_subscriptionSync)
        {
            handle = _activeSubscription;
            cts = _subscriptionCts;
            _activeSubscription = null;
            _subscriptionCts = null;
            IsSubscribed = false;
        }

        _subscribedNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        cts?.Cancel();
        cts?.Dispose();

        if (handle is null)
            return false;

        await handle.DisposeAsync();
        return true;
    }
}
