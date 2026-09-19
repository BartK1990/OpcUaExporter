using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Server;
using OpcUaBridge.Configuration;
using OpcUaBridge.Namespaces;
using OpcUaBridge.Tags;

namespace OpcUaBridge.Server;

/// <summary>
/// Publishes the captured upstream address space as this server's own.
/// </summary>
/// <remarks>
/// <para>
/// The point of the whole product: a downstream application changes its endpoint URL to
/// the bridge and everything else about it keeps working. That only holds if the browse
/// names, the hierarchy and the NodeIds it already has configured all still resolve, so
/// this mirrors identifiers verbatim and registers the upstream namespace URIs as its own.
/// </para>
/// <para>
/// Values are pushed in from <see cref="TagValueStore"/> in batches under one lock, rather
/// than one node at a time. At a few thousand tags the lock traffic of the naive approach
/// would contend with every downstream browse, read and publish the server is trying to
/// serve.
/// </para>
/// </remarks>
public sealed class MirrorNodeManager : CustomNodeManager2
{
    private readonly TagRegistry _registry;
    private readonly TagValueStore _values;
    private readonly MirrorServerOptions _options;
    private readonly IUpstreamWriteRouter _writeRouter;
    private readonly ILogger _logger;

    /// <summary>Mirror nodes indexed by tag index, so applying a value never needs a lookup.</summary>
    private readonly BaseDataVariableState?[] _nodesByTagIndex;

    /// <summary>Maps each snapshot namespace ordinal to this server's index for the same URI.</summary>
    private readonly ushort[] _mirrorNamespaceIndexes;

    public MirrorNodeManager(
        IServerInternal server,
        ApplicationConfiguration configuration,
        TagRegistry registry,
        TagValueStore values,
        MirrorServerOptions options,
        IUpstreamWriteRouter writeRouter,
        ILogger logger)
        : base(server, configuration, BuildNamespaceUris(registry.Snapshot))
    {
        _registry = registry;
        _values = values;
        _options = options;
        _writeRouter = writeRouter;
        _logger = logger;
        _nodesByTagIndex = new BaseDataVariableState?[registry.Count];
        _mirrorNamespaceIndexes = new ushort[registry.Snapshot.NamespaceUris.Count];
    }

    /// <summary>
    /// The namespaces this node manager owns: every one the upstream server published,
    /// except the standard OPC UA namespace, which every server already has at index 0.
    /// </summary>
    private static string[] BuildNamespaceUris(NamespaceSnapshot snapshot)
        => snapshot.NamespaceUris
            .Where(uri => !string.Equals(uri, Opc.Ua.Namespaces.OpcUa, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            MapNamespaceIndexes();

            var root = CreateFolder(null, "Bridge", "Bridge");
            root.AddReference(ReferenceTypeIds.Organizes, isInverse: true, ObjectIds.ObjectsFolder);

            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                externalReferences[ObjectIds.ObjectsFolder] = references = [];

            references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, root.NodeId));
            AddPredefinedNode(SystemContext, root);

            foreach (var node in _registry.Snapshot.Nodes)
                CreateNode(node, root);

            _logger.LogInformation(
                "Mirrored address space built: {VariableCount} variable(s) under {NamespaceCount} namespace(s).",
                _registry.Count, _mirrorNamespaceIndexes.Length);
        }
    }

    /// <summary>
    /// Works out which of this server's namespace indexes corresponds to each of the
    /// upstream server's, and reports whether they line up.
    /// </summary>
    /// <remarks>
    /// Spec-correct clients resolve namespaces by URI and do not care about the numbers.
    /// Plenty of real ones have <c>ns=2;s=Something</c> written into a configuration file,
    /// and for those the index is part of the contract. So the table is logged on every
    /// start and any mismatch is reported loudly -- this failing quietly is the one thing
    /// that would break a downstream client in a way nobody could diagnose from the
    /// symptoms.
    /// </remarks>
    private void MapNamespaceIndexes()
    {
        var misaligned = new List<string>();

        for (var ordinal = 0; ordinal < _registry.Snapshot.NamespaceUris.Count; ordinal++)
        {
            var uri = _registry.Snapshot.NamespaceUris[ordinal];
            var index = Server.NamespaceUris.GetIndexOrAppend(uri);

            _mirrorNamespaceIndexes[ordinal] = (ushort)index;

            _logger.LogInformation(
                "Mirrored namespace {Uri}: upstream index {UpstreamIndex}, bridge index {BridgeIndex}.",
                uri, ordinal, index);

            if (index != ordinal)
                misaligned.Add($"{uri} (upstream ns={ordinal}, bridge ns={index})");
        }

        if (misaligned.Count > 0 && _options.PreserveNamespaceIndexes)
        {
            _logger.LogWarning(
                "Namespace indexes could not be preserved for: {Misaligned}. Clients that resolve nodes by " +
                "namespace URI are unaffected, but any client with a literal 'ns=N;...' NodeId configured for " +
                "those namespaces must be updated to the bridge's index.",
                string.Join("; ", misaligned));
        }
    }

    private void CreateNode(SnapshotNode node, NodeState parent)
    {
        if (node.IsVariable)
        {
            CreateVariable(node, parent);
            return;
        }

        var folder = CreateFolder(parent, node.BrowseName, node.EffectiveDisplayName, MirrorNamespaceIndex(node));
        AddPredefinedNode(SystemContext, folder);

        foreach (var child in node.Children)
            CreateNode(child, folder);
    }

    private FolderState CreateFolder(NodeState? parent, string browseName, string displayName, ushort namespaceIndex = 0)
    {
        var effectiveNamespace = namespaceIndex != 0 ? namespaceIndex : NamespaceIndexes[0];

        var folder = new FolderState(parent)
        {
            SymbolicName = browseName,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = ObjectTypeIds.FolderType,
            NodeId = new NodeId($"{(parent is null ? string.Empty : parent.NodeId.Identifier + ".")}{browseName}", effectiveNamespace),
            BrowseName = new QualifiedName(browseName, effectiveNamespace),
            DisplayName = new LocalizedText(displayName),
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            EventNotifier = EventNotifiers.None
        };

        parent?.AddChild(folder);
        return folder;
    }

    private void CreateVariable(SnapshotNode node, NodeState parent)
    {
        if (!_registry.TryGetTagByUpstreamIdentity(MirrorNamespaceUri(node), node.Identifier, out var tag))
            return;

        var namespaceIndex = MirrorNamespaceIndex(node);

        // The identifier is carried across verbatim, so a downstream client's existing
        // NodeId strings resolve against the bridge exactly as they did upstream.
        var mirrorNodeId = NodeId.Parse($"ns={namespaceIndex};{node.Identifier}");

        var accessLevel = _options.AllowWrites
            ? node.AccessLevel
            : (byte)(node.AccessLevel & ~AccessLevels.CurrentWrite);

        var variable = new BaseDataVariableState(parent)
        {
            SymbolicName = node.BrowseName,
            ReferenceTypeId = ReferenceTypes.HasComponent,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = mirrorNodeId,
            BrowseName = new QualifiedName(node.BrowseName, namespaceIndex),
            DisplayName = new LocalizedText(node.EffectiveDisplayName),
            DataType = tag.DataType,
            ValueRank = node.ValueRank,
            AccessLevel = accessLevel,
            UserAccessLevel = accessLevel,
            Historizing = false,
            StatusCode = StatusCodes.BadWaitingForInitialData,
            MinimumSamplingInterval = MinimumSamplingIntervals.Indeterminate,
            Handle = tag
        };

        if ((accessLevel & AccessLevels.CurrentWrite) != 0)
            variable.OnWriteValue = OnWriteMirrorValue;

        parent.AddChild(variable);
        AddPredefinedNode(SystemContext, variable);

        _registry.SetMirrorNodeId(tag, mirrorNodeId);
        _nodesByTagIndex[tag.Index] = variable;
    }

    private ushort MirrorNamespaceIndex(SnapshotNode node)
        => node.NamespaceIndex >= 0 && node.NamespaceIndex < _mirrorNamespaceIndexes.Length
            ? _mirrorNamespaceIndexes[node.NamespaceIndex]
            : NamespaceIndexes[0];

    private string MirrorNamespaceUri(SnapshotNode node)
        => node.NamespaceIndex >= 0 && node.NamespaceIndex < _registry.Snapshot.NamespaceUris.Count
            ? _registry.Snapshot.NamespaceUris[node.NamespaceIndex]
            : string.Empty;

    /// <summary>
    /// Forwards a downstream write to the upstream server.
    /// </summary>
    /// <remarks>
    /// The write is not applied to the mirror. The next value from the acquisition engine
    /// carries whatever the plant server actually did, so the mirror never claims a write
    /// took effect when it did not.
    /// </remarks>
    private ServiceResult OnWriteMirrorValue(
        ISystemContext context,
        NodeState node,
        NumericRange indexRange,
        QualifiedName dataEncoding,
        ref object value,
        ref StatusCode statusCode,
        ref DateTime timestamp)
    {
        if (node.Handle is not MirrorTag tag)
            return StatusCodes.BadNodeIdUnknown;

        return _writeRouter.Write(tag, value);
    }

    /// <summary>
    /// Copies pending values from the store onto the mirror's nodes.
    /// </summary>
    /// <remarks>
    /// One lock for the whole batch. Taking and releasing the node-manager lock per value
    /// would, at a few thousand tags a second, spend most of its time contending with the
    /// downstream reads and publishes this server exists to serve.
    /// </remarks>
    public int ApplyPendingValues(int[] buffer)
    {
        var count = _values.DrainChanged(buffer, buffer.Length);
        if (count == 0)
            return 0;

        lock (Lock)
        {
            for (var i = 0; i < count; i++)
            {
                var tagIndex = buffer[i];
                var node = _nodesByTagIndex[tagIndex];
                var value = _values.Read(tagIndex);

                if (node is null || value is null)
                    continue;

                // Assigning WrappedValue reuses the Variant the SDK already built for the
                // notification, rather than boxing the value again.
                node.WrappedValue = value.WrappedValue;
                node.StatusCode = value.StatusCode;
                node.Timestamp = value.SourceTimestamp;
                node.ClearChangeMasks(SystemContext, includeChildren: false);
            }
        }

        return count;
    }
}
