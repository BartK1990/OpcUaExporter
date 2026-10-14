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
}
