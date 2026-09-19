using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpcUaBridge.Configuration;

namespace OpcUaBridge.Tags;

/// <summary>
/// Keeps the cached values' status honest for as long as the upstream link is down.
/// </summary>
/// <remarks>
/// <para>
/// Sweeping once when the link drops is not enough. At that moment every value is seconds
/// old, so all of them become <c>UncertainLastUsableValue</c> and none is old enough to
/// degrade further. Without a repeating sweep a bridge disconnected overnight would still
/// be offering fourteen-hour-old numbers as merely uncertain the next morning -- which is
/// exactly what <c>Bridge:Server:StaleAfterSeconds</c> exists to prevent.
/// </para>
/// <para>
/// It also keeps the work off the SDK's keep-alive thread, which raises the event that
/// starts it.
/// </para>
/// </remarks>
public sealed class TagStalenessMonitor(
    TagValueStore values,
    IOptionsMonitor<BridgeOptions> options,
    ILogger<TagStalenessMonitor> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>How often the status of cached values is re-evaluated during an outage.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    private volatile bool _upstreamDown;
    private bool _reported;

    /// <summary>Called when the upstream link drops. Returns immediately.</summary>
    public void UpstreamLost() => _upstreamDown = true;

    /// <summary>Called when the upstream link is back, so sweeping stops.</summary>
    public void UpstreamRestored()
    {
        _upstreamDown = false;
        _reported = false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, _time);

        while (await WaitAsync(timer, stoppingToken))
        {
            if (!_upstreamDown)
                continue;

            try
            {
                var staleAfter = TimeSpan.FromSeconds(options.CurrentValue.Server.StaleAfterSeconds);
                var affected = values.MarkStale(staleAfter);

                if (affected == 0)
                    continue;

                // The first sweep of an outage is the interesting one; later sweeps only
                // move values past the grace period, so they are logged at debug to keep a
                // long outage from filling the log.
                if (!_reported)
                {
                    _reported = true;
                    logger.LogWarning(
                        "Upstream link lost; {AffectedCount} cached value(s) are now flagged as no longer trustworthy.",
                        affected);
                }
                else
                {
                    logger.LogDebug(
                        "{AffectedCount} cached value(s) have now outlived the {StaleAfterSeconds}s grace period.",
                        affected, staleAfter.TotalSeconds);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to re-evaluate the status of cached values.");
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
