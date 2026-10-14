using System.Text.Json.Serialization;

namespace OpcUaBridge.Namespaces;

/// <summary>
/// A captured picture of the upstream server's address space.
/// </summary>
/// <remarks>
/// <para>
/// This file is the contract between the bridge and the application behind it. Once
/// captured it is never rewritten by the running pipeline -- only by an explicit operator
/// command -- so the tags a downstream client sees cannot change underneath it because
/// somebody reconfigured the plant server.
/// </para>
/// <para>
/// Property names are abbreviated because a few thousand tags is a few megabytes of JSON,
/// and this file is parsed on every start of a service that is expected to come back
/// quickly after a reboot.
/// </para>
/// </remarks>
public sealed class NamespaceSnapshot
{
    /// <summary>Incremented when the shape of this file changes in a way older readers cannot handle.</summary>
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("capturedUtc")]
    public DateTimeOffset CapturedUtc { get; set; }

    [JsonPropertyName("source")]
    public SnapshotSource Source { get; set; } = new();

    /// <summary>
    /// The upstream server's namespace table as it stood at capture time, in index order.
    /// </summary>
    /// <remarks>
    /// Recording the URIs, not just the indexes, is what makes the snapshot safe to reuse.
    /// A namespace index is only meaningful within the session that reported it: servers
    /// reorder their namespace array across restarts and insert entries when reconfigured.
    /// A snapshot keyed on bare indexes would, after such a restart, cheerfully serve
    /// completely different tags under the same names and report them as Good.
    /// </remarks>
    [JsonPropertyName("namespaceUris")]
    public List<string> NamespaceUris { get; set; } = [];

    [JsonPropertyName("stats")]
    public SnapshotStats Stats { get; set; } = new();

    [JsonPropertyName("nodes")]
    public List<SnapshotNode> Nodes { get; set; } = [];

    /// <summary>Every variable in the tree, depth first.</summary>
    public IEnumerable<SnapshotNode> Variables() => Nodes.SelectMany(n => n.SelfAndDescendants()).Where(n => n.IsVariable);
}

/// <summary>Which server this snapshot came from.</summary>
public sealed class SnapshotSource
{
    [JsonPropertyName("endpointUrl")]
    public string EndpointUrl { get; set; } = string.Empty;

    [JsonPropertyName("applicationUri")]
    public string? ApplicationUri { get; set; }

    [JsonPropertyName("serverName")]
    public string? ServerName { get; set; }
}

/// <summary>Counts, so a diff can be summarised without walking the tree.</summary>
public sealed class SnapshotStats
{
    [JsonPropertyName("objectCount")]
    public int ObjectCount { get; set; }

    [JsonPropertyName("variableCount")]
    public int VariableCount { get; set; }
}

/// <summary>One mirrored node: a folder or a variable.</summary>
public sealed class SnapshotNode
{
    /// <summary>Index into <see cref="NamespaceSnapshot.NamespaceUris"/>.</summary>
    [JsonPropertyName("ns")]
    public int NamespaceIndex { get; set; }

    /// <summary>NodeId identifier without the <c>ns=N;</c> prefix, e.g. <c>s=Line1.Speed</c>.</summary>
    [JsonPropertyName("id")]
    public string Identifier { get; set; } = string.Empty;

    [JsonPropertyName("bn")]
    public string BrowseName { get; set; } = string.Empty;

    /// <summary>Omitted when it equals the browse name.</summary>
    [JsonPropertyName("dn")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("var")]
    public bool IsVariable { get; set; }

    /// <summary>Resolved built-in type name, e.g. <c>Double</c>. Variables only.</summary>
    [JsonPropertyName("dt")]
    public string? DataType { get; set; }

    /// <summary>OPC UA ValueRank; -1 is a scalar.</summary>
    [JsonPropertyName("rank")]
    public int ValueRank { get; set; } = -1;

    /// <summary>
    /// AccessLevel bits as reported upstream (1 = CurrentRead, 2 = CurrentWrite).
    /// </summary>
    /// <remarks>
    /// Mirrored verbatim onto the downstream node, so a tag the plant server considers
    /// read-only is rejected by the bridge's own server before a write is ever forwarded.
    /// </remarks>
    [JsonPropertyName("acc")]
    public byte AccessLevel { get; set; } = 1;

    [JsonPropertyName("ch")]
    public List<SnapshotNode> Children { get; set; } = [];

    /// <summary>The display name, falling back to the browse name.</summary>
    [JsonIgnore]
    public string EffectiveDisplayName => string.IsNullOrEmpty(DisplayName) ? BrowseName : DisplayName;

    /// <summary>This node and every descendant, depth first.</summary>
    public IEnumerable<SnapshotNode> SelfAndDescendants()
    {
        yield return this;

        foreach (var child in Children)
            foreach (var descendant in child.SelfAndDescendants())
                yield return descendant;
    }
}
