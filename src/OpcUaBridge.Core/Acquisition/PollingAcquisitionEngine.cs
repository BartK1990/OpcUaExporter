using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Client;
using OpcUaBridge.Configuration;
using OpcUaBridge.Tags;
using OpcUaExporter.Operations;

namespace OpcUaBridge.Acquisition;

/// <summary>
/// Reads every mirrored tag on a fixed interval.
/// </summary>
/// <remarks>
/// <para>
/// For servers whose subscription support is absent, limited or simply untrustworthy --
/// which is not rare on older gateways and PLC adapters, and is often exactly why a bridge
/// is needed in the first place.
/// </para>
/// <para>
/// It costs the upstream server much more than a subscription: every tag is read every
/// cycle whether or not anything changed. The interval is therefore the operator's main
/// lever, and <see cref="AcquisitionStatistics.CyclesSkipped"/> is how they know it is set
/// too tight.
/// </para>
/// </remarks>
public sealed class PollingAcquisitionEngine(
    TagRegistry registry,
    TagValueStore values,
    IOptionsMonitor<BridgeOptions> options,
    ILogger<PollingAcquisitionEngine> logger,
    TimeProvider? timeProvider = null) : IAcquisitionEngine
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _cycleCancellation;
    private Task? _pollingLoop;
    private DateTimeOffset _lastOverrunWarning = DateTimeOffset.MinValue;

    public AcquisitionMode Mode => AcquisitionMode.Polling;

    public AcquisitionStatistics Statistics { get; } = new();

    public async Task StartAsync(ISession session, bool subscriptionsTransferred, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        await _gate.WaitAsync(ct);
        try
        {
            await StopLoopAsync();

            _cycleCancellation = new CancellationTokenSource();

            // The loop runs for the engine's lifetime, so it cannot be awaited here. It
            // can still fail before reaching the loop -- reading the server's operation
            // limits, say -- and an unobserved fault would stop acquisition silently and
            // then resurface much later out of StopAsync. Log it where it happens.
            _pollingLoop = RunAndLogAsync(session, _cycleCancellation.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunAndLogAsync(ISession session, CancellationToken ct)
    {
        try
        {
            await RunAsync(session, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ordinary stop.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Polling stopped because it could not start. No values will be read.");
        }
    }

    private async Task RunAsync(ISession session, CancellationToken ct)
    {
        var acquisition = options.CurrentValue.Acquisition;
        var interval = TimeSpan.FromMilliseconds(acquisition.PollingIntervalMs);

        var nodeIds = registry.Tags.Where(t => t.UpstreamNodeId is not null).Select(t => t.UpstreamNodeId!).ToList();
        var tagIndexes = registry.Tags.Where(t => t.UpstreamNodeId is not null).Select(t => t.Index).ToList();

        if (nodeIds.Count == 0)
        {
            logger.LogWarning("No mirrored tag resolved to an upstream node, so polling has nothing to read.");
            return;
        }

        var serverLimit = await OpcUaValueReader.GetMaxNodesPerReadAsync(session, ct);
        var chunkSize = Math.Max(1, Math.Min(acquisition.MaxNodesPerRead, serverLimit));

        // Built once and reused every cycle. Slicing the tag list per chunk per cycle
        // would allocate and re-enumerate thousands of entries a second for a result that
        // never changes.
        var chunks = BuildChunks(nodeIds.Count, chunkSize)
            .Select(c => new PollChunk(
                nodeIds.GetRange(c.Offset, c.Count),
                tagIndexes.GetRange(c.Offset, c.Count)))
            .ToList();

        logger.LogInformation(
            "Polling {TagCount} tag(s) every {IntervalMs}ms in {ChunkCount} chunk(s) of up to {ChunkSize} " +
            "(server limit {ServerLimit}).",
            nodeIds.Count, acquisition.PollingIntervalMs, chunks.Count, chunkSize, serverLimit);

        if (acquisition.Deadband != AcquisitionDeadband.None)
        {
            // Inert rather than harmful, so it does not stop the service -- but an operator
            // who set it to spare the plant server should know it is not being spared.
            logger.LogWarning(
                "Bridge:Acquisition:Deadband is {Deadband}, which only applies in Subscription mode. " +
                "Polling reads every tag on every cycle, so no value is being filtered.",
                acquisition.Deadband);
        }

        using var timer = new PeriodicTimer(interval, _time);

        while (await SafeWaitAsync(timer, ct))
        {
            try
            {
                var elapsed = await RunCycleAsync(
                    session, chunks, chunkSize, acquisition.MaxConcurrentReads, interval, ct);

                // Cycles are never queued: running one to completion before waiting for
                // the next tick means a slow server simply gets polled less often, rather
                // than building an unbounded backlog. PeriodicTimer discards the ticks
                // that came due meanwhile, so count them here -- this is the number an
                // operator needs to see that the interval is too tight for the tag count.
                var missedTicks = (int)(elapsed.Ticks / interval.Ticks);
                if (missedTicks > 0)
                {
                    for (var i = 0; i < missedTicks; i++)
                        Statistics.RecordSkippedCycle();

                    WarnAboutOverrun(acquisition.PollingIntervalMs);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed cycle is not a failed engine: the connection manager owns
                // reconnection, and the next tick may well succeed.
                logger.LogWarning(ex, "A polling cycle failed.");
            }
        }
    }

    /// <summary>One read's worth of tags, prepared once and reused every cycle.</summary>
    private sealed record PollChunk(List<NodeId> NodeIds, List<int> TagIndexes);

    /// <returns>How long the cycle took, so the caller can tell whether it overran.</returns>
    private async Task<TimeSpan> RunCycleAsync(
        ISession session,
        IReadOnlyList<PollChunk> chunks,
        int chunkSize,
        int maxConcurrentReads,
        TimeSpan interval,
        CancellationToken ct)
    {
        // A hung read must not wedge the engine forever, so a cycle gets a hard ceiling
        // of a few intervals regardless of what the server does.
        using var cycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cycleCancellation.CancelAfter(CycleTimeout(interval));

        var started = Stopwatch.GetTimestamp();
        var received = 0;

        await Parallel.ForEachAsync(
            chunks,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, maxConcurrentReads),
                CancellationToken = cycleCancellation.Token
            },
            async (chunk, token) =>
            {
                var results = await OpcUaValueReader.ReadValuesAsync(session, chunk.NodeIds, chunkSize, token);

                for (var i = 0; i < results.Length; i++)
                {
                    values.Publish(chunk.TagIndexes[i], results[i]);
                    Interlocked.Increment(ref received);
                }
            });

        var elapsed = Stopwatch.GetElapsedTime(started);
        Statistics.RecordCycle(received, elapsed, _time.GetUtcNow());

        return elapsed;
    }

    /// <summary>A cycle may run long, but not indefinitely.</summary>
    internal static TimeSpan CycleTimeout(TimeSpan interval)
        => TimeSpan.FromMilliseconds(Math.Min(interval.TotalMilliseconds * 3, TimeSpan.FromSeconds(30).TotalMilliseconds));

    /// <summary>Splits the tag list into read-sized chunks.</summary>
    internal static IReadOnlyList<(int Offset, int Count)> BuildChunks(int total, int chunkSize)
    {
        var chunks = new List<(int, int)>();

        for (var offset = 0; offset < total; offset += chunkSize)
            chunks.Add((offset, Math.Min(chunkSize, total - offset)));

        return chunks;
    }

    private void WarnAboutOverrun(int pollingIntervalMs)
    {
        // Throttled: an overloaded server would otherwise fill the log with this and
        // bury whatever else went wrong.
        var now = _time.GetUtcNow();
        if (now - _lastOverrunWarning < TimeSpan.FromSeconds(30))
            return;

        _lastOverrunWarning = now;
        logger.LogWarning(
            "A polling cycle was still running when the next was due, so it was skipped " +
            "({SkippedCount} skipped so far; last cycle took {LastCycleMs:F0}ms against a {IntervalMs}ms interval). " +
            "Consider a longer interval, or subscription mode.",
            Statistics.CyclesSkipped, Statistics.LastCycleDuration.TotalMilliseconds, pollingIntervalMs);
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await StopLoopAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopLoopAsync()
    {
        if (_cycleCancellation is null)
            return;

        await _cycleCancellation.CancelAsync();

        if (_pollingLoop is not null)
        {
            try { await _pollingLoop; }
            catch (OperationCanceledException) { }
        }

        _cycleCancellation.Dispose();
        _cycleCancellation = null;
        _pollingLoop = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _gate.Dispose();
    }
}
