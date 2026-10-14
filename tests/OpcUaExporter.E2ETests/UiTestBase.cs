using Microsoft.Playwright;

namespace OpcUaExporter.E2ETests;

/// <summary>
/// Per test: a fresh web app and browser page; collects page errors so every
/// test also asserts the UI raised none.
/// </summary>
[Collection(E2ECollection.Name)]
public abstract class UiTestBase(E2EEnvironment env) : IAsyncLifetime
{
    protected E2EEnvironment Env { get; } = env;
    protected RunningApp App { get; private set; } = null!;
    protected IPage Page { get; private set; } = null!;
    protected List<string> PageErrors { get; } = [];

    public async Task InitializeAsync()
    {
        App = await Env.StartAppAsync();
        Page = await Env.Browser.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = 1600, Height = 1000 } });
        Page.SetDefaultTimeout(15_000);
        Page.PageError += (_, error) => PageErrors.Add(error);
        Page.Response += (_, response) =>
        {
            if (response.Status >= 400)
                PageErrors.Add($"HTTP {response.Status} {response.Url}");
        };

        await Page.GotoAsync(App.BaseUrl);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Browse Tags" }).WaitForAsync();
    }

    public async Task DisposeAsync()
    {
        await Page.CloseAsync();
        await App.DisposeAsync();
    }

    protected ILocator TreeRow(string displayName) =>
        Page.Locator($".tag-row:has(> .tag-name:text-is(\"{displayName}\"))").First;

    protected ILocator SelectedRow(string nodeIdSuffix) =>
        Page.Locator($"tr:has-text(\"{nodeIdSuffix}\")").First;

    /// <summary>Points the connection form at the simulation server and browses it.</summary>
    protected async Task BrowseSimServerAsync()
    {
        var endpoint = Page.Locator(".sidebar input").Nth(1);
        await endpoint.FillAsync(Env.Sim.EndpointUrl);
        await endpoint.DispatchEventAsync("change");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Browse Tags" }).ClickAsync();
        await TreeRow("Simulation").WaitForAsync(new() { Timeout = 30_000 });
    }

    protected async Task ExpandAsync(params string[] path)
    {
        foreach (var name in path)
        {
            await TreeRow(name).Locator(".chevron").ClickAsync();
        }
    }

    protected async Task SelectTagsAsync(params string[] names)
    {
        foreach (var name in names)
            await TreeRow(name).Locator("input.tag-check").CheckAsync();
    }

    protected void AssertNoPageErrors() => Assert.Empty(PageErrors);
}
