using Microsoft.Extensions.Logging.Abstractions;
using OpcUaExporter.Models;
using OpcUaExporter.Services;
using Xunit;

namespace OpcUaExporter.Tests;

/// <summary>Tag selection in <see cref="OpcUaService"/>, including nodes the tag browser's filtered view clones.</summary>
public class OpcUaServiceSelectionTests
{
    private readonly OpcTag _member1 = new() { NodeId = "ns=2;s=Struct.A", DisplayName = "A", NodeClass = "Variable" };
    private readonly OpcTag _member2 = new() { NodeId = "ns=2;s=Struct.B", DisplayName = "B", NodeClass = "Variable" };
    private readonly OpcTag _structVar;
    private readonly OpcTag _folder;
    private readonly OpcUaService _service;

    public OpcUaServiceSelectionTests()
    {
        _structVar = new OpcTag { NodeId = "ns=2;s=Struct", DisplayName = "Struct", NodeClass = "Variable", Children = [_member1, _member2] };
        _folder    = new OpcTag { NodeId = "ns=2;s=Folder", DisplayName = "Folder", NodeClass = "Object", Children = [_structVar] };

        _service = new OpcUaService(
            new OpcUaClientService(NullLogger<OpcUaClientService>.Instance, new DiagnosticsLogService()),
            NullLogger<OpcUaService>.Instance,
            new DiagnosticsLogService());
        // TagTree has a private setter (it's normally filled by browsing a live server).
        typeof(OpcUaService).GetProperty(nameof(OpcUaService.TagTree))!.SetValue(_service, new List<OpcTag> { _folder });
    }

    [Fact]
    public void SelectInFolder_OnVariableWithChildren_SelectsItselfAndChildren()
    {
        _service.SelectInFolder(_structVar, true);

        Assert.Equal(["ns=2;s=Struct", "ns=2;s=Struct.A", "ns=2;s=Struct.B"], _service.GetSelectedNodeIds().Order());

        _service.SelectInFolder(_structVar, false);

        Assert.Empty(_service.GetSelectedNodeIds());
    }

    [Fact]
    public void ToggleTag_OnFilterClone_UpdatesTheTagInTheTree()
    {
        var clone = new OpcTag { NodeId = _structVar.NodeId, NodeClass = "Variable", Children = [_member1] };

        _service.ToggleTag(clone);

        Assert.True(_structVar.IsSelected);
        Assert.False(clone.IsSelected);
    }

    [Fact]
    public void SelectInFolder_OnFilterClone_SelectsOnlyVisibleChildrenInTheTree()
    {
        var clone = new OpcTag { NodeId = _folder.NodeId, NodeClass = "Object", Children = [_member1] };

        _service.SelectInFolder(clone, true);

        Assert.Equal(["ns=2;s=Struct.A"], _service.GetSelectedNodeIds());
    }
}
