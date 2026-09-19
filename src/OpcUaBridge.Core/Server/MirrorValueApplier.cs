using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpcUaBridge.Configuration;

namespace OpcUaBridge.Server;

/// <summary>
/// Moves values from the store onto the mirror's nodes.
/// </summary>
/// <remarks>
/// A separate loop rather than work done inline when a value arrives, for two reasons.
/// Applying on the SDK's publish thread would hold up the upstream feed while contending
/// for the node-manager lock; and batching lets thousands of changes share one lock
/// acquisition instead of taking thousands.
/// </remarks>
public sealed class MirrorValueApplier(
    BridgeServer server,
    IOptionsMonitor<BridgeOptions> options,
    ILogger<MirrorValueApplier> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// How often pending values are swept onto the mirror.
    /// </summary>
    /// <remarks>
    /// Short enough that a downstream client sampling at the usual 250ms or more never
    /// notices the indirection, long enough that batches are worth taking a lock for.
    /// </remarks>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(100);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var buffer = new int[Math.Max(1, options.CurrentValue.Server.ApplyBatchSize)];
        using var timer = new PeriodicTimer(SweepInterval, _time);

        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                var nodeManager = server.NodeManager;
                if (nodeManager is null)
                    continue;

                // Keep sweeping while a batch comes back full: a burst larger than one
                // batch should be drained promptly rather than trickled out one sweep at
                // a time.
                int applied;
                do
                {
                    applied = nodeManager.ApplyPendingValues(buffer);
                }
                while (applied == buffer.Length && !stoppingToken.IsCancellationRequested);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply pending values to the mirrored address space.");
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
