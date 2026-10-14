using System.Collections.Concurrent;

namespace OpcUaExporter.Operations;

/// <summary>
/// DataType NodeId to display name, so each distinct type costs one round trip rather than
/// one per variable.
/// </summary>
/// <remarks>
/// <para>
/// Owned by the caller rather than by one browse, because the exporter shares one between its
/// background deep scan and the on-demand expands a user makes while that scan is running.
/// </para>
/// <para>
/// Keyed by NodeId text, and a namespace index is only meaningful against one server's
/// namespace table, so a cache must never outlive the server it was filled from. Clear it
/// before browsing a server afresh.
/// </para>
/// </remarks>
public sealed class DataTypeNameCache
{
    private readonly ConcurrentDictionary<string, string?> _names = new(StringComparer.Ordinal);

    internal bool TryGet(string dataTypeId, out string? name) => _names.TryGetValue(dataTypeId, out name);

    internal void Add(string dataTypeId, string? name) => _names.TryAdd(dataTypeId, name);

    public void Clear() => _names.Clear();
}
