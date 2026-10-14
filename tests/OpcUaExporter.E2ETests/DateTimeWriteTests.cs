using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace OpcUaExporter.E2ETests;

/// <summary>The DateTime write field: fixed 24-hour "YYYY-MM-DD HH:MM:SS" text, no locale-dependent picker.</summary>
public sealed class DateTimeWriteTests(E2EEnvironment env) : UiTestBase(env)
{
    private async Task<ILocator> OpenDateTimeRowAsync()
    {
        await BrowseSimServerAsync();
        await ExpandAsync("Simulation", "Static");
        await SelectTagsAsync("DateTime");
        var row = SelectedRow("Simulation/Static/DateTime");
        await Expect(row).ToBeVisibleAsync();
        return row;
    }

    [Fact]
    public async Task Field_is_plain_text_with_24h_placeholder_and_fits_a_full_value()
    {
        var row = await OpenDateTimeRowAsync();
        var input = row.Locator("input.write-input-datetime");

        await Expect(input).ToHaveAttributeAsync("type", "text");
        await Expect(input).ToHaveAttributeAsync("placeholder", "YYYY-MM-DD HH:MM:SS");

        await input.FillAsync("2026-10-14 17:45:30");
        await Page.Locator("body").ClickAsync(new() { Position = new Position { X = 5, Y = 5 } });
        var widths = await input.EvaluateAsync<int[]>("e => [e.clientWidth, e.scrollWidth]");
        Assert.True(widths[1] <= widths[0], $"Value is clipped: scrollWidth {widths[1]} > clientWidth {widths[0]}.");
        AssertNoPageErrors();
    }

    [Theory]
    [InlineData("2026-13-01 10:00:00")]
    [InlineData("2026-10-14 25:00:00")]
    [InlineData("10/14/2026 5:45 PM")]
    [InlineData("2026-10-14")]
    public async Task Invalid_text_is_flagged_and_cannot_be_written(string text)
    {
        var row = await OpenDateTimeRowAsync();
        var input = row.Locator("input.write-input-datetime");

        await input.FillAsync(text);

        await Expect(input).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bwrite-input-invalid\b"));
        await Expect(row.Locator("button[title='Write value']")).ToBeDisabledAsync();
    }

    [Fact]
    public async Task Valid_24h_value_is_written_to_the_server()
    {
        var row = await OpenDateTimeRowAsync();
        var input = row.Locator("input.write-input-datetime");

        await input.FillAsync("2026-10-14 17:45:30");
        await Expect(input).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bwrite-input-invalid\b"));
        await row.Locator("button[title='Write value']").ClickAsync();

        await Page.GetByRole(AriaRole.Button, new() { Name = "Read Values" }).ClickAsync();
        // The value column formats with the current culture; only the 24h time part is culture-independent.
        await Expect(row).ToContainTextAsync("17:45:30");
        AssertNoPageErrors();
    }
}
