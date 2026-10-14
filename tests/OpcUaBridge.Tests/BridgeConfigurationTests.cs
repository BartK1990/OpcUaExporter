using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpcUaBridge.Host;
using Xunit;

namespace OpcUaBridge.Tests;

/// <summary>
/// The precedence of the settings files, which is the whole of their behaviour.
/// </summary>
/// <remarks>
/// A file that loads but loses to a shipped default is, from outside the process,
/// indistinguishable from a file that never loaded at all. That is the failure these pin.
/// </remarks>
public sealed class BridgeConfigurationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("opcua-bridge-config").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Json(string endpoint)
        => JsonSerializer.Serialize(new { Bridge = new { Upstream = new { EndpointUrl = endpoint } } });

    private void WriteDefaults(string endpoint)
        => File.WriteAllText(
            Path.Combine(_root, BridgeConfiguration.DefaultsFileName),
            Json(endpoint));

    private void WriteInstallation(string endpoint)
        => File.WriteAllText(
            Path.Combine(_root, BridgeConfiguration.InstallationFileName),
            Json(endpoint));

    private void WriteLocal(string endpoint)
    {
        var path = Path.Combine(_root, BridgeConfiguration.LocalFileRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Json(endpoint));
    }

    private string Endpoint(params string[] args)
        => new ConfigurationBuilder()
            .AddBridgeConfiguration(_root, args)
            .Build()["Bridge:Upstream:EndpointUrl"]!;

    [Fact]
    public void AddBridgeConfiguration_WithOnlyTheShippedDefaults_UsesThem()
    {
        WriteDefaults("opc.tcp://shipped:4840");

        Assert.Equal("opc.tcp://shipped:4840", Endpoint());
    }

    [Fact]
    public void AddBridgeConfiguration_InstallationSettings_BeatTheShippedDefaults()
    {
        // The point of the file: a release drops a new appsettings.json into the install
        // folder, and the endpoint this site was configured with has to survive it.
        WriteDefaults("opc.tcp://shipped:4840");
        WriteInstallation("opc.tcp://plant:49320");

        Assert.Equal("opc.tcp://plant:49320", Endpoint());
    }

    [Fact]
    public void AddBridgeConfiguration_ADeveloperOverride_BeatsTheInstallationSettings()
    {
        // Last and most specific, so somebody debugging on a machine that also holds real
        // installation settings does not have to move them out of the way.
        WriteDefaults("opc.tcp://shipped:4840");
        WriteInstallation("opc.tcp://plant:49320");
        WriteLocal("opc.tcp://localhost:48411");

        Assert.Equal("opc.tcp://localhost:48411", Endpoint());
    }

    [Fact]
    public void AddBridgeConfiguration_TheCommandLine_BeatsEveryFile()
    {
        WriteDefaults("opc.tcp://shipped:4840");
        WriteInstallation("opc.tcp://plant:49320");
        WriteLocal("opc.tcp://localhost:48411");

        Assert.Equal(
            "opc.tcp://override:4840",
            Endpoint("--Bridge:Upstream:EndpointUrl=opc.tcp://override:4840"));
    }

    [Fact]
    public void AddBridgeConfiguration_InstallationSettings_AreOptional()
    {
        // Nothing creates this file, so a fresh install has to start without it.
        WriteDefaults("opc.tcp://shipped:4840");

        Assert.False(File.Exists(Path.Combine(_root, BridgeConfiguration.InstallationFileName)));
        Assert.Equal("opc.tcp://shipped:4840", Endpoint());
    }

    [Fact]
    public void AddBridgeConfiguration_OverridesOnlyTheKeysItSets()
    {
        // Installation settings are a patch, not a replacement: an operator setting the
        // endpoint must not silently drop every other setting the release shipped.
        File.WriteAllText(
            Path.Combine(_root, BridgeConfiguration.DefaultsFileName),
            """{"Bridge":{"Upstream":{"EndpointUrl":"opc.tcp://shipped:4840","SessionTimeoutMs":60000},"Server":{"Port":4841}}}""");
        WriteInstallation("opc.tcp://plant:49320");

        var configuration = new ConfigurationBuilder().AddBridgeConfiguration(_root, []).Build();

        Assert.Equal("opc.tcp://plant:49320", configuration["Bridge:Upstream:EndpointUrl"]);
        Assert.Equal("60000", configuration["Bridge:Upstream:SessionTimeoutMs"]);
        Assert.Equal("4841", configuration["Bridge:Server:Port"]);
    }

    [Fact]
    public void AddBridgeConfiguration_WithoutTheShippedDefaults_Throws()
    {
        // The release is broken if appsettings.json is missing, and starting on built-in
        // defaults would hide that until the gateway pointed somewhere unexpected.
        Assert.Throws<FileNotFoundException>(() => Endpoint());
    }

    [Fact]
    public void DescribeFiles_ReportsEachFileInPrecedenceOrderAndWhetherItIsThere()
    {
        WriteDefaults("opc.tcp://shipped:4840");
        WriteInstallation("opc.tcp://plant:49320");

        var files = BridgeConfiguration.DescribeFiles(_root);

        Assert.Collection(files,
            defaults =>
            {
                Assert.True(defaults.Exists);
                Assert.True(defaults.Required);
                Assert.EndsWith(BridgeConfiguration.DefaultsFileName, defaults.Path);
            },
            installation =>
            {
                Assert.True(installation.Exists);
                Assert.False(installation.Required);
                Assert.EndsWith(BridgeConfiguration.InstallationFileName, installation.Path);
            },
            local =>
            {
                // Absent, and reported as such rather than omitted: "I edited the settings
                // and nothing changed" is usually a file in the wrong place.
                Assert.False(local.Exists);
                Assert.False(local.Required);
            });
    }
}
