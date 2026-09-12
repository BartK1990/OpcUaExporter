using Microsoft.Extensions.Logging.Abstractions;
using OpcUaExporter.Models;
using OpcUaExporter.Services;
using Xunit;

namespace OpcUaExporter.Tests;

/// <summary>
/// Exercises the saved-profile library in <see cref="OpcUaService"/>. There's no seam to fake
/// <see cref="AppPaths"/> (see AppPathsTests for the same tradeoff), so every profile a test
/// creates is deleted again and the active-profile pointer is restored, to avoid leaving test
/// artifacts behind in the real per-user app data directory.
/// </summary>
public class OpcUaServiceProfileTests : IDisposable
{
    private readonly List<Guid> _createdProfileIds = new();
    private readonly string? _originalActiveProfileId;

    public OpcUaServiceProfileTests()
    {
        _originalActiveProfileId = File.Exists(AppPaths.ActiveProfileIdFile)
            ? File.ReadAllText(AppPaths.ActiveProfileIdFile)
            : null;
    }

    public void Dispose()
    {
        foreach (var id in _createdProfileIds)
        {
            var path = Path.Combine(AppPaths.ProfilesDirectory, $"{id}.json");
            if (File.Exists(path))
                File.Delete(path);
        }

        if (_originalActiveProfileId is not null)
            File.WriteAllText(AppPaths.ActiveProfileIdFile, _originalActiveProfileId);
        else if (File.Exists(AppPaths.ActiveProfileIdFile))
            File.Delete(AppPaths.ActiveProfileIdFile);
    }

    private static OpcUaService CreateService()
        => new(
            new OpcUaClientService(NullLogger<OpcUaClientService>.Instance, new DiagnosticsLogService()),
            NullLogger<OpcUaService>.Instance,
            new DiagnosticsLogService());

    private async Task<ConnectionProfile> AddTrackedProfileAsync(OpcUaService svc, string name)
    {
        await svc.AddProfileAsync(name);
        _createdProfileIds.Add(svc.Profile.Id);
        return svc.Profile;
    }

    [Fact]
    public async Task AddProfileAsync_AddsToLibraryAndMakesItActive()
    {
        var svc = CreateService();

        var created = await AddTrackedProfileAsync(svc, "Test Line 1");

        Assert.Equal("Test Line 1", svc.Profile.Name);
        Assert.Same(created, svc.Profile);
        Assert.Contains(svc.SavedProfiles, p => p.Id == created.Id);
    }

    [Fact]
    public async Task SelectProfile_SwitchesToTheRequestedProfile()
    {
        var svc = CreateService();
        var lineA = await AddTrackedProfileAsync(svc, "Line A");
        await AddTrackedProfileAsync(svc, "Line B");

        var switched = svc.SelectProfile(lineA.Id);

        Assert.True(switched);
        Assert.Equal(lineA.Id, svc.Profile.Id);
    }

    [Fact]
    public void SelectProfile_ReturnsFalseForAnUnknownId()
    {
        var svc = CreateService();

        Assert.False(svc.SelectProfile(Guid.NewGuid()));
    }

    [Fact]
    public async Task DuplicateProfileAsync_CreatesADistinctCopyAndSelectsIt()
    {
        var svc = CreateService();
        var source = await AddTrackedProfileAsync(svc, "Line A");
        source.EndpointUrl = "opc.tcp://10.0.0.5:4840";
        await svc.SaveActiveProfileAsync();

        await svc.DuplicateProfileAsync(source.Id);
        _createdProfileIds.Add(svc.Profile.Id);

        Assert.NotEqual(source.Id, svc.Profile.Id);
        Assert.Equal("Line A (Copy)", svc.Profile.Name);
        Assert.Equal(source.EndpointUrl, svc.Profile.EndpointUrl);
    }

    [Fact]
    public async Task DeleteProfileAsync_RemovesItFromTheLibraryAndFromDisk()
    {
        var svc = CreateService();
        var created = await AddTrackedProfileAsync(svc, "Temp Line");
        var path = Path.Combine(AppPaths.ProfilesDirectory, $"{created.Id}.json");
        Assert.True(File.Exists(path));

        await svc.DeleteProfileAsync(created.Id);

        Assert.False(File.Exists(path));
        Assert.DoesNotContain(svc.SavedProfiles, p => p.Id == created.Id);
    }

    [Fact]
    public async Task ExportProfileAsync_ThenImportProfileAsync_AddsANewLibraryEntry()
    {
        var svc = CreateService();
        var source = await AddTrackedProfileAsync(svc, "Exportable Line");
        var exportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");

        try
        {
            await svc.ExportProfileAsync(exportPath);
            await svc.ImportProfileAsync(exportPath);
            _createdProfileIds.Add(svc.Profile.Id);

            Assert.NotEqual(source.Id, svc.Profile.Id);
            Assert.Equal(source.Name, svc.Profile.Name);
            Assert.Equal(source.EndpointUrl, svc.Profile.EndpointUrl);
        }
        finally
        {
            if (File.Exists(exportPath))
                File.Delete(exportPath);
        }
    }
}
