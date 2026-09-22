using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using OpcUaBridge.Namespaces;
using OpcUaBridge.Tags;
using Xunit;

namespace OpcUaBridge.Tests;

public class TagRegistryTests
{
    private const string PlantUri = "http://plant/data";
    private const string ServerUri = "urn:plant:Server";

    private static NamespaceSnapshot Snapshot() => new()
    {
        NamespaceUris = [Opc.Ua.Namespaces.OpcUa, ServerUri, PlantUri],
        Stats = new SnapshotStats { ObjectCount = 1, VariableCount = 2 },
        Nodes =
        [
            new SnapshotNode
            {
                NamespaceIndex = 2, Identifier = "s=Line1", BrowseName = "Line1",
                Children =
                [
                    new SnapshotNode
                    {
                        NamespaceIndex = 2, Identifier = "s=Line1.Speed", BrowseName = "Speed",
                        IsVariable = true, DataType = "Double", AccessLevel = 3
                    },
                    new SnapshotNode
                    {
                        NamespaceIndex = 1, Identifier = "i=2258", BrowseName = "CurrentTime",
                        IsVariable = true, DataType = "DateTime", AccessLevel = 1
                    }
                ]
            }
        ]
    };

    [Fact]
    public void FromSnapshot_FlattensVariablesIntoContiguousIndexes()
    {
        var registry = TagRegistry.FromSnapshot(Snapshot());

        Assert.Equal(2, registry.Count);
        Assert.Equal([0, 1], registry.Tags.Select(t => t.Index));
        Assert.Equal(["Line1/Speed", "Line1/CurrentTime"], registry.Tags.Select(t => t.BrowsePath));
    }

    [Fact]
    public void FromSnapshot_CarriesTheNamespaceUriRatherThanTheIndex()
    {
        var registry = TagRegistry.FromSnapshot(Snapshot());

        Assert.Equal(PlantUri, registry.Tags[0].UpstreamNamespaceUri);
        Assert.Equal(ServerUri, registry.Tags[1].UpstreamNamespaceUri);
    }

    [Fact]
    public void FromSnapshot_MirrorsTheUpstreamAccessLevel()
    {
        var registry = TagRegistry.FromSnapshot(Snapshot());

        Assert.True(registry.Tags[0].IsWritable);
        Assert.False(registry.Tags[1].IsWritable);
    }

    [Fact]
    public void ResolveAgainst_BindsEachTagToTheSessionsOwnNamespaceIndex()
    {
        var registry = TagRegistry.FromSnapshot(Snapshot());
        var sessionNamespaces = new NamespaceTable([Opc.Ua.Namespaces.OpcUa, ServerUri, PlantUri]);

        var missing = registry.ResolveAgainst(sessionNamespaces, NullLogger.Instance);

        Assert.Empty(missing);
        Assert.Equal(NodeId.Parse("ns=2;s=Line1.Speed"), registry.Tags[0].UpstreamNodeId);
        Assert.Equal(NodeId.Parse("ns=1;i=2258"), registry.Tags[1].UpstreamNodeId);
    }

    [Fact]
    public void ResolveAgainst_FollowsTheUriWhenTheServerReordersItsNamespaceTable()
    {
        // The failure this prevents is the nastiest one a gateway can have: after a server
        // restart that renumbered namespaces, reusing the captured index would read a
        // different node entirely and report it as Good.
        var registry = TagRegistry.FromSnapshot(Snapshot());
        var reordered = new NamespaceTable([Opc.Ua.Namespaces.OpcUa, ServerUri, "http://plant/other", PlantUri]);

        registry.ResolveAgainst(reordered, NullLogger.Instance);

        Assert.Equal(NodeId.Parse("ns=3;s=Line1.Speed"), registry.Tags[0].UpstreamNodeId);
    }

    [Fact]
    public void ResolveAgainst_ReportsNamespacesTheServerNoLongerPublishes()
    {
        var registry = TagRegistry.FromSnapshot(Snapshot());
        var withoutPlant = new NamespaceTable([Opc.Ua.Namespaces.OpcUa, ServerUri]);

        var missing = registry.ResolveAgainst(withoutPlant, NullLogger.Instance);

        Assert.Equal([PlantUri], missing);
        Assert.Null(registry.Tags[0].UpstreamNodeId);
        Assert.NotNull(registry.Tags[1].UpstreamNodeId);
    }

    [Fact]
    public void FromSnapshot_LeavesAnOutOfRangeNamespaceIndexUnresolvable()
    {
        var snapshot = Snapshot();
        snapshot.Nodes[0].Children[0].NamespaceIndex = 99;

        var registry = TagRegistry.FromSnapshot(snapshot);
        registry.ResolveAgainst(new NamespaceTable([Opc.Ua.Namespaces.OpcUa, ServerUri, PlantUri]), NullLogger.Instance);

        // Better an honest BadNodeIdUnknown on one tag than a guess that mirrors the wrong node.
        Assert.Null(registry.Tags[0].UpstreamNodeId);
    }

    [Theory]
    [InlineData("Double", BuiltInType.Double)]
    [InlineData("Int32", BuiltInType.Int32)]
    [InlineData("Boolean", BuiltInType.Boolean)]
    public void FromSnapshot_ResolvesTheDeclaredBuiltInDataType(string name, BuiltInType expected)
    {
        var snapshot = Snapshot();
        snapshot.Nodes[0].Children[0].DataType = name;

        var registry = TagRegistry.FromSnapshot(snapshot);

        Assert.Equal(new NodeId((uint)expected), registry.Tags[0].DataType);
    }

    [Fact]
    public void FromSnapshot_FallsBackToBaseDataTypeWhenTheTypeIsUnknown()
    {
        var snapshot = Snapshot();
        snapshot.Nodes[0].Children[0].DataType = "SomeVendorStructure";

        var registry = TagRegistry.FromSnapshot(snapshot);

        Assert.Equal(DataTypeIds.BaseDataType, registry.Tags[0].DataType);
    }
}
