using Opc.Ua;

namespace OpcUaBridge.Tags;

/// <summary>
/// One mirrored variable: where it lives upstream, and where it appears downstream.
/// </summary>
/// <remarks>
/// Allocated once when the snapshot is loaded and then reused for the process's lifetime.
/// <see cref="Index"/> is its slot in every dense array the bridge keeps, so a value
/// arriving from the upstream server reaches its cache slot and its mirror node by array
/// indexing rather than by hashing a NodeId.
/// </remarks>
public sealed class MirrorTag
{
    public MirrorTag(
        int index,
        string upstreamNamespaceUri,
        string identifier,
        string browsePath,
        string displayName,
        NodeId dataType,
        int valueRank,
        byte accessLevel)
    {
        Index = index;
        UpstreamNamespaceUri = upstreamNamespaceUri;
        Identifier = identifier;
        BrowsePath = browsePath;
        DisplayName = displayName;
        DataType = dataType;
        ValueRank = valueRank;
        AccessLevel = accessLevel;
    }

    /// <summary>Dense, contiguous index into the value store and the mirror node array.</summary>
    public int Index { get; }

    /// <summary>The namespace URI this node belongs to upstream. Stable across server restarts; the index is not.</summary>
    public string UpstreamNamespaceUri { get; }

    /// <summary>NodeId identifier without the namespace prefix, e.g. <c>s=Line1.Speed</c>.</summary>
    public string Identifier { get; }

    /// <summary>Slash-separated path through the browse tree, for display and filtering.</summary>
    public string BrowsePath { get; }

    public string DisplayName { get; }

    public NodeId DataType { get; }

    public int ValueRank { get; }

    /// <summary>AccessLevel bits mirrored from upstream, so a read-only tag stays read-only.</summary>
    public byte AccessLevel { get; }

    /// <summary>Whether the upstream node allows writes.</summary>
    public bool IsWritable => (AccessLevel & AccessLevels.CurrentWrite) != 0;

    /// <summary>
    /// The upstream NodeId for the current session, or null when the namespace URI is
    /// absent from the server's table.
    /// </summary>
    /// <remarks>
    /// Re-resolved on every new session rather than stored, because the namespace index
    /// baked into a NodeId is only valid for the session that reported it.
    /// </remarks>
    public NodeId? UpstreamNodeId { get; internal set; }

    /// <summary>The NodeId this tag is published under by the bridge's own server.</summary>
    public NodeId MirrorNodeId { get; internal set; } = NodeId.Null;
}
