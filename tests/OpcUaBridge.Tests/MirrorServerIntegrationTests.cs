using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using OpcUaBridge.Configuration;
using OpcUaBridge.Namespaces;
using OpcUaBridge.Server;
using OpcUaBridge.Tags;
using Xunit;

namespace OpcUaBridge.Tests;

/// <summary>
/// Starts the real mirror server and connects a real OPC UA client to it.
/// </summary>
/// <remarks>
/// <para>
/// Slow, and it generates certificates, so it is excluded from the default run with
/// <c>--filter "Category!=Integration"</c>. It earns its place anyway: it is the only
/// test that proves the thing the whole product is for -- that a client pointed at the
/// bridge sees the upstream server's structure, its values, and honest status codes when
/// the upstream link is down.
/// </para>
/// <para>
/// The OPC UA SDK is cross-platform, so this runs on Linux as well as Windows.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class MirrorServerIntegrationTests : IAsyncLifetime
{
    private const string PlantUri = "http://plant/data";
    private const int Port = 48401;

    private readonly string _pkiRoot = Directory.CreateTempSubdirectory("opcua-bridge-mirror").FullName;
    private readonly TagRegistry _registry = TagRegistry.FromSnapshot(Snapshot());
    private TagValueStore _values = null!;
    private BridgeServer _server = null!;
    private ApplicationInstance _application = null!;
    private StubWriteRouter _writeRouter = null!;

    private static NamespaceSnapshot Snapshot() => new()
    {
        NamespaceUris = [Opc.Ua.Namespaces.OpcUa, "urn:plant:Server", PlantUri],
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

    private static MirrorServerOptions ServerOptions() => new()
    {
        Host = "localhost",
        Port = Port,
        EndpointPath = "/OpcUaBridgeTest",
        ApplicationName = "OpcUaBridgeTest",
        AllowNoSecurity = true,
        AllowAnonymous = true,
        AllowWrites = true
    };

    public async Task InitializeAsync()
    {
        _values = new TagValueStore(_registry.Count);
        _writeRouter = new StubWriteRouter();
        _server = new BridgeServer(_registry, _values, ServerOptions(), _writeRouter, NullLoggerFactory.Instance);

        var configuration = await BridgeServerConfigurationFactory.CreateAsync(ServerOptions(), _pkiRoot);

        _application = new ApplicationInstance(configuration.CreateMessageContext().Telemetry)
        {
            ApplicationName = configuration.ApplicationName,
            ApplicationType = ApplicationType.Server,
            ApplicationConfiguration = configuration
        };

        Assert.True(await _application.CheckApplicationInstanceCertificatesAsync(silent: true, 2048));
        await _application.StartAsync(_server);
    }

    public Task DisposeAsync()
    {
        _application.StopAsync().GetAwaiter().GetResult();
        Directory.Delete(_pkiRoot, recursive: true);
        return Task.CompletedTask;
    }

    private async Task<ISession> ConnectAsync()
    {
        var clientPki = Path.Combine(_pkiRoot, "client");
        var configuration = new ApplicationConfiguration
        {
            ApplicationName = "MirrorTestClient",
            ApplicationType = ApplicationType.Client,
            ApplicationUri = $"urn:{Utils.GetHostName()}:MirrorTestClient",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(clientPki, "own"),
                    SubjectName = "CN=MirrorTestClient"
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(clientPki, "trusted")
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(clientPki, "issuer")
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(clientPki, "rejected")
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

        var endpointUrl = $"opc.tcp://localhost:{Port}/OpcUaBridgeTest";
        var endpoint = await CoreClientUtils.SelectEndpointAsync(configuration, endpointUrl, useSecurity: false, configuration.CreateMessageContext().Telemetry, default);
        var configured = new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(configuration));

        var factory = new DefaultSessionFactory(configuration.CreateMessageContext().Telemetry);
        return await factory.CreateAsync(
            configuration, configured, false, "MirrorTest", 60_000,
            new UserIdentity(new AnonymousIdentityToken()), null, default);
    }

    [Fact]
    public async Task ADownstreamClient_BrowsesTheUpstreamStructure()
    {
        using var session = (Session)await ConnectAsync();

        var bridgeFolder = (await session.FetchReferencesAsync(ObjectIds.ObjectsFolder))
            .Single(r => r.IsForward && r.NodeClass == NodeClass.Object && r.DisplayName?.Text == "Bridge");

        var line = Assert.Single(await ChildrenOfAsync(session, bridgeFolder));
        Assert.Equal("Line 1", line.DisplayName?.Text);

        var variables = await ChildrenOfAsync(session, line);

        Assert.Equal(["Speed", "State"], variables.Select(r => r.DisplayName?.Text).Order());
    }

    [Fact]
    public async Task MirroredNodeIds_KeepTheUpstreamIdentifierAndNamespaceUri()
    {
        // The whole promise of the gateway: the downstream application changes its
        // endpoint URL and its existing NodeIds keep resolving.
        using var session = (Session)await ConnectAsync();

        var namespaceIndex = session.NamespaceUris.GetIndex(PlantUri);
        Assert.True(namespaceIndex > 0, $"The bridge should publish the upstream namespace '{PlantUri}'.");

        var node = await session.ReadNodeAsync(NodeId.Parse($"ns={namespaceIndex};s=Line1.Speed"));

        Assert.Equal("Speed", node.DisplayName?.Text);
    }

    [Fact]
    public async Task AValueFromUpstream_IsServedToDownstreamClients()
    {
        var speed = _registry.Tags.Single(t => t.BrowsePath.EndsWith("Speed"));
        var sourceTimestamp = DateTime.UtcNow.AddSeconds(-5);
        _values.Publish(speed.Index, new DataValue(new Variant(42.5))
        {
            StatusCode = StatusCodes.Good,
            SourceTimestamp = sourceTimestamp
        });
        _server.NodeManager!.ApplyPendingValues(new int[16]);

        using var session = (Session)await ConnectAsync();
        var read = await ReadAsync(session, speed.MirrorNodeId);

        Assert.Equal(42.5, read.Value);
        Assert.Equal(StatusCodes.Good, read.StatusCode.Code);
        Assert.Equal(sourceTimestamp, read.SourceTimestamp);
    }

    [Fact]
    public async Task WhenUpstreamIsLost_CachedValuesAreServedAsUncertainRatherThanDropped()
    {
        // This is what lets the downstream application stay connected through an outage
        // instead of losing its session -- the reason the bridge exists.
        var speed = _registry.Tags.Single(t => t.BrowsePath.EndsWith("Speed"));
        var sourceTimestamp = DateTime.UtcNow.AddSeconds(-5);
        _values.Publish(speed.Index, new DataValue(new Variant(42.5))
        {
            StatusCode = StatusCodes.Good,
            SourceTimestamp = sourceTimestamp
        });
        _values.MarkStale(TimeSpan.FromMinutes(5));
        _server.NodeManager!.ApplyPendingValues(new int[16]);

        using var session = (Session)await ConnectAsync();
        var read = await ReadAsync(session, speed.MirrorNodeId);

        Assert.Equal(StatusCodes.UncertainLastUsableValue, read.StatusCode.Code);
        Assert.Equal(42.5, read.Value);
        Assert.Equal(sourceTimestamp, read.SourceTimestamp);
    }

    [Fact]
    public async Task AWriteFromDownstream_IsForwardedUpstream()
    {
        var speed = _registry.Tags.Single(t => t.BrowsePath.EndsWith("Speed"));
        using var session = (Session)await ConnectAsync();

        var response = await session.WriteAsync(null, [
            new WriteValue
            {
                NodeId = speed.MirrorNodeId,
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(88.0))
            }
        ], default);

        Assert.True(StatusCode.IsGood(response.Results[0]));
        Assert.Equal(88.0, Assert.Single(_writeRouter.Writes).Value);
    }

    [Fact]
    public async Task AWriteToAReadOnlyUpstreamTag_IsRejectedBeforeItLeavesTheBridge()
    {
        // 'State' is CurrentRead only upstream. Mirroring the access level means the
        // bridge's own server refuses the write, rather than forwarding one the plant
        // server was always going to reject.
        var state = _registry.Tags.Single(t => t.BrowsePath.EndsWith("State"));
        using var session = (Session)await ConnectAsync();

        var response = await session.WriteAsync(null, [
            new WriteValue
            {
                NodeId = state.MirrorNodeId,
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(3))
            }
        ], default);

        Assert.True(StatusCode.IsBad(response.Results[0]));
        Assert.Empty(_writeRouter.Writes);
    }

    [Fact]
    public async Task WhenUpstreamIsDown_ADownstreamWriteFailsCleanlyRatherThanHanging()
    {
        _writeRouter.NextStatus = StatusCodes.BadNoCommunication;
        var speed = _registry.Tags.Single(t => t.BrowsePath.EndsWith("Speed"));
        using var session = (Session)await ConnectAsync();

        var response = await session.WriteAsync(null, [
            new WriteValue
            {
                NodeId = speed.MirrorNodeId,
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(88.0))
            }
        ], default);

        Assert.Equal(StatusCodes.BadNoCommunication, response.Results[0].Code);
    }

    /// <summary>
    /// The object and variable children of a node.
    /// </summary>
    /// <remarks>
    /// FetchReferences also returns the inverse reference back to the parent and the
    /// node's HasTypeDefinition reference, so filter the way a real browse does.
    /// </remarks>
    private static async Task<List<ReferenceDescription>> ChildrenOfAsync(Session session, ReferenceDescription parent)
    {
        var references = await session.FetchReferencesAsync(
            ExpandedNodeId.ToNodeId(parent.NodeId, session.NamespaceUris));

        return references
            .Where(r => r.IsForward && r.NodeClass is NodeClass.Object or NodeClass.Variable)
            .ToList();
    }

    private static async Task<DataValue> ReadAsync(Session session, NodeId nodeId)
    {
        var response = await session.ReadAsync(null, 0, TimestampsToReturn.Both, [
            new ReadValueId { NodeId = nodeId, AttributeId = Attributes.Value }
        ], default);

        return response.Results[0];
    }

    private sealed class StubWriteRouter : IUpstreamWriteRouter
    {
        public List<(MirrorTag Tag, object? Value)> Writes { get; } = [];

        public StatusCode NextStatus { get; set; } = StatusCodes.Good;

        public StatusCode Write(MirrorTag tag, object? value)
        {
            if (StatusCode.IsBad(NextStatus))
                return NextStatus;

            Writes.Add((tag, value));
            return NextStatus;
        }
    }
}
