using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Client;
using OpcUaBridge.Configuration;
using OpcUaExporter.Sessions;

namespace OpcUaBridge.Upstream;

/// <summary>
/// Keeps one session to the upstream server open, and puts it back when it drops.
/// </summary>
/// <remarks>
/// <para>
/// This is the reason the bridge exists. The application behind it keeps losing its
/// connection to a flaky server; here that failure is absorbed -- detected by keep-alive,
/// recovered by the SDK's reconnect handler, and retried with backoff for as long as it
/// takes -- so the downstream endpoint never has to drop its own clients.
/// </para>
/// <para>
/// Failures are split in two. Anything transient (timeouts, refused connections, a server
/// that is down) is retried forever, because a gateway that gives up is useless. Anything
/// the server has told us plainly it will not accept -- a rejected certificate, bad
/// credentials, no endpoint matching the configured security -- moves to
/// <see cref="UpstreamConnectionState.Faulted"/> and waits, because retrying that is just
/// noise in the log and load on the server.
/// </para>
/// </remarks>
public sealed class UpstreamConnectionManager : BackgroundService, IUpstreamConnection
{
    private readonly IOptionsMonitor<BridgeOptions> _options;
    private readonly OpcUaSessionFactory _sessionFactory;
    private readonly ILogger<UpstreamConnectionManager> _logger;
    private readonly TimeProvider _time;

    private readonly SemaphoreSlim _reconnectRequested = new(0, 1);
    private readonly Lock _stateGate = new();

    private Session? _session;
    private SessionReconnectHandler? _reconnectHandler;
    private int _epoch;
    private int _reconnectCount;
    private volatile bool _reconnectInFlight;

    private UpstreamConnectionState _state = UpstreamConnectionState.Disconnected;
    private DateTimeOffset _stateSince;
    private DateTimeOffset? _connectedSince;
    private DateTimeOffset? _lastKeepAliveUtc;
    private string? _lastError;
    private IReadOnlyList<string> _missingNamespaceUris = [];

    public UpstreamConnectionManager(
        IOptionsMonitor<BridgeOptions> options,
        OpcUaSessionFactory sessionFactory,
        ILogger<UpstreamConnectionManager> logger,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _sessionFactory = sessionFactory;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _stateSince = _time.GetUtcNow();
    }

    public UpstreamConnectionState State
    {
        get { lock (_stateGate) return _state; }
    }

    public ISession? Session => State == UpstreamConnectionState.Connected ? _session : null;

    public UpstreamStatus Status
    {
        get
        {
            lock (_stateGate)
            {
                return new UpstreamStatus(
                    _state, _stateSince, _connectedSince, _reconnectCount, _epoch,
                    _lastError, _lastKeepAliveUtc, _missingNamespaceUris);
            }
        }
    }

    public event EventHandler<UpstreamStatus>? StateChanged;
    public event EventHandler<SessionReadyEventArgs>? SessionReady;
    public event EventHandler? SessionLost;

    public ISession RequireSession()
        => Session ?? throw new ServiceResultException(
            StatusCodes.BadNoCommunication,
            $"The upstream OPC UA server is not connected (state: {State}).");

    public void RequestReconnect()
    {
        // The semaphore is capped at one permit: several operators clicking "reconnect"
        // should produce one attempt, not a queue of them.
        try { _reconnectRequested.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>Records which snapshot namespaces the server no longer publishes, for the dashboard.</summary>
    public void ReportMissingNamespaces(IReadOnlyList<string> missingNamespaceUris)
    {
        lock (_stateGate)
            _missingNamespaceUris = missingNamespaceUris;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var upstream = _options.CurrentValue.Upstream;
        var backoff = new ExponentialBackoff(
            TimeSpan.FromMilliseconds(upstream.ReconnectMinDelayMs),
            TimeSpan.FromMilliseconds(upstream.ReconnectMaxDelayMs));

        // Every wait below throws when the service stops, so the shutdown close has to sit
        // in a finally: leaving the session open would strand it on the upstream server
        // until its timeout expires, and DeleteSubscriptionsOnClose is off, so the server
        // would hold thousands of monitored items too. A restart loop would stack those up.
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (State is UpstreamConnectionState.Connected or UpstreamConnectionState.Reconnecting)
                {
                    await WaitForWorkAsync(stoppingToken);
                    continue;
                }

                if (State == UpstreamConnectionState.Faulted)
                {
                    // Wait indefinitely for an operator to fix the cause and ask us to retry.
                    await _reconnectRequested.WaitAsync(stoppingToken);
                    SetState(UpstreamConnectionState.Disconnected);
                    backoff.Reset();
                    continue;
                }

                if (await TryConnectAsync(stoppingToken))
                {
                    backoff.Reset();
                    continue;
                }

                if (State == UpstreamConnectionState.Faulted)
                    continue;

                var delay = backoff.Next();
                _logger.LogWarning(
                    "Upstream connect attempt {Attempt} failed; retrying in {DelaySeconds:F1}s. {Error}",
                    backoff.Attempt, delay.TotalSeconds, _lastError);

                // Wait on the operator's request rather than a plain timer, so "Reconnect
                // now" works while a connection is failing. That is exactly when it is
                // reached for -- an operator who has just fixed a certificate or an
                // endpoint should not sit through the remaining backoff wondering whether
                // the button did anything.
                if (await WaitForRetrySignalAsync(delay, stoppingToken))
                {
                    _logger.LogInformation("Reconnect requested by an operator; retrying immediately.");
                    backoff.Reset();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Ordinary shutdown.
        }
        finally
        {
            CancelReconnect();
            await CloseSessionAsync();
        }
    }

    private async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        var upstream = _options.CurrentValue.Upstream;
        SetState(UpstreamConnectionState.Connecting);

        try
        {
            _logger.LogInformation("Connecting to upstream OPC UA server at {EndpointUrl}.", upstream.EndpointUrl);

            var session = await _sessionFactory.CreateAsync(
                upstream.ToConnectionProfile(), upstream.SessionTimeoutMs, ct);

            AttachSession(session, upstream);

            lock (_stateGate)
            {
                _epoch++;
                _connectedSince = _time.GetUtcNow();
                _lastKeepAliveUtc = _connectedSince;
                _lastError = null;
            }

            SetState(UpstreamConnectionState.Connected);
            _logger.LogInformation(
                "Upstream OPC UA session established (epoch {Epoch}, keep-alive every {KeepAliveMs}ms).",
                _epoch, upstream.KeepAliveIntervalMs);

            RaiseSessionReady(session, subscriptionsTransferred: false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_stateGate) _lastError = ex.Message;

            if (IsUnrecoverable(ex))
            {
                _logger.LogError(ex,
                    "Upstream connection cannot succeed as configured, so the bridge has stopped retrying. " +
                    "Fix the cause and reconnect from the dashboard.");
                SetState(UpstreamConnectionState.Faulted);
            }

            return false;
        }
    }

    private void AttachSession(Session session, UpstreamOptions upstream)
    {
        session.KeepAliveInterval = upstream.KeepAliveIntervalMs;

        // Ask the server to keep our subscriptions across a reconnect, and not to delete
        // them when a session closes. Transferring 5000 monitored items beats recreating
        // them, both for us and for the server.
        session.TransferSubscriptionsOnReconnect = true;
        session.DeleteSubscriptionsOnClose = false;

        session.KeepAlive += OnKeepAlive;
        _session = session;
    }

    private void OnKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        // Runs on the SDK's own thread. An exception escaping here would take down the
        // process, so nothing in this method is allowed to throw.
        try
        {
            if (!ReferenceEquals(session, _session))
                return;                                     // a stale event from a session we already replaced

            if (ServiceResult.IsGood(e.Status))
            {
                lock (_stateGate) _lastKeepAliveUtc = _time.GetUtcNow();
                return;
            }

            lock (_stateGate) _lastError = e.Status?.ToString() ?? "Keep-alive reported a bad status.";

            // Stop the SDK's own keep-alive retries: we are taking over with the
            // reconnect handler, and two recovery mechanisms racing helps nobody.
            e.CancelKeepAlive = true;
            BeginReconnect(session);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle an upstream keep-alive notification.");
        }
    }

    private void BeginReconnect(ISession session)
    {
        lock (_stateGate)
        {
            if (_reconnectInFlight)
                return;

            _reconnectInFlight = true;
        }

        var upstream = _options.CurrentValue.Upstream;
        _logger.LogWarning("Upstream keep-alive failed ({Error}); reconnecting.", _lastError);

        SetState(UpstreamConnectionState.Reconnecting);
        SessionLost?.Invoke(this, EventArgs.Empty);

        _reconnectHandler?.Dispose();
        _reconnectHandler = new SessionReconnectHandler(
            session.MessageContext.Telemetry,
            reconnectAbort: true,
            maxReconnectPeriod: upstream.ReconnectMaxDelayMs);

        _reconnectHandler.BeginReconnect(session, upstream.ReconnectMinDelayMs, OnReconnectComplete);
    }

    private void OnReconnectComplete(object? sender, EventArgs e)
    {
        try
        {
            if (!ReferenceEquals(sender, _reconnectHandler))
                return;                                     // superseded by a newer attempt

            var recovered = _reconnectHandler?.Session as Session;
            if (recovered is null)
                return;                                     // still failing; the handler keeps trying

            // Three outcomes, and an acquisition engine recovers differently from each:
            // the same session reactivated (subscriptions still live), a new session that
            // inherited them, or a new session with nothing (the server restarted).
            var sameSession = ReferenceEquals(recovered, _session);
            if (!sameSession)
            {
                var previous = _session;
                if (previous is not null)
                    previous.KeepAlive -= OnKeepAlive;

                AttachSession(recovered, _options.CurrentValue.Upstream);

                lock (_stateGate) _epoch++;
            }

            var transferred = sameSession || recovered.Subscriptions.Any(s => s.Created);

            lock (_stateGate)
            {
                _reconnectCount++;
                _connectedSince = _time.GetUtcNow();
                _lastKeepAliveUtc = _connectedSince;
                _lastError = null;
                _reconnectInFlight = false;
            }

            SetState(UpstreamConnectionState.Connected);
            _logger.LogInformation(
                "Upstream reconnected (epoch {Epoch}, reconnect #{ReconnectCount}). Subscriptions {Disposition}.",
                _epoch, _reconnectCount, transferred ? "were transferred" : "must be recreated");

            RaiseSessionReady(recovered, transferred);
        }
        catch (Exception ex)
        {
            _reconnectInFlight = false;
            _logger.LogError(ex, "Failed to complete an upstream reconnect.");
        }
    }

    private void RaiseSessionReady(ISession session, bool subscriptionsTransferred)
        => SessionReady?.Invoke(this, new SessionReadyEventArgs(session, _epoch, subscriptionsTransferred));

    /// <summary>
    /// Whether an error means retrying is pointless until a human changes something.
    /// </summary>
    /// <remarks>
    /// The distinction matters: a bridge that retries a rejected certificate every second
    /// forever buries the real problem in log noise and loads a server that has already
    /// said no.
    /// </remarks>
    internal static bool IsUnrecoverable(Exception exception)
    {
        if (exception is ServiceResultException serviceResult)
        {
            return serviceResult.StatusCode switch
            {
                StatusCodes.BadCertificateUntrusted
                    or StatusCodes.BadCertificateInvalid
                    or StatusCodes.BadCertificateTimeInvalid
                    or StatusCodes.BadCertificateHostNameInvalid
                    or StatusCodes.BadCertificateUriInvalid
                    or StatusCodes.BadCertificateUseNotAllowed
                    or StatusCodes.BadSecurityChecksFailed
                    or StatusCodes.BadUserAccessDenied
                    or StatusCodes.BadIdentityTokenInvalid
                    or StatusCodes.BadIdentityTokenRejected => true,
                _ => false
            };
        }

        // Endpoint selection throws a plain InvalidOperationException when nothing the
        // server offers matches the configured security mode, policy or authentication
        // type. That genuinely cannot resolve itself.
        //
        // Failures that only look like configuration errors must not land here. A server
        // answering discovery with an empty endpoint list because it is still starting up
        // raises a ServiceResultException instead, so it is classified above as transient:
        // stranding the gateway in Faulted while the plant server merely reboots is the one
        // thing it exists to ride out.
        return exception is InvalidOperationException;
    }

    private async Task WaitForWorkAsync(CancellationToken ct)
    {
        // Wake either when an operator asks for a reconnect, or periodically so the
        // watchdog below can notice a session that went quiet without an event.
        if (await _reconnectRequested.WaitAsync(TimeSpan.FromSeconds(5), ct))
        {
            if (_session is not null)
            {
                _logger.LogInformation("Reconnect requested by an operator.");

                // Abandon any SDK reconnect already in flight. Its completion callback
                // will not run for the session we are about to close, so this is the only
                // place _reconnectInFlight can be cleared -- and leaving it set would
                // silently disable the keep-alive handler and the watchdog for the rest
                // of the process's life.
                CancelReconnect();
                await CloseSessionAsync();
                SetState(UpstreamConnectionState.Disconnected);
            }

            return;
        }

        CheckKeepAliveWatchdog();
    }

    /// <summary>
    /// Forces a reconnect when keep-alives simply stopped arriving.
    /// </summary>
    /// <remarks>
    /// Belt and braces over the SDK's own detection: a half-open TCP connection can leave
    /// a session that never reports a failure and never receives anything either, which
    /// would otherwise leave the bridge serving stale values while believing it is healthy.
    /// </remarks>
    private void CheckKeepAliveWatchdog()
    {
        var session = _session;
        if (session is null || State != UpstreamConnectionState.Connected || _reconnectInFlight)
            return;

        var interval = TimeSpan.FromMilliseconds(_options.CurrentValue.Upstream.KeepAliveIntervalMs);
        var silenceLimit = interval * 3;

        DateTimeOffset? lastKeepAlive;
        lock (_stateGate) lastKeepAlive = _lastKeepAliveUtc;

        if (lastKeepAlive is null || _time.GetUtcNow() - lastKeepAlive.Value <= silenceLimit)
            return;

        _logger.LogWarning(
            "No upstream keep-alive for {SilenceSeconds:F0}s (limit {LimitSeconds:F0}s); forcing a reconnect.",
            (_time.GetUtcNow() - lastKeepAlive.Value).TotalSeconds, silenceLimit.TotalSeconds);

        lock (_stateGate) _lastError = "Keep-alive watchdog timed out.";
        BeginReconnect(session);
    }

    /// <summary>Abandons an in-flight SDK reconnect and re-arms the keep-alive path.</summary>
    private void CancelReconnect()
    {
        var handler = Interlocked.Exchange(ref _reconnectHandler, null);

        try
        {
            handler?.CancelReconnect();
            handler?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cancelling the upstream reconnect handler reported an error.");
        }

        lock (_stateGate)
            _reconnectInFlight = false;
    }

    /// <summary>
    /// Waits out the backoff, returning early when an operator asks to retry.
    /// </summary>
    /// <returns>True when the wait was cut short by a reconnect request.</returns>
    private async Task<bool> WaitForRetrySignalAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            return await _reconnectRequested.WaitAsync(delay, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private void SetState(UpstreamConnectionState state)
    {
        UpstreamStatus status;

        lock (_stateGate)
        {
            if (_state == state)
                return;

            _state = state;
            _stateSince = _time.GetUtcNow();

            if (state != UpstreamConnectionState.Connected)
                _connectedSince = null;

            status = new UpstreamStatus(
                _state, _stateSince, _connectedSince, _reconnectCount, _epoch,
                _lastError, _lastKeepAliveUtc, _missingNamespaceUris);
        }

        if (state is UpstreamConnectionState.Disconnected or UpstreamConnectionState.Faulted)
            SessionLost?.Invoke(this, EventArgs.Empty);

        StateChanged?.Invoke(this, status);
    }

    private async Task CloseSessionAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null)
            return;

        session.KeepAlive -= OnKeepAlive;

        try
        {
            await session.CloseAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The link is probably already gone; that is why we are closing.
            _logger.LogDebug(ex, "Closing the upstream session reported an error.");
        }
        finally
        {
            session.Dispose();
        }
    }

    public override void Dispose()
    {
        _reconnectHandler?.Dispose();
        _reconnectRequested.Dispose();
        base.Dispose();
    }
}
