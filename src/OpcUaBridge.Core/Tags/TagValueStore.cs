using System.Collections.Concurrent;
using Opc.Ua;

namespace OpcUaBridge.Tags;

/// <summary>
/// The last value seen for every mirrored tag.
/// </summary>
/// <remarks>
/// <para>
/// This sits between the OPC UA SDK's publish thread and the bridge's own server, and at a
/// few thousand tags it is the hottest code in the process. Two properties matter:
/// </para>
/// <para>
/// <b>Publishing never blocks and never allocates.</b> It stores the reference to the
/// <see cref="DataValue"/> the SDK already built and sets a dirty flag. Blocking here
/// would stall the SDK's publish pipeline and starve the upstream feed.
/// </para>
/// <para>
/// <b>Updates coalesce.</b> Ten values for one tag arriving before the applier drains
/// produce one apply of the newest. A gateway mirrors the current value, so superseded
/// ones are simply work nobody asked for.
/// </para>
/// </remarks>
public sealed class TagValueStore(int tagCount, TimeProvider? timeProvider = null)
{
    private readonly DataValue?[] _values = new DataValue?[tagCount];
    private readonly int[] _dirty = new int[tagCount];
    private readonly ConcurrentQueue<int> _dirtyQueue = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Number of tags this store holds a slot for.</summary>
    public int Count => _values.Length;

    /// <summary>Tags changed since the last drain.</summary>
    public int PendingCount => _dirtyQueue.Count;

    /// <summary>
    /// Records a new value for a tag. Safe to call from the SDK's publish thread.
    /// </summary>
    public void Publish(int tagIndex, DataValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        Volatile.Write(ref _values[tagIndex], value);

        // Only enqueue on the transition from clean to dirty. Without this a tag updating
        // faster than the applier drains would grow the queue without bound.
        if (Interlocked.Exchange(ref _dirty[tagIndex], 1) == 0)
            _dirtyQueue.Enqueue(tagIndex);
    }

    /// <summary>The current value of a tag, or null if none has arrived yet.</summary>
    public DataValue? Read(int tagIndex) => Volatile.Read(ref _values[tagIndex]);

    /// <summary>
    /// Takes up to <paramref name="maxCount"/> changed tag indexes, clearing their dirty flags.
    /// </summary>
    /// <remarks>
    /// The caller applies these to the mirror's nodes under a single node-manager lock.
    /// Draining in batches is what keeps the bridge from taking and releasing that lock
    /// thousands of times a second, contending with every downstream browse and read.
    /// </remarks>
    public int DrainChanged(int[] buffer, int maxCount)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var count = 0;
        var limit = Math.Min(maxCount, buffer.Length);

        while (count < limit && _dirtyQueue.TryDequeue(out var tagIndex))
        {
            Volatile.Write(ref _dirty[tagIndex], 0);
            buffer[count++] = tagIndex;
        }

        return count;
    }

    /// <summary>
    /// Marks every value as no longer trustworthy, because the upstream link is down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Values are kept, not discarded. <c>UncertainLastUsableValue</c> is precisely the
    /// OPC UA status for "this is the last value I could get, but I cannot vouch for it
    /// now" -- a correct client keeps displaying it and flags it, which is what lets the
    /// downstream application stay connected through an outage instead of dropping its
    /// session.
    /// </para>
    /// <para>
    /// The source timestamp is preserved so the true age of the data stays visible; only
    /// the server timestamp advances. After <paramref name="staleAfter"/> a value that was
    /// already uncertain degrades to <c>BadNoCommunication</c>, so a bridge left
    /// disconnected overnight does not keep offering day-old numbers as merely uncertain.
    /// </para>
    /// </remarks>
    /// <returns>How many tags changed status.</returns>
    public int MarkStale(TimeSpan staleAfter)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var changed = 0;

        for (var i = 0; i < _values.Length; i++)
        {
            var current = Volatile.Read(ref _values[i]);
            if (current is null)
                continue;

            var age = now - current.SourceTimestamp;
            var degraded = staleAfter > TimeSpan.Zero && age > staleAfter;

            var target = degraded
                ? StatusCodes.BadNoCommunication
                : StatusCodes.UncertainLastUsableValue;

            if (current.StatusCode.Code == target)
                continue;

            var stale = new DataValue(current.WrappedValue)
            {
                StatusCode = target,
                SourceTimestamp = current.SourceTimestamp,
                ServerTimestamp = now
            };

            Publish(i, stale);
            changed++;
        }

        return changed;
    }
}
