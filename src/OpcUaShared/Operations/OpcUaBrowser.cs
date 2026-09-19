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
    /// Taking the session as a parameter is what lets a long-lived gateway browse on
    /// its existing connection instead of opening a second one.
    /// </remarks>
    public async Task<List<OpcTag>> BrowseAsync(
        Session session,
        BrowseOptions options,
        Action<List<OpcTag>>? onTopStructureReady = null,
        Action<int>? onVariableCountChanged = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);

        var rootNodeId = ObjectIds.ObjectsFolder;
        var progress = new BrowseProgressState();
        progress.TryVisitNode(rootNodeId.ToString());

        _diagnostics.Add("Browse started.");
        var references = await session.FetchReferencesAsync(rootNodeId, ct: ct);
        var topLevelChildren = references
            .Where(r => r.IsForward && r.NodeClass is NodeClass.Object or NodeClass.Variable)
            .Select(r => (Reference: r, NodeId: ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris)))
            .Where(x => x.NodeId is not null)
            .Select(x => (x.Reference, NodeId: x.NodeId!))
            .ToList();

        var variableDataTypes = await ReadVariableDataTypesAsync(session, topLevelChildren, ct);

        var tags = new List<OpcTag>(topLevelChildren.Count);
        foreach (var (reference, childNodeId) in topLevelChildren)
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
                tag.DataType = await OpcUaDataTypes.GetDataTypeNameCachedAsync(dataTypeId, session, progress);
            }

            if (reference.NodeClass == NodeClass.Variable)
                progress.IncrementVariableCount();

            tags.Add(tag);
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
                await BrowseTopLevelTagAsync(session, tag, progress, onVariableCountChanged, token);
            });
        }
        else
        {
            foreach (var tag in tags)
            {
                ct.ThrowIfCancellationRequested();
                await BrowseTopLevelTagAsync(session, tag, progress, onVariableCountChanged, ct);
            }
        }

        _diagnostics.Add($"Browse completed. Scanned {progress.ScannedNodes} node(s). Found {CountVariables(tags)} variable tag(s).");
        return tags;
    }

    private async Task<List<OpcTag>> BrowseNodeRecursiveAsync(
        Session session,
        NodeId nodeId,
        BrowseProgressState progress,
        Action<int>? onVariableCountChanged,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var references = await session.FetchReferencesAsync(nodeId, ct: ct);
        var forwardChildren = references
            .Where(r => r.IsForward && r.NodeClass is NodeClass.Object or NodeClass.Variable)
            .Select(r => (Reference: r, NodeId: ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris)))
            .Where(x => x.NodeId is not null)
            .Select(x => (x.Reference, NodeId: x.NodeId!))
            .ToList();

        var variableDataTypes = await ReadVariableDataTypesAsync(session, forwardChildren, ct);
        var children = new List<OpcTag>();

        foreach (var (reference, childNodeId) in forwardChildren)
        {
            ct.ThrowIfCancellationRequested();

            var childNodeIdText = childNodeId.ToString();
            var isFirstVisit = progress.TryVisitNode(childNodeIdText);

            var scannedNodes = progress.IncrementScannedNodes();
            if (scannedNodes % BrowseProgressLogInterval == 0)
            {
                _diagnostics.Add($"Browse in progress: scanned {scannedNodes} node(s). Latest node: {childNodeId}");
            }

            var tag = new OpcTag
            {
                NodeId = childNodeIdText,
                BrowseName = reference.BrowseName?.ToString() ?? string.Empty,
                DisplayName = reference.DisplayName?.Text ?? childNodeId.ToString(),
                NodeClass = reference.NodeClass.ToString()
            };

            if (reference.NodeClass == NodeClass.Variable &&
                variableDataTypes.TryGetValue(tag.NodeId, out var dataTypeId) &&
                dataTypeId is not null)
            {
                tag.DataType = await OpcUaDataTypes.GetDataTypeNameCachedAsync(dataTypeId, session, progress);
            }

            if (reference.NodeClass == NodeClass.Variable)
            {
                var variableCount = progress.IncrementVariableCount();
                if (variableCount % BrowseVariableProgressReportInterval == 0)
                    onVariableCountChanged?.Invoke(variableCount);
            }

            if (isFirstVisit && reference.NodeClass is NodeClass.Object or NodeClass.Variable)
            {
                try
                {
                    var nested = await BrowseNodeRecursiveAsync(session, childNodeId, progress, onVariableCountChanged, ct);
                    tag.Children = nested;
                }
                catch
                {
                    tag.Children = [];
                }
            }

            children.Add(tag);
        }

        return children;
    }

    private async Task BrowseTopLevelTagAsync(
        Session session,
        OpcTag tag,
        BrowseProgressState progress,
        Action<int>? onVariableCountChanged,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var childNodeId = NodeId.Parse(tag.NodeId);
        var isFirstVisit = progress.TryVisitNode(tag.NodeId);
        if (isFirstVisit && (tag.NodeClass == NodeClass.Object.ToString() || tag.NodeClass == NodeClass.Variable.ToString()))
        {
            try
            {
                var nested = await BrowseNodeRecursiveAsync(session, childNodeId, progress, onVariableCountChanged, ct);
                tag.Children = nested;
            }
            catch
            {
                tag.Children = [];
            }
        }

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
