using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Client;
using OpcUaBridge.Configuration;
using OpcUaBridge.Tags;

namespace OpcUaBridge.Acquisition;

/// <summary>
/// Lets the upstream server report changes, rather than asking it repeatedly.
/// </summary>
/// <remarks>
/// <para>
/// The default, and the right choice at a few thousand tags: the server decides what
/// actually changed, so the bridge does no work when nothing is happening and the upstream
/// load is a fraction of polling's.
/// </para>
/// <para>
/// Three choices here matter at scale, and all three differ from what the desktop exporter
/// does:
/// </para>
/// <list type="bullet">
/// <item>
/// Items are split across several subscriptions rather than one. One subscription
/// serialises publish handling, and a single bad node fails the whole create call.
/// </item>
/// <item>
/// Values arrive through <see cref="Subscription.FastDataChangeCallback"/>, which delivers
/// a whole publish in one call, instead of one delegate invocation per changed item.
/// </item>
/// <item>
/// The server-side queue is one deep. A gateway serves the current value, so asking the
/// server to buffer superseded ones costs it memory for data nobody will ever read.
/// </item>
/// </list>
/// </remarks>
public sealed class SubscriptionAcquisitionEngine(
    TagRegistry registry,
    TagValueStore values,
    IOptionsMonitor<BridgeOptions> options,
    ILogger<SubscriptionAcquisitionEngine> logger,
    TimeProvider? timeProvider = null) : IAcquisitionEngine
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly List<Subscription> _subscriptions = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Maps a monitored item's client handle to its dense tag index.</summary>
    /// <remarks>
    /// Read on the SDK's publish thread and rebuilt on reconnect, so it is replaced by
    /// swapping the reference rather than mutated in place: a publish arriving from the
    /// old session while the new one is being built would otherwise read a dictionary
    /// mid-write, which is not merely stale but undefined.
    /// </remarks>
    private volatile Dictionary<uint, int> _tagIndexByClientHandle = [];

    public AcquisitionMode Mode => AcquisitionMode.Subscription;

    public AcquisitionStatistics Statistics { get; } = new();

    public async Task StartAsync(ISession session, bool subscriptionsTransferred, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        await _gate.WaitAsync(ct);
        try
        {
            if (subscriptionsTransferred && _subscriptions.Count > 0 && _subscriptions.All(s => s.Created))
            {
                logger.LogInformation(
                    "Upstream kept {SubscriptionCount} subscription(s) across the reconnect; nothing to recreate.",
                    _subscriptions.Count);
                return;
            }

            await TearDownAsync(ct);
            await CreateSubscriptionsAsync(session, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CreateSubscriptionsAsync(ISession session, CancellationToken ct)
    {
        var acquisition = options.CurrentValue.Acquisition;

        var resolved = registry.Tags.Where(t => t.UpstreamNodeId is not null).ToList();
        var unresolved = registry.Count - resolved.Count;

        if (resolved.Count == 0)
        {
            logger.LogWarning("No mirrored tag resolved to an upstream node, so no subscription was created.");
            return;
        }

        var chunkSize = Math.Max(1, acquisition.MaxItemsPerSubscription);
        var chunkCount = (resolved.Count + chunkSize - 1) / chunkSize;

        logger.LogInformation(
            "Creating {ChunkCount} subscription(s) for {TagCount} tag(s) at a {PublishingIntervalMs}ms publishing interval.",
            chunkCount, resolved.Count, acquisition.PublishingIntervalMs);

        var filter = BuildDataChangeFilter(acquisition);

        if (filter is not null)
        {
            logger.LogInformation(
                "Upstream changes smaller than a {DeadbandType} deadband of {DeadbandValue} will not be reported. " +
                "The mirror therefore holds the last value outside that band, not the last value the server saw.",
                acquisition.Deadband, acquisition.DeadbandValue);
        }

        for (var offset = 0; offset < resolved.Count; offset += chunkSize)
        {
            ct.ThrowIfCancellationRequested();

            var chunk = resolved.GetRange(offset, Math.Min(chunkSize, resolved.Count - offset));
            var subscription = BuildSubscription(session, acquisition, _subscriptions.Count + 1);

            foreach (var tag in chunk)
            {
                subscription.AddItem(new MonitoredItem(subscription.DefaultItem)
                {
                    DisplayName = tag.BrowsePath,
                    // Non-null: the chunk is drawn from tags that resolved against this session.
                    StartNodeId = tag.UpstreamNodeId!,
                    AttributeId = Attributes.Value,
                    SamplingInterval = acquisition.SamplingIntervalMs,
                    QueueSize = (uint)acquisition.QueueSize,
                    DiscardOldest = true,
                    MonitoringMode = MonitoringMode.Reporting,
                    // Shared between items: the SDK encodes it per item and never mutates it.
                    Filter = filter,
                    Handle = tag
                });
            }

            session.AddSubscription(subscription);

            // One create call per subscription, so the SDK batches CreateMonitoredItems
            // rather than issuing one service call per tag.
            await subscription.CreateAsync(ct);

            RecordClientHandles(subscription);
            _subscriptions.Add(subscription);
        }

        var failed = _subscriptions
            .SelectMany(s => s.MonitoredItems)
            .Count(i => ServiceResult.IsBad(i.Status.Error));

        if (failed > 0 || unresolved > 0)
        {
            logger.LogWarning(
                "{FailedCount} monitored item(s) were rejected by the server and {UnresolvedCount} tag(s) " +
                "could not be resolved. Those tags will report a bad status until the namespace is captured again.",
                failed, unresolved);

            if (filter is not null)
            {
                // A percent deadband is only defined for a node that publishes an EURange,
                // which discrete and string tags do not. Naming it here saves working back
                // from a BadFilterNotAllowed on a tag that was fine yesterday.
                logger.LogWarning(
                    "A deadband is configured, and a server rejects one on any tag it does not apply to -- " +
                    "a percent deadband needs the tag to publish an EURange. Set Bridge:Acquisition:Deadband " +
                    "to None if those tags matter more than the saving.");
            }
        }

        logger.LogInformation(
            "Subscribed to {ItemCount} tag(s) across {SubscriptionCount} subscription(s).",
            _tagIndexByClientHandle.Count, _subscriptions.Count);
    }

    /// <summary>
    /// The server-side filter, or null to report every change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Filtering at the server is worth far more than filtering here would be: a value
    /// that is never reported costs the upstream server no notification, the network no
    /// bytes, this process no publish handling, and the mirror no node update. At a few
    /// tens of thousands of values a second, that is the difference the CPU shows.
    /// </para>
    /// <para>
    /// <see cref="DataChangeTrigger.StatusValue"/> rather than value alone, because a tag
    /// going bad must reach the downstream application even when its number has not moved.
    /// A gateway that swallowed that would be worse than useless.
    /// </para>
    /// </remarks>
    internal static DataChangeFilter? BuildDataChangeFilter(AcquisitionOptions acquisition)
    {
        if (acquisition.Deadband == AcquisitionDeadband.None || acquisition.DeadbandValue <= 0)
            return null;

        return new DataChangeFilter
        {
            Trigger = DataChangeTrigger.StatusValue,
            DeadbandType = (uint)(acquisition.Deadband == AcquisitionDeadband.Percent
                ? Opc.Ua.DeadbandType.Percent
                : Opc.Ua.DeadbandType.Absolute),
            DeadbandValue = acquisition.DeadbandValue
        };
    }

    private Subscription BuildSubscription(ISession session, AcquisitionOptions acquisition, int ordinal)
    {
        // Keep-alive and lifetime are expressed in publishing intervals, so a slow
        // publishing interval does not silently shorten the subscription's life. The
        // specification requires the lifetime to be at least three keep-alives.
        var keepAliveCount = (uint)Math.Max(3, 10_000 / Math.Max(1, acquisition.PublishingIntervalMs));

        var subscription = new Subscription(session.DefaultSubscription)
        {
            DisplayName = $"OpcUaBridge#{ordinal}",
            PublishingInterval = acquisition.PublishingIntervalMs,
            KeepAliveCount = keepAliveCount,
            LifetimeCount = keepAliveCount * 3,
            MaxNotificationsPerPublish = 0,
            Priority = 0,
            PublishingEnabled = true,
            TimestampsToReturn = TimestampsToReturn.Both,

            // The SDK's per-item value cache is memory we would never read: the bridge's
            // own value store is the cache.
            DisableMonitoredItemCache = true
        };

        subscription.FastDataChangeCallback = OnDataChange;
        return subscription;
    }

    /// <summary>
    /// Records the client handles the SDK assigned, publishing them as one atomic swap.
    /// </summary>
    /// <remarks>
    /// Client handles are only assigned once the items have been created, so this cannot
    /// happen before <c>CreateAsync</c>.
    /// </remarks>
    private void RecordClientHandles(Subscription subscription)
    {
        var updated = new Dictionary<uint, int>(_tagIndexByClientHandle);

        foreach (var item in subscription.MonitoredItems)
        {
            if (item.Handle is MirrorTag tag)
                updated[item.ClientHandle] = tag.Index;
        }

        _tagIndexByClientHandle = updated;
    }

    /// <summary>
    /// Receives a whole publish response from the SDK's publish thread.
    /// </summary>
    /// <remarks>
    /// This is the hottest callback in the process and it runs on a thread the SDK needs
    /// back promptly. It does no I/O, takes no lock and must never throw: an escaping
    /// exception here would take the process down, and a slow one stalls the upstream
    /// feed for every tag.
    /// </remarks>
    private void OnDataChange(Subscription subscription, DataChangeNotification notification, IList<string> stringTable)
    {
        try
        {
            var count = 0;

            // One volatile read, so the whole publish is handled against a consistent map
            // even if a reconnect swaps it mid-loop.
            var tagIndexByClientHandle = _tagIndexByClientHandle;

            foreach (var item in notification.MonitoredItems)
            {
                if (!tagIndexByClientHandle.TryGetValue(item.ClientHandle, out var tagIndex))
                    continue;

                values.Publish(tagIndex, item.Value);
                count++;
            }

            if (count > 0)
                Statistics.RecordCycle(count, TimeSpan.Zero, _time.GetUtcNow());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle an upstream publish for subscription {Subscription}.",
                subscription.DisplayName);
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await TearDownAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task TearDownAsync(CancellationToken ct)
    {
        foreach (var subscription in _subscriptions)
        {
            try
            {
                subscription.FastDataChangeCallback = null;

                if (subscription.Created && subscription.Session is { } session)
                    await session.RemoveSubscriptionAsync(subscription, ct);
            }
            catch (Exception ex)
            {
                // The link is usually already gone, which is why we are tearing down.
                logger.LogDebug(ex, "Removing subscription {Subscription} reported an error.", subscription.DisplayName);
            }
        }

        _subscriptions.Clear();
        _tagIndexByClientHandle = [];
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _gate.Dispose();
    }
}
