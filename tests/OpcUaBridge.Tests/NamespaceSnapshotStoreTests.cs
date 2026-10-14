using Microsoft.Extensions.Logging.Abstractions;
using OpcUaBridge.Namespaces;
using Xunit;

namespace OpcUaBridge.Tests;

public class NamespaceSnapshotStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("opcua-bridge-snapshot").FullName;

    private JsonNamespaceSnapshotStore CreateStore() => new(
        Path.Combine(_directory, "namespace.json"),
        timestamp => Path.Combine(_directory, $"namespace.{timestamp:yyyyMMdd-HHmmss}.json"),
        NullLogger<JsonNamespaceSnapshotStore>.Instance);

    private static NamespaceSnapshot SampleSnapshot() => new()
    {
        CapturedUtc = new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero),
        Source = new SnapshotSource { EndpointUrl = "opc.tcp://plant:4840", ServerName = "Plant" },
        NamespaceUris = ["http://opcfoundation.org/UA/", "urn:plant:Server", "http://plant/data"],
        Stats = new SnapshotStats { ObjectCount = 1, VariableCount = 2 },
        Nodes =
        [
            new SnapshotNode
            {
                NamespaceIndex = 2, Identifier = "s=Line1", BrowseName = "Line1", DisplayName = "Line 1",
                Children =
                [
                    new SnapshotNode
                    {
                        NamespaceIndex = 2, Identifier = "s=Line1.Speed", BrowseName = "Speed",
                        IsVariable = true, DataType = "Double", AccessLevel = 3
                    },
                    new SnapshotNode
                    {
                        NamespaceIndex = 2, Identifier = "s=Line1.State", BrowseName = "State",
                        IsVariable = true, DataType = "Int32", AccessLevel = 1
                    }
                ]
            }
        ]
    };

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsEveryField()
    {
        var store = CreateStore();

        await store.SaveAsync(SampleSnapshot());
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(NamespaceSnapshot.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal("opc.tcp://plant:4840", loaded.Source.EndpointUrl);
        Assert.Equal(["http://opcfoundation.org/UA/", "urn:plant:Server", "http://plant/data"], loaded.NamespaceUris);

        var speed = Assert.Single(loaded.Variables(), v => v.BrowseName == "Speed");
        Assert.Equal("s=Line1.Speed", speed.Identifier);
        Assert.Equal(2, speed.NamespaceIndex);
        Assert.Equal("Double", speed.DataType);
        Assert.Equal(3, speed.AccessLevel);
    }

    [Fact]
    public async Task LoadAsync_ReturnsNullWhenNothingHasBeenCaptured()
    {
        Assert.Null(await CreateStore().LoadAsync());
    }

    [Fact]
    public async Task SaveAsync_BacksUpThePreviousSnapshotBeforeReplacingIt()
    {
        var store = CreateStore();
        await store.SaveAsync(SampleSnapshot());

        var replacement = SampleSnapshot();
        replacement.Source.ServerName = "Replaced";
        var backupPath = await store.SaveAsync(replacement);

        Assert.NotNull(backupPath);
        Assert.True(File.Exists(backupPath));
        Assert.Contains("\"serverName\": \"Plant\"", await File.ReadAllTextAsync(backupPath));
        Assert.Equal("Replaced", (await store.LoadAsync())!.Source.ServerName);
    }

    [Fact]
    public async Task SaveAsync_ReportsNoBackupOnTheFirstCapture()
    {
        Assert.Null(await CreateStore().SaveAsync(SampleSnapshot()));
    }

    [Fact]
    public async Task LoadAsync_RefusesASnapshotWrittenByANewerBuild()
    {
        var store = CreateStore();
        var snapshot = SampleSnapshot();
        snapshot.SchemaVersion = NamespaceSnapshot.CurrentSchemaVersion + 1;
        await store.SaveAsync(snapshot);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());

        Assert.Contains("schema version", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ComputeHashAsync_ChangesWhenTheFileChanges()
    {
        var store = CreateStore();
        Assert.Null(await store.ComputeHashAsync());

        await store.SaveAsync(SampleSnapshot());
        var first = await store.ComputeHashAsync();

        var replacement = SampleSnapshot();
        replacement.Source.ServerName = "Different";
        await store.SaveAsync(replacement);

        Assert.NotNull(first);
        Assert.NotEqual(first, await store.ComputeHashAsync());
    }

    [Fact]
    public async Task SaveAsync_LeavesNoTemporaryFileBehind()
    {
        var store = CreateStore();
        await store.SaveAsync(SampleSnapshot());

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
