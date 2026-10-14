using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace OpcUaExporter.E2ETests;

public sealed class TrendChartTests(E2EEnvironment env) : UiTestBase(env)
{
    [Fact]
    public async Task Trending_shows_series_and_a_large_24h_clock()
    {
        await BrowseSimServerAsync();
        await ExpandAsync("Simulation", "Dynamic");
        await SelectTagsAsync("Counter", "Sine");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Trend All" }).ClickAsync();

        await Expect(Page.Locator(".chart-panel .badge")).ToHaveTextAsync("2 series");

        var clock = Page.Locator(".chart-clock");
        await Expect(clock).ToHaveTextAsync(new Regex(@"^([01]\d|2[0-3]):[0-5]\d:[0-5]\d$"));
        Assert.Equal("18px", await clock.EvaluateAsync<string>("e => getComputedStyle(e).fontSize"));

        // Ticks: the clock advances on its own.
        var first = await clock.TextContentAsync();
        await Expect(clock).Not.ToHaveTextAsync(first!, new() { Timeout = 5_000 });

        // Live values reach the legend.
        await Expect(Page.Locator(".chart-legend-panel")).ToContainTextAsync("Counter");
        AssertNoPageErrors();
    }

    [Fact]
    public async Task Chart_shows_the_same_latest_value_as_the_table_after_quick_writes()
    {
        await BrowseSimServerAsync();
        await ExpandAsync("Simulation", "Static");
        await SelectTagsAsync("Int32");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Trend All" }).ClickAsync();

        var row = SelectedRow("Simulation/Static/Int32");
        var nodeId = Regex.Match(await row.InnerTextAsync(), @"ns=\d+;s=Simulation/Static/Int32").Value;
        Assert.False(string.IsNullOrEmpty(nodeId), "Node id not shown in the row.");

        // Wait until the chart plots the current value (other tests share the sim server and may have written it).
        await Expect(Page.Locator(".chart-panel")).ToBeVisibleAsync();
        await WaitForChartValueAsync(nodeId, null);
        var first = Random.Shared.Next(10_000, 20_000);
        var second = first + 1;

        // Two writes well within a second: both changes reach the client in the same publish.
        var input = row.Locator("input.write-input");
        var write = row.Locator("button[title='Write value']");
        await input.FillAsync(first.ToString());
        await input.DispatchEventAsync("change");
        await write.ClickAsync();
        await input.FillAsync(second.ToString());
        await input.DispatchEventAsync("change");
        await write.ClickAsync();

        await Expect(row).ToContainTextAsync(second.ToString());
        // The table shows the second value; the chart must catch up to it too (the tag won't change again).
        await WaitForChartValueAsync(nodeId, second);
        AssertNoPageErrors();
    }

    /// <summary>Waits until the chart draws <paramref name="nodeId"/> at <paramref name="expected"/> (any value when null).</summary>
    private async Task WaitForChartValueAsync(string nodeId, double? expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        double? shown = null;
        while (DateTime.UtcNow < deadline)
        {
            shown = await Page.EvaluateAsync<double?>("id => { const v = window.trendChart.shownValue(id); return v === undefined ? null : v; }", nodeId);
            if (shown is not null && (expected is null || shown == expected))
                return;
            await Task.Delay(200);
        }
        Assert.Fail($"Chart shows {shown?.ToString() ?? "nothing"} for {nodeId}, expected {expected?.ToString() ?? "any value"}.");
    }
}
