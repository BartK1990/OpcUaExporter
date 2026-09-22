using Xunit;

namespace OpcUaBridge.Tests;

public class BridgePathsTests
{
    private static readonly BridgePaths Paths = new(Path.Combine(Path.GetTempPath(), "OpcUaBridgeTests"));

    public static TheoryData<string> AllPaths =>
    [
        Paths.LogDirectory,
        Paths.ConfigDirectory,
        Paths.PkiDirectory,
        Paths.ClientPkiDirectory,
        Paths.ServerPkiDirectory,
        Paths.NamespaceSnapshotFile
    ];

    [Theory]
    [MemberData(nameof(AllPaths))]
    public void EveryPath_LivesUnderTheBaseDirectory(string path)
    {
        // A Windows service's working directory is C:\Windows\System32, so anything that
        // resolved relative to the current directory would land there.
        Assert.StartsWith(Paths.BaseDirectory + Path.DirectorySeparatorChar, path);
    }

    [Fact]
    public void Default_IsRootedAtTheRunningExecutable()
    {
        Assert.Equal(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar),
            BridgePaths.Default.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void ClientAndServerPki_AreSeparateStores()
    {
        // Two OPC UA identities: one the upstream server trusts, one downstream clients
        // trust. Sharing a store would mean sharing a key pair between the two roles.
        Assert.NotEqual(Paths.ClientPkiDirectory, Paths.ServerPkiDirectory);
    }

    [Fact]
    public void NamespaceBackupFile_IsTimestampedAndSitsBesideTheSnapshot()
    {
        var backup = Paths.NamespaceBackupFile(new DateTimeOffset(2026, 9, 19, 14, 5, 3, TimeSpan.Zero));

        Assert.Equal(Paths.ConfigDirectory, Path.GetDirectoryName(backup));
        Assert.Equal("namespace.20260919-140503.json", Path.GetFileName(backup));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsAnEmptyBaseDirectory(string baseDirectory)
    {
        Assert.Throws<ArgumentException>(() => new BridgePaths(baseDirectory));
    }
}
