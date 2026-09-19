using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opc.Ua.Client;
using OpcUaBridge.Configuration;
using OpcUaBridge.Tags;
using OpcUaBridge.Upstream;

namespace OpcUaBridge.Acquisition;

/// <summary>Creates the engine the configuration asks for.</summary>
public sealed class AcquisitionEngineFactory(
    TagRegistry registry,
    TagValueStore values,
    IOptionsMonitor<BridgeOptions> options,
    ILoggerFactory loggerFactory,
    TimeProvider? timeProvider = null)
{
    public IAcquisitionEngine Create(AcquisitionMode mode) => mode switch
    {
        AcquisitionMode.Polling => new PollingAcquisitionEngine(
            registry, values, options, loggerFactory.CreateLogger<PollingAcquisitionEngine>(), timeProvider),
        _ => new SubscriptionAcquisitionEngine(
            registry, values, options, loggerFactory.CreateLogger<SubscriptionAcquisitionEngine>(), timeProvider)
    };
}

/// <summary>
/// Keeps acquisition in step with the upstream connection.
/// </summary>
/// <remarks>
/// The engines know how to read; this knows when. It starts one when a session becomes
/// available, rebuilds it after a reconnect that did not preserve the subscriptions, and
/// flags every cached value as no longer trustworthy the moment the link drops -- which is
/// what keeps the downstream application connected and honestly informed rather than
/// disconnected or lied to.
/// </remarks>
public sealed class AcquisitionCoordinator(
    IUpstreamConnection upstream,
    AcquisitionEngineFactory engineFactory,
    TagRegistry registry,
    TagStalenessMonitor staleness,
    IOptionsMonitor<BridgeOptions> options,
    ILogger<AcquisitionCoordinator> logger) : IHostedService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IAcquisitionEngine? _engine;

    /// <summary>The running engine, for the dashboard.</summary>
    public IAcquisitionEngine? Engine => _engine;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        upstream.SessionReady += OnSessionReady;
        upstream.SessionLost += OnSessionLost;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        upstream.SessionReady -= OnSessionReady;
        upstream.SessionLost -= OnSessionLost;

        if (_engine is not null)
            await _engine.DisposeAsync();
    }

    /// <summary>Swaps between subscription and polling while the bridge is running.</summary>
    public async Task SwitchModeAsync(AcquisitionMode mode, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_engine?.Mode == mode)
                return;

            logger.LogInformation("Switching acquisition to {Mode} mode.", mode);

            if (_engine is not null)
            {
                await _engine.DisposeAsync();
                _engine = null;
            }

            if (upstream.Session is { } session)
                await StartEngineAsync(session, subscriptionsTransferred: false, mode, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnSessionReady(object? sender, SessionReadyEventArgs e)
        => _ = HandleSessionReadyAsync(e);

    private async Task HandleSessionReadyAsync(SessionReadyEventArgs e)
    {
        await _gate.WaitAsync();
        try
        {
            // Re-resolve before touching the engine. A namespace index is only meaningful
            // within the session that reported it, so every NodeId the engine is about to
            // use has to be rebuilt from its namespace URI against this session's table.
            var missing = registry.ResolveAgainst(e.Session.NamespaceUris, logger);
            if (upstream is UpstreamConnectionManager manager)
                manager.ReportMissingNamespaces(missing);

            staleness.UpstreamRestored();
            await StartEngineAsync(e.Session, e.SubscriptionsTransferred, options.CurrentValue.Acquisition.Mode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start acquisition on the new upstream session.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartEngineAsync(
        ISession session,
        bool subscriptionsTransferred,
        AcquisitionMode mode,
        CancellationToken ct = default)
    {
        _engine ??= engineFactory.Create(mode);
        await _engine.StartAsync(session, subscriptionsTransferred, ct);
    }

    /// <summary>
    /// Marks the cached values as no longer trustworthy, on the applier's thread.
    /// </summary>
    /// <remarks>
    /// This event is raised from the SDK's keep-alive callback. Sweeping thousands of tags
    /// inline there would block the very thread the rest of this code goes out of its way
    /// to keep free, and allocate a value per tag at the worst possible moment. So the
    /// handler only records that the link is down; <see cref="TagStalenessMonitor"/> does
    /// the work, and keeps doing it so an outage that outlasts the grace period degrades
    /// from uncertain to bad as promised.
    /// </remarks>
    private void OnSessionLost(object? sender, EventArgs e) => staleness.UpstreamLost();
}
