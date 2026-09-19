using Opc.Ua.Client;

namespace OpcUaBridge.Upstream;

/// <summary>A new session is available to build subscriptions or reads on.</summary>
/// <param name="Session">The live session.</param>
/// <param name="Epoch">Increments only when the session object itself was replaced.</param>
/// <param name="SubscriptionsTransferred">
/// Whether the server kept the previous session's subscriptions alive. When true an
/// acquisition engine re-binds to them; when false it must recreate everything.
/// </param>
public sealed record SessionReadyEventArgs(ISession Session, int Epoch, bool SubscriptionsTransferred);

/// <summary>
/// The supervised connection to the upstream OPC UA server.
/// </summary>
/// <remarks>
/// Exists because the desktop exporter's model -- open a session, do one thing, close it --
/// is the wrong shape for a gateway. Here a single session is held open, watched by
/// keep-alives, and rebuilt automatically when the link drops.
/// </remarks>
public interface IUpstreamConnection
{
    UpstreamConnectionState State { get; }

    UpstreamStatus Status { get; }

    /// <summary>The live session, or null unless <see cref="State"/> is Connected.</summary>
    ISession? Session { get; }

    event EventHandler<UpstreamStatus>? StateChanged;

    /// <summary>Raised on first connect and after any reconnect, so acquisition can rebuild.</summary>
    event EventHandler<SessionReadyEventArgs>? SessionReady;

    /// <summary>Raised when the session is no longer usable, so cached values can be flagged.</summary>
    event EventHandler? SessionLost;

    /// <summary>
    /// The live session, or a <see cref="Opc.Ua.ServiceResultException"/> carrying
    /// <c>BadNoCommunication</c> when the link is down.
    /// </summary>
    /// <remarks>
    /// Used by the write pass-through, which must fail a downstream write cleanly rather
    /// than block it waiting for a reconnect that may be minutes away.
    /// </remarks>
    ISession RequireSession();

    /// <summary>Retries immediately, including from the Faulted state after an operator fixes the cause.</summary>
    void RequestReconnect();
}
