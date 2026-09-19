using System.Reflection;
using OpcUaBridge.Host;
using Serilog;
using Xunit;

namespace OpcUaBridge.Tests;

public class BridgeLoggingTests
{
    [Fact]
    public void FileSink_IsCappedAtThirtyMegabytesPerFile()
    {
        Assert.Equal(30L * 1024 * 1024, BridgeLogging.FileSizeLimitBytes);
    }

    [Fact]
    public void FileSink_KeepsTenFiles()
    {
        // Thirty megabytes across ten files bounds the logs at roughly 300 MB, which is
        // what a service running unattended for months needs to be true.
        Assert.Equal(10, BridgeLogging.RetainedFileCountLimit);
    }

    [Fact]
    public void Configure_ProducesAUsableLogger()
    {
        using var logger = BridgeLogging.Configure(new LoggerConfiguration()).CreateLogger();

        logger.Information("A test message.");
    }

    [Fact]
    public void LogFiles_LiveBesideTheExecutable()
    {
        Assert.StartsWith(BridgePaths.Default.BaseDirectory, BridgePaths.Default.LogDirectory);
        Assert.EndsWith("Logs", BridgePaths.Default.LogDirectory);
    }
}

public class ArchitectureTests
{
    private static readonly Assembly BridgeCore = typeof(OpcUaBridge.Configuration.BridgeOptions).Assembly;
    private static readonly Assembly BridgeHost = typeof(BridgeLogging).Assembly;

    [Fact]
    public void BridgeCore_DoesNotDependOnTheDesktopExporter()
    {
        // The gateway shares the OPC UA client code, not the desktop application. Letting
        // OpcUaExporter.Core in would drag CsvHelper, an Excel writer and a %LocalAppData%
        // path layout into a Windows service that wants none of them.
        Assert.DoesNotContain(
            BridgeCore.GetReferencedAssemblies(),
            a => a.Name is "OpcUaExporter.Core" or "OpcUaExporter.UI");
    }

    [Fact]
    public void BridgeHost_DoesNotDependOnTheDesktopExporter()
    {
        Assert.DoesNotContain(
            BridgeHost.GetReferencedAssemblies(),
            a => a.Name is "OpcUaExporter.Core" or "OpcUaExporter.UI");
    }

    [Fact]
    public void BridgeCore_DoesNotDependOnAspNetCore()
    {
        // The gateway's domain must stay testable without a web host. Web concerns belong
        // to OpcUaBridge.Host.
        Assert.DoesNotContain(
            BridgeCore.GetReferencedAssemblies(),
            a => a.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void BridgeCore_SharesTheOpcUaClientCode()
    {
        Assert.Contains(BridgeCore.GetReferencedAssemblies(), a => a.Name == "OpcUaShared");
    }
}
