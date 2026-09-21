using Microsoft.Extensions.Time.Testing;
using OpcUaBridge.Diagnostics;
using Xunit;

namespace OpcUaBridge.Tests;

public sealed class ProcessCostSamplerTests
{
    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Sample_FirstCall_ReportsAZeroWindowRatherThanTimeSinceProcessStart()
    {
        var sampler = new ProcessCostSampler(Clock());

        var cost = sampler.Sample();

        // Processor time is cumulative, so the first call has nothing to subtract from.
        // Reporting it against the process's whole lifetime would read as a flat zero on a
        // service that has been up for a week, whatever it is doing now.
        Assert.Equal(TimeSpan.Zero, cost.Window);
        Assert.Equal(0, cost.CpuPercentOfCore);
        Assert.Equal(0, cost.CpuPercentOfMachine);
    }

    [Fact]
    public void Sample_SecondCall_ReportsTheWindowBetweenTheTwo()
    {
        var time = Clock();
        var sampler = new ProcessCostSampler(time);
        sampler.Sample();

        time.Advance(TimeSpan.FromSeconds(2));
        var cost = sampler.Sample();

        Assert.Equal(TimeSpan.FromSeconds(2), cost.Window);
        Assert.True(cost.CpuPercentOfCore >= 0);
    }

    [Fact]
    public void Sample_ScalesTheMachineFigureByTheCoreCount()
    {
        var time = Clock();
        var sampler = new ProcessCostSampler(time);
        sampler.Sample();

        // Busy work, so the percentage is not trivially zero on both sides.
        var spin = 0L;
        for (var i = 0; i < 20_000_000; i++)
            spin += i;

        Assert.True(spin > 0);

        time.Advance(TimeSpan.FromSeconds(1));
        var cost = sampler.Sample();

        Assert.Equal(cost.CpuPercentOfCore / Environment.ProcessorCount, cost.CpuPercentOfMachine, 6);
    }

    [Fact]
    public void Sample_ClockSteppingBack_DoesNotReportANegativePercentage()
    {
        // FakeTimeProvider refuses to move backwards, which is the case under test.
        var clock = new SteppingClock(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
        var sampler = new ProcessCostSampler(clock);
        sampler.Sample();

        // A machine resuming from sleep, or an NTP correction, must not turn into a
        // nonsense figure on the dashboard.
        clock.Now -= TimeSpan.FromSeconds(5);
        var cost = sampler.Sample();

        Assert.Equal(TimeSpan.Zero, cost.Window);
        Assert.Equal(0, cost.CpuPercentOfCore);
        Assert.Equal(0, cost.CpuPercentOfMachine);
    }

    [Fact]
    public void Sample_ReportsMemoryAndThreadsOfThisProcess()
    {
        var cost = new ProcessCostSampler(Clock()).Sample();

        Assert.True(cost.WorkingSetBytes > 0);
        Assert.True(cost.ManagedHeapBytes > 0);
        Assert.True(cost.ThreadCount > 0);
        Assert.True(cost.Gen0Collections >= 0);
    }

    /// <summary>A clock that will move backwards, which <c>FakeTimeProvider</c> will not.</summary>
    private sealed class SteppingClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
