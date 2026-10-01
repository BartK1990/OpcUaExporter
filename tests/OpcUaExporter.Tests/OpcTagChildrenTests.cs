using OpcUaExporter.Models;
using Xunit;

namespace OpcUaExporter.Tests;

/// <summary>Lazy child loading shared by the background deep scan and on-demand expands.</summary>
public class OpcTagChildrenTests
{
    [Fact]
    public void NewObjectOrVariable_CanLoadChildren_UntilPublished()
    {
        var folder = new OpcTag { NodeId = "ns=2;s=F", NodeClass = "Object" };
        var variable = new OpcTag { NodeId = "ns=2;s=V", NodeClass = "Variable" };
        var method = new OpcTag { NodeId = "ns=2;s=M", NodeClass = "Method" };

        Assert.True(folder.CanLoadChildren);
        Assert.True(variable.CanLoadChildren);
        Assert.False(method.CanLoadChildren);

        folder.PublishChildren([]);

        Assert.True(folder.ChildrenLoaded);
        Assert.False(folder.CanLoadChildren);
    }

    [Fact]
    public void PublishChildren_FirstPublisherWins_SoSelectionsOnEarlyLoadedNodesSurvive()
    {
        var folder = new OpcTag { NodeId = "ns=2;s=F", NodeClass = "Object" };
        var onDemand = new OpcTag { NodeId = "ns=2;s=F.A", NodeClass = "Variable", IsSelected = true };
        var fromScan = new OpcTag { NodeId = "ns=2;s=F.A", NodeClass = "Variable" };

        var first = folder.PublishChildren([onDemand]);
        var second = folder.PublishChildren([fromScan]);

        Assert.Same(first, second);
        Assert.Same(onDemand, Assert.Single(folder.Children));
        Assert.True(folder.Children[0].IsSelected);
    }

    [Fact]
    public void PublishChildren_ConcurrentPublishers_AllSeeTheSameList()
    {
        var folder = new OpcTag { NodeId = "ns=2;s=F", NodeClass = "Object" };

        var results = Enumerable.Range(0, 16)
            .AsParallel()
            .Select(i => folder.PublishChildren([new OpcTag { NodeId = $"ns=2;s=F.{i}", NodeClass = "Variable" }]))
            .ToList();

        Assert.All(results, r => Assert.Same(folder.Children, r));
    }
}
