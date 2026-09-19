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

        EndpointUrl = configuration.ServerConfiguration.BaseAddresses.FirstOrDefault();

        try
        {
            await _application.StartAsync(server);
        }
        catch (Exception ex)
        {
            // The SDK reports almost every start-up failure as BadInternalError with the
            // text "Unexpected error starting application", which on its own tells an
            // operator nothing. The real cause is usually in the inner exception -- a port
            // already in use, an unresolvable host name in the endpoint URL, or a
            // certificate the process cannot read -- so name it and what to do about it.
            throw new InvalidOperationException(
                $"Could not start the mirrored OPC UA endpoint on '{EndpointUrl}'. " +
                $"{DescribeStartupFailure(ex)} " +
                "Check that the port is free, that Bridge:Server:Host resolves on this machine, " +
                $"and that the service account can read '{paths.ServerPkiDirectory}'.",
                ex);
        }

        logger.LogInformation("Mirrored OPC UA endpoint listening on {EndpointUrl}.", EndpointUrl);
    }

    /// <summary>
    /// Digs the useful message out of the SDK's wrapping.
    /// </summary>
    /// <remarks>
    /// A <see cref="ServiceResultException"/> carries the real diagnostic in its nested
    /// <see cref="ServiceResult.InnerResult"/> chain, not in its message, which is why the
    /// bare exception text is so uninformative.
    /// </remarks>
    private static string DescribeStartupFailure(Exception exception)
    {
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is ServiceResultException serviceResult)
            {
                var detail = DescribeServiceResult(serviceResult.Result);
                if (!string.IsNullOrWhiteSpace(detail))
                    return $"The underlying error was: {detail}";

                continue;
            }

            return $"The underlying error was: {inner.GetType().Name}: {inner.Message}.";
        }

        return $"The underlying error was: {exception.Message}.";
    }

    private static string DescribeServiceResult(ServiceResult? result)
    {
        var parts = new List<string>();

        for (var current = result; current is not null; current = current.InnerResult)
        {
            if (!string.IsNullOrWhiteSpace(current.AdditionalInfo))
                parts.Add(current.AdditionalInfo.Trim());
            else if (!string.IsNullOrWhiteSpace(current.LocalizedText?.Text))
                parts.Add(current.LocalizedText.Text);
        }

        return string.Join(" -> ", parts);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_application is null)
            return;

        try
        {
            await _application.StopAsync();
            logger.LogInformation("Mirrored OPC UA endpoint stopped.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stopping the mirrored OPC UA endpoint reported an error.");
        }
    }
}
