using Microsoft.Extensions.Logging;
using Opc.Ua;
using OpcUaExporter.Abstractions;
using OpcUaExporter.Certificates;
using OpcUaExporter.Configuration;
using OpcUaExporter.Discovery;
using OpcUaExporter.Models;
using OpcUaExporter.Operations;
using OpcUaExporter.Sessions;

namespace OpcUaExporter.Services;

/// <summary>
/// Native OPC UA client service implemented using OPCFoundation UA-.NETStandard.
/// </summary>
/// <remarks>
/// <para>
/// Every operation here opens a session, does its work and closes it again. That suits
/// an interactive tool, where the user browses, reads, exports and moves on, and it
/// means a dropped connection can never leave stale state behind.
/// </para>
/// <para>
/// It is the wrong shape for anything that must stay connected. The pieces this class
/// composes -- <see cref="OpcUaSessionFactory"/>, <see cref="OpcUaBrowser"/>,
/// <see cref="OpcUaValueReader"/>, <see cref="OpcUaValueWriter"/> -- all take a session
/// the caller owns, so a long-running gateway drives the same code against one
/// supervised, reconnecting session instead.
/// </para>
/// </remarks>
public class OpcUaClientService
{
    private readonly ILogger<OpcUaClientService> _logger;
    private readonly DiagnosticsLogService _diagnostics;
    private readonly OpcUaApplicationIdentity _identity;
    private readonly IOpcUaPkiLocation _pki;
    private readonly Lazy<Task<ApplicationConfiguration>> _configuration;

    private readonly CertificateTrustStore _trustStore;
    private readonly OpcUaSessionFactory _sessions;
    private readonly OpcUaBrowser _browser;
    private readonly OpcUaValueReader _reader;
    private readonly OpcUaValueWriter _writer;
    private readonly OpcUaNodeInspector _inspector;
    private readonly OpcUaSubscriber _subscriber;
    private readonly OpcUaServerScanner _scanner;

    /// <summary>Ports commonly used by OPC UA servers, checked before the rest of the range.</summary>
    public static IReadOnlyList<int> WellKnownOpcUaPorts => OpcUaServerScanner.WellKnownOpcUaPorts;

    /// <summary>
    /// Parses a comma-delimited list of ports and/or dash-delimited ranges (e.g. "4840,4842,502-520")
    /// into a sorted, de-duplicated port list. Throws <see cref="FormatException"/> on invalid input.
    /// </summary>
    public static List<int> ParsePortSpec(string spec) => OpcUaServerScanner.ParsePortSpec(spec);

    /// <param name="identity">
    /// Identity presented to the server. Defaults to the exporter's, so an existing
    /// <c>CN=OpcUaExporter</c> certificate store keeps working untouched.
    /// </param>
    /// <param name="pki">
    /// Where the certificate stores live. Defaults to
    /// <c>%LocalAppData%\OpcUaExporter\pki</c>, the exporter's long-standing location.
    /// </param>
    public OpcUaClientService(
        ILogger<OpcUaClientService> logger,
        DiagnosticsLogService diagnostics,
        OpcUaApplicationIdentity? identity = null,
        IOpcUaPkiLocation? pki = null)
    {
        _logger = logger;
        _diagnostics = diagnostics;
        _identity = identity ?? OpcUaApplicationIdentity.Exporter;
        _pki = pki ?? OpcUaPkiLocation.LocalAppData(_identity.ApplicationName);

        _trustStore = new CertificateTrustStore(_diagnostics);
        _configuration = new Lazy<Task<ApplicationConfiguration>>(() =>
            OpcUaApplicationConfigurationFactory.CreateClientAsync(_identity, _pki, _trustStore, _diagnostics));

        var configurationAccessor = () => _configuration.Value;
        _sessions = new OpcUaSessionFactory(configurationAccessor, _identity.ApplicationName, _diagnostics);
        _browser = new OpcUaBrowser(_diagnostics);
        _reader = new OpcUaValueReader(_diagnostics);
        _writer = new OpcUaValueWriter(_diagnostics);
        _inspector = new OpcUaNodeInspector(_diagnostics);
        _subscriber = new OpcUaSubscriber(_logger, _diagnostics);
        _scanner = new OpcUaServerScanner(configurationAccessor, _diagnostics);
    }

    public async Task<List<OpcTag>> BrowseAsync(
        ConnectionProfile profile,
        Action<List<OpcTag>>? onTopStructureReady = null,
        Action<int>? onVariableCountChanged = null,
        CancellationToken ct = default)
    {
        using var session = await _sessions.CreateAsync(profile, ct: ct);
        return await _browser.BrowseAsync(session, BrowseOptions.From(profile), onTopStructureReady, onVariableCountChanged, ct);
    }

    public async Task<List<TagReading>> ReadAsync(ConnectionProfile profile, IEnumerable<string> nodeIds, CancellationToken ct = default)
    {
        using var session = await _sessions.CreateAsync(profile, ct: ct);
        return await _reader.ReadDescribedAsync(session, nodeIds, ct);
    }

    /// <summary>Writes a single value to a node's Value attribute, converting the raw input to the node's data type.</summary>
    public async Task<TagReading> WriteAsync(ConnectionProfile profile, string nodeId, string rawValue, string? dataTypeHint = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            throw new InvalidOperationException("Node id is required.");

        using var session = await _sessions.CreateAsync(profile, ct: ct);
        return await _writer.WriteAsync(session, nodeId, rawValue, dataTypeHint, ct);
    }

    public async Task TestConnectionAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        using var session = await _sessions.CreateAsync(profile, ct: ct);
        _diagnostics.Add("Connection test completed successfully.");
    }

    public async Task<(IAsyncDisposable Handle, List<TagReading> InitialReadings)> SubscribeAsync(
        ConnectionProfile profile,
        IEnumerable<string> nodeIds,
        Action<TagReading> onUpdate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(onUpdate);

        // Ownership of the session passes to the returned handle, so this one is not disposed here.
        var session = await _sessions.CreateAsync(profile, ct: ct);

        try
        {
            return await _subscriber.SubscribeAsync(
                session, nodeIds, onUpdate, $"{_identity.ApplicationName} Live Subscription", ct);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public Task<ServerCapabilitiesInfo> GetServerCapabilitiesAsync(string endpointUrl, CancellationToken ct = default)
        => _scanner.GetServerCapabilitiesAsync(endpointUrl, ct);

    public async Task<NodeDetails> GetNodeDetailsAsync(ConnectionProfile profile, string nodeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            throw new InvalidOperationException("Node id is required.");

        using var session = await _sessions.CreateAsync(profile, ct: ct);
        return await _inspector.GetNodeDetailsAsync(session, nodeId, ct);
    }

    public Task ScanForServersAsync(
        string host,
        IReadOnlyList<int> ports,
        int maxDegreeOfParallelism,
        int tcpProbeTimeoutMs,
        Action<int, int>? onProgress,
        Action<DiscoveredServerInfo>? onServerFound,
        CancellationToken ct = default)
        => _scanner.ScanForServersAsync(host, ports, maxDegreeOfParallelism, tcpProbeTimeoutMs, onProgress, onServerFound, ct);

    public List<PendingCertificateInfo> GetPendingCertificates() => _trustStore.GetPending();

    public async Task<bool> TrustPendingCertificateAsync(string thumbprint, CancellationToken ct = default)
        => await _trustStore.TrustAsync(await _configuration.Value, thumbprint, ct);

    public bool RejectPendingCertificate(string thumbprint) => _trustStore.Reject(thumbprint);
}
