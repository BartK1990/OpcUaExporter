namespace OpcUaBridge.Diagnostics;

/// <summary>
/// Turns a running total into a rate averaged over a trailing window.
/// </summary>
/// <remarks>
/// <para>
/// A rate taken from the last tick alone is unreadable here. Values arrive one publish at
/// a time, so with a 2000ms publishing interval and a dashboard sampling every second, the
/// true rate alternates between zero and double -- and an operator reading it sees a
/// gateway that has stopped, twice a second.
/// </para>
/// <para>
/// Averaging over a window wide enough to contain several publishes fixes that without
/// smoothing away a real change: this is a mean over a fixed period, not a filter with a
/// tail, so a rate that genuinely drops to zero reads as zero within the window.
/// </para>
/// </remarks>
public sealed class RateWindow(TimeSpan window)
{
    private readonly Queue<(DateTimeOffset At, long Counter)> _samples = new();

    /// <summary>The averaging period. Samples older than this are discarded.</summary>
    public TimeSpan Window { get; } = window > TimeSpan.Zero ? window : TimeSpan.FromSeconds(1);

    /// <summary>The rate per second across the window, or zero until it holds two samples.</summary>
    public double PerSecond { get; private set; }

    /// <summary>
    /// Records the counter's current total and recomputes the rate.
    /// </summary>
    /// <remarks>
    /// The counter only ever rises, so a fall means the engine was rebuilt and its
    /// statistics started again. That is reported as zero for one sample rather than as a
    /// negative rate.
    /// </remarks>
    public void Add(long counter, DateTimeOffset at)
    {
        if (_samples.Count > 0 && counter < _samples.Last().Counter)
        {
            _samples.Clear();
            PerSecond = 0;
        }

        _samples.Enqueue((at, counter));

        // Keep one sample older than the window, so the span measured is the full window
        // rather than whatever happens to remain after trimming.
        while (_samples.Count > 2 && at - _samples.Peek().At > Window)
            _samples.Dequeue();

        if (_samples.Count < 2)
        {
            PerSecond = 0;
            return;
        }

        var oldest = _samples.Peek();
        var elapsed = (at - oldest.At).TotalSeconds;

        PerSecond = elapsed > 0 ? Math.Max(0, counter - oldest.Counter) / elapsed : 0;
    }
}
