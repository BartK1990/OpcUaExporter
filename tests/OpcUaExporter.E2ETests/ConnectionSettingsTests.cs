using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace OpcUaExporter.E2ETests;

/// <summary>Username/password authentication: some servers accept a user with an empty password.</summary>
public sealed class ConnectionSettingsTests(E2EEnvironment env) : UiTestBase(env)
{
    [Fact]
    public async Task Username_with_empty_password_passes_validation()
    {
        await Page.GotoAsync($"{App.BaseUrl}/connection-settings");
        var auth = Page.Locator(".settings-panel:has(.panel-title:text-is(\"Authentication\"))");
        await auth.Locator("select").SelectOptionAsync("UsernamePassword");

        var username = auth.Locator("input[autocomplete='username']");
        var password = auth.Locator("input[type='password']");
        await Expect(password).ToBeVisibleAsync();
        await Expect(auth.Locator("label:text-is(\"Password (optional)\")")).ToBeVisibleAsync();

        // An empty username is still rejected...
        await username.FillAsync("");
        await Expect(Page.GetByText("Username is required for UsernamePassword authentication.")).ToBeVisibleAsync();

        // ...but a username with no password is a valid identity.
        await username.FillAsync("operator");
        await password.FillAsync("");

        await Expect(Page.Locator(".panel-title:text-is(\"Validation Warnings\")")).ToHaveCountAsync(0);
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Test Connection" })).ToBeEnabledAsync();
        AssertNoPageErrors();
    }
}
