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
            _pollingLoop = RunAsync(session, _cycleCancellation.Token);
        }
        finally
        {
            _gate.Release();
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

        logger.LogInformation(
            "Polling {TagCount} tag(s) every {IntervalMs}ms in chunks of {ChunkSize} (server limit {ServerLimit}).",
            nodeIds.Count, acquisition.PollingIntervalMs, chunkSize, serverLimit);

        using var timer = new PeriodicTimer(interval, _time);
        var cycleInFlight = 0;

        while (await SafeWaitAsync(timer, ct))
        {
            // Never queue a cycle. If the server cannot keep up, a queue turns a slow
            // server into an unbounded backlog and then an exhausted process; skipping
            // keeps the bridge serving the freshest values it can actually get.
            if (Interlocked.CompareExchange(ref cycleInFlight, 1, 0) != 0)
            {
                Statistics.RecordSkippedCycle();
                WarnAboutOverrun(acquisition.PollingIntervalMs);
                continue;
            }

            try
            {
                await RunCycleAsync(session, nodeIds, tagIndexes, chunkSize, acquisition.MaxConcurrentReads, interval, ct);
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
            finally
            {
                Volatile.Write(ref cycleInFlight, 0);
            }
        }
    }

    private async Task RunCycleAsync(
        ISession session,
        IReadOnlyList<NodeId> nodeIds,
        IReadOnlyList<int> tagIndexes,
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
        var chunks = BuildChunks(nodeIds.Count, chunkSize);
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
                var slice = nodeIds.Skip(chunk.Offset).Take(chunk.Count).ToList();
                var results = await OpcUaValueReader.ReadValuesAsync(session, slice, chunk.Count, token);

                for (var i = 0; i < results.Length; i++)
                {
                    values.Publish(tagIndexes[chunk.Offset + i], results[i]);
                    Interlocked.Increment(ref received);
                }
            });

        Statistics.RecordCycle(received, Stopwatch.GetElapsedTime(started), _time.GetUtcNow());
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
