using System.Collections.Concurrent;

namespace OpcUaExporter.Operations;

/// <summary>
/// Counters and caches shared by every branch of one browse.
/// </summary>
/// <remarks>
/// A browse may walk branches concurrently, so all of this is thread-safe. The
/// visited-node set is what stops a cyclic reference graph from browsing forever,
/// and the data-type cache keeps one round trip per distinct type rather than one
/// per variable.
/// </remarks>
internal sealed class BrowseProgressState
{
    private int _scannedNodes;
    private int _variableCount;

    public int ScannedNodes => Volatile.Read(ref _scannedNodes);
    public int VariableCount => Volatile.Read(ref _variableCount);

    public ConcurrentDictionary<string, string?> DataTypeNameCache { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, byte> VisitedNodeIds { get; } = new(StringComparer.Ordinal);

    public int IncrementScannedNodes()
        => Interlocked.Increment(ref _scannedNodes);

    public int IncrementVariableCount()
        => Interlocked.Increment(ref _variableCount);

    public bool TryVisitNode(string nodeId)
        => VisitedNodeIds.TryAdd(nodeId, 0);
}
