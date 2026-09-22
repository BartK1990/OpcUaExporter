using Opc.Ua.Client;
using OpcUaBridge.Configuration;

namespace OpcUaBridge.Acquisition;

/// <summary>How the acquisition side is performing, for the dashboard.</summary>
public sealed class AcquisitionStatistics
{
    private long _valuesReceived;
    private long _cyclesCompleted;
    private long _cyclesSkipped;
    private long _publishesReceived;

    // Written from the SDK's publish thread (one per subscription, concurrently) and from
    // polling cycles; read from the Blazor circuit. Kept as 64-bit primitives written with
    // Interlocked rather than a TimeSpan and a DateTimeOffset?, whose multi-word stores can
    // be read torn -- which would render a nonsense age on the dashboard.
    private long _lastCycleDurationTicks;
    private long _lastUpdateUtcTicks;

    /// <summary>Values delivered since the service started.</summary>
    public long ValuesReceived => Interlocked.Read(ref _valuesReceived);

    /// <summary>Publish responses or polling cycles that carried at least one value.</summary>
    public long CyclesCompleted => Interlocked.Read(ref _cyclesCompleted);

    /// <summary>
    /// Every publish response the upstream server has sent, including empty ones.
    /// </summary>
    /// <remarks>
    /// Counted separately from <see cref="CyclesCompleted"/> because the gap between them
    /// is diagnostic. A server that answers a publish request the instant it arrives --
    /// rather than holding it until the publishing interval elapses or data appears --
    /// puts the SDK into a request/response loop that costs a great deal of CPU and
    /// delivers nothing. That shows up here as thousands of publishes a second against a
    /// value rate of almost none, and is invisible in every other number on the dashboard.
    /// </remarks>
    public long PublishesReceived => Interlocked.Read(ref _publishesReceived);

    /// <summary>
    /// Polling cycles skipped because the previous one was still running.
    /// </summary>
    /// <remarks>
    /// The number an operator needs when deciding whether the polling interval is too
    /// tight for the tag count. Cycles are never queued, so this rising means the server
    /// cannot keep up, not that work is piling up somewhere.
    /// </remarks>
    public long CyclesSkipped => Interlocked.Read(ref _cyclesSkipped);

    public TimeSpan LastCycleDuration => TimeSpan.FromTicks(Interlocked.Read(ref _lastCycleDurationTicks));

    public DateTimeOffset? LastUpdateUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastUpdateUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void RecordCycle(int valueCount, TimeSpan duration, DateTimeOffset completedUtc)
    {
        Interlocked.Add(ref _valuesReceived, valueCount);
        Interlocked.Increment(ref _cyclesCompleted);
        Interlocked.Exchange(ref _lastCycleDurationTicks, duration.Ticks);
        Interlocked.Exchange(ref _lastUpdateUtcTicks, completedUtc.UtcTicks);
    }

    public void RecordSkippedCycle() => Interlocked.Increment(ref _cyclesSkipped);

    /// <summary>Records a publish response, whether or not it carried anything.</summary>
    public void RecordPublish() => Interlocked.Increment(ref _publishesReceived);
}

/// <summary>
/// Gets values out of the upstream server and into the value store.
/// </summary>
/// <remarks>
/// Two implementations, chosen by configuration: one lets the server report changes, the
/// other reads every tag on a timer. They differ enough in shape -- and in what they cost
/// the upstream server -- to be worth keeping as separate strategies rather than one class
/// with a mode flag running through it.
/// </remarks>
public interface IAcquisitionEngine : IAsyncDisposable
{
    AcquisitionMode Mode { get; }

    AcquisitionStatistics Statistics { get; }

    /// <summary>Begins acquiring on a session. Called again after every reconnect.</summary>
    /// <param name="subscriptionsTransferred">
    /// Whether the server preserved this engine's subscriptions across a reconnect. When
    /// true there is nothing to recreate.
    /// </param>
    Task StartAsync(ISession session, bool subscriptionsTransferred, CancellationToken ct);

    /// <summary>Stops acquiring, releasing anything held on the upstream server.</summary>
    Task StopAsync(CancellationToken ct);
}
