using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opc.Ua;
using OpcUaBridge.Acquisition;
using OpcUaBridge.Diagnostics;
using OpcUaBridge.Namespaces;
using OpcUaBridge.Server;
using OpcUaBridge.Tags;
using OpcUaBridge.Upstream;
using OpcUaExporter.Abstractions;
using OpcUaExporter.Certificates;
using OpcUaExporter.Operations;
using OpcUaExporter.Services;
using OpcUaExporter.Sessions;

namespace OpcUaBridge.Configuration;

/// <summary>The single registration point for everything the gateway is made of.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the gateway's services.
    /// </summary>
    /// <remarks>
    /// The tag registry and value store are built eagerly from the snapshot, because
    /// their size is fixed by it and the mirrored address space is constructed from them
    /// as the OPC UA server starts. When no snapshot has been captured the bridge still
    /// starts -- with an empty mirror and a dashboard saying so -- rather than refusing
    /// to run or silently browsing the upstream server to invent one.
    /// </remarks>
    public static IServiceCollection AddOpcUaBridge(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<BridgeOptions>()
            .Bind(configuration.GetSection(BridgeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<BridgeOptions>, BridgeOptionsValidator>();

        var paths = BridgePaths.Default;
        paths.EnsureCreated();
        services.AddSingleton(paths);

        services.AddSingleton<DiagnosticsLogService>();
        services.AddSingleton<ProcessCostSampler>();
        services.AddSingleton(TimeProvider.System);

        AddOpcUaClient(services, paths);
        AddNamespaceServices(services, paths, configuration);
        AddAcquisition(services);
        AddMirrorServer(services);

        return services;
    }

    private static void AddOpcUaClient(IServiceCollection services, BridgePaths paths)
    {
        services.AddSingleton<IOpcUaPkiLocation>(_ => new OpcUaPkiLocation(paths.ClientPkiDirectory));

        services.AddSingleton(provider =>
        {
            var upstream = provider.GetRequiredService<IOptions<BridgeOptions>>().Value.Upstream;
            var name = upstream.ClientApplicationName;

            return new OpcUaExporter.Configuration.OpcUaApplicationIdentity(
                name,
                upstream.ClientApplicationUri ?? $"urn:{Utils.GetHostName()}:{name}",
                upstream.ClientSubjectName ?? $"CN={name}");
        });

        services.AddSingleton<CertificateTrustStore>();
        services.AddSingleton<OpcUaBridge.Certificates.ExporterCertificateImporter>();

        services.AddSingleton(provider =>
        {
            var identity = provider.GetRequiredService<OpcUaExporter.Configuration.OpcUaApplicationIdentity>();
            var pki = provider.GetRequiredService<IOpcUaPkiLocation>();
            var trustStore = provider.GetRequiredService<CertificateTrustStore>();
            var diagnostics = provider.GetRequiredService<DiagnosticsLogService>();

            var configuration = new Lazy<Task<ApplicationConfiguration>>(() =>
                OpcUaExporter.Configuration.OpcUaApplicationConfigurationFactory.CreateClientAsync(
                    identity, pki, trustStore, diagnostics));

            return new OpcUaSessionFactory(() => configuration.Value, identity.ApplicationName, diagnostics);
        });

        services.AddSingleton<OpcUaBrowser>();
    }

    private static void AddNamespaceServices(IServiceCollection services, BridgePaths paths, IConfiguration configuration)
    {
        services.AddSingleton<INamespaceSnapshotWriter>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<BridgeOptions>>().Value.Snapshot;
            var path = string.IsNullOrWhiteSpace(options.Path)
                ? paths.NamespaceSnapshotFile
                : Path.IsPathRooted(options.Path) ? options.Path : Path.Combine(paths.BaseDirectory, options.Path);

            return new JsonNamespaceSnapshotStore(
                path,
                paths.NamespaceBackupFile,
                provider.GetRequiredService<ILogger<JsonNamespaceSnapshotStore>>());
        });

        // Everything except the capture service sees the read-only view, so no runtime
        // code path has a method that could rewrite the snapshot.
        services.AddSingleton<INamespaceSnapshotStore>(p => p.GetRequiredService<INamespaceSnapshotWriter>());
        services.AddSingleton<NamespaceCaptureService>();

        services.AddSingleton(provider =>
        {
            var store = provider.GetRequiredService<INamespaceSnapshotStore>();
            var logger = provider.GetRequiredService<ILogger<TagRegistry>>();

            var snapshot = store.LoadAsync().GetAwaiter().GetResult();
            if (snapshot is null)
            {
                logger.LogWarning(
                    "No namespace snapshot at {SnapshotPath}. The bridge will start with an empty mirrored " +
                    "address space; capture the namespace from the dashboard to populate it.",
                    store.SnapshotPath);

                return TagRegistry.FromSnapshot(new NamespaceSnapshot());
            }

            var hash = store.ComputeHashAsync().GetAwaiter().GetResult();
            logger.LogInformation(
                "Loaded namespace snapshot captured {CapturedUtc:u} from {EndpointUrl} (sha256 {Hash}).",
                snapshot.CapturedUtc, snapshot.Source.EndpointUrl, hash);

            return TagRegistry.FromSnapshot(snapshot);
        });

        services.AddSingleton(provider =>
        {
            var registry = provider.GetRequiredService<TagRegistry>();
            return new TagValueStore(registry.Count, provider.GetRequiredService<TimeProvider>());
        });
    }

    private static void AddAcquisition(IServiceCollection services)
    {
        services.AddSingleton<UpstreamConnectionManager>();
        services.AddSingleton<IUpstreamConnection>(p => p.GetRequiredService<UpstreamConnectionManager>());
        services.AddHostedService(p => p.GetRequiredService<UpstreamConnectionManager>());

        services.AddSingleton<TagStalenessMonitor>();
        services.AddHostedService(p => p.GetRequiredService<TagStalenessMonitor>());

        services.AddSingleton<AcquisitionEngineFactory>();
        services.AddSingleton<AcquisitionCoordinator>();
        services.AddHostedService(p => p.GetRequiredService<AcquisitionCoordinator>());
    }

    private static void AddMirrorServer(IServiceCollection services)
    {
        services.AddSingleton<IUpstreamWriteRouter, UpstreamWriteRouter>();

        services.AddSingleton(provider => new BridgeServer(
            provider.GetRequiredService<TagRegistry>(),
            provider.GetRequiredService<TagValueStore>(),
            provider.GetRequiredService<IOptions<BridgeOptions>>().Value.Server,
            provider.GetRequiredService<IUpstreamWriteRouter>(),
            provider.GetRequiredService<ILoggerFactory>()));

        services.AddSingleton<BridgeServerHost>();
        services.AddHostedService(p => p.GetRequiredService<BridgeServerHost>());
        services.AddHostedService<MirrorValueApplier>();
    }
}
