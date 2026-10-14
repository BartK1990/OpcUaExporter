using Opc.Ua;
using Opc.Ua.Server;

namespace OpcUaExporter.SimServer;

internal sealed class SimServer : StandardServer
{
    protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
    {
        var nodeManagers = new List<INodeManager> { new SimNodeManager(server, configuration) };
        return new MasterNodeManager(server, configuration, null, nodeManagers.ToArray());
    }

    protected override ServerProperties LoadServerProperties() => new()
    {
        ManufacturerName = "OpcUaExporter",
        ProductName = "OpcUaExporter Simulation Server",
        ProductUri = "urn:OpcUaExporter:SimServer",
        SoftwareVersion = Utils.GetAssemblySoftwareVersion(),
        BuildNumber = Utils.GetAssemblyBuildNumber(),
        BuildDate = Utils.GetAssemblyTimestamp()
    };
}
