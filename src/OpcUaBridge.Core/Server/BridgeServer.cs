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

    /// <summary>
    /// How much of the mirror the downstream applications are actually using.
    /// </summary>
    /// <remarks>
    /// Read straight off the SDK's session and subscription managers rather than counted
    /// as sessions come and go: a counter maintained here would drift the first time a
    /// client vanished without closing its session, which is the case that matters.
    /// </remarks>
    public DownstreamActivity GetDownstreamActivity()
    {
        // Null until the server has started, and again once it has stopped.
        var instance = ServerInternal;
        if (instance is null)
            return DownstreamActivity.None;

        try
        {
            var sessions = instance.SessionManager.GetSessions().Count;
            var subscriptions = instance.SubscriptionManager.GetSubscriptions();

            var monitoredItems = 0;
            foreach (var subscription in subscriptions)
                monitoredItems += subscription.MonitoredItemCount;

            return new DownstreamActivity(sessions, subscriptions.Count, monitoredItems);
        }
        catch (Exception)
        {
            // Racing a shutdown. The dashboard asking for a count is never worth a fault.
            return DownstreamActivity.None;
        }
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
