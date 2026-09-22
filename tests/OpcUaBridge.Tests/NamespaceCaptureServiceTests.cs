using OpcUaBridge.Namespaces;
using Xunit;

namespace OpcUaBridge.Tests;

public class NamespaceCaptureServiceTests
{
    private const string PlantUri = "http://plant/data";

    private static NamespaceSnapshot Snapshot(params SnapshotNode[] variables) => new()
    {
        NamespaceUris = [Opc.Ua.Namespaces.OpcUa, "urn:plant:Server", PlantUri],
        Nodes = [new SnapshotNode { NamespaceIndex = 2, Identifier = "s=Line1", BrowseName = "Line1", Children = [.. variables] }]
    };

    private static SnapshotNode Variable(string identifier, string dataType = "Double", byte accessLevel = 1) => new()
    {
        NamespaceIndex = 2, Identifier = identifier, BrowseName = identifier, IsVariable = true,
        DataType = dataType, AccessLevel = accessLevel
    };

    [Fact]
    public void Compare_ReportsNothingWhenTheServerIsUnchanged()
    {
        var diff = NamespaceCaptureService.Compare(
            Snapshot(Variable("s=Speed")), Snapshot(Variable("s=Speed")));

        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void Compare_ReportsTagsTheServerGained()
    {
        var diff = NamespaceCaptureService.Compare(
            Snapshot(Variable("s=Speed")), Snapshot(Variable("s=Speed"), Variable("s=Torque")));

        Assert.Equal([$"{PlantUri}|s=Torque"], diff.Added);
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public void Compare_ReportsTagsTheServerNoLongerHas()
    {
        var diff = NamespaceCaptureService.Compare(
            Snapshot(Variable("s=Speed"), Variable("s=Torque")), Snapshot(Variable("s=Speed")));

        Assert.Equal([$"{PlantUri}|s=Torque"], diff.Removed);
        Assert.Empty(diff.Added);
    }

    [Theory]
    [InlineData("Int32", (byte)1)]
    [InlineData("Double", (byte)3)]
    public void Compare_ReportsATagWhoseTypeOrWritabilityChanged(string dataType, byte accessLevel)
    {
        var diff = NamespaceCaptureService.Compare(
            Snapshot(Variable("s=Speed", "Double", 1)),
            Snapshot(Variable("s=Speed", dataType, accessLevel)));

        Assert.Equal([$"{PlantUri}|s=Speed"], diff.Changed);
    }

    [Fact]
    public void Compare_IsUnaffectedByTheServerRenumberingItsNamespaces()
    {
        // Identity is the namespace URI plus the identifier. Keying on the index instead
        // would turn a routine refresh into a diff claiming every tag had been replaced.
        var before = Snapshot(Variable("s=Speed"));

        var after = new NamespaceSnapshot
        {
            NamespaceUris = [Opc.Ua.Namespaces.OpcUa, "urn:plant:Server", "http://plant/other", PlantUri],
            Nodes =
            [
                new SnapshotNode
                {
                    NamespaceIndex = 3, Identifier = "s=Line1", BrowseName = "Line1",
                    Children =
                    [
                        new SnapshotNode
                        {
                            NamespaceIndex = 3, Identifier = "s=Speed", BrowseName = "s=Speed",
                            IsVariable = true, DataType = "Double", AccessLevel = 1
                        }
                    ]
                }
            ]
        };

        Assert.True(NamespaceCaptureService.Compare(before, after).IsEmpty);
    }

    [Fact]
    public void SnapshotDiff_Empty_HasNoEntries()
    {
        Assert.True(SnapshotDiff.Empty.IsEmpty);
    }
}
