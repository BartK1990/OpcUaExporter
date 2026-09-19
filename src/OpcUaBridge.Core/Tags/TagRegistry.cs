using Microsoft.Extensions.Logging;
using Opc.Ua;
using OpcUaBridge.Namespaces;

namespace OpcUaBridge.Tags;

/// <summary>
/// The snapshot projected into the flat, indexable form the hot paths need.
/// </summary>
/// <remarks>
/// Built once at startup. The tree shape is kept only for the mirror server's address
/// space; acquisition and value caching work entirely off the dense
/// <see cref="Tags"/> array.
/// </remarks>
public sealed class TagRegistry
{
    /// <summary>Stands in for a namespace index the snapshot cannot resolve. Never matches a real server namespace.</summary>
    internal const string UnknownNamespaceUri = "urn:opcuabridge:unresolved-namespace";

    private readonly Dictionary<NodeId, MirrorTag> _byMirrorNodeId = [];
    private Dictionary<string, MirrorTag>? _byUpstreamIdentity;

    private TagRegistry(NamespaceSnapshot snapshot, IReadOnlyList<MirrorTag> tags)
    {
        Snapshot = snapshot;
        Tags = tags;
    }

    public NamespaceSnapshot Snapshot { get; }

    /// <summary>Every mirrored variable, indexed by <see cref="MirrorTag.Index"/>.</summary>
    public IReadOnlyList<MirrorTag> Tags { get; }

    public int Count => Tags.Count;

    /// <summary>Walks the snapshot and assigns every variable a dense index.</summary>
    public static TagRegistry FromSnapshot(NamespaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var tags = new List<MirrorTag>(snapshot.Stats.VariableCount);

        foreach (var root in snapshot.Nodes)
            Collect(root, parentPath: string.Empty, snapshot, tags);

        return new TagRegistry(snapshot, tags);
    }

    private static void Collect(SnapshotNode node, string parentPath, NamespaceSnapshot snapshot, List<MirrorTag> tags)
    {
        var path = string.IsNullOrEmpty(parentPath) ? node.BrowseName : $"{parentPath}/{node.BrowseName}";

        if (node.IsVariable)
        {
            // A snapshot is machine-written, so an out-of-range index means the file was
            // damaged or hand-edited. Rather than fail the whole service or guess a
            // namespace, give the tag one that can never match a server's table: it then
            // reports BadNodeIdUnknown and shows up in the drift warning by name.
            var namespaceUri = node.NamespaceIndex >= 0 && node.NamespaceIndex < snapshot.NamespaceUris.Count
                ? snapshot.NamespaceUris[node.NamespaceIndex]
                : UnknownNamespaceUri;

            tags.Add(new MirrorTag(
                index: tags.Count,
                upstreamNamespaceUri: namespaceUri,
                identifier: node.Identifier,
                browsePath: path,
                displayName: node.EffectiveDisplayName,
                dataType: ResolveDataType(node.DataType),
                valueRank: node.ValueRank,
                accessLevel: node.AccessLevel));
        }

        foreach (var child in node.Children)
            Collect(child, path, snapshot, tags);
    }

    private static NodeId ResolveDataType(string? builtInTypeName)
    {
        if (!string.IsNullOrWhiteSpace(builtInTypeName) &&
            Enum.TryParse<BuiltInType>(builtInTypeName, ignoreCase: true, out var builtInType) &&
            builtInType != BuiltInType.Null)
        {
            return new NodeId((uint)builtInType);
        }

        // A server that declared the abstract BaseDataType, or a type we could not name.
        // BaseDataType lets the node hold whatever the server actually sends.
        return DataTypeIds.BaseDataType;
    }

    /// <summary>
    /// Records the NodeId the bridge's own server publishes a tag under.
    /// </summary>
    /// <returns>
    /// False when that NodeId is already taken, which happens when the snapshot reached
    /// the same upstream node by two paths. The caller skips the duplicate rather than
    /// publishing two mirror nodes with one identity.
    /// </returns>
    public bool SetMirrorNodeId(MirrorTag tag, NodeId mirrorNodeId)
    {
        if (!_byMirrorNodeId.TryAdd(mirrorNodeId, tag))
            return false;

        tag.MirrorNodeId = mirrorNodeId;
        return true;
    }

    /// <summary>Finds a tag by the upstream identity recorded in the snapshot.</summary>
    /// <remarks>
    /// Built with an explicit first-wins loop rather than <c>ToDictionary</c>, because an
    /// address space can legitimately reach the same node from two parents -- the browse
    /// records each path it finds -- and <c>ToDictionary</c> would throw on the duplicate.
    /// That exception surfaces while the mirrored address space is being built, so the
    /// whole service would fail to start with a message about ports and certificates.
    /// </remarks>
    public bool TryGetTagByUpstreamIdentity(string namespaceUri, string identifier, out MirrorTag tag)
    {
        if (_byUpstreamIdentity is null)
        {
            var index = new Dictionary<string, MirrorTag>(Tags.Count, StringComparer.Ordinal);

            foreach (var candidate in Tags)
                index.TryAdd($"{candidate.UpstreamNamespaceUri}|{candidate.Identifier}", candidate);

            _byUpstreamIdentity = index;
        }

        return _byUpstreamIdentity.TryGetValue($"{namespaceUri}|{identifier}", out tag!);
    }

    /// <summary>Finds a tag by the NodeId a downstream client used.</summary>
    public bool TryGetByMirrorNodeId(NodeId mirrorNodeId, out MirrorTag tag)
        => _byMirrorNodeId.TryGetValue(mirrorNodeId, out tag!);

    /// <summary>
    /// Rebuilds every tag's upstream NodeId against a session's namespace table.
    /// </summary>
    /// <remarks>
    /// Must run on every new session. A namespace index is scoped to the session that
    /// reported it, and servers do reorder their namespace array across restarts -- so
    /// reusing the indexes captured at snapshot time would eventually mean reading a
    /// completely different tag and reporting it as Good. Resolving from the URI each
    /// time is what prevents that.
    /// </remarks>
    /// <returns>The namespace URIs from the snapshot that the server no longer publishes.</returns>
    public IReadOnlyList<string> ResolveAgainst(NamespaceTable serverNamespaces, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(serverNamespaces);

        var missingUris = new List<string>();
        var unresolved = 0;

        foreach (var tag in Tags)
        {
            var namespaceIndex = serverNamespaces.GetIndex(tag.UpstreamNamespaceUri);
            if (namespaceIndex < 0)
            {
                tag.UpstreamNodeId = null;
                unresolved++;

                if (!missingUris.Contains(tag.UpstreamNamespaceUri, StringComparer.Ordinal))
                    missingUris.Add(tag.UpstreamNamespaceUri);

                continue;
            }

            tag.UpstreamNodeId = NodeId.Parse($"ns={namespaceIndex};{tag.Identifier}");
        }

        if (unresolved > 0)
        {
            logger.LogWarning(
                "{UnresolvedCount} of {TagCount} mirrored tag(s) could not be resolved against the server's " +
                "namespace table. Missing namespace URI(s): {MissingUris}. Those tags will report " +
                "BadNodeIdUnknown until the namespace is captured again.",
                unresolved, Tags.Count, string.Join(", ", missingUris));
        }

        return missingUris;
    }
}
