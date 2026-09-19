using OpcUaExporter.Abstractions;
using Xunit;

namespace OpcUaExporter.Tests;

public class OpcUaPkiLocationTests
{
    [Fact]
    public void LocalAppData_ResolvesToTheSameDirectoryAsAppPaths()
    {
        // OpcUaShared cannot reference AppPaths (that would be a project cycle), so it
        // recomputes the exporter's PKI directory. This test is what keeps the two in step.
        var fromShared = OpcUaPkiLocation.LocalAppData("OpcUaExporter").PkiRoot;

        Assert.Equal(AppPaths.PkiDirectory, fromShared);
    }

    [Fact]
    public void BesideExecutable_PutsThePkiStoreUnderTheGivenBaseDirectory()
    {
        var location = OpcUaPkiLocation.BesideExecutable(Path.Combine("C:", "OpcUaBridge"));

        Assert.Equal(Path.Combine("C:", "OpcUaBridge", "pki"), location.PkiRoot);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsAnEmptyPkiRoot(string pkiRoot)
    {
        Assert.Throws<ArgumentException>(() => new OpcUaPkiLocation(pkiRoot));
    }
}
