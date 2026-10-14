using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using OpcUaExporter.Models;
using OpcUaExporter.Services;

namespace OpcUaExporter.Operations;

/// <summary>Creates a live-value subscription over a session it takes ownership of.</summary>
public sealed class OpcUaSubscriber(ILogger logger, DiagnosticsLogService diagnostics)
{
    private readonly ILogger _logger = logger;
    private readonly DiagnosticsLogService _diagnostics = diagnostics;

    /// <summary>
    /// Creates one subscription monitoring every requested node, and hands back a
    /// handle that owns both the subscription and <paramref name="session"/>.
    /// </summary>
    /// <remarks>
    /// Disposing the handle disposes the session, so the caller passes ownership in. The
    /// handle can add and remove monitored items in place (<see cref="ILiveSubscription.UpdateAsync"/>),
    /// which is far cheaper than tearing the session down and building a new one for every
    /// change. Suited to an interactive client watching a hand-picked set of tags; a gateway
    /// watching thousands wants several subscriptions and a fast notification callback
    /// rather than one subscription with a delegate per item.
    /// </remarks>
    public async Task<(ILiveSubscription Handle, List<TagReading> InitialReadings)> SubscribeAsync(
        Session session,
        IEnumerable<string> nodeIds,
        Action<TagReading> onUpdate,
        string subscriptionDisplayName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(onUpdate);

        var ids = nodeIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count == 0)
            throw new InvalidOperationException("No tags were selected for subscription.");

        try
        {
            var initialReadings = await OpcUaValueReader.ReadCurrentValuesAsync(session, ids, ct);

            var subscription = new Subscription(session.DefaultSubscription)
            {
                DisplayName = subscriptionDisplayName,
                PublishingEnabled = true,
                PublishingInterval = 1000,
                KeepAliveCount = 10,
                LifetimeCount = 30,
                MaxNotificationsPerPublish = 0,
                Priority = 0
            };

            var handle = new SessionSubscriptionHandle(session, subscription, onUpdate, _logger, _diagnostics);
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                handle.AddItem(id);
            }

            session.AddSubscription(subscription);
            await subscription.CreateAsync(ct);

            _diagnostics.Add($"Subscription started. Monitoring {ids.Count} tag(s).");
            return (handle, initialReadings);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private sealed class SessionSubscriptionHandle : ILiveSubscription
    {
        private readonly Session _session;
        private readonly Subscription _subscription;
        private readonly Action<TagReading> _onUpdate;
        private readonly ILogger _logger;
        private readonly DiagnosticsLogService _diagnostics;
        private readonly Dictionary<string, MonitoredItem> _items = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;

        public SessionSubscriptionHandle(
            Session session,
            Subscription subscription,
            Action<TagReading> onUpdate,
            ILogger logger,
            DiagnosticsLogService diagnostics)
        {
            _session = session;
            _subscription = subscription;
            _onUpdate = onUpdate;
            _logger = logger;
            _diagnostics = diagnostics;
        }

        public IReadOnlyCollection<string> NodeIds => _items.Keys.ToList();

        public void AddItem(string id)
        {
            var monitoredItem = new MonitoredItem(_subscription.DefaultItem)
            {
                DisplayName = id,
                StartNodeId = NodeId.Parse(id),
                AttributeId = Attributes.Value,
                SamplingInterval = 1000,
                QueueSize = 100,
                DiscardOldest = true
            };

            monitoredItem.Notification += (_, e) =>
            {
                try
                {
                    if (e.NotificationValue is not MonitoredItemNotification notification)
                        return;

                    var value = notification.Value;
                    var update = new TagReading
                    {
                        NodeId = monitoredItem.DisplayName,
                        DisplayName = monitoredItem.DisplayName,
                        Value = value.WrappedValue.Value,
                        Quality = value.StatusCode.ToString(),
                        Timestamp = value.SourceTimestamp.ToString("o")
                    };

                    _onUpdate(update);
                }
                catch (Exception ex)
                {
                    // This runs on the OPC UA SDK's internal publish-response thread.
                    // An unhandled exception here (e.g. while the UI thread is busy
                    // pumping a modal dialog) would otherwise take down the whole process.
                    _logger.LogError(ex, "Error handling subscription notification for {NodeId}", monitoredItem.DisplayName);
                }
            };

            _subscription.AddItem(monitoredItem);
            _items[id] = monitoredItem;
        }

        public async Task<List<TagReading>> UpdateAsync(IReadOnlyCollection<string> nodeIds, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                var desired = new HashSet<string>(nodeIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
                var toRemove = _items.Where(kv => !desired.Contains(kv.Key)).ToList();
                var toAdd = desired.Where(id => !_items.ContainsKey(id)).ToList();

                if (toRemove.Count == 0 && toAdd.Count == 0)
                    return [];

                // The local item list changes synchronously, so if applying is canceled half-way the
                // SDK still holds the pending creates/deletes and the next ApplyChanges finishes them.
                if (toRemove.Count > 0)
                {
                    _subscription.RemoveItems(toRemove.Select(kv => kv.Value));
                    foreach (var (id, _) in toRemove)
                        _items.Remove(id);
                }

                foreach (var id in toAdd)
                    AddItem(id);

                await _subscription.ApplyChangesAsync(ct);
                _diagnostics.Add($"Subscription updated: +{toAdd.Count} / -{toRemove.Count} tag(s). Monitoring {_items.Count} tag(s).");

                return toAdd.Count > 0
                    ? await OpcUaValueReader.ReadCurrentValuesAsync(_session, toAdd, ct)
                    : [];
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (_disposed)
                    return;

                try
                {
                    if (_session.Connected)
                    {
                        await _subscription.DeleteAsync(true);
                        await _session.RemoveSubscriptionAsync(_subscription);
                    }
                }
                catch
                {
                    // best effort cleanup
                }

                _session.Dispose();
                _disposed = true;
                _diagnostics.Add("Subscription stopped.");
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
