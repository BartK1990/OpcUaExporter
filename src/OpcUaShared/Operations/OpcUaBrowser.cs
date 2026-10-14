using Opc.Ua;
using Opc.Ua.Client;
using OpcUaExporter.Models;
using OpcUaExporter.Services;

namespace OpcUaExporter.Operations;

/// <summary>How a single browse of the address space should behave.</summary>
public sealed class BrowseOptions
{
    /// <summary>Walk sibling branches concurrently. Worth it on deep address spaces.</summary>
    public bool EnableParallelBrowse { get; init; } = true;

    /// <summary>Concurrent branch walks, clamped to 1..32.</summary>
    public int MaxDegreeOfParallelism { get; init; } = 10;

    /// <summary>Options matching a <see cref="ConnectionProfile"/>'s browse settings.</summary>
    public static BrowseOptions From(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new BrowseOptions
        {
            EnableParallelBrowse = profile.EnableParallelBrowse,
            MaxDegreeOfParallelism = profile.ParallelBrowseMaxDegree
        };
    }
}

/// <summary>Reads the shape of a server's address space into an <see cref="OpcTag"/> tree.</summary>
public sealed class OpcUaBrowser(DiagnosticsLogService diagnostics)
{
    private const int BrowseProgressLogInterval = 1000;
    private const int BrowseVariableProgressReportInterval = 100;

    private readonly DiagnosticsLogService _diagnostics = diagnostics;

    /// <summary>
    /// Walks the server's address space from the Objects folder down, over a session
    /// the caller owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Taking the session as a parameter is what lets a long-lived gateway browse on
    /// its existing connection instead of opening a second one.
    /// </para>
    /// <para>
    /// The tree is built top-down and published as it goes: <paramref name="onTopStructureReady"/>
    /// receives the (live) top-level list as soon as it is known, and each node's children
    /// appear via <see cref="OpcTag.PublishChildren"/> the moment they are fetched, so a UI can
    /// show and explore the tree while the scan continues. Nodes whose children were already
    /// fetched on demand (<see cref="FetchChildrenAsync"/>) are reused rather than fetched twice.
    /// </para>
    /// </remarks>
    /// <param name="dataTypeNames">
    /// Shared with on-demand fetches of the same server. Null gives this browse its own,
    /// which is right for a one-off capture.
    /// </param>
    public async Task<List<OpcTag>> BrowseAsync(
        Session session,
        BrowseOptions options,
        Action<List<OpcTag>>? onTopStructureReady = null,
        Action<int>? onVariableCountChanged = null,
        DataTypeNameCache? dataTypeNames = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);

        dataTypeNames ??= new DataTypeNameCache();

        var rootNodeId = ObjectIds.ObjectsFolder;
        var progress = new BrowseProgressState();
        progress.TryVisitNode(rootNodeId.ToString());

        _diagnostics.Add("Browse started.");
        var tags = await FetchChildrenAsync(session, rootNodeId, dataTypeNames, ct);
        foreach (var tag in tags)
        {
            if (tag.IsSelectable)
                progress.IncrementVariableCount();
        }

        onTopStructureReady?.Invoke(tags);
        onVariableCountChanged?.Invoke(progress.VariableCount);

        if (options.EnableParallelBrowse)
        {
            var maxDegree = Math.Clamp(options.MaxDegreeOfParallelism, 1, 32);
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegree,
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(tags, parallelOptions, async (tag, token) =>
            {
                await BrowseTopLevelTagAsync(session, tag, progress, dataTypeNames, onVariableCountChanged, token);
            });
        }
        else
        {
            foreach (var tag in tags)
            {
                ct.ThrowIfCancellationRequested();
                await BrowseTopLevelTagAsync(session, tag, progress, dataTypeNames, onVariableCountChanged, ct);
            }
        }

        _diagnostics.Add($"Browse completed. Scanned {progress.ScannedNodes} node(s). Found {CountVariables(tags)} variable tag(s).");
        return tags;
    }

    /// <summary>
    /// Fetches the Object/Variable children of one node as unpublished <see cref="OpcTag"/>s,
    /// sorted by NodeId, with Variable data types resolved.
    /// </summary>
    /// <remarks>
    /// One level only. This is what expanding a node on demand needs, and what the deep scan
    /// is built from.
    /// </remarks>
    public async Task<List<OpcTag>> FetchChildrenAsync(
        Session session,
        NodeId nodeId,
        DataTypeNameCache dataTypeNames,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentNullException.ThrowIfNull(dataTypeNames);
        ct.ThrowIfCancellationRequested();

        var references = await session.FetchReferencesAsync(nodeId, ct: ct);
        var forwardChildren = references
            .Where(r => r.IsForward && r.NodeClass is NodeClass.Object or NodeClass.Variable)
            .Select(r => (Reference: r, NodeId: ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris)))
            .Where(x => x.NodeId is not null)
            .Select(x => (x.Reference, NodeId: x.NodeId!))
            .ToList();

        var variableDataTypes = await ReadVariableDataTypesAsync(session, forwardChildren, ct);
        var children = new List<OpcTag>(forwardChildren.Count);

        foreach (var (reference, childNodeId) in forwardChildren)
        {
            ct.ThrowIfCancellationRequested();

            var tag = new OpcTag
            {
                NodeId = childNodeId.ToString(),
                BrowseName = reference.BrowseName?.ToString() ?? string.Empty,
                DisplayName = reference.DisplayName?.Text ?? childNodeId.ToString(),
                NodeClass = reference.NodeClass.ToString()
            };

            if (reference.NodeClass == NodeClass.Variable &&
                variableDataTypes.TryGetValue(tag.NodeId, out var dataTypeId) &&
                dataTypeId is not null)
            {
                tag.DataType = await OpcUaDataTypes.GetDataTypeNameCachedAsync(dataTypeId, session, dataTypeNames);
            }

            children.Add(tag);
        }

        children.Sort((a, b) => string.Compare(a.NodeId, b.NodeId, StringComparison.OrdinalIgnoreCase));
        return children;
    }

    /// <summary>
    /// Deep-scans the subtree under <paramref name="tag"/>: publishes its children (unless an
    /// on-demand fetch already did), then recurses.
    /// </summary>
    /// <remarks>
    /// A node reached a second time -- the address space is a graph, not a tree -- is left
    /// unloaded instead of being scanned again, so it can still be expanded on demand. A node
    /// whose children cannot be fetched is left unloaded too, so the user can retry; genuine
    /// cancellation still propagates.
    /// </remarks>
    private async Task BrowseSubtreeAsync(
        Session session,
        OpcTag tag,
        BrowseProgressState progress,
        DataTypeNameCache dataTypeNames,
        Action<int>? onVariableCountChanged,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        List<OpcTag> children;
        if (tag.ChildrenLoaded)
        {
            children = tag.Children;
        }
        else
        {
            try
            {
                children = tag.PublishChildren(await FetchChildrenAsync(session, NodeId.Parse(tag.NodeId), dataTypeNames, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return;
            }
        }

        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();

            var scannedNodes = progress.IncrementScannedNodes();
            if (scannedNodes % BrowseProgressLogInterval == 0)
                _diagnostics.Add($"Browse in progress: scanned {scannedNodes} node(s). Latest node: {child.NodeId}");

            if (child.IsSelectable)
            {
                var variableCount = progress.IncrementVariableCount();
                if (variableCount % BrowseVariableProgressReportInterval == 0)
                    onVariableCountChanged?.Invoke(variableCount);
            }

            if (progress.TryVisitNode(child.NodeId))
                await BrowseSubtreeAsync(session, child, progress, dataTypeNames, onVariableCountChanged, ct);
        }
    }

    private async Task BrowseTopLevelTagAsync(
        Session session,
        OpcTag tag,
        BrowseProgressState progress,
        DataTypeNameCache dataTypeNames,
        Action<int>? onVariableCountChanged,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (progress.TryVisitNode(tag.NodeId))
            await BrowseSubtreeAsync(session, tag, progress, dataTypeNames, onVariableCountChanged, ct);

        onVariableCountChanged?.Invoke(progress.VariableCount);
    }

    private static async Task<Dictionary<string, NodeId>> ReadVariableDataTypesAsync(
        Session session,
        List<(ReferenceDescription Reference, NodeId NodeId)> children,
        CancellationToken ct)
    {
        var variableChildren = children
            .Where(c => c.Reference.NodeClass == NodeClass.Variable)
            .ToList();

        if (variableChildren.Count == 0)
            return new Dictionary<string, NodeId>(StringComparer.Ordinal);

        var requests = new ReadValueIdCollection(variableChildren.Count);
        foreach (var child in variableChildren)
        {
            requests.Add(new ReadValueId
            {
                NodeId = child.NodeId,
                AttributeId = Attributes.DataType
            });
        }

        var response = await session.ReadAsync(
            null,
            0,
            TimestampsToReturn.Neither,
            requests,
            ct);

        var map = new Dictionary<string, NodeId>(StringComparer.Ordinal);
        var results = response?.Results;
        if (results is null)
            return map;

        for (var i = 0; i < variableChildren.Count && i < results.Count; i++)
        {
            var result = results[i];
            if (result is null || StatusCode.IsBad(result.StatusCode))
                continue;

            var dataTypeId = result.Value as NodeId;
            if (dataTypeId is null)
                continue;

            map[variableChildren[i].NodeId.ToString()] = dataTypeId;
        }

        return map;
    }

    private static int CountVariables(IEnumerable<OpcTag> tags)
        => Flatten(tags).Count(t => string.Equals(t.NodeClass, "Variable", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<OpcTag> Flatten(IEnumerable<OpcTag> tags)
    {
        foreach (var tag in tags)
        {
            yield return tag;
            foreach (var child in Flatten(tag.Children))
                yield return child;
        }
    }
}
