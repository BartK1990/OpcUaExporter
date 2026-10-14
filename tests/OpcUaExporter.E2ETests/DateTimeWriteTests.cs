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
        // Shown in the same fixed 24h format it was typed in, whatever the machine's culture.
        await Expect(row).ToContainTextAsync("2026-10-14 17:45:30");
        AssertNoPageErrors();
    }

    [Fact]
    public async Task DateTime_values_are_shown_as_24h_text_in_the_table_and_legend()
    {
        await BrowseSimServerAsync();
        await ExpandAsync("Simulation", "Dynamic");
        await SelectTagsAsync("Clock");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Trend All" }).ClickAsync();

        var format = new System.Text.RegularExpressions.Regex(@"\b\d{4}-\d{2}-\d{2} ([01]\d|2[0-3]):[0-5]\d:[0-5]\d\b");
        var row = SelectedRow("Simulation/Dynamic/Clock");

        var valueCell = row.Locator("td").Nth(await ValueColumnIndexAsync());
        await Expect(valueCell).ToHaveTextAsync(format);
        // The column may be narrower than the value; the full text is on hover.
        await Expect(valueCell).ToHaveAttributeAsync("title", format);
        await Expect(Page.Locator(".chart-legend-panel .legend-value").First).ToHaveTextAsync(format);

        await Expect(row).Not.ToContainTextAsync(new System.Text.RegularExpressions.Regex("AM|PM"));
        AssertNoPageErrors();
    }

    private async Task<int> ValueColumnIndexAsync()
    {
        var headers = await Page.Locator("table:has(tr:has-text(\"Simulation/\")) thead th").AllInnerTextsAsync();
        var index = headers.ToList().FindIndex(h => h.Trim().Equals("Value", StringComparison.OrdinalIgnoreCase));
        Assert.True(index >= 0, $"No Value column among: {string.Join(", ", headers)}");
        return index;
    }
}
