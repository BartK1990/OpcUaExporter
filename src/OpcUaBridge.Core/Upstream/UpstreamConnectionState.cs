namespace OpcUaBridge.Upstream;

/// <summary>Where the upstream connection currently stands.</summary>
public enum UpstreamConnectionState
{
    /// <summary>Not connected and not trying: before startup, or after an operator stopped it.</summary>
    Disconnected,

    /// <summary>Working through the initial connect, retrying with backoff.</summary>
    Connecting,

    /// <summary>Session live and keep-alives healthy.</summary>
    Connected,

    /// <summary>Keep-alive failed; the SDK's reconnect handler is trying to recover the session.</summary>
    Reconnecting,

    /// <summary>
    /// Stopped by something retrying cannot fix -- a rejected certificate, bad credentials,
    /// or no endpoint matching the configured security.
    /// </summary>
    /// <remarks>
    /// Kept distinct from <see cref="Connecting"/> so the bridge does not hammer a server
    /// that has told it plainly to go away, and so the dashboard can say what needs fixing
    /// rather than showing a connection attempt that will never succeed.
    /// </remarks>
    Faulted
}
