namespace OpcUaBridge.Upstream;

/// <summary>An immutable snapshot of the upstream link, for the dashboard and the mirror's status nodes.</summary>
/// <param name="State">Current state.</param>
/// <param name="Since">When the bridge entered that state.</param>
/// <param name="ConnectedSince">When the current session was established, if connected.</param>
/// <param name="ReconnectCount">Reconnects since the service started.</param>
/// <param name="SessionEpoch">Increments whenever a new session object replaces the old one.</param>
/// <param name="LastError">Why the last attempt failed, if it did.</param>
/// <param name="LastKeepAliveUtc">When the server last confirmed the session was alive.</param>
/// <param name="MissingNamespaceUris">Snapshot namespaces the server no longer publishes.</param>
public sealed record UpstreamStatus(
    UpstreamConnectionState State,
    DateTimeOffset Since,
    DateTimeOffset? ConnectedSince,
    int ReconnectCount,
    int SessionEpoch,
    string? LastError,
    DateTimeOffset? LastKeepAliveUtc,
    IReadOnlyList<string> MissingNamespaceUris)
{
    public bool IsConnected => State == UpstreamConnectionState.Connected;
}
