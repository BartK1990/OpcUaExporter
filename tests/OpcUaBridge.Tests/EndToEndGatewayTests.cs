using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using OpcUaBridge.Acquisition;
using OpcUaBridge.Configuration;
using OpcUaBridge.Namespaces;
using OpcUaBridge.Server;
using OpcUaBridge.Tags;
using OpcUaExporter.Operations;
using OpcUaExporter.Services;
using Xunit;

namespace OpcUaBridge.Tests;

/// <summary>
/// Drives the whole gateway: browse an upstream server, capture its namespace, subscribe
/// to it, and serve the result from the bridge's own endpoint.
/// </summary>
/// <remarks>
/// <para>
/// The "upstream server" here is a second <see cref="BridgeServer"/> standing in for the
/// plant server, which is convenient and also a fair test: it is a real OPC UA server
/// speaking the real protocol over a real socket.
/// </para>
/// <para>
/// This is the test that would catch a regression in the join between the pieces -- a
/// namespace URI recorded wrongly, a NodeId that does not survive the round trip, a
/// subscription whose values never reach the mirror. The unit tests cover each piece;
/// only this covers them working together.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class EndToEndGatewayTests : IAsyncLifetime
{
    private const string PlantNamespaceUri = "http://plant/line1";
    private const int UpstreamPort = 48411;

    private readonly string _pkiRoot = Directory.CreateTempSubdirectory("opcua-bridge-e2e").FullName;

    private ApplicationInstance _upstreamApplication = null!;
    private TagValueStore _upstreamValues = null!;
    private TagRegistry _upstreamRegistry = null!;
    private BridgeServer _upstreamServer = null!;

    /// <summary>The address space the stand-in plant server publishes.</summary>
    private static NamespaceSnapshot PlantSnapshot() => new()
    {
        NamespaceUris = [Opc.Ua.Namespaces.OpcUa, "urn:plant:Server", PlantNamespaceUri],
        Stats = new SnapshotStats { ObjectCount = 1, VariableCount = 2 },
        Nodes =
        [
            new SnapshotNode
            {
                NamespaceIndex = 2, Identifier = "s=Line1", BrowseName = "Line1", DisplayName = "Line 1",
                Children =
                [
                    new SnapshotNode
                    {
                        NamespaceIndex = 2, Identifier = "s=Line1.Speed", BrowseName = "Speed",
                        IsVariable = true, DataType = "Double", AccessLevel = 3
                    },
                    new SnapshotNode
                    {
                        NamespaceIndex = 2, Identifier = "s=Line1.State", BrowseName = "State",
                        IsVariable = true, DataType = "Int32", AccessLevel = 1
                    }
                ]
            }
        ]
    };

    public async Task InitializeAsync()
    {
        var options = new MirrorServerOptions
        {
            Host = "localhost",
            Port = UpstreamPort,
            EndpointPath = "/Plant",
            ApplicationName = "PlantServerStub",
            AllowNoSecurity = true,
            AllowAnonymous = true
        };

        _upstreamRegistry = TagRegistry.FromSnapshot(PlantSnapshot());
        _upstreamValues = new TagValueStore(_upstreamRegistry.Count);
        _upstreamServer = new BridgeServer(
            _upstreamRegistry, _upstreamValues, options, new NullWriteRouter(), NullLoggerFactory.Instance);

        var configuration = await BridgeServerConfigurationFactory.CreateAsync(
            options, Path.Combine(_pkiRoot, "plant"));

        _upstreamApplication = new ApplicationInstance(configuration.CreateMessageContext().Telemetry)
        {
            ApplicationName = configuration.ApplicationName,
            ApplicationType = ApplicationType.Server,
            ApplicationConfiguration = configuration
        };

        await _upstreamApplication.CheckApplicationInstanceCertificatesAsync(silent: true, 2048);
        await _upstreamApplication.StartAsync(_upstreamServer);

        PublishUpstream("Speed", 12.5);
        PublishUpstream("State", 3);
    }

    public Task DisposeAsync()
    {
        _upstreamApplication.StopAsync().GetAwaiter().GetResult();
        Directory.Delete(_pkiRoot, recursive: true);
        return Task.CompletedTask;
    }

    private void PublishUpstream(string browseNameSuffix, object value)
    {
        var tag = _upstreamRegistry.Tags.Single(t => t.BrowsePath.EndsWith(browseNameSuffix, StringComparison.Ordinal));

        _upstreamValues.Publish(tag.Index, new DataValue(new Variant(value))
        {
            StatusCode = StatusCodes.Good,
            SourceTimestamp = DateTime.UtcNow
        });

        _upstreamServer.NodeManager!.ApplyPendingValues(new int[16]);
    }

    [Fact]
    public async Task TheGateway_CapturesTheUpstreamNamespaceAndThenServesItsValues()
    {
        await using var session = await ConnectToUpstreamAsync();

        // 1. Capture: browse the upstream server and turn it into a snapshot.
        var capture = new NamespaceCaptureService(
            new ThrowawaySnapshotStore(),
            new OpcUaBrowser(new DiagnosticsLogService()),
            BridgeOptionsMonitor(),
            NullLogger<NamespaceCaptureService>.Instance);

        var snapshot = await capture.CaptureAsync(session.Session);

        Assert.Contains(PlantNamespaceUri, snapshot.NamespaceUris);
        var speedNode = Assert.Single(snapshot.Variables(), v => v.BrowseName == "Speed");
        Assert.Equal("s=Line1.Speed", speedNode.Identifier);
        Assert.Equal(PlantNamespaceUri, snapshot.NamespaceUris[speedNode.NamespaceIndex]);

        // The upstream node is writable, so the capture's access-level sweep must say so.
        Assert.True((speedNode.AccessLevel & AccessLevels.CurrentWrite) != 0);
        Assert.False((Assert.Single(snapshot.Variables(), v => v.BrowseName == "State").AccessLevel
            & AccessLevels.CurrentWrite) != 0);

        // 2. Resolve: rebuild every NodeId from its namespace URI against this session.
        var registry = TagRegistry.FromSnapshot(snapshot);
        var missing = registry.ResolveAgainst(session.Session.NamespaceUris, NullLogger.Instance);

        Assert.Empty(missing);
        Assert.All(registry.Tags, tag => Assert.NotNull(tag.UpstreamNodeId));

        // 3. Acquire: subscribe and wait for the upstream server to report the values.
        var values = new TagValueStore(registry.Count);
        await using var engine = new SubscriptionAcquisitionEngine(
            registry, values, BridgeOptionsMonitor(), NullLogger<SubscriptionAcquisitionEngine>.Instance);

        await engine.StartAsync(session.Session, subscriptionsTransferred: false, CancellationToken.None);

        var speed = registry.Tags.Single(t => t.BrowsePath.EndsWith("Speed", StringComparison.Ordinal));
        var received = await WaitForValueAsync(values, speed.Index);

        Assert.Equal(12.5, received.Value);
        Assert.True(StatusCode.IsGood(received.StatusCode));
    }

    [Fact]
    public async Task ACapturedSnapshot_SurvivesASaveAndReloadWithEveryNodeStillResolvable()
    {
        await using var session = await ConnectToUpstreamAsync();

        var store = new ThrowawaySnapshotStore();
        var capture = new NamespaceCaptureService(
            store, new OpcUaBrowser(new DiagnosticsLogService()),
            BridgeOptionsMonitor(), NullLogger<NamespaceCaptureService>.Instance);

        await capture.ApplyAsync(await capture.CaptureAsync(session.Session));

        // A restart reads the snapshot back from disk: the values a downstream client
        // depends on must survive the round trip through JSON intact.
        var reloaded = await store.LoadAsync();
        Assert.NotNull(reloaded);

        var registry = TagRegistry.FromSnapshot(reloaded);
        Assert.Empty(registry.ResolveAgainst(session.Session.NamespaceUris, NullLogger.Instance));

        var speed = Assert.Single(registry.Tags, t => t.BrowsePath.EndsWith("Line1/Speed", StringComparison.Ordinal));
        var state = Assert.Single(registry.Tags, t => t.BrowsePath.EndsWith("Line1/State", StringComparison.Ordinal));

        Assert.Equal(PlantNamespaceUri, speed.UpstreamNamespaceUri);
        Assert.Equal("s=Line1.Speed", speed.Identifier);
        Assert.True(speed.IsWritable);
        Assert.False(state.IsWritable);

        // The namespace index the server reports need not be the one the snapshot recorded,
        // which is the entire reason the snapshot stores URIs: resolution follows the URI
        // and lands on the right node either way.
        var currentIndex = session.Session.NamespaceUris.GetIndex(PlantNamespaceUri);
        Assert.Equal(NodeId.Parse($"ns={currentIndex};s=Line1.Speed"), speed.UpstreamNodeId);
    }

    private static async Task<DataValue> WaitForValueAsync(TagValueStore values, int tagIndex)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            if (values.Read(tagIndex) is { } value && StatusCode.IsGood(value.StatusCode))
                return value;

            await Task.Delay(100);
        }

        throw new TimeoutException($"No value arrived for tag {tagIndex} within 20 seconds.");
    }

    private static IOptionsMonitor<BridgeOptions> BridgeOptionsMonitor()
        => new StaticOptionsMonitor(new BridgeOptions
        {
            Upstream = new UpstreamOptions { EndpointUrl = $"opc.tcp://localhost:{UpstreamPort}/Plant" },
            Acquisition = new AcquisitionOptions { PublishingIntervalMs = 200, SamplingIntervalMs = 200 }
        });

    private async Task<SessionHandle> ConnectToUpstreamAsync()
    {
        var clientPki = Path.Combine(_pkiRoot, "client");
        var configuration = new ApplicationConfiguration
        {
            ApplicationName = "GatewayTestClient",
            ApplicationType = ApplicationType.Client,
            ApplicationUri = $"urn:{Utils.GetHostName()}:GatewayTestClient",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(clientPki, "own"),
                    SubjectName = "CN=GatewayTestClient"
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(clientPki, "trusted")
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(clientPki, "issuer")
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(clientPki, "rejected")
                },
                AutoAcceptUntrustedCertificates = true
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 30_000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 }
        };

        await configuration.ValidateAsync(ApplicationType.Client, default);

        var instance = new ApplicationInstance(configuration.CreateMessageContext().Telemetry)
        {
            ApplicationName = configuration.ApplicationName,
            ApplicationType = ApplicationType.Client,
            ApplicationConfiguration = configuration
        };
        await instance.CheckApplicationInstanceCertificatesAsync(silent: true, 2048);

        var endpointUrl = $"opc.tcp://localhost:{UpstreamPort}/Plant";
        var endpoint = await CoreClientUtils.SelectEndpointAsync(
            configuration, endpointUrl, useSecurity: false, configuration.CreateMessageContext().Telemetry, default);

        var factory = new DefaultSessionFactory(configuration.CreateMessageContext().Telemetry);
        var session = await factory.CreateAsync(
            configuration, new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(configuration)),
            false, "GatewayTest", 60_000, new UserIdentity(new AnonymousIdentityToken()), null, default);

        return new SessionHandle((Session)session);
    }

    private sealed record SessionHandle(Session Session) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await Session.CloseAsync(CancellationToken.None); } catch { /* already gone */ }
            Session.Dispose();
        }
    }

    private sealed class NullWriteRouter : IUpstreamWriteRouter
    {
        public StatusCode Write(MirrorTag tag, object? value) => StatusCodes.Good;
    }

    /// <summary>A snapshot store backed by a temporary file, discarded with the test.</summary>
    private sealed class ThrowawaySnapshotStore : INamespaceSnapshotWriter
    {
        private readonly JsonNamespaceSnapshotStore _inner;

        public ThrowawaySnapshotStore()
        {
            var directory = Directory.CreateTempSubdirectory("opcua-bridge-e2e-snapshot").FullName;
            _inner = new JsonNamespaceSnapshotStore(
                Path.Combine(directory, "namespace.json"),
                timestamp => Path.Combine(directory, $"namespace.{timestamp:yyyyMMddHHmmss}.json"),
                NullLogger<JsonNamespaceSnapshotStore>.Instance);
        }

        public string SnapshotPath => _inner.SnapshotPath;
        public bool Exists => _inner.Exists;
        public Task<NamespaceSnapshot?> LoadAsync(CancellationToken ct = default) => _inner.LoadAsync(ct);
        public Task<string?> ComputeHashAsync(CancellationToken ct = default) => _inner.ComputeHashAsync(ct);
        public Task<string?> SaveAsync(NamespaceSnapshot snapshot, CancellationToken ct = default)
            => _inner.SaveAsync(snapshot, ct);
    }

    private sealed class StaticOptionsMonitor(BridgeOptions options) : IOptionsMonitor<BridgeOptions>
    {
        public BridgeOptions CurrentValue => options;
        public BridgeOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<BridgeOptions, string?> listener) => null;
    }
}
