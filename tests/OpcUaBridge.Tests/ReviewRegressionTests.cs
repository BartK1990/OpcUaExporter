using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using OpcUaBridge.Acquisition;
using OpcUaBridge.Namespaces;
using OpcUaBridge.Tags;
using OpcUaBridge.Upstream;
using Xunit;

namespace OpcUaBridge.Tests;

/// <summary>
/// Covers defects found by review of the gateway's first implementation.
/// </summary>
/// <remarks>
/// Each of these was a real failure the existing tests did not catch, and each would have
/// shown up only in production: a service that will not start, a gateway that silently
/// stops reconnecting, an outage that never escalates.
/// </remarks>
public class DuplicateSnapshotNodeTests
{
    private const string PlantUri = "http://plant/data";

    /// <summary>
    /// A node reachable by two browse paths appears twice in the snapshot.
    /// </summary>
    /// <remarks>
    /// The browse records every forward reference it follows, so a variable organised
    /// under two folders is captured under both. That is legitimate, and the registry has
    /// to cope with it.
    /// </remarks>
    private static NamespaceSnapshot SnapshotWithDuplicate() => new()
    {
        NamespaceUris = [Opc.Ua.Namespaces.OpcUa, "urn:plant:Server", PlantUri],
        Nodes =
        [
            new SnapshotNode
            {
                NamespaceIndex = 2, Identifier = "s=Line1", BrowseName = "Line1",
                Children =
                [
                    new SnapshotNode
                    {
                        NamespaceIndex = 2, Identifier = "s=Shared.Speed", BrowseName = "Speed",
                        IsVariable = true, DataType = "Double", AccessLevel = 1
                    }
                ]
            },
            new SnapshotNode
            {
                NamespaceIndex = 2, Identifier = "s=Line2", BrowseName = "Line2",
                Children =
                [
                    new SnapshotNode
                    {
                        NamespaceIndex = 2, Identifier = "s=Shared.Speed", BrowseName = "Speed",
                        IsVariable = true, DataType = "Double", AccessLevel = 1
                    }
                ]
            }
        ]
    };

    [Fact]
    public void TryGetTagByUpstreamIdentity_DoesNotThrowWhenTheSameNodeWasReachedTwice()
    {
        // ToDictionary would have thrown here, and it throws while the mirrored address
        // space is being built -- so the whole service failed to start, reporting a
        // problem with ports and certificates.
        var registry = TagRegistry.FromSnapshot(SnapshotWithDuplicate());

        Assert.True(registry.TryGetTagByUpstreamIdentity(PlantUri, "s=Shared.Speed", out var tag));
        Assert.Equal(0, tag.Index);
    }

    [Fact]
    public void SetMirrorNodeId_RefusesASecondTagClaimingTheSameNodeId()
    {
        var registry = TagRegistry.FromSnapshot(SnapshotWithDuplicate());
        var nodeId = NodeId.Parse("ns=2;s=Shared.Speed");

        Assert.True(registry.SetMirrorNodeId(registry.Tags[0], nodeId));
        Assert.False(registry.SetMirrorNodeId(registry.Tags[1], nodeId));

        // The first claim keeps the mapping, so the mirror publishes one node per NodeId.
        Assert.True(registry.TryGetByMirrorNodeId(nodeId, out var mapped));
        Assert.Same(registry.Tags[0], mapped);
    }
}

public class TransientUpstreamFailureTests
{
    [Fact]
    public void IsUnrecoverable_IsFalseWhenAServerAnswersDiscoveryWithNoEndpoints()
    {
        // A server that is still starting up does exactly this. Treating it as fatal
        // stranded the gateway in Faulted while the plant server merely rebooted -- the
        // one thing it exists to ride out.
        var stillStarting = new ServiceResultException(
            StatusCodes.BadServerNotConnected, "No OPC UA endpoints were returned by the server.");

        Assert.False(UpstreamConnectionManager.IsUnrecoverable(stillStarting));
    }

    [Fact]
    public void IsUnrecoverable_IsStillTrueWhenNoEndpointMatchesTheConfiguredSecurity()
    {
        Assert.True(UpstreamConnectionManager.IsUnrecoverable(
            new InvalidOperationException("No endpoint matches SecurityMode='SignAndEncrypt'.")));
    }
}

public class AcquisitionStatisticsConcurrencyTests
{
    [Fact]
    public void RecordCycle_PublishesTheDurationAndTimestampWithoutTearing()
    {
        // These are read from the Blazor circuit while the SDK's publish thread writes
        // them. As a TimeSpan and a DateTimeOffset? their multi-word stores could be read
        // half-updated, rendering a nonsense age on the dashboard.
        var statistics = new AcquisitionStatistics();
        var completed = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

        statistics.RecordCycle(10, TimeSpan.FromMilliseconds(250), completed);

        Assert.Equal(TimeSpan.FromMilliseconds(250), statistics.LastCycleDuration);
        Assert.Equal(completed, statistics.LastUpdateUtc);
    }

    [Fact]
    public void LastUpdateUtc_IsNullBeforeTheFirstCycle()
    {
        Assert.Null(new AcquisitionStatistics().LastUpdateUtc);
    }

    [Fact]
    public void RecordCycle_StaysConsistentUnderConcurrentWriters()
    {
        var statistics = new AcquisitionStatistics();
        var completed = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

        Parallel.For(0, 2_000, _ => statistics.RecordCycle(1, TimeSpan.FromMilliseconds(5), completed));

        Assert.Equal(2_000, statistics.ValuesReceived);
        Assert.Equal(2_000, statistics.CyclesCompleted);
        Assert.Equal(TimeSpan.FromMilliseconds(5), statistics.LastCycleDuration);
        Assert.Equal(completed, statistics.LastUpdateUtc);
    }
}

public class OutageEscalationTests
{
    private static DataValue Good(object value, DateTime sourceTimestamp) =>
        new(new Variant(value)) { StatusCode = StatusCodes.Good, SourceTimestamp = sourceTimestamp };

    [Fact]
    public void RepeatedSweeps_EscalateAnOutageThatOutlivesTheGracePeriod()
    {
        // The first sweep of an outage finds every value seconds old, so nothing is past
        // the grace period yet. Sweeping only once meant a bridge disconnected overnight
        // still served fourteen-hour-old numbers as merely "uncertain" the next morning.
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
        var store = new TagValueStore(1, time);
        store.Publish(0, Good(42.0, time.GetUtcNow().UtcDateTime));

        var graceperiod = TimeSpan.FromMinutes(5);

        store.MarkStale(graceperiod);
        Assert.Equal(StatusCodes.UncertainLastUsableValue, store.Read(0)!.StatusCode.Code);

        time.Advance(TimeSpan.FromMinutes(10));
        store.MarkStale(graceperiod);

        Assert.Equal(StatusCodes.BadNoCommunication, store.Read(0)!.StatusCode.Code);
    }
}
