using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Client;
using OpcUaBridge.Configuration;
using OpcUaExporter.Models;
using OpcUaExporter.Operations;

namespace OpcUaBridge.Namespaces;

/// <summary>What a capture would change, so an operator can decide before anything is written.</summary>
/// <param name="Added">Nodes present upstream but not in the current snapshot.</param>
/// <param name="Removed">Nodes in the snapshot the server no longer has.</param>
/// <param name="Changed">Nodes whose data type, access level or display name differs.</param>
public sealed record SnapshotDiff(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Changed)
{
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;

    public static SnapshotDiff Empty { get; } = new([], [], []);
}

/// <summary>
/// The only path by which the namespace snapshot is ever written.
/// </summary>
/// <remarks>
/// <para>
/// Everything else in the bridge is injected <see cref="INamespaceSnapshotStore"/>, which
/// has no save method. This service is the single holder of
/// <see cref="INamespaceSnapshotWriter"/>, and it is reached only from an explicit
/// operator action. That is how "the saved namespace never changes without an explicit
/// command" is made structural rather than a rule somebody has to remember.
/// </para>
/// <para>
/// Capturing is two steps on purpose: browse and diff, then apply. An operator sees what
/// would change before it changes, because the snapshot is the contract the downstream
/// application depends on.
/// </para>
/// </remarks>
public sealed class NamespaceCaptureService(
    INamespaceSnapshotWriter store,
    OpcUaBrowser browser,
    IOptionsMonitor<BridgeOptions> options,
    ILogger<NamespaceCaptureService> logger)
{
    /// <summary>
    /// Browses the upstream server and builds a snapshot, without writing anything.
    /// </summary>
    public async Task<NamespaceSnapshot> CaptureAsync(
        Session session,
        Action<int>? onVariableCountChanged = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var snapshotOptions = options.CurrentValue.Snapshot;
        var upstream = options.CurrentValue.Upstream;

        logger.LogInformation("Browsing the upstream address space at {EndpointUrl}.", upstream.EndpointUrl);

        var browseOptions = new BrowseOptions
        {
            EnableParallelBrowse = snapshotOptions.EnableParallelBrowse,
            MaxDegreeOfParallelism = snapshotOptions.ParallelBrowseMaxDegree
        };

        var tree = await browser.BrowseAsync(session, browseOptions, onVariableCountChanged: onVariableCountChanged, ct: ct);

        if (!snapshotOptions.IncludeServerDiagnostics)
        {
            var removed = tree.RemoveAll(IsStandardServerObject);
            if (removed > 0)
            {
                logger.LogInformation(
                    "Excluded the upstream server's standard Server object from the capture. " +
                    "Set Bridge:Snapshot:IncludeServerDiagnostics to mirror it as well.");
            }
        }

        var snapshot = SnapshotFactory.FromBrowseResult(tree, session, upstream.EndpointUrl);
        await ReadAccessLevelsAsync(session, snapshot, ct);

        logger.LogInformation(
            "Browse produced {ObjectCount} object(s) and {VariableCount} variable(s) across {NamespaceCount} namespace(s).",
            snapshot.Stats.ObjectCount, snapshot.Stats.VariableCount, snapshot.NamespaceUris.Count);

        return snapshot;
    }

    /// <summary>
    /// Whether a browsed node is the standard <c>Server</c> object every OPC UA server has.
    /// </summary>
    private static bool IsStandardServerObject(OpcTag tag)
        => NodeId.TryParse(tag.NodeId, out var nodeId) && nodeId == ObjectIds.Server;

    /// <summary>
    /// Fills in each variable's real AccessLevel, so a tag the plant server considers
    /// read-only stays read-only on the mirror.
    /// </summary>
    /// <remarks>
    /// One batched sweep rather than an attribute read per node: at a few thousand tags
    /// the difference is seconds against minutes. A node whose AccessLevel cannot be read
    /// keeps the conservative read-only default.
    /// </remarks>
    private async Task ReadAccessLevelsAsync(Session session, NamespaceSnapshot snapshot, CancellationToken ct)
    {
        var variables = snapshot.Variables().ToList();
        if (variables.Count == 0)
            return;

        var nodeIds = new List<NodeId>(variables.Count);
        foreach (var variable in variables)
        {
            var namespaceUri = snapshot.NamespaceUris[variable.NamespaceIndex];
            var namespaceIndex = session.NamespaceUris.GetIndex(namespaceUri);
            nodeIds.Add(NodeId.Parse($"ns={Math.Max(namespaceIndex, 0)};{variable.Identifier}"));
        }

        var maxNodesPerRead = await OpcUaValueReader.GetMaxNodesPerReadAsync(session, ct);
        var results = await OpcUaValueReader.ReadAttributeAsync(
            session, nodeIds, Attributes.AccessLevel, maxNodesPerRead, TimestampsToReturn.Neither, ct);

        var writable = 0;
        for (var i = 0; i < variables.Count; i++)
        {
            if (StatusCode.IsGood(results[i].StatusCode) && results[i].Value is byte accessLevel)
            {
                variables[i].AccessLevel = accessLevel;
                if ((accessLevel & AccessLevels.CurrentWrite) != 0)
                    writable++;
            }
        }

        logger.LogInformation(
            "Read access levels for {VariableCount} variable(s); {WritableCount} are writable upstream.",
            variables.Count, writable);
    }

    /// <summary>Compares a freshly browsed snapshot against the saved one. Reads only.</summary>
    public async Task<SnapshotDiff> DiffAsync(NamespaceSnapshot candidate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var current = await store.LoadAsync(ct);
        return current is null
            ? new SnapshotDiff(Keys(candidate).Keys.Order(StringComparer.Ordinal).ToList(), [], [])
            : Compare(current, candidate);
    }

    /// <summary>
    /// Writes a snapshot, replacing the saved one. The explicit command.
    /// </summary>
    /// <returns>Path of the backup taken first, or null on the very first capture.</returns>
    public async Task<string?> ApplyAsync(NamespaceSnapshot snapshot, CancellationToken ct = default)
    {
        var backupPath = await store.SaveAsync(snapshot, ct);

        logger.LogWarning(
            "The namespace snapshot has been replaced on an explicit command. " +
            "Restart OPC UA Bridge for the mirrored address space to reflect it.");

        return backupPath;
    }

    internal static SnapshotDiff Compare(NamespaceSnapshot current, NamespaceSnapshot candidate)
    {
        var before = Keys(current);
        var after = Keys(candidate);

        var added = after.Keys.Where(k => !before.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var removed = before.Keys.Where(k => !after.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();

        var changed = after
            .Where(pair => before.TryGetValue(pair.Key, out var old) && Differs(old, pair.Value))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new SnapshotDiff(added, removed, changed);
    }

    /// <summary>
    /// Indexes a snapshot's variables by identity: namespace URI plus identifier.
    /// </summary>
    /// <remarks>
    /// Keyed on the URI rather than the namespace index, because a server that renumbered
    /// its namespace array between captures would otherwise look as though it had
    /// replaced every tag it has -- turning a routine refresh into an alarming diff that
    /// describes nothing real.
    /// </remarks>
    private static Dictionary<string, SnapshotNode> Keys(NamespaceSnapshot snapshot)
    {
        var keyed = new Dictionary<string, SnapshotNode>(StringComparer.Ordinal);

        foreach (var node in snapshot.Variables())
        {
            var namespaceUri = node.NamespaceIndex >= 0 && node.NamespaceIndex < snapshot.NamespaceUris.Count
                ? snapshot.NamespaceUris[node.NamespaceIndex]
                : $"<index {node.NamespaceIndex}>";

            keyed[$"{namespaceUri}|{node.Identifier}"] = node;
        }

        return keyed;
    }

    private static bool Differs(SnapshotNode a, SnapshotNode b)
        => !string.Equals(a.DataType, b.DataType, StringComparison.Ordinal)
        || a.AccessLevel != b.AccessLevel
        || a.ValueRank != b.ValueRank
        || !string.Equals(a.EffectiveDisplayName, b.EffectiveDisplayName, StringComparison.Ordinal);
}
