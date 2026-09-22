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
    private long _lastAllocatedBytes;
    private long _lastCompletedWorkItems;
    private bool _primed;

    /// <summary>Processor time per OS thread at the previous sample, so the busiest can be found.</summary>
    private Dictionary<int, TimeSpan> _lastThreadTimes = [];

    /// <summary>
    /// Processor time and memory since the previous call, or a zero-window sample on the first.
    /// </summary>
    public ProcessCost Sample()
    {
        using var process = Process.GetCurrentProcess();

        var now = _time.GetUtcNow();
        var processorTime = process.TotalProcessorTime;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        var completedWorkItems = ThreadPool.CompletedWorkItemCount;

        TimeSpan window;
        TimeSpan consumed;
        long allocated;
        long workItems;
        TimeSpan busiestThread;

        lock (_gate)
        {
            window = _primed ? now - _lastSampledAt : TimeSpan.Zero;
            consumed = _primed ? processorTime - _lastProcessorTime : TimeSpan.Zero;
            allocated = _primed ? allocatedBytes - _lastAllocatedBytes : 0;
            workItems = _primed ? completedWorkItems - _lastCompletedWorkItems : 0;
            busiestThread = BusiestThreadSince(process, _primed);

            _lastSampledAt = now;
            _lastProcessorTime = processorTime;
            _lastAllocatedBytes = allocatedBytes;
            _lastCompletedWorkItems = completedWorkItems;
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
        var seconds = window.TotalSeconds;

        return new ProcessCost(
            window,
            percentOfCore,
            percentOfCore / cores,
            process.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false),
            process.Threads.Count,
            GC.CollectionCount(0),
            GC.CollectionCount(2),
            seconds > 0 ? Math.Max(0, allocated) / seconds : 0,
            seconds > 0 ? Math.Max(0, workItems) / seconds : 0,
            seconds > 0 && busiestThread > TimeSpan.Zero
                ? busiestThread.TotalMilliseconds / window.TotalMilliseconds * 100
                : 0);
    }

    /// <summary>
    /// Processor time of the hottest single thread since the previous sample.
    /// </summary>
    /// <remarks>
    /// This is what separates a spinning loop from honest work: one thread at nearly a
    /// full core is a loop that is not yielding, while the same total spread across the
    /// pool is the gateway doing what it was asked to. Per-thread times are not reportable
    /// on every platform, and a diagnostic figure is never worth a fault, so a failure
    /// here simply reports zero. Must be called under <c>_gate</c>.
    /// </remarks>
    private TimeSpan BusiestThreadSince(Process process, bool primed)
    {
        var current = new Dictionary<int, TimeSpan>(_lastThreadTimes.Count);
        var busiest = TimeSpan.Zero;

        try
        {
            foreach (ProcessThread thread in process.Threads)
            {
                TimeSpan total;
                try { total = thread.TotalProcessorTime; }
                catch { continue; } // The thread exited between the listing and the read.

                current[thread.Id] = total;

                // A thread started since the last sample has no baseline, so it is not a
                // candidate: counting its whole lifetime would look like a spin.
                if (primed && _lastThreadTimes.TryGetValue(thread.Id, out var previous))
                {
                    var delta = total - previous;
                    if (delta > busiest)
                        busiest = delta;
                }
            }
        }
        catch (PlatformNotSupportedException)
        {
            return TimeSpan.Zero;
        }
        catch (InvalidOperationException)
        {
            return TimeSpan.Zero;
        }

        _lastThreadTimes = current;
        return busiest;
    }
}
