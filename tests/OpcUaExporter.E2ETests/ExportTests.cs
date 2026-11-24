using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace OpcUaExporter.E2ETests;

public sealed class ExportTests(E2EEnvironment env) : UiTestBase(env)
{
    [Fact]
    public async Task Export_with_last_values_writes_value_quality_and_timestamp()
    {
        await BrowseSimServerAsync();
        await ExpandAsync("Simulation", "Static");
        await SelectTagsAsync("Int32", "Double");

        await Page.GetByRole(AriaRole.Button, new() { Name = "Read Values" }).ClickAsync();
        await Expect(SelectedRow("Simulation/Static/Int32")).ToContainTextAsync("Good");

        var path = Path.Combine(Path.GetTempPath(), $"opcua-export-{Guid.NewGuid():N}.csv");
        try
        {
            await Page.Locator("input[placeholder='C:\\\\export\\\\tags.csv']").FillAsync(path);
            await Page.GetByLabel("Include last value, quality & timestamp").CheckAsync();
            await Page.GetByRole(AriaRole.Button, new() { Name = "Export Selected" }).ClickAsync();
            await Expect(Page.GetByText($"Exported to: {path}")).ToBeVisibleAsync();

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal("Display Name,Node ID,Data Type,Value,Quality,Timestamp", lines[0]);
            var int32 = Assert.Single(lines, l => l.Contains("Simulation/Static/Int32"));
            var fields = int32.Split(',');
            Assert.Equal(6, fields.Length);
            Assert.False(string.IsNullOrEmpty(fields[3]), int32);
            Assert.Contains("Good", fields[4]);
            Assert.False(string.IsNullOrEmpty(fields[5]), int32);
        }
        finally
        {
            File.Delete(path);
        }
        AssertNoPageErrors();
    }
}
