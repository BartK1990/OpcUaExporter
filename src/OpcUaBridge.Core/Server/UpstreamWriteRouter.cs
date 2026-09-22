using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opc.Ua;
using OpcUaBridge.Configuration;
using OpcUaBridge.Tags;
using OpcUaBridge.Upstream;
using OpcUaExporter.Operations;

namespace OpcUaBridge.Server;

/// <summary>
/// Passes downstream writes through to the upstream server, and nothing more.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately has no queue. A write held while the upstream link is down and replayed on
/// reconnect would apply a setpoint minutes after an operator issued it, to a process that
/// has moved on -- a genuinely dangerous behaviour to have by default. A write that cannot
/// be delivered fails immediately and says so, and the downstream application decides what
/// to do about it.
/// </para>
/// <para>
/// The upstream status code is returned verbatim. If the plant server says the value is
/// out of range, the downstream client should see exactly that, not a code the bridge
/// invented. Transparency is the whole job.
/// </para>
/// </remarks>
public sealed class UpstreamWriteRouter(
    IUpstreamConnection upstream,
    IOptionsMonitor<BridgeOptions> options,
    ILogger<UpstreamWriteRouter> logger) : IUpstreamWriteRouter, IDisposable
{
    private readonly SemaphoreSlim _concurrency = new(
        options.CurrentValue.Upstream.MaxConcurrentWrites,
        options.CurrentValue.Upstream.MaxConcurrentWrites);

    public StatusCode Write(MirrorTag tag, object? value)
    {
        ArgumentNullException.ThrowIfNull(tag);

        var upstreamOptions = options.CurrentValue.Upstream;
        var writeTimeout = TimeSpan.FromMilliseconds(upstreamOptions.WriteTimeoutMs);

        if (tag.UpstreamNodeId is null)
        {
            logger.LogWarning(
                "Rejected a write to '{BrowsePath}': its namespace is absent from the upstream server's table.",
                tag.BrowsePath);
            return StatusCodes.BadNodeIdUnknown;
        }

        // Cap concurrent writes so a downstream write storm cannot occupy every thread
        // this server has for serving reads and browses.
        if (!_concurrency.Wait(writeTimeout))
        {
            logger.LogWarning("Rejected a write to '{BrowsePath}': too many writes already in flight.", tag.BrowsePath);
            return StatusCodes.BadTooManyOperations;
        }

        try
        {
            var session = upstream.Session;
            if (session is null)
            {
                logger.LogWarning("Rejected a write to '{BrowsePath}': the upstream server is not connected.", tag.BrowsePath);
                return StatusCodes.BadNoCommunication;
            }

            using var timeout = new CancellationTokenSource(writeTimeout);

            // The server SDK's write hook is synchronous, so there is no async path to
            // take here. The timeout above is what keeps a hung upstream from holding a
            // server thread indefinitely.
            var status = OpcUaValueWriter
                .WriteValueAsync(session, tag.UpstreamNodeId, new DataValue(new Variant(value)), timeout.Token)
                .GetAwaiter()
                .GetResult();

            if (StatusCode.IsBad(status))
                logger.LogWarning("Upstream rejected a write to '{BrowsePath}': {Status}.", tag.BrowsePath, status);
            else
                logger.LogInformation("Wrote {Value} to '{BrowsePath}' upstream.", value, tag.BrowsePath);

            return status;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("A write to '{BrowsePath}' timed out after {TimeoutMs}ms.", tag.BrowsePath, upstreamOptions.WriteTimeoutMs);
            return StatusCodes.BadTimeout;
        }
        catch (ServiceResultException ex)
        {
            logger.LogWarning(ex, "A write to '{BrowsePath}' failed.", tag.BrowsePath);
            return ex.StatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "A write to '{BrowsePath}' failed unexpectedly.", tag.BrowsePath);
            return StatusCodes.BadInternalError;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public void Dispose() => _concurrency.Dispose();
}
