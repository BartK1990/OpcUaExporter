using OpcUaBridge.Acquisition;
using Xunit;

namespace OpcUaBridge.Tests;

public class PollingChunkingTests
{
    [Theory]
    [InlineData(0, 1000, 0)]
    [InlineData(1, 1000, 1)]
    [InlineData(1000, 1000, 1)]
    [InlineData(1001, 1000, 2)]
    [InlineData(5000, 1000, 5)]
    public void BuildChunks_SplitsTheTagListIntoReadSizedRequests(int total, int chunkSize, int expectedChunks)
    {
        var chunks = PollingAcquisitionEngine.BuildChunks(total, chunkSize);

        Assert.Equal(expectedChunks, chunks.Count);
    }

    [Fact]
    public void BuildChunks_CoversEveryTagExactlyOnce()
    {
        var chunks = PollingAcquisitionEngine.BuildChunks(total: 5000, chunkSize: 1000);

        Assert.Equal(5000, chunks.Sum(c => c.Count));
        Assert.Equal(0, chunks[0].Offset);
        Assert.Equal(4000, chunks[^1].Offset);
    }

    [Fact]
    public void BuildChunks_MakesTheFinalChunkShorterRatherThanOverrunning()
    {
        var chunks = PollingAcquisitionEngine.BuildChunks(total: 2500, chunkSize: 1000);

        Assert.Equal([1000, 1000, 500], chunks.Select(c => c.Count));
    }

    [Theory]
    [InlineData(1000, 3000)]
    [InlineData(5000, 15000)]
    [InlineData(20000, 30000)]
    public void CycleTimeout_AllowsARunLongCycleButNotAHungOne(int intervalMs, int expectedTimeoutMs)
    {
        // A read that never returns must not wedge the engine forever, but a cycle that
        // is merely slower than its interval should be allowed to finish.
        var timeout = PollingAcquisitionEngine.CycleTimeout(TimeSpan.FromMilliseconds(intervalMs));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedTimeoutMs), timeout);
    }
}

public class AcquisitionStatisticsTests
{
    [Fact]
    public void RecordCycle_AccumulatesValuesAndCycles()
    {
        var statistics = new AcquisitionStatistics();

        statistics.RecordCycle(100, TimeSpan.FromMilliseconds(40), DateTimeOffset.UnixEpoch);
        statistics.RecordCycle(50, TimeSpan.FromMilliseconds(60), DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Equal(150, statistics.ValuesReceived);
        Assert.Equal(2, statistics.CyclesCompleted);
        Assert.Equal(TimeSpan.FromMilliseconds(60), statistics.LastCycleDuration);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1), statistics.LastUpdateUtc);
    }

    [Fact]
    public void RecordPublish_CountsPublishesThatCarriedNothing()
    {
        var statistics = new AcquisitionStatistics();

        statistics.RecordPublish();
        statistics.RecordPublish();

        // The gap between publishes and cycles is the diagnostic. A server answering a
        // publish request the instant it arrives, instead of holding it until data
        // appears, shows up here and in no other figure on the dashboard.
        Assert.Equal(2, statistics.PublishesReceived);
        Assert.Equal(0, statistics.CyclesCompleted);
        Assert.Equal(0, statistics.ValuesReceived);
    }

    [Fact]
    public void RecordCycle_DoesNotCountAsAPublish()
    {
        // A polling cycle has no publish behind it at all, so it must not inflate a rate
        // whose whole purpose is to describe the subscription pipeline.
        var statistics = new AcquisitionStatistics();

        statistics.RecordCycle(7, TimeSpan.FromMilliseconds(12), DateTimeOffset.UnixEpoch);

        Assert.Equal(0, statistics.PublishesReceived);
        Assert.Equal(1, statistics.CyclesCompleted);
    }

    [Fact]
    public void RecordSkippedCycle_IsCountedSeparatelyFromCompletedOnes()
    {
        // An operator reads these two together to decide whether the polling interval is
        // too tight for the tag count.
        var statistics = new AcquisitionStatistics();

        statistics.RecordCycle(10, TimeSpan.Zero, DateTimeOffset.UnixEpoch);
        statistics.RecordSkippedCycle();
        statistics.RecordSkippedCycle();

        Assert.Equal(1, statistics.CyclesCompleted);
        Assert.Equal(2, statistics.CyclesSkipped);
    }

    [Fact]
    public void Statistics_StartEmpty()
    {
        var statistics = new AcquisitionStatistics();

        Assert.Equal(0, statistics.ValuesReceived);
        Assert.Equal(0, statistics.CyclesCompleted);
        Assert.Equal(0, statistics.CyclesSkipped);
        Assert.Null(statistics.LastUpdateUtc);
    }
}
