using OpcUaExporter.Models;
using Xunit;

namespace OpcUaExporter.Tests;

public class OpcTagTests
{
    [Theory]
    [InlineData("Variable", true)]
    [InlineData("Object", false)]
    [InlineData("Method", false)]
    [InlineData("", false)]
    public void IsSelectable_IsTrueOnlyForVariableNodes(string nodeClass, bool expected)
    {
        var tag = new OpcTag { NodeClass = nodeClass };

        Assert.Equal(expected, tag.IsSelectable);
    }

    [Fact]
    public void Flatten_ReturnsVariablesFromEveryLevelAndSkipsContainers()
    {
        var root = Folder("Root",
            Variable("ns=2;s=Temp"),
            Folder("Nested",
                Variable("ns=2;s=Pressure"),
                Folder("Deeper", Variable("ns=2;s=Flow"))));

        var nodeIds = root.Flatten().Select(t => t.NodeId).ToArray();

        Assert.Equal(["ns=2;s=Temp", "ns=2;s=Pressure", "ns=2;s=Flow"], nodeIds);
    }

    [Fact]
    public void Flatten_IncludesTheNodeItselfWhenItIsAVariable()
    {
        var tag = Variable("ns=2;s=Temp");

        Assert.Same(tag, Assert.Single(tag.Flatten()));
    }

    [Fact]
    public void Flatten_ReturnsNothingForAnEmptyFolder()
    {
        Assert.Empty(Folder("Empty").Flatten());
    }

    private static OpcTag Variable(string nodeId) =>
        new() { NodeId = nodeId, DisplayName = nodeId, NodeClass = "Variable" };

    private static OpcTag Folder(string name, params OpcTag[] children) =>
        new() { NodeId = name, DisplayName = name, NodeClass = "Object", Children = [.. children] };
}
