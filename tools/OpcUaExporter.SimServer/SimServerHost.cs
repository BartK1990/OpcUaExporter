using Opc.Ua;
using Opc.Ua.Configuration;

namespace OpcUaExporter.SimServer;

/// <summary>
/// A running simulation server. Used by <c>Program</c> and by the end-to-end
/// tests, which start one in-process on a free port.
/// </summary>
public sealed class SimServerHost : IAsyncDisposable
{
    private readonly SimServer _server;

    private SimServerHost(SimServer server, int port)
    {
        _server = server;
        Port = port;
    }

    public int Port { get; }

    /// <summary>Endpoint URL to connect to, e.g. <c>opc.tcp://localhost:4840</c>.</summary>
    public string EndpointUrl => $"opc.tcp://localhost:{Port}";

    public IEnumerable<EndpointDescription> Endpoints => _server.GetEndpoints();

    public static async Task<SimServerHost> StartAsync(int port)
    {
        var config = await SimServerConfiguration.CreateAsync(port);
        var application = new ApplicationInstance(config.CreateMessageContext().Telemetry)
        {
            ApplicationName = config.ApplicationName,
            ApplicationType = ApplicationType.Server,
            ApplicationConfiguration = config
        };

        if (!await application.CheckApplicationInstanceCertificatesAsync(true, 2048, ct: default))
            throw new InvalidOperationException("Unable to create the simulation server certificate.");

        var server = new SimServer();
        await application.StartAsync(server);
        return new SimServerHost(server, port);
    }

    public async ValueTask DisposeAsync() => await _server.StopAsync();
}
