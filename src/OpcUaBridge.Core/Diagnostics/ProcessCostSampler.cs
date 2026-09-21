using System.Diagnostics;

namespace OpcUaBridge.Diagnostics;

/// <summary>
/// Reports what the bridge process itself is costing, sampled between calls.
/// </summary>
/// <remarks>
/// <para>
/// A gateway that looks busy is usually one of two very different things: it is moving a
/// lot of values, or it is moving none and burning the CPU anyway. Telling those apart
/// from outside the process means attaching a profiler to a Windows service, which nobody
/// does. Reporting processor time beside the value rate on the dashboard answers it in a
/// glance instead.
/// </para>
/// <para>
/// Processor time is cumulative, so a percentage only means anything between two samples.
/// The first call therefore has nothing to compare against and reports a zero window.
/// </para>
/// </remarks>
public sealed class ProcessCostSampler(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();

    private DateTimeOffset _lastSampledAt;
    private TimeSpan _lastProcessorTime;
    private bool _primed;

    /// <summary>
    /// Processor time and memory since the previous call, or a zero-window sample on the first.
    /// </summary>
    public ProcessCost Sample()
    {
        using var process = Process.GetCurrentProcess();

        var now = _time.GetUtcNow();
        var processorTime = process.TotalProcessorTime;

        TimeSpan window;
        TimeSpan consumed;

        lock (_gate)
        {
            window = _primed ? now - _lastSampledAt : TimeSpan.Zero;
            consumed = _primed ? processorTime - _lastProcessorTime : TimeSpan.Zero;

            _lastSampledAt = now;
            _lastProcessorTime = processorTime;
            _primed = true;
        }

        // A machine resuming from sleep, or a clock corrected backwards, would otherwise
        // produce a percentage from a negative or absurdly small denominator. The window
        // is reported clamped too, so a caller deriving its own rate from it cannot be
        // handed a negative divisor either.
        if (window < TimeSpan.Zero)
            window = TimeSpan.Zero;

        var percentOfCore = window > TimeSpan.Zero && consumed >= TimeSpan.Zero
            ? consumed.TotalMilliseconds / window.TotalMilliseconds * 100
            : 0;

        var cores = Math.Max(1, Environment.ProcessorCount);

        return new ProcessCost(
            window,
            percentOfCore,
            percentOfCore / cores,
            process.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false),
            process.Threads.Count,
            GC.CollectionCount(0),
            GC.CollectionCount(2));
    }
}
