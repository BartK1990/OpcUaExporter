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
    /// Disposing the handle disposes the session, so the caller passes ownership in.
    /// Suited to an interactive client watching a hand-picked set of tags; a gateway
    /// watching thousands wants several subscriptions and a fast notification callback
    /// rather than one subscription with a delegate per item.
    /// </remarks>
    public async Task<(IAsyncDisposable Handle, List<TagReading> InitialReadings)> SubscribeAsync(
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

            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();

                var monitoredItem = new MonitoredItem(subscription.DefaultItem)
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

                        onUpdate(update);
                    }
                    catch (Exception ex)
                    {
                        // This runs on the OPC UA SDK's internal publish-response thread.
                        // An unhandled exception here (e.g. while the UI thread is busy
                        // pumping a modal dialog) would otherwise take down the whole process.
                        _logger.LogError(ex, "Error handling subscription notification for {NodeId}", monitoredItem.DisplayName);
                    }
                };

                subscription.AddItem(monitoredItem);
            }

            session.AddSubscription(subscription);
            await subscription.CreateAsync(ct);

            _diagnostics.Add($"Subscription started. Monitoring {ids.Count} tag(s).");

            var handle = new SessionSubscriptionHandle(session, subscription, _diagnostics);
            return (handle, initialReadings);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private sealed class SessionSubscriptionHandle : IAsyncDisposable
    {
        private readonly Session _session;
        private readonly Subscription _subscription;
        private readonly DiagnosticsLogService _diagnostics;
        private bool _disposed;

        public SessionSubscriptionHandle(Session session, Subscription subscription, DiagnosticsLogService diagnostics)
        {
            _session = session;
            _subscription = subscription;
            _diagnostics = diagnostics;
        }

        public async ValueTask DisposeAsync()
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
    }
}
