using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace OpcUaExporter.E2ETests;

public sealed class TagBrowserTests(E2EEnvironment env) : UiTestBase(env)
{
    [Fact]
    public async Task Home_page_renders_without_errors()
    {
        await Expect(Page.GetByText("No Tags Loaded")).ToBeVisibleAsync();
        await Expect(Page.Locator("img.logo-icon")).ToHaveJSPropertyAsync("complete", true);
        Assert.True(await Page.Locator("img.logo-icon").EvaluateAsync<int>("img => img.naturalWidth") > 0, "Sidebar logo did not load.");
        AssertNoPageErrors();
    }

    [Fact]
    public async Task Browsing_the_server_fills_the_tree_and_selection_adds_rows()
    {
        await BrowseSimServerAsync();
        await ExpandAsync("Simulation", "Static");
        await SelectTagsAsync("Int32", "Double");

        await Expect(SelectedRow("Simulation/Static/Int32")).ToBeVisibleAsync();
        await Expect(SelectedRow("Simulation/Static/Double")).ToBeVisibleAsync();
        await Expect(Page.GetByText("2 tag(s) selected")).ToBeVisibleAsync();
        AssertNoPageErrors();
    }

    [Fact]
    public async Task Inline_write_of_an_Int32_round_trips_to_the_server()
    {
        await BrowseSimServerAsync();
        await ExpandAsync("Simulation", "Static");
        await SelectTagsAsync("Int32");

        var row = SelectedRow("Simulation/Static/Int32");
        await row.Locator("input.write-input").FillAsync("1234");
        await row.Locator("input.write-input").DispatchEventAsync("change");
        await row.Locator("button[title='Write value']").ClickAsync();

        await Page.GetByRole(AriaRole.Button, new() { Name = "Read Values" }).ClickAsync();
        await Expect(row).ToContainTextAsync("1234");
        AssertNoPageErrors();
    }
}
