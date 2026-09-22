using OpcUaBridge.Diagnostics;
using Xunit;

namespace OpcUaBridge.Tests;

public sealed class RateWindowTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PerSecond_BeforeASecondSample_IsZero()
    {
        var rate = new RateWindow(TimeSpan.FromSeconds(10));
        rate.Add(100, Start);

        // One reading of a running total says nothing about a rate.
        Assert.Equal(0, rate.PerSecond);
    }

    [Fact]
    public void PerSecond_AveragesAcrossTheWindowRatherThanTheLastTick()
    {
        // The case this exists for: a 2000ms publishing interval sampled every second, so
        // the counter moves on every other tick. Per-tick it reads 0, 200, 0, 200; the
        // true rate is 100/s throughout, and that is what an operator has to see.
        var rate = new RateWindow(TimeSpan.FromSeconds(10));
        var counter = 0L;

        for (var second = 0; second < 8; second++)
        {
            if (second % 2 == 1)
                counter += 200;

            rate.Add(counter, Start.AddSeconds(second));
        }

        Assert.InRange(rate.PerSecond, 85, 115);
    }

    [Fact]
    public void PerSecond_WhenTheRateDropsToZero_ReadsZeroWithinTheWindow()
    {
        var rate = new RateWindow(TimeSpan.FromSeconds(4));

        for (var second = 0; second < 5; second++)
            rate.Add(second * 100, Start.AddSeconds(second));

        Assert.InRange(rate.PerSecond, 90, 110);

        // A mean over a fixed period, not a filter with a tail: once the window holds only
        // the flat stretch, the rate is zero and stays zero.
        for (var second = 5; second < 12; second++)
            rate.Add(400, Start.AddSeconds(second));

        Assert.Equal(0, rate.PerSecond);
    }

    [Fact]
    public void Add_WhenTheCounterRestarts_DoesNotReportANegativeRate()
    {
        // The acquisition engine is rebuilt on a reconnect and its statistics start again.
        var rate = new RateWindow(TimeSpan.FromSeconds(10));
        rate.Add(5_000, Start);
        rate.Add(6_000, Start.AddSeconds(1));

        rate.Add(0, Start.AddSeconds(2));

        Assert.Equal(0, rate.PerSecond);

        rate.Add(300, Start.AddSeconds(5));
        Assert.InRange(rate.PerSecond, 99, 101);
    }

    [Fact]
    public void Window_RejectsANonPositivePeriod()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), new RateWindow(TimeSpan.Zero).Window);
        Assert.Equal(TimeSpan.FromSeconds(1), new RateWindow(TimeSpan.FromSeconds(-5)).Window);
    }
}
