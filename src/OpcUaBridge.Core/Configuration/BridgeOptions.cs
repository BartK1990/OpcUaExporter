using System.ComponentModel.DataAnnotations;
using OpcUaExporter.Models;

namespace OpcUaBridge.Configuration;

/// <summary>Root of everything the bridge reads from <c>appsettings.json</c>.</summary>
public sealed class BridgeOptions
{
    public const string SectionName = "Bridge";

    public UpstreamOptions Upstream { get; set; } = new();
    public AcquisitionOptions Acquisition { get; set; } = new();
    public MirrorServerOptions Server { get; set; } = new();
    public SnapshotOptions Snapshot { get; set; } = new();
    public WebOptions Web { get; set; } = new();
}

/// <summary>How the bridge connects to the OPC UA server it is fronting.</summary>
public sealed class UpstreamOptions
{
    [Required]
    public string EndpointUrl { get; set; } = "opc.tcp://localhost:4840";

    public ConnectionSecurityMode SecurityMode { get; set; } = ConnectionSecurityMode.None;

    public string SecurityPolicy { get; set; } = "http://opcfoundation.org/UA/SecurityPolicy#None";

    public AuthenticationType AuthenticationType { get; set; } = AuthenticationType.Anonymous;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Name this bridge presents as an OPC UA <em>client</em>.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>OpcUaExporter</c> rather than <c>OpcUaBridge</c>, and that is
    /// deliberate. The SDK finds the application certificate by subject name, and an
    /// operator migrating from the desktop exporter copies a certificate store whose
    /// certificate is <c>CN=OpcUaExporter</c> -- already trusted by their server. Naming
    /// the bridge's client identity anything else would silently generate a fresh
    /// certificate that the server rejects. Change this only when issuing a new
    /// certificate and trusting it upstream.
    /// </remarks>
    public string ClientApplicationName { get; set; } = "OpcUaExporter";

    /// <summary>Overrides the certificate subject. Defaults to <c>CN={ClientApplicationName}</c>.</summary>
    public string? ClientSubjectName { get; set; }

    /// <summary>Overrides the application URI. Defaults to <c>urn:{hostname}:{ClientApplicationName}</c>.</summary>
    public string? ClientApplicationUri { get; set; }

    [Range(1000, 3_600_000)]
    public int SessionTimeoutMs { get; set; } = 60_000;

    /// <summary>
    /// How often the SDK pings the server to prove the session is alive. This is the
    /// bridge's detection latency for a dropped upstream: a dead link is noticed within
    /// roughly one interval.
    /// </summary>
    [Range(500, 300_000)]
    public int KeepAliveIntervalMs { get; set; } = 5_000;

    [Range(100, 600_000)]
    public int ReconnectMinDelayMs { get; set; } = 1_000;

    [Range(1_000, 3_600_000)]
    public int ReconnectMaxDelayMs { get; set; } = 30_000;

    /// <summary>Timeout for a single write forwarded from a downstream client.</summary>
    [Range(100, 120_000)]
    public int WriteTimeoutMs { get; set; } = 5_000;

    /// <summary>Concurrent upstream writes. Caps how much of the server's thread pool a write storm can occupy.</summary>
    [Range(1, 64)]
    public int MaxConcurrentWrites { get; set; } = 8;

    /// <summary>Builds the <see cref="ConnectionProfile"/> the shared OPC UA client code expects.</summary>
    public ConnectionProfile ToConnectionProfile() => new()
    {
        Name = "Upstream",
        EndpointUrl = EndpointUrl,
        SecurityMode = SecurityMode,
        SecurityPolicy = SecurityPolicy,
        AuthenticationType = AuthenticationType,
        Username = Username,
        Password = Password
    };
}

/// <summary>Whether values are pushed by the server or pulled on a timer.</summary>
public enum AcquisitionMode
{
    /// <summary>The server reports changes. Cheaper for both ends; the default.</summary>
    Subscription,

    /// <summary>The bridge reads every tag on a fixed interval.</summary>
    Polling
}

/// <summary>How much a value must move before the upstream server bothers reporting it.</summary>
/// <remarks>
/// A gateway's cost is very nearly proportional to the values flowing through it, and on a
/// plant server most of those values are an analogue input jittering in its last digit.
/// A deadband drops them at the source, so the upstream server, the network, this process
/// and the application behind it all stop paying for them.
/// </remarks>
public enum AcquisitionDeadband
{
    /// <summary>Report every change. The default, and the only setting that mirrors the upstream server exactly.</summary>
    None,

    /// <summary>Report only when the value moves by more than <c>DeadbandValue</c>, in the tag's own units.</summary>
    Absolute,

    /// <summary>
    /// Report only when the value moves by more than <c>DeadbandValue</c> percent of the
    /// tag's engineering-unit range.
    /// </summary>
    /// <remarks>
    /// Requires the upstream node to publish an <c>EURange</c> property. A server rejects
    /// the filter on any node that does not, and those tags are reported as rejected
    /// monitored items at startup rather than silently going quiet.
    /// </remarks>
    Percent
}

/// <summary>How the bridge gets values out of the upstream server.</summary>
public sealed class AcquisitionOptions
{
    public AcquisitionMode Mode { get; set; } = AcquisitionMode.Subscription;

    /// <summary>
    /// Monitored items per subscription. Several medium subscriptions beat one huge one:
    /// the SDK can keep multiple publish requests in flight, and one bad node fails only
    /// its own subscription's create call.
    /// </summary>
    [Range(1, 50_000)]
    public int MaxItemsPerSubscription { get; set; } = 1_000;

    [Range(50, 3_600_000)]
    public int PublishingIntervalMs { get; set; } = 1_000;

    [Range(-1, 3_600_000)]
    public int SamplingIntervalMs { get; set; } = 1_000;

    /// <summary>
    /// Server-side queue depth per monitored item. One is right for a gateway: it mirrors
    /// the current value, so buffering superseded ones only costs the upstream server
    /// memory -- at a few thousand tags, a great deal of it.
    /// </summary>
    [Range(1, 1_000)]
    public int QueueSize { get; set; } = 1;

    /// <summary>
    /// Whether the upstream server filters out changes too small to care about.
    /// </summary>
    /// <remarks>
    /// Off by default, because a bridge's job is to mirror what the upstream server says
    /// and a deadband is a deliberate decision to stop doing that exactly. It is the
    /// largest single lever on this process's CPU when one is needed: cost tracks the
    /// value rate, and on analogue tags a small deadband commonly removes most of it.
    /// Subscription mode only -- polling reads every tag by definition.
    /// </remarks>
    public AcquisitionDeadband Deadband { get; set; } = AcquisitionDeadband.None;

    /// <summary>The deadband's magnitude: engineering units for Absolute, percent of range for Percent.</summary>
    [Range(0, 1_000_000_000)]
    public double DeadbandValue { get; set; }

    /// <summary>How often a polling cycle starts. Cycles never queue; a late one is skipped.</summary>
    [Range(50, 3_600_000)]
    public int PollingIntervalMs { get; set; } = 1_000;

    /// <summary>Upper bound on nodes per Read call, further clamped by the server's own declared limit.</summary>
    [Range(1, 100_000)]
    public int MaxNodesPerRead { get; set; } = 1_000;

    /// <summary>Concurrent read chunks within one polling cycle.</summary>
    [Range(1, 64)]
    public int MaxConcurrentReads { get; set; } = 4;
}

/// <summary>What downstream clients see: the mirrored endpoint and its address space.</summary>
public sealed class MirrorServerOptions
{
    /// <summary>Host in the endpoint URL. Must match the server certificate's subject alternative name.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Defaults to 4841 rather than 4840 so the bridge can coexist with a server on the same machine.</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 4841;

    public string EndpointPath { get; set; } = "/OpcUaBridge";

    public string ApplicationName { get; set; } = "OpcUaBridge";

    /// <summary>Whether downstream writes are forwarded upstream at all. Off makes the whole mirror read-only.</summary>
    public bool AllowWrites { get; set; } = true;

    /// <summary>Offer an unsecured endpoint. Convenient on a trusted network, and nowhere else.</summary>
    public bool AllowNoSecurity { get; set; } = true;

    public bool AllowAnonymous { get; set; } = true;

    /// <summary>
    /// Warn at startup about mirrored namespaces whose index differs from the upstream one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The indexes cannot be made to match, and this option does not try. Every OPC UA
    /// server's namespace table begins with the standard OPC UA namespace at 0 and its own
    /// application URI at 1, so the bridge's own URI occupies the slot the upstream server
    /// used for its first namespace, and everything after it shifts by at least one.
    /// </para>
    /// <para>
    /// This matters because a downstream client with a literal <c>ns=2;s=Something</c> in
    /// its configuration will break. Clients that resolve by namespace URI -- which is the
    /// spec-correct way, and what the bridge itself does -- are unaffected. The resolved
    /// table is logged on every start either way; this only controls whether a mismatch is
    /// called out as a warning, which is worth silencing once you know your clients resolve
    /// by URI.
    /// </para>
    /// </remarks>
    public bool WarnOnNamespaceIndexShift { get; set; } = true;

    /// <summary>How long a cached value keeps <c>UncertainLastUsableValue</c> before degrading to <c>BadNoCommunication</c>.</summary>
    [Range(0, 86_400)]
    public int StaleAfterSeconds { get; set; } = 300;

    /// <summary>Mirror nodes updated under one node-manager lock per pass.</summary>
    [Range(1, 100_000)]
    public int ApplyBatchSize { get; set; } = 2_000;

    /// <summary>The endpoint URL downstream clients connect to.</summary>
    public string BuildEndpointUrl(string fallbackHost)
    {
        var host = !string.IsNullOrWhiteSpace(Host) ? Host : fallbackHost;
        var path = string.IsNullOrWhiteSpace(EndpointPath) ? string.Empty
            : EndpointPath.StartsWith('/') ? EndpointPath : "/" + EndpointPath;

        return $"opc.tcp://{host}:{Port}{path}";
    }
}

/// <summary>The captured address space and the rules for changing it.</summary>
public sealed class SnapshotOptions
{
    /// <summary>Overrides the snapshot location. Relative paths resolve against the executable's folder.</summary>
    public string? Path { get; set; }

    public bool EnableParallelBrowse { get; set; } = true;

    [Range(1, 32)]
    public int ParallelBrowseMaxDegree { get; set; } = 10;

    /// <summary>
    /// Mirror the upstream server's standard <c>Server</c> object -- its status,
    /// capabilities and diagnostics -- alongside its data.
    /// </summary>
    /// <remarks>
    /// Off by default. Every OPC UA server publishes that subtree, the bridge included, so
    /// mirroring the upstream's would give a downstream client two of them: one describing
    /// the bridge and one describing a different server, updated only as often as the
    /// bridge polls it. On a real server it is also hundreds of nodes of diagnostics that
    /// nobody asked the gateway to carry. The plant's data is what a downstream
    /// application is here for.
    /// </remarks>
    public bool IncludeServerDiagnostics { get; set; }
}

/// <summary>Where the operator dashboard listens.</summary>
public sealed class WebOptions
{
    /// <summary>
    /// Loopback only by default: an unauthenticated administration UI for a plant gateway
    /// has no business being reachable from the network.
    /// </summary>
    public string Urls { get; set; } = "http://127.0.0.1:5080";

    /// <summary>
    /// Required before the UI may bind to anything but loopback. Startup fails rather than
    /// silently exposing the dashboard.
    /// </summary>
    public string? AdminToken { get; set; }
}
