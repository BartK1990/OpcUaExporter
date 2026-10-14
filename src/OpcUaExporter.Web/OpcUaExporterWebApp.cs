using OpcUaExporter.Abstractions;
using OpcUaExporter.UI;
using OpcUaExporter.Web.Components;
using OpcUaExporter.Web.Services;

namespace OpcUaExporter.Web;

/// <summary>
/// Builds the browser host. Kept out of <c>Program</c> so end-to-end tests can
/// start the real app (Kestrel on a free port) in-process.
/// </summary>
public static class OpcUaExporterWebApp
{
    public static WebApplication Build(string[] args, string? environmentName = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            EnvironmentName = environmentName,
            // Fixed so static web assets (_content/OpcUaExporter.UI/...) resolve
            // even when the entry assembly is a test host.
            ApplicationName = typeof(OpcUaExporterWebApp).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory
        });

        // Serve _content/... straight from the referenced projects in every
        // environment, not only Development; this is a local dev/test host.
        builder.WebHost.UseStaticWebAssets();

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // Platform services first: AddOpcUaExporterUi only fills in the abstractions
        // the host has not already supplied. Scoped, because it talks to the browser
        // tab (IJSRuntime) of the circuit that asked.
        builder.Services.AddScoped<IFileDialogService, BrowserFileDialogService>();

        // Application + UI services. All singletons, so every page (and every open
        // tab) observes the same connection, tag tree and subscription.
        builder.Services.AddOpcUaExporterUi();

        var app = builder.Build();

        app.UseStaticFiles();
        app.UseAntiforgery();

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .AddAdditionalAssemblies(typeof(OpcUaExporter.UI.Components.Routes).Assembly);

        return app;
    }
}
