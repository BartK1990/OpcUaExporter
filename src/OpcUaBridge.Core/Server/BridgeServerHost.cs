using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Configuration;
using OpcUaBridge.Configuration;

namespace OpcUaBridge.Server;

/// <summary>Starts and stops the mirrored OPC UA endpoint with the host.</summary>
public sealed class BridgeServerHost(
    BridgeServer server,
    BridgePaths paths,
    IOptions<BridgeOptions> options,
    ILogger<BridgeServerHost> logger) : IHostedService
{
    private ApplicationInstance? _application;

    /// <summary>The endpoint downstream clients connect to, once started.</summary>
    public string? EndpointUrl { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var serverOptions = options.Value.Server;

        var configuration = await BridgeServerConfigurationFactory.CreateAsync(
            serverOptions, paths.ServerPkiDirectory, cancellationToken);

        _application = new ApplicationInstance(configuration.CreateMessageContext().Telemetry)
        {
            ApplicationName = configuration.ApplicationName,
            ApplicationType = ApplicationType.Server,
            ApplicationConfiguration = configuration
        };

        var hasCertificate = await _application.CheckApplicationInstanceCertificatesAsync(
            silent: true, 2048, ct: cancellationToken);

        if (!hasCertificate)
        {
            throw new InvalidOperationException(
                $"Could not create or load the bridge's server certificate under '{paths.ServerPkiDirectory}'. " +
                "Check that the service account can write there.");
        }

        await _application.StartAsync(server);

        EndpointUrl = configuration.ServerConfiguration.BaseAddresses.FirstOrDefault();
        logger.LogInformation("Mirrored OPC UA endpoint listening on {EndpointUrl}.", EndpointUrl);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _application?.Stop();
            logger.LogInformation("Mirrored OPC UA endpoint stopped.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stopping the mirrored OPC UA endpoint reported an error.");
        }

        return Task.CompletedTask;
    }
}
