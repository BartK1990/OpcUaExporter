using Serilog;
using Serilog.Events;

namespace OpcUaBridge.Host;

/// <summary>
/// Serilog configuration for the bridge.
/// </summary>
/// <remarks>
/// Configured in code rather than from <c>appsettings.json</c> so the file-size and
/// retention limits cannot be lost to a careless configuration edit on a service that is
/// expected to run unattended for months. Levels remain overridable from configuration.
/// </remarks>
public static class BridgeLogging
{
    /// <summary>30 MB per file.</summary>
    public const long FileSizeLimitBytes = 30L * 1024 * 1024;

    /// <summary>Ten files kept, so the logs cannot exceed roughly 300 MB on disk.</summary>
    public const int RetainedFileCountLimit = 10;

    private const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    private const string FileTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// A logger available before the host is built, so a configuration or certificate
    /// failure during start-up is still recorded rather than lost.
    /// </summary>
    public static Serilog.ILogger CreateBootstrapLogger()
        => Configure(new LoggerConfiguration()).CreateBootstrapLogger();

    /// <summary>Applies the bridge's sinks to a logger configuration.</summary>
    public static LoggerConfiguration Configure(LoggerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("Opc.Ua", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "OpcUaBridge")
            .WriteTo.Console(outputTemplate: ConsoleTemplate)
            // Asynchronous, and non-blocking when the buffer fills. A disk stall must
            // never block the OPC UA SDK's publish thread, which is what would happen if
            // a value notification logged synchronously to a wedged volume.
            .WriteTo.Async(sink => sink.File(
                path: Path.Combine(BridgePaths.Default.LogDirectory, "opcua-bridge.log"),

                // Size-based rolling rather than daily. A quiet gateway rolling daily
                // would discard its history after ten days even though it had written
                // almost nothing; rolling by size keeps the last 300 MB of what actually
                // happened, however long that took to accumulate.
                rollingInterval: RollingInterval.Infinite,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: FileSizeLimitBytes,
                retainedFileCountLimit: RetainedFileCountLimit,
                flushToDiskInterval: TimeSpan.FromSeconds(2),
                outputTemplate: FileTemplate),
                blockWhenFull: false);
    }
}
