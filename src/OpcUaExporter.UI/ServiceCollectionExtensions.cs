using Microsoft.Extensions.DependencyInjection;

namespace OpcUaExporter.UI;

/// <summary>
/// Dependency-injection wiring for the Blazor UI layer.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything the components in this assembly need, including the
    /// application services they inject. A host should call this and then
    /// override the platform-specific abstractions (for example
    /// <see cref="OpcUaExporter.Abstractions.IFileDialogService"/>) with its own
    /// implementations.
    /// </summary>
    public static IServiceCollection AddOpcUaExporterUi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddOpcUaExporterCore();
    }
}
