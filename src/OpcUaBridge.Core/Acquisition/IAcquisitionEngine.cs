using Opc.Ua.Client;
using OpcUaBridge.Configuration;

namespace OpcUaBridge.Acquisition;

/// <summary>How the acquisition side is performing, for the dashboard.</summary>
public sealed class AcquisitionStatistics
{
    private long _valuesReceived;
    private long _cyclesCompleted;
    private long _cyclesSkipped;

    /// <summary>Values delivered since the service started.</summary>
    public long ValuesReceived => Interlocked.Read(ref _valuesReceived);

    /// <summary>Publish responses or polling cycles completed.</summary>
    public long CyclesCompleted => Interlocked.Read(ref _cyclesCompleted);

    /// <summary>
    /// Polling cycles skipped because the previous one was still running.
    /// </summary>
    /// <remarks>
    /// The number an operator needs when deciding whether the polling interval is too
    /// tight for the tag count. Cycles are never queued, so this rising means the server
    /// cannot keep up, not that work is piling up somewhere.
    /// </remarks>
    public long CyclesSkipped => Interlocked.Read(ref _cyclesSkipped);

    public TimeSpan LastCycleDuration { get; private set; }

    public DateTimeOffset? LastUpdateUtc { get; private set; }

    public void RecordCycle(int valueCount, TimeSpan duration, DateTimeOffset completedUtc)
    {
        Interlocked.Add(ref _valuesReceived, valueCount);
        Interlocked.Increment(ref _cyclesCompleted);
        LastCycleDuration = duration;
        LastUpdateUtc = completedUtc;
    }

    public void RecordSkippedCycle() => Interlocked.Increment(ref _cyclesSkipped);
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
