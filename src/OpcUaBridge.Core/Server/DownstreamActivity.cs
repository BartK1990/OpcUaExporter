namespace OpcUaBridge.Server;

/// <summary>What the applications behind the bridge are actually asking it for.</summary>
/// <param name="Sessions">Downstream client sessions currently open.</param>
/// <param name="Subscriptions">Subscriptions those sessions hold.</param>
/// <param name="MonitoredItems">
/// Mirrored tags those subscriptions monitor. Far below the mirrored tag count means the
/// bridge is acquiring tags nobody downstream has asked for.
/// </param>
public sealed record DownstreamActivity(int Sessions, int Subscriptions, int MonitoredItems)
{
    public static readonly DownstreamActivity None = new(0, 0, 0);
}
