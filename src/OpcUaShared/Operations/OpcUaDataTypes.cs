using Opc.Ua;
using Opc.Ua.Client;

namespace OpcUaExporter.Operations;

/// <summary>
/// Resolves a node's DataType attribute to a readable built-in type name.
/// </summary>
/// <remarks>
/// Kept apart from the browse and read paths because both need it and because
/// resolving a data type can itself cost a round trip, which is why the browse
/// path caches the results per scan.
/// </remarks>
internal static class OpcUaDataTypes
{
    internal static async Task<string?> GetDataTypeNameCachedAsync(NodeId dataTypeId, Session session, BrowseProgressState progress)
    {
        var key = dataTypeId.ToString();
        if (progress.DataTypeNameCache.TryGetValue(key, out var cached))
            return cached;

        var resolved = await GetDataTypeName(dataTypeId, session);
        progress.DataTypeNameCache.TryAdd(key, resolved);
        return resolved;
    }

    internal static async Task<string?> TryGetDataTypeName(Node? node, Session session)
    {
        if (node is VariableNode variable)
            return await GetDataTypeName(variable.DataType, session);

        return null;
    }

    internal static async Task<string?> GetDataTypeName(NodeId dataTypeId, Session session)
    {
        if (dataTypeId.IsNullNodeId)
            return null;

        var builtInType = await ResolveBuiltInTypeAsync(dataTypeId, session);
        if (builtInType != BuiltInType.Null)
            return builtInType.ToString();

        try
        {
            var dataTypeNode = await session.ReadNodeAsync(dataTypeId);
            return dataTypeNode?.DisplayName?.Text ?? dataTypeId.ToString();
        }
        catch
        {
            return dataTypeId.ToString();
        }
    }

    /// <summary>
    /// Resolves a DataType NodeId to its underlying OPC UA built-in type, walking up the
    /// "HasSubtype" hierarchy so subtypes (e.g. the standard UtcTime type, which subtypes
    /// DateTime, or a vendor-specific alias) are still recognized as their base built-in type.
    /// Also guards against misreading a numeric identifier from a non-zero namespace as a
    /// standard type id (only namespace 0 numeric ids map directly to a BuiltInType).
    /// </summary>
    internal static async Task<BuiltInType> ResolveBuiltInTypeAsync(NodeId dataTypeId, Session session)
    {
        var currentId = dataTypeId;

        for (var depth = 0; depth < 10 && currentId is not null && !currentId.IsNullNodeId; depth++)
        {
            var builtInType = TypeInfo.GetBuiltInType(currentId);
            if (builtInType != BuiltInType.Null)
                return builtInType;

            try
            {
                var references = await session.FetchReferencesAsync(currentId);
                var superTypeRef = references.FirstOrDefault(r =>
                    !r.IsForward && r.ReferenceTypeId == ReferenceTypeIds.HasSubtype);

                if (superTypeRef is null)
                    break;

                currentId = ExpandedNodeId.ToNodeId(superTypeRef.NodeId, session.NamespaceUris);
            }
            catch
            {
                break;
            }
        }

        return BuiltInType.Null;
    }

    /// Some servers declare a Variable's DataType attribute as the abstract BaseDataType
    /// (shown as "Variant"), letting the node hold any concrete type at runtime. In that
    /// case the static DataType attribute alone can't tell us the real type, so this
    /// refines it using the .NET type of the actual value most recently read.
    /// </summary>
    internal static string? RefineDataTypeFromValue(string? dataTypeName, object? value)
    {
        if (value is DateTime && (dataTypeName is null || string.Equals(dataTypeName, nameof(BuiltInType.Variant), StringComparison.OrdinalIgnoreCase)))
            return nameof(BuiltInType.DateTime);

        return dataTypeName;
    }
}
