using OpcUaExporter.Abstractions;
using OpcUaExporter.UI;
using OpcUaExporter.Web.Components;
using OpcUaExporter.Web.Services;

var builder = WebApplication.CreateBuilder(args);

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

app.Run();

/// <summary>Entry point type, exposed so end-to-end tests can host the app with <c>WebApplicationFactory</c>.</summary>
public partial class Program;
