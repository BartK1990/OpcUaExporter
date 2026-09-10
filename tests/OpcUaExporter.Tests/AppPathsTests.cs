using Xunit;

namespace OpcUaExporter.Tests;

public class AppPathsTests
{
    [Fact]
    public void RootDirectory_IsNamedAfterTheApplication()
    {
        Assert.Equal("OpcUaExporter", Path.GetFileName(AppPaths.RootDirectory));
    }

    [Fact]
    public void RootDirectory_ExistsAfterAccess()
    {
        Assert.True(Directory.Exists(AppPaths.RootDirectory));
    }

    // Everything the app persists must stay under one per-user directory, so it
    // can be inspected or wiped in a single place.
    public static TheoryData<string> PersistedPaths =>
    [
        AppPaths.PkiDirectory,
        AppPaths.CrashLogFile,
        AppPaths.SettingsFile,
        AppPaths.LastProfilePointerFile
    ];

    [Theory]
    [MemberData(nameof(PersistedPaths))]
    public void PersistedState_LivesUnderTheRootDirectory(string path)
    {
        Assert.StartsWith(AppPaths.RootDirectory + Path.DirectorySeparatorChar, path);
    }
}
