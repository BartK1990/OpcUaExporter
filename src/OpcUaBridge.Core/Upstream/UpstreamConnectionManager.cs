using System.Threading.Channels;
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
/// <para>
/// <b>Threading.</b> Every piece of connection state belongs to the supervisor loop
/// (<see cref="ExecuteAsync"/>), and nothing else writes it. The SDK's keep-alive and
/// reconnect callbacks, and operators on the dashboard, only post an
/// <see cref="UpstreamEvent"/> to <see cref="_events"/>; the loop handles those one at a
/// time, between its own connect attempts and watchdog ticks. That is what makes a
/// reconnect completing at the same moment the loop abandons it an ordinary sequence of
/// two events rather than a race. Other threads read only <see cref="_published"/>, an
/// immutable snapshot the loop replaces after each change. There are no locks here: add
/// a new input as an event, not as a field another thread writes.
/// </para>
/// </remarks>
public sealed class UpstreamConnectionManager : BackgroundService, IUpstreamConnection
{
    /// <summary>How often the loop runs its watchdogs while a session exists.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan SessionCloseTimeout = TimeSpan.FromSeconds(10);

    private readonly IOptionsMonitor<BridgeOptions> _options;
    private readonly OpcUaSessionFactory _sessionFactory;
    private readonly ILogger<UpstreamConnectionManager> _logger;
    private readonly TimeProvider _time;

    private readonly Channel<UpstreamEvent> _events =
        Channel.CreateUnbounded<UpstreamEvent>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>1 while an operator's reconnect request is queued and not yet handled.</summary>
    /// <remarks>
    /// Several operators clicking "reconnect" should produce one attempt, not a queue of
    /// them: each queued request would close the session the previous one just opened.
    /// </remarks>
    private int _reconnectRequestQueued;

    /// <summary>What other threads see. Replaced, never mutated, by the loop.</summary>
    private volatile Snapshot _published;

    // ---- Owned by the supervisor loop. Nothing else reads or writes these. ----
    private ExponentialBackoff? _backoff;
    private bool _retryNow;
    private Session? _session;
    private SessionReconnectHandler? _reconnectHandler;
    private int _epoch;
    private int _reconnectCount;
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
        _published = BuildSnapshot();
    }

    public UpstreamConnectionState State => _published.Status.State;

    public ISession? Session => _published.Session;

    public UpstreamStatus Status => _published.Status;

    public event EventHandler<UpstreamStatus>? StateChanged;
    public event EventHandler<SessionReadyEventArgs>? SessionReady;
    public event EventHandler? SessionLost;

    public ISession RequireSession()
        => Session ?? throw new ServiceResultException(
            StatusCodes.BadNoCommunication,
            $"The upstream OPC UA server is not connected (state: {State}).");

    public void RequestReconnect()
    {
        if (Interlocked.Exchange(ref _reconnectRequestQueued, 1) == 0)
            Post(new OperatorReconnectRequested());
    }

    /// <summary>Records which snapshot namespaces the server no longer publishes, for the dashboard.</summary>
    public void ReportMissingNamespaces(IReadOnlyList<string> missingNamespaceUris)
        => Post(new MissingNamespacesReported(missingNamespaceUris));

    /// <summary>
    /// Tells the loop a keep-alive on <paramref name="session"/> failed.
    /// </summary>
    /// <remarks>
    /// What <see cref="OnKeepAlive"/> posts; internal so tests can start the reconnect path
    /// on a session that is in fact healthy, which is what a server answering one
    /// keep-alive late looks like.
    /// </remarks>
    internal void ReportKeepAliveFailure(ISession session, string error)
        => Post(new KeepAliveFailed(session, error));

    private void Post(UpstreamEvent e) => _events.Writer.TryWrite(e);

    // ------------------------------------------------------------------------------------
    // Supervisor loop
    // ------------------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var upstream = _options.CurrentValue.Upstream;
        _backoff = new ExponentialBackoff(
            TimeSpan.FromMilliseconds(upstream.ReconnectMinDelayMs),
            TimeSpan.FromMilliseconds(upstream.ReconnectMaxDelayMs));

        // Every wait below throws when the service stops, so the shutdown close has to sit
        // in a finally: leaving the session open would strand it on the upstream server
        // until its timeout expires, and DeleteSubscriptionsOnClose is off, so the server
        // would hold thousands of monitored items too. A restart loop would stack those up.
        try
        {
            while (true)
            {
                stoppingToken.ThrowIfCancellationRequested();

                switch (_state)
                {
                    case UpstreamConnectionState.Connected or UpstreamConnectionState.Reconnecting:
                        await PumpEventsAsync(
                            _time.GetUtcNow() + TickInterval,
                            () => _state is not (UpstreamConnectionState.Connected or UpstreamConnectionState.Reconnecting),
                            stoppingToken);

                        if (_state is UpstreamConnectionState.Connected or UpstreamConnectionState.Reconnecting)
                            await OnTickAsync();
                        break;

                    case UpstreamConnectionState.Faulted:
                        // Wait indefinitely for an operator to fix the cause and ask us to retry.
                        await PumpEventsAsync(
                            until: null, () => _state != UpstreamConnectionState.Faulted, stoppingToken);
                        break;

                    default:
                        await ConnectOrBackOffAsync(stoppingToken);
                        break;
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

    private async Task ConnectOrBackOffAsync(CancellationToken ct)
    {
        if (await TryConnectAsync(ct))
        {
            _backoff!.Reset();
            return;
        }

        if (_state == UpstreamConnectionState.Faulted)
            return;

        var delay = _backoff!.Next();
        _logger.LogWarning(
            "Upstream connect attempt {Attempt} failed; retrying in {DelaySeconds:F1}s. {Error}",
            _backoff.Attempt, delay.TotalSeconds, _lastError);

        // Wait on events rather than a plain timer, so "Reconnect now" works while a
        // connection is failing. That is exactly when it is reached for -- an operator who
        // has just fixed a certificate or an endpoint should not sit through the remaining
        // backoff wondering whether the button did anything.
        _retryNow = false;
        await PumpEventsAsync(_time.GetUtcNow() + delay, () => _retryNow, ct);
    }

    /// <summary>
    /// Handles events until <paramref name="done"/> holds or <paramref name="until"/> passes.
    /// </summary>
    private async Task PumpEventsAsync(DateTimeOffset? until, Func<bool> done, CancellationToken ct)
    {
        while (!done())
        {
            TimeSpan? remaining = until - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                return;

            var next = await NextEventAsync(remaining, ct);
            if (next is null)
                return;

            await HandleAsync(next);
        }
    }

    /// <returns>The next event, or null once <paramref name="timeout"/> elapses.</returns>
    private async Task<UpstreamEvent?> NextEventAsync(TimeSpan? timeout, CancellationToken ct)
    {
        if (_events.Reader.TryRead(out var ready))
            return ready;

        using var expiry = timeout is { } t
            ? new CancellationTokenSource(t, _time)
            : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, expiry.Token);

        try
        {
            while (await _events.Reader.WaitToReadAsync(linked.Token))
            {
                if (_events.Reader.TryRead(out var next))
                    return next;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timed out.
        }

        return null;
    }

    private async Task HandleAsync(UpstreamEvent e)
    {
        switch (e)
        {
            case KeepAliveSucceeded ok when ReferenceEquals(ok.Session, _session):
                _lastKeepAliveUtc = _time.GetUtcNow();
                Publish();
                break;

            case KeepAliveFailed failed
                when ReferenceEquals(failed.Session, _session) && _state == UpstreamConnectionState.Connected:
                _lastError = failed.Error;
                BeginReconnect();
                break;

            case ReconnectCompleted completed:
                await CompleteReconnectAsync(completed.Handler);
                break;

            case OperatorReconnectRequested:
                Volatile.Write(ref _reconnectRequestQueued, 0);
                await OnOperatorReconnectAsync();
                break;

            case MissingNamespacesReported missing:
                _missingNamespaceUris = missing.NamespaceUris;
                Publish();
                break;

            // Anything else is about a session already replaced, or arrived in a state
            // where it means nothing (a keep-alive failure while already reconnecting).
        }
    }

    private async Task OnOperatorReconnectAsync()
    {
        switch (_state)
        {
            case UpstreamConnectionState.Connected or UpstreamConnectionState.Reconnecting:
                _logger.LogInformation("Reconnect requested by an operator.");
                await StartOverAsync();
                break;

            case UpstreamConnectionState.Faulted:
                _logger.LogInformation("Reconnect requested by an operator; retrying.");
                _backoff!.Reset();
                SetState(UpstreamConnectionState.Disconnected);
                break;

            default:
                _logger.LogInformation("Reconnect requested by an operator; retrying immediately.");
                _backoff!.Reset();
                _retryNow = true;
                break;
        }
    }

    // ------------------------------------------------------------------------------------
    // Connecting
    // ------------------------------------------------------------------------------------

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

            _epoch++;
            _connectedSince = _time.GetUtcNow();
            _lastKeepAliveUtc = _connectedSince;
            _lastError = null;

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
            _lastError = ex.Message;

            if (IsUnrecoverable(ex))
            {
                _logger.LogError(ex,
                    "Upstream connection cannot succeed as configured, so the bridge has stopped retrying. " +
                    "Fix the cause and reconnect from the dashboard.");
                SetState(UpstreamConnectionState.Faulted);
            }
            else
            {
                Publish();
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

    /// <summary>Runs on the SDK's keep-alive thread: report, never act.</summary>
    private void OnKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        // An exception escaping here would take down the process.
        try
        {
            if (ServiceResult.IsGood(e.Status))
            {
                Post(new KeepAliveSucceeded(session));
                return;
            }

            // Stop the SDK's own keep-alive retries: the loop takes over with the reconnect
            // handler, and two recovery mechanisms racing helps nobody. This has to be set
            // here, synchronously, which is why it is the one decision not left to the loop.
            e.CancelKeepAlive = true;
            ReportKeepAliveFailure(session, e.Status?.ToString() ?? "Keep-alive reported a bad status.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle an upstream keep-alive notification.");
        }
    }

    // ------------------------------------------------------------------------------------
    // Reconnecting
    // ------------------------------------------------------------------------------------

    private void BeginReconnect()
    {
        var upstream = _options.CurrentValue.Upstream;
        _logger.LogWarning("Upstream keep-alive failed ({Error}); reconnecting.", _lastError);

        SetState(UpstreamConnectionState.Reconnecting);
        SessionLost?.Invoke(this, EventArgs.Empty);

        // Both callers have just checked there is a session; the interface's MessageContext
        // is the non-nullable one.
        ISession session = _session!;

        _reconnectHandler?.Dispose();
        var handler = new SessionReconnectHandler(
            session.MessageContext.Telemetry,
            reconnectAbort: true,
            maxReconnectPeriod: upstream.ReconnectMaxDelayMs);
        _reconnectHandler = handler;

        // The callback runs on the handler's timer thread; it only hands the outcome to the loop.
        handler.BeginReconnect(
            session, upstream.ReconnectMinDelayMs, (_, _) => Post(new ReconnectCompleted(handler)));
    }

    private async Task CompleteReconnectAsync(SessionReconnectHandler handler)
    {
        if (!ReferenceEquals(handler, _reconnectHandler))
        {
            // Superseded: the loop already gave up on this attempt, or started a newer one.
            // A session it recreated at the last moment belongs to nobody now, and would sit
            // on the upstream server holding thousands of monitored items until it timed out.
            if (handler.Session is Session orphan && !ReferenceEquals(orphan, _session))
                _ = DisposeSessionAsync(orphan);
            return;
        }

        // The handler calls back exactly once per attempt and is idle afterwards: it does
        // not keep trying. A null session does not mean "still failing" -- it is the SDK's
        // way of saying it aborted the reconnect because keep-alives on the existing session
        // came back by themselves. Treating it as anything else leaves the bridge in
        // Reconnecting with nothing left running that would ever leave that state.
        var recovered = handler.Session as Session;
        if (recovered is null)
        {
            if (_session is not { Connected: true, KeepAliveStopped: false })
            {
                _lastError = "The upstream reconnect ended without a usable session.";
                _logger.LogWarning("{Error} Opening a fresh one.", _lastError);
                await StartOverAsync();
                return;
            }

            _logger.LogInformation("Upstream keep-alives recovered on the existing session.");
            recovered = _session;
        }

        _reconnectHandler = null;
        handler.Dispose();

        // Three outcomes, and an acquisition engine recovers differently from each: the same
        // session reactivated (subscriptions still live), a new session that inherited them,
        // or a new session with nothing (the server restarted).
        var sameSession = ReferenceEquals(recovered, _session);
        if (!sameSession)
        {
            if (_session is { } previous)
            {
                previous.KeepAlive -= OnKeepAlive;

                // The SDK recreates from the old session as a template and leaves that
                // template for its owner to dispose.
                _ = DisposeSessionAsync(previous, close: false);
            }

            AttachSession(recovered, _options.CurrentValue.Upstream);
            _epoch++;
        }

        var transferred = sameSession || recovered.Subscriptions.Any(s => s.Created);

        _reconnectCount++;
        _connectedSince = _time.GetUtcNow();
        _lastKeepAliveUtc = _connectedSince;
        _lastError = null;

        SetState(UpstreamConnectionState.Connected);
        _logger.LogInformation(
            "Upstream reconnected (epoch {Epoch}, reconnect #{ReconnectCount}). Subscriptions {Disposition}.",
            _epoch, _reconnectCount, transferred ? "were transferred" : "must be recreated");

        RaiseSessionReady(recovered, transferred);
    }

    private async Task OnTickAsync()
    {
        if (_state == UpstreamConnectionState.Connected)
            CheckKeepAliveWatchdog();
        else if (_state == UpstreamConnectionState.Reconnecting)
            await CheckReconnectDeadlineAsync();
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
        if (_session is null || _lastKeepAliveUtc is not { } lastKeepAlive)
            return;

        var silenceLimit = TimeSpan.FromMilliseconds(_options.CurrentValue.Upstream.KeepAliveIntervalMs) * 3;
        var silence = _time.GetUtcNow() - lastKeepAlive;
        if (silence <= silenceLimit)
            return;

        _logger.LogWarning(
            "No upstream keep-alive for {SilenceSeconds:F0}s (limit {LimitSeconds:F0}s).",
            silence.TotalSeconds, silenceLimit.TotalSeconds);

        _lastError = "Keep-alive watchdog timed out.";
        BeginReconnect();
    }

    /// <summary>
    /// Gives up on resuming the old session once that has taken too long.
    /// </summary>
    /// <remarks>
    /// The SDK's reconnect handler has no deadline. Faced with a server that half-closed
    /// the connection it can retry, or wait on a reply that never comes, for as long as
    /// the process lives -- the bridge has been seen sitting in Reconnecting for hours
    /// while a service restart connected at the first attempt. Opening a fresh session is
    /// what that restart does, so past the deadline the loop does it itself.
    /// </remarks>
    private async Task CheckReconnectDeadlineAsync()
    {
        var timeout = TimeSpan.FromMilliseconds(_options.CurrentValue.Upstream.ReconnectResumeTimeoutMs);
        var elapsed = _time.GetUtcNow() - _stateSince;
        if (elapsed <= timeout)
            return;

        _logger.LogWarning(
            "Upstream session did not resume within {TimeoutSeconds:F0}s ({ElapsedSeconds:F0}s in Reconnecting); " +
            "abandoning it and opening a fresh session.",
            timeout.TotalSeconds, elapsed.TotalSeconds);
        _lastError =
            $"Upstream session did not resume within {timeout.TotalSeconds:F0}s; abandoning it and opening a fresh session.";

        await StartOverAsync();
    }

    /// <summary>
    /// Drops the session and any reconnect in flight; the loop then opens a fresh session,
    /// exactly as it does on service start.
    /// </summary>
    /// <remarks>
    /// A callback still to come from the abandoned handler arrives as an ordinary
    /// <see cref="ReconnectCompleted"/> naming a handler that is no longer current, and
    /// <see cref="CompleteReconnectAsync"/> disposes whatever it recovered.
    /// </remarks>
    private async Task StartOverAsync()
    {
        CancelReconnect();
        await CloseSessionAsync();
        SetState(UpstreamConnectionState.Disconnected);
    }

    private void CancelReconnect()
    {
        var handler = _reconnectHandler;
        _reconnectHandler = null;

        try
        {
            handler?.CancelReconnect();
            handler?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cancelling the upstream reconnect handler reported an error.");
        }
    }

    // ------------------------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------------------------

    private void SetState(UpstreamConnectionState state)
    {
        if (_state == state)
        {
            Publish();
            return;
        }

        _state = state;
        _stateSince = _time.GetUtcNow();

        if (state != UpstreamConnectionState.Connected)
            _connectedSince = null;

        var status = Publish();

        if (state is UpstreamConnectionState.Disconnected or UpstreamConnectionState.Faulted)
            SessionLost?.Invoke(this, EventArgs.Empty);

        StateChanged?.Invoke(this, status);
    }

    private UpstreamStatus Publish()
    {
        var snapshot = BuildSnapshot();
        _published = snapshot;
        return snapshot.Status;
    }

    private Snapshot BuildSnapshot() => new(
        new UpstreamStatus(
            _state, _stateSince, _connectedSince, _reconnectCount, _epoch,
            _lastError, _lastKeepAliveUtc, _missingNamespaceUris),
        _state == UpstreamConnectionState.Connected ? _session : null);

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

    // ------------------------------------------------------------------------------------
    // Session disposal
    // ------------------------------------------------------------------------------------

    private async Task CloseSessionAsync()
    {
        var session = _session;
        _session = null;
        if (session is null)
            return;

        session.KeepAlive -= OnKeepAlive;
        await DisposeSessionAsync(session);
    }

    /// <summary>Closes (optionally) and disposes a session this manager no longer uses.</summary>
    /// <remarks>Touches nothing but the session, so it is safe to run off the loop.</remarks>
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

    public override void Dispose()
    {
        // ExecuteAsync has finished by the time the host disposes us, so this does not
        // race the loop.
        _reconnectHandler?.Dispose();
        base.Dispose();
    }

    // ------------------------------------------------------------------------------------
    // Types
    // ------------------------------------------------------------------------------------

    /// <summary>The status and the session, published together so readers never see one without the other.</summary>
    private sealed record Snapshot(UpstreamStatus Status, Session? Session);

    /// <summary>Something the loop must handle, posted from any thread.</summary>
    private abstract record UpstreamEvent;

    private sealed record KeepAliveSucceeded(ISession Session) : UpstreamEvent;

    private sealed record KeepAliveFailed(ISession Session, string Error) : UpstreamEvent;

    private sealed record ReconnectCompleted(SessionReconnectHandler Handler) : UpstreamEvent;

    private sealed record OperatorReconnectRequested : UpstreamEvent;

    private sealed record MissingNamespacesReported(IReadOnlyList<string> NamespaceUris) : UpstreamEvent;
}
