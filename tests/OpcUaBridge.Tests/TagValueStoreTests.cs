using Microsoft.Extensions.Time.Testing;
using Opc.Ua;
using OpcUaBridge.Tags;
using Xunit;

namespace OpcUaBridge.Tests;

public class TagValueStoreTests
{
    private static DataValue Good(object value, DateTime sourceTimestamp) =>
        new(new Variant(value)) { StatusCode = StatusCodes.Good, SourceTimestamp = sourceTimestamp };

    [Fact]
    public void Publish_ThenRead_ReturnsTheValue()
    {
        var store = new TagValueStore(3);
        var value = Good(42.0, DateTime.UtcNow);

        store.Publish(1, value);

        Assert.Same(value, store.Read(1));
        Assert.Null(store.Read(0));
    }

    [Fact]
    public void DrainChanged_ReturnsEachChangedTagOnce()
    {
        var store = new TagValueStore(4);
        store.Publish(0, Good(1.0, DateTime.UtcNow));
        store.Publish(3, Good(2.0, DateTime.UtcNow));

        var buffer = new int[8];
        var count = store.DrainChanged(buffer, buffer.Length);

        Assert.Equal(2, count);
        Assert.Equal([0, 3], buffer.Take(count).Order());
        Assert.Equal(0, store.DrainChanged(buffer, buffer.Length));
    }

    [Fact]
    public void Publish_CoalescesRepeatedUpdatesToOneDrainEntry()
    {
        // A tag updating faster than the applier drains must not grow the queue: the
        // mirror only ever serves the newest value, so superseded ones are dead work.
        var store = new TagValueStore(1);
        store.Publish(0, Good(1.0, DateTime.UtcNow));
        store.Publish(0, Good(2.0, DateTime.UtcNow));
        var newest = Good(3.0, DateTime.UtcNow);
        store.Publish(0, newest);

        var buffer = new int[4];

        Assert.Equal(1, store.DrainChanged(buffer, buffer.Length));
        Assert.Same(newest, store.Read(0));
    }

    [Fact]
    public void DrainChanged_HonoursTheBatchLimitAndLeavesTheRestQueued()
    {
        var store = new TagValueStore(5);
        for (var i = 0; i < 5; i++)
            store.Publish(i, Good(i, DateTime.UtcNow));

        var buffer = new int[5];

        Assert.Equal(2, store.DrainChanged(buffer, maxCount: 2));
        Assert.Equal(3, store.PendingCount);
    }

    [Fact]
    public void MarkStale_KeepsTheValueButFlagsItUncertain()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
        var store = new TagValueStore(1, time);
        var captured = time.GetUtcNow().UtcDateTime;
        store.Publish(0, Good(42.0, captured));
        store.DrainChanged(new int[1], 1);

        time.Advance(TimeSpan.FromSeconds(30));
        var changed = store.MarkStale(TimeSpan.FromMinutes(5));

        var stale = store.Read(0)!;
        Assert.Equal(1, changed);
        Assert.Equal(StatusCodes.UncertainLastUsableValue, stale.StatusCode.Code);
        Assert.Equal(42.0, stale.Value);
        // The source timestamp is what tells a downstream client how old the data really is.
        Assert.Equal(captured, stale.SourceTimestamp);
        Assert.Equal(time.GetUtcNow().UtcDateTime, stale.ServerTimestamp);
    }

    [Fact]
    public void MarkStale_DegradesToBadOnceTheValueIsOlderThanTheGracePeriod()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
        var store = new TagValueStore(1, time);
        store.Publish(0, Good(42.0, time.GetUtcNow().UtcDateTime));

        time.Advance(TimeSpan.FromMinutes(10));
        store.MarkStale(TimeSpan.FromMinutes(5));

        Assert.Equal(StatusCodes.BadNoCommunication, store.Read(0)!.StatusCode.Code);
    }

    [Fact]
    public void MarkStale_IsIdempotent()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
        var store = new TagValueStore(1, time);
        store.Publish(0, Good(42.0, time.GetUtcNow().UtcDateTime));

        Assert.Equal(1, store.MarkStale(TimeSpan.FromMinutes(5)));
        Assert.Equal(0, store.MarkStale(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void MarkStale_SkipsTagsThatNeverReceivedAValue()
    {
        var store = new TagValueStore(3);

        Assert.Equal(0, store.MarkStale(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Publish_DoesNotAllocateOnTheHotPath()
    {
        // This runs on the SDK's publish thread for every value of every tag. Allocating
        // here is what turns a few thousand tags into constant GC pressure.
        var store = new TagValueStore(1);
        var value = Good(1.0, DateTime.UtcNow);
        store.Publish(0, value);                      // prime the dirty flag and the queue segment
        store.DrainChanged(new int[1], 1);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            store.Publish(0, value);                  // already dirty after the first: pure interlocked writes
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }
}
