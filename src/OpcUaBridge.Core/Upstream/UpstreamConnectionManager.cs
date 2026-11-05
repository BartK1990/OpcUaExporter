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

    // Serialises the SDK's reconnect-complete callback against the supervisor abandoning
    // that reconnect, so a session recovered at the last moment is either adopted or
    // disposed -- never attached after the supervisor has already moved on.
    private readonly Lock _reconnectGate = new();

    /// <summary>Why the supervisor was asked for a fresh session; null when an operator asked.</summary>
    private string? _freshSessionReason;

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

    public void RequestReconnect() => SignalSupervisor();

    /// <summary>
    /// Asks the supervisor loop to drop the current session and open a fresh one.
    /// </summary>
    /// <remarks>
    /// Used from SDK threads, which must not block on closing a session themselves.
    /// </remarks>
    private void RequestFreshSession(string reason)
    {
        Interlocked.Exchange(ref _freshSessionReason, reason);
        SignalSupervisor();
    }

    private void SignalSupervisor()
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
            lock (_reconnectGate) CancelReconnect();
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

    /// <remarks>Internal so tests can trigger the reconnect path on a healthy session.</remarks>
    internal void BeginReconnect(ISession session)
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

        lock (_reconnectGate)
        {
            _reconnectHandler?.Dispose();
            _reconnectHandler = new SessionReconnectHandler(
                session.MessageContext.Telemetry,
                reconnectAbort: true,
                maxReconnectPeriod: upstream.ReconnectMaxDelayMs);

            _reconnectHandler.BeginReconnect(session, upstream.ReconnectMinDelayMs, OnReconnectComplete);
        }
    }

    private void OnReconnectComplete(object? sender, EventArgs e)
    {
        try
        {
            Session recovered;
            bool transferred;

            lock (_reconnectGate)
            {
                var handler = sender as SessionReconnectHandler;

                if (handler is null || !ReferenceEquals(handler, _reconnectHandler))
                {
                    // Superseded: the supervisor gave up on this attempt, or a newer one
                    // replaced it. A session it recreated at the last moment belongs to
                    // nobody now, and would sit on the upstream server holding thousands
                    // of monitored items until it timed out.
                    if (handler?.Session is Session orphan && !ReferenceEquals(orphan, _session))
                        _ = DisposeSessionAsync(orphan);
                    return;
                }

                // The handler calls back exactly once per attempt, and is idle afterwards:
                // it does not keep trying. A null session does not mean "still failing" --
                // it is the SDK's way of saying it aborted the reconnect because keep-alives
                // on the existing session came back by themselves. Treating it as anything
                // else leaves the bridge in Reconnecting forever, with nothing left running
                // that would ever leave that state.
                var candidate = handler.Session as Session;
                if (candidate is null)
                {
                    var current = _session;
                    if (current is null || !current.Connected || current.KeepAliveStopped)
                    {
                        _reconnectInFlight = false;
                        RequestFreshSession(
                            "The upstream reconnect ended without a usable session; opening a fresh one.");
                        return;
                    }

                    _logger.LogInformation("Upstream keep-alives recovered on the existing session.");
                    candidate = current;
                }

                recovered = candidate;

                // Three outcomes, and an acquisition engine recovers differently from each:
                // the same session reactivated (subscriptions still live), a new session that
                // inherited them, or a new session with nothing (the server restarted).
                var sameSession = ReferenceEquals(recovered, _session);
                if (!sameSession)
                {
                    var previous = _session;
                    if (previous is not null)
                    {
                        previous.KeepAlive -= OnKeepAlive;

                        // The SDK recreates from the old session as a template and leaves
                        // that template for its owner to dispose.
                        _ = DisposeSessionAsync(previous, close: false);
                    }

                    AttachSession(recovered, _options.CurrentValue.Upstream);

                    lock (_stateGate) _epoch++;
                }

                transferred = sameSession || recovered.Subscriptions.Any(s => s.Created);

                lock (_stateGate)
                {
                    _reconnectCount++;
                    _connectedSince = _time.GetUtcNow();
                    _lastKeepAliveUtc = _connectedSince;
                    _lastError = null;
                    _reconnectInFlight = false;
                }

                SetState(UpstreamConnectionState.Connected);
            }

            _logger.LogInformation(
                "Upstream reconnected (epoch {Epoch}, reconnect #{ReconnectCount}). Subscriptions {Disposition}.",
                _epoch, _reconnectCount, transferred ? "were transferred" : "must be recreated");

            RaiseSessionReady(recovered, transferred);
        }
        catch (Exception ex)
        {
            // Whatever state this left behind, the reconnect deadline in the supervisor
            // loop will notice it and start over with a fresh session.
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
            var reason = Interlocked.Exchange(ref _freshSessionReason, null);
            if (reason is null)
            {
                _logger.LogInformation("Reconnect requested by an operator.");
            }
            else
            {
                _logger.LogWarning("{Reason}", reason);
                lock (_stateGate) _lastError = reason;
            }

            await StartOverAsync();
            return;
        }

        CheckKeepAliveWatchdog();
        await CheckReconnectDeadlineAsync();
    }

    /// <summary>
    /// Drops the session and any reconnect in flight; the supervisor loop then opens a
    /// fresh session, exactly as it does on service start.
    /// </summary>
    private async Task StartOverAsync()
    {
        // Abandon any SDK reconnect already in flight. Its completion callback will not
        // run for the session we are about to close, so this is the only place
        // _reconnectInFlight can be cleared -- and leaving it set would silently disable
        // the keep-alive handler and the watchdog for the rest of the process's life.
        lock (_reconnectGate) CancelReconnect();
        await CloseSessionAsync();
        SetState(UpstreamConnectionState.Disconnected);
    }

    /// <summary>
    /// Gives up on resuming the old session once that has taken too long.
    /// </summary>
    /// <remarks>
    /// The SDK's reconnect handler has no deadline. Faced with a server that half-closed
    /// the connection it can retry, or wait on a reply that never comes, for as long as
    /// the process lives -- the bridge has been seen sitting in Reconnecting for hours
    /// while a service restart connected at the first attempt. Opening a fresh session is
    /// what that restart does, so past the deadline the supervisor does it itself.
    /// </remarks>
    private async Task CheckReconnectDeadlineAsync()
    {
        var timeout = TimeSpan.FromMilliseconds(_options.CurrentValue.Upstream.ReconnectResumeTimeoutMs);
        TimeSpan elapsed;

        lock (_reconnectGate)
        {
            if (State != UpstreamConnectionState.Reconnecting)
                return;

            lock (_stateGate) elapsed = _time.GetUtcNow() - _stateSince;
            if (elapsed <= timeout)
                return;

            // Detach the handler while holding the gate, so a callback arriving from here
            // on finds itself superseded and disposes whatever it recovered.
            CancelReconnect();
        }

        var message =
            $"Upstream session did not resume within {timeout.TotalSeconds:F0}s; abandoning it and opening a fresh session.";
        _logger.LogWarning(
            "Upstream session did not resume within {TimeoutSeconds:F0}s ({ElapsedSeconds:F0}s in Reconnecting); " +
            "abandoning it and opening a fresh session.",
            timeout.TotalSeconds, elapsed.TotalSeconds);
        lock (_stateGate) _lastError = message;

        await StartOverAsync();
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
    /// <remarks>Callers hold <see cref="_reconnectGate"/>.</remarks>
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
        await DisposeSessionAsync(session);
    }

    /// <summary>Closes (optionally) and disposes a session this manager no longer uses.</summary>
    private async Task DisposeSessionAsync(Session session, bool close = true)
    {
        try
        {
            if (close)
            {
                // Bounded: this runs when the link is suspect, and a close waiting on a
                // reply that never comes must not hold up opening the replacement.
                using var timeout = new CancellationTokenSource(SessionCloseTimeout);
                await session.CloseAsync(timeout.Token);
            }
        }
        catch (Exception ex)
        {
            // The link is probably already gone; that is why we are closing.
            _logger.LogDebug(ex, "Closing the upstream session reported an error.");
        }
        finally
        {
            try { session.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Disposing the upstream session reported an error."); }
        }
    }

    private static readonly TimeSpan SessionCloseTimeout = TimeSpan.FromSeconds(10);

    public override void Dispose()
    {
        lock (_reconnectGate) _reconnectHandler?.Dispose();
        _reconnectRequested.Dispose();
        base.Dispose();
    }
}
