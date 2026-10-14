using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpcUaExporter.Abstractions;
using OpcUaExporter.Services;

namespace OpcUaExporter;

/// <summary>
/// Dependency-injection wiring for the application services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the OPC UA client, its stateful facade and the supporting
    /// services.
    /// <para>
    /// Everything is a singleton: this is a single-window desktop app whose
    /// pages must observe one shared connection, tag tree and subscription.
    /// </para>
    /// <para>
    /// A host that can show native dialogs should register its own
    /// <see cref="IFileDialogService"/>; otherwise the no-op
    /// <see cref="NullFileDialogService"/> is used and every prompt is
    /// treated as cancelled.
    /// </para>
    /// </summary>
    public static IServiceCollection AddOpcUaExporterCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<DiagnosticsLogService>();

        // The client lives in OpcUaShared, which cannot see AppPaths, so left to itself it
        // recomputes %LocalAppData%\OpcUaExporter\pki. Handing it AppPaths' answer keeps one
        // source of truth -- and is what makes OPCUAEXPORTER_DATA_DIR move the certificates
        // along with everything else, rather than leaving a browser host or test run writing
        // into the user's real desktop certificate store.
        services.TryAddSingleton<IOpcUaPkiLocation>(_ => new OpcUaPkiLocation(AppPaths.PkiDirectory));
        services.TryAddSingleton<OpcUaClientService>();
        services.TryAddSingleton<OpcUaService>();
        services.TryAddSingleton<ThemeService>();
        services.TryAddSingleton<IFileDialogService, NullFileDialogService>();

        return services;
    }
}
