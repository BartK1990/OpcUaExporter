using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Server;
using OpcUaBridge.Configuration;
using OpcUaBridge.Tags;

namespace OpcUaBridge.Server;

/// <summary>The OPC UA server downstream applications connect to instead of the flaky one.</summary>
public sealed class BridgeServer(
    TagRegistry registry,
    TagValueStore values,
    MirrorServerOptions options,
    IUpstreamWriteRouter writeRouter,
    ILoggerFactory loggerFactory) : StandardServer
{
    /// <summary>The mirror's node manager, once the server has started.</summary>
    public MirrorNodeManager? NodeManager { get; private set; }

    protected override MasterNodeManager CreateMasterNodeManager(
        IServerInternal server,
        ApplicationConfiguration configuration)
    {
        NodeManager = new MirrorNodeManager(
            server, configuration, registry, values, options, writeRouter,
            loggerFactory.CreateLogger<MirrorNodeManager>());

        return new MasterNodeManager(server, configuration, dynamicNamespaceUri: null, [NodeManager]);
    }

    protected override ServerProperties LoadServerProperties() => new()
    {
        ManufacturerName = "OPC UA Exporter project",
        ProductName = "OPC UA Bridge",
        ProductUri = "urn:opcuabridge",
        SoftwareVersion = typeof(BridgeServer).Assembly.GetName().Version?.ToString() ?? "1.0.0",
        BuildNumber = typeof(BridgeServer).Assembly.GetName().Version?.Revision.ToString() ?? "0",
        BuildDate = File.GetLastWriteTimeUtc(typeof(BridgeServer).Assembly.Location)
    };
}
