using Opc.Ua;
using Opc.Ua.Client;
using OpcUaExporter.Models;
using OpcUaExporter.Services;

namespace OpcUaExporter.Operations;

/// <summary>Reads a single node's full attribute and reference detail.</summary>
public sealed class OpcUaNodeInspector(DiagnosticsLogService diagnostics)
{
    private readonly DiagnosticsLogService _diagnostics = diagnostics;

    /// <summary>Reads every attribute and reference of one node, for a properties panel.</summary>
    public async Task<NodeDetails> GetNodeDetailsAsync(Session session, string nodeId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(nodeId))
            throw new InvalidOperationException("Node id is required.");

        var parsedNodeId = NodeId.Parse(nodeId);
        var node = await session.ReadNodeAsync(parsedNodeId, ct);

        var details = new NodeDetails
        {
            NodeId = parsedNodeId.ToString(),
            BrowseName = node.BrowseName?.ToString() ?? string.Empty,
            DisplayName = node.DisplayName?.Text ?? parsedNodeId.ToString(),
            NodeClass = node.NodeClass.ToString()
        };

        await PopulateAttributesAsync(session, node, details, ct);

        var references = await session.FetchReferencesAsync(parsedNodeId, ct: ct);
        foreach (var reference in references)
        {
            var referenceTypeName = reference.ReferenceTypeId is not null
                ? await GetReferenceTypeNameAsync(reference.ReferenceTypeId, session)
                : "Unknown";

            details.References.Add(new NodeReferenceInfo
            {
                ReferenceTypeName = referenceTypeName,
                IsForward = reference.IsForward,
                TargetNodeId = reference.NodeId?.ToString() ?? string.Empty,
                TargetBrowseName = reference.BrowseName?.ToString() ?? string.Empty,
                TargetDisplayName = reference.DisplayName?.Text ?? string.Empty,
                TargetNodeClass = reference.NodeClass.ToString()
            });
        }

        details.References = details.References
            .OrderBy(r => r.ReferenceTypeName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(r => r.IsForward)
            .ThenBy(r => r.TargetDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _diagnostics.Add($"Loaded properties for '{details.DisplayName}' ({details.Attributes.Count} attribute(s), {details.References.Count} reference(s)).");

        return details;
    }

    private static async Task PopulateAttributesAsync(Session session, Node node, NodeDetails details, CancellationToken ct)
    {
        details.Attributes.Add(new NodeAttributeInfo { Name = "NodeId", Value = details.NodeId });
        details.Attributes.Add(new NodeAttributeInfo { Name = "BrowseName", Value = details.BrowseName });
        details.Attributes.Add(new NodeAttributeInfo { Name = "DisplayName", Value = details.DisplayName });
        details.Attributes.Add(new NodeAttributeInfo { Name = "NodeClass", Value = details.NodeClass });

        if (!string.IsNullOrWhiteSpace(node.Description?.Text))
            details.Attributes.Add(new NodeAttributeInfo { Name = "Description", Value = node.Description.Text });

        switch (node)
        {
            case VariableNode variable:
                var dataTypeName = await OpcUaDataTypes.GetDataTypeName(variable.DataType, session);
                details.Attributes.Add(new NodeAttributeInfo { Name = "DataType", Value = dataTypeName ?? variable.DataType?.ToString() ?? string.Empty });
                details.Attributes.Add(new NodeAttributeInfo { Name = "ValueRank", Value = variable.ValueRank.ToString() });
                if (variable.ArrayDimensions is { Count: > 0 })
                    details.Attributes.Add(new NodeAttributeInfo { Name = "ArrayDimensions", Value = string.Join(",", variable.ArrayDimensions) });
                details.Attributes.Add(new NodeAttributeInfo { Name = "AccessLevel", Value = ((AccessLevelType)variable.AccessLevel).ToString() });
                details.Attributes.Add(new NodeAttributeInfo { Name = "UserAccessLevel", Value = ((AccessLevelType)variable.UserAccessLevel).ToString() });
                details.Attributes.Add(new NodeAttributeInfo { Name = "Historizing", Value = variable.Historizing.ToString() });
                details.Attributes.Add(new NodeAttributeInfo { Name = "MinimumSamplingInterval", Value = variable.MinimumSamplingInterval.ToString(System.Globalization.CultureInfo.InvariantCulture) });

                try
                {
                    var readValueIdCollection = new ReadValueIdCollection
                    {
                        new ReadValueId { NodeId = variable.NodeId, AttributeId = Attributes.Value }
                    };
                    var readResponse = await session.ReadAsync(null, 0, TimestampsToReturn.Both, readValueIdCollection, ct);
                    var value = readResponse?.Results?[0];
                    if (value is not null)
                    {
                        details.Attributes.Add(new NodeAttributeInfo { Name = "Value", Value = value.Value?.ToString() ?? "null" });
                        details.Attributes.Add(new NodeAttributeInfo { Name = "Quality", Value = value.StatusCode.ToString() });
                    }
                }
                catch
                {
                    // Value read is best-effort — attributes above are still useful without it.
                }
                break;

            case VariableTypeNode variableType:
                var vtDataTypeName = await OpcUaDataTypes.GetDataTypeName(variableType.DataType, session);
                details.Attributes.Add(new NodeAttributeInfo { Name = "DataType", Value = vtDataTypeName ?? variableType.DataType?.ToString() ?? string.Empty });
                details.Attributes.Add(new NodeAttributeInfo { Name = "ValueRank", Value = variableType.ValueRank.ToString() });
                details.Attributes.Add(new NodeAttributeInfo { Name = "IsAbstract", Value = variableType.IsAbstract.ToString() });
                break;

            case ObjectNode obj:
                details.Attributes.Add(new NodeAttributeInfo { Name = "EventNotifier", Value = ((EventNotifierType)obj.EventNotifier).ToString() });
                break;

            case ObjectTypeNode objectType:
                details.Attributes.Add(new NodeAttributeInfo { Name = "IsAbstract", Value = objectType.IsAbstract.ToString() });
                break;

            case MethodNode method:
                details.Attributes.Add(new NodeAttributeInfo { Name = "Executable", Value = method.Executable.ToString() });
                details.Attributes.Add(new NodeAttributeInfo { Name = "UserExecutable", Value = method.UserExecutable.ToString() });
                break;

            case ReferenceTypeNode referenceType:
                details.Attributes.Add(new NodeAttributeInfo { Name = "IsAbstract", Value = referenceType.IsAbstract.ToString() });
                details.Attributes.Add(new NodeAttributeInfo { Name = "Symmetric", Value = referenceType.Symmetric.ToString() });
                if (!string.IsNullOrWhiteSpace(referenceType.InverseName?.Text))
                    details.Attributes.Add(new NodeAttributeInfo { Name = "InverseName", Value = referenceType.InverseName.Text });
                break;

            case DataTypeNode dataType:
                details.Attributes.Add(new NodeAttributeInfo { Name = "IsAbstract", Value = dataType.IsAbstract.ToString() });
                break;
        }
    }

    private static readonly Lazy<Dictionary<uint, string>> WellKnownReferenceTypeNames = new(() =>
    {
        var map = new Dictionary<uint, string>();
        foreach (var field in typeof(ReferenceTypeIds).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (field.GetValue(null) is NodeId nodeId && nodeId.IdType == IdType.Numeric && nodeId.Identifier is uint id)
                map[id] = field.Name;
        }
        return map;
    });

    private static async Task<string> GetReferenceTypeNameAsync(NodeId referenceTypeId, Session session)
    {
        if (referenceTypeId.NamespaceIndex == 0 &&
            referenceTypeId.IdType == IdType.Numeric &&
            referenceTypeId.Identifier is uint id &&
            WellKnownReferenceTypeNames.Value.TryGetValue(id, out var wellKnownName))
        {
            return wellKnownName;
        }

        try
        {
            var node = await session.ReadNodeAsync(referenceTypeId);
            return node?.DisplayName?.Text ?? referenceTypeId.ToString();
        }
        catch
        {
            return referenceTypeId.ToString();
        }
    }

    /// <summary>
    /// Scans the given ports on a host for OPC UA servers. Ports are attempted in the order
    /// supplied by the caller (well-known ports first, then the rest of the range), but
    /// <paramref name="onServerFound"/> fires whenever a probe completes since probes run concurrently.
    /// </summary>
}
