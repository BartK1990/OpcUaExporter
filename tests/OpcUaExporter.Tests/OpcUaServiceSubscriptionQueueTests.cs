using Microsoft.Extensions.Logging.Abstractions;
using OpcUaExporter.Models;
using OpcUaExporter.Services;
using Xunit;

namespace OpcUaExporter.Tests;

/// <summary>
/// Subscription changes are queued: UI actions update the requested set at once and a background loop
/// reconciles the live subscription. No server is running here, so anything that actually reaches the
/// network fails fast (connection refused) — which is itself one of the paths under test.
/// </summary>
public class OpcUaServiceSubscriptionQueueTests
{
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(60);

    private readonly OpcTag _a = new() { NodeId = "ns=2;s=A", DisplayName = "A", NodeClass = "Variable", IsSelected = true };
    private readonly OpcTag _b = new() { NodeId = "ns=2;s=B", DisplayName = "B", NodeClass = "Variable", IsSelected = true };
    private readonly OpcUaService _service;

    public OpcUaServiceSubscriptionQueueTests()
    {
        _service = new OpcUaService(
            new OpcUaClientService(NullLogger<OpcUaClientService>.Instance, new DiagnosticsLogService()),
            NullLogger<OpcUaService>.Instance,
            new DiagnosticsLogService());
        _service.SetProfile(new ConnectionProfile { EndpointUrl = "opc.tcp://127.0.0.1:1" });
        typeof(OpcUaService).GetProperty(nameof(OpcUaService.TagTree))!.SetValue(_service, new List<OpcTag> { _a, _b });
    }

    [Fact]
    public void ToggleTagSubscribe_ReturnsImmediately_WithRequestPending()
    {
        _service.ToggleTagSubscribe(_a);

        Assert.True(_service.IsSubscriptionRequested(_a.NodeId));
        Assert.True(_service.IsSubscriptionPending);
        Assert.False(_service.IsBusy);
        Assert.False(_service.IsSubscribed);
    }

    [Fact]
    public async Task ToggleOnThenOff_WithinDebounce_CoalescesToNothing()
    {
        _service.ToggleTagSubscribe(_a);
        _service.ToggleTagSubscribe(_a);

        await _service.WaitForSubscriptionSyncAsync().WaitAsync(SyncTimeout);

        Assert.False(_service.IsSubscriptionPending);
        Assert.False(_service.IsSubscriptionRequested(_a.NodeId));
        // Never reached the (non-existent) server, so nothing failed.
        Assert.False(_service.HasError);
    }

    [Fact]
    public async Task FailedSubscribe_FallsBackToWhatIsActuallySubscribed()
    {
        _service.TrendSelected();
        Assert.True(_service.IsSubscriptionRequested(_a.NodeId));
        Assert.Equal(2, _service.TrendedNodeIds.Count);

        await _service.WaitForSubscriptionSyncAsync().WaitAsync(SyncTimeout);

        Assert.True(_service.HasError);
        Assert.False(_service.IsSubscriptionPending);
        Assert.False(_service.IsSubscriptionRequested(_a.NodeId));
        Assert.False(_service.IsSubscriptionRequested(_b.NodeId));
        Assert.Empty(_service.TrendedNodeIds);
    }

    [Fact]
    public void DeselectingATag_DropsItFromSubscriptionAndTrend()
    {
        _service.TrendSelected();

        _service.ToggleTag(_a);

        Assert.False(_service.IsSubscriptionRequested(_a.NodeId));
        Assert.True(_service.IsSubscriptionRequested(_b.NodeId));
        Assert.Equal([_b.NodeId], _service.TrendedNodeIds);
    }

    [Fact]
    public void UnsubscribingATrendedTag_RemovesItFromTheTrend()
    {
        _service.AddToTrend(_a.NodeId);

        _service.ToggleTagSubscribe(_a);

        Assert.False(_service.IsSubscriptionRequested(_a.NodeId));
        Assert.Empty(_service.TrendedNodeIds);
        Assert.False(_service.IsChartVisible);
    }

    [Fact]
    public void StopSubscription_ClearsEverythingRequested()
    {
        _service.SubscribeSelected();
        _service.AddToTrend(_a.NodeId);

        _service.StopSubscription();

        Assert.False(_service.IsSubscriptionRequested(_a.NodeId));
        Assert.False(_service.IsSubscriptionRequested(_b.NodeId));
        Assert.Empty(_service.TrendedNodeIds);
    }
}
