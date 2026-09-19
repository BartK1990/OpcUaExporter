using Opc.Ua;
using Opc.Ua.Client;
using OpcUaExporter.Models;
using OpcUaExporter.Services;

namespace OpcUaExporter.Operations;

/// <summary>Writes node values over a session the caller owns.</summary>
public sealed class OpcUaValueWriter(DiagnosticsLogService diagnostics)
{
    private readonly DiagnosticsLogService _diagnostics = diagnostics;

    /// <summary>Writes a single value to a node's Value attribute, converting the raw input to the node's data type.</summary>
    public async Task<TagReading> WriteAsync(Session session, string nodeId, string rawValue, string? dataTypeHint = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            throw new InvalidOperationException("Node id is required.");

        var parsedNodeId = NodeId.Parse(nodeId);
        var row = new TagReading { NodeId = nodeId };

        try
        {
            var dataTypeName = dataTypeHint;
            if (string.IsNullOrWhiteSpace(dataTypeName))
            {
                var node = await session.ReadNodeAsync(parsedNodeId, ct);
                row.DisplayName = node?.DisplayName?.Text ?? nodeId;
                dataTypeName = await OpcUaDataTypes.TryGetDataTypeName(node, session);
            }

            var value = ConvertValueForWrite(dataTypeName, rawValue);

            var writeValueCollection = new WriteValueCollection
            {
                new WriteValue
                {
                    NodeId = parsedNodeId,
                    AttributeId = Attributes.Value,
                    Value = new DataValue(new Variant(value))
                }
            };

            var writeResponse = await session.WriteAsync(null, writeValueCollection, ct);
            var status = writeResponse?.Results?[0] ?? StatusCodes.BadUnexpectedError;

            row.Quality = status.ToString();
            row.DataType = dataTypeName;
            row.Timestamp = DateTime.UtcNow.ToString("o");

            if (StatusCode.IsBad(status))
                row.Error = $"Write rejected by server: {status}";
            else
                _diagnostics.Add($"Wrote '{rawValue}' to '{nodeId}'.");
        }
        catch (Exception ex)
        {
            row.Error = ex.Message;
        }

        return row;
    }

    /// <summary>Converts a raw string into the .NET type matching the node's OPC UA built-in data type.</summary>
    internal static object ConvertValueForWrite(string? dataTypeName, string rawValue)
    {
        if (dataTypeName is null || !Enum.TryParse<BuiltInType>(dataTypeName, ignoreCase: true, out var builtInType))
            return rawValue;

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        return builtInType switch
        {
            BuiltInType.Boolean  => bool.Parse(rawValue),
            BuiltInType.SByte    => sbyte.Parse(rawValue, ci),
            BuiltInType.Byte     => byte.Parse(rawValue, ci),
            BuiltInType.Int16    => short.Parse(rawValue, ci),
            BuiltInType.UInt16   => ushort.Parse(rawValue, ci),
            BuiltInType.Int32    => int.Parse(rawValue, ci),
            BuiltInType.UInt32   => uint.Parse(rawValue, ci),
            BuiltInType.Int64    => long.Parse(rawValue, ci),
            BuiltInType.UInt64   => ulong.Parse(rawValue, ci),
            BuiltInType.Float    => float.Parse(rawValue, ci),
            BuiltInType.Double   => double.Parse(rawValue, ci),
            BuiltInType.DateTime => DateTime.Parse(rawValue, ci, System.Globalization.DateTimeStyles.RoundtripKind),
            BuiltInType.Guid     => Guid.Parse(rawValue),
            BuiltInType.String   => rawValue,
            _                    => rawValue
        };
    }

    /// <summary>
    /// Writes a value that is already typed, returning the server's status code verbatim.
    /// </summary>
    /// <remarks>
    /// This is the gateway path: a downstream client has already supplied a
    /// <see cref="DataValue"/> of the right type, so there is nothing to parse and
    /// nothing to guess. Only the value itself is forwarded -- source and server
    /// timestamps are the upstream server's to assign.
    /// </remarks>
    public static async Task<StatusCode> WriteValueAsync(
        Session session,
        NodeId nodeId,
        DataValue value,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentNullException.ThrowIfNull(value);

        var request = new WriteValueCollection
        {
            new WriteValue
            {
                NodeId = nodeId,
                AttributeId = Attributes.Value,
                Value = new DataValue(value.WrappedValue)
            }
        };

        var response = await session.WriteAsync(null, request, ct);
        return response?.Results?[0] ?? StatusCodes.BadUnexpectedError;
    }
}
