using Opc.Ua;
using Opc.Ua.Client;
using OpcUaExporter.Models;
using OpcUaExporter.Services;

namespace OpcUaExporter.Operations;

/// <summary>Reads node values over a session the caller owns.</summary>
public sealed class OpcUaValueReader(DiagnosticsLogService diagnostics)
{
    /// <summary>Used when the server declares no <c>MaxNodesPerRead</c> limit.</summary>
    public const int DefaultMaxNodesPerRead = 1000;

    private readonly DiagnosticsLogService _diagnostics = diagnostics;

    /// <summary>
    /// Reads each node's value together with its display name and data type.
    /// </summary>
    /// <remarks>
    /// This costs two round trips per node, which is the price of returning the
    /// descriptive metadata an interactive table shows. Callers polling thousands of
    /// nodes on a timer want <see cref="ReadValuesAsync"/> instead.
    /// </remarks>
    public async Task<List<TagReading>> ReadDescribedAsync(Session session, IEnumerable<string> nodeIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var ids = nodeIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<TagReading>(ids.Count);

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();

            var row = new TagReading
            {
                NodeId = id
            };

            try
            {
                var readValueIdCollection = new ReadValueIdCollection
                {
                    new ReadValueId
                    {
                        NodeId = NodeId.Parse(id),
                        AttributeId = Attributes.Value
                    }
                };

                var readResponse = await session.ReadAsync(null, 0, TimestampsToReturn.Both, readValueIdCollection, ct);
                var value = readResponse?.Results?[0];
                row.Value = value?.Value;
                row.Quality = value?.StatusCode.ToString();
                row.Timestamp = value?.SourceTimestamp.ToString("o");

                var node = await session.ReadNodeAsync(NodeId.Parse(id), ct);
                row.DisplayName = node?.DisplayName?.Text ?? id;
                row.DataType = OpcUaDataTypes.RefineDataTypeFromValue(await OpcUaDataTypes.TryGetDataTypeName(node, session), row.Value);
            }
            catch (Exception ex)
            {
                row.DisplayName = id;
                row.Error = ex.Message;
            }

            rows.Add(row);
        }

        _diagnostics.Add($"Read completed. Returned {rows.Count} row(s).");
        return rows;
    }

    /// <summary>
    /// Reads the Value attribute of many nodes in as few service calls as the server
    /// allows, returning results in the same order as <paramref name="nodeIds"/>.
    /// </summary>
    /// <remarks>
    /// This is the path a polling gateway uses. It reads nothing but the value -- no
    /// display names, no data types -- because at a few thousand tags on a timer the
    /// per-node metadata round trips dominate everything else.
    /// A node the server rejects yields a <see cref="DataValue"/> carrying that status
    /// rather than throwing, so one bad node cannot fail a whole cycle.
    /// </remarks>
    /// <param name="maxNodesPerRead">
    /// Server-declared limit on nodes per Read call; values below 1 mean "no limit
    /// declared" and fall back to <see cref="DefaultMaxNodesPerRead"/>.
    /// </param>
    public static async Task<DataValue[]> ReadValuesAsync(
        Session session,
        IReadOnlyList<NodeId> nodeIds,
        int maxNodesPerRead = DefaultMaxNodesPerRead,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(nodeIds);

        var results = new DataValue[nodeIds.Count];
        if (nodeIds.Count == 0)
            return results;

        var chunkSize = maxNodesPerRead > 0 ? maxNodesPerRead : DefaultMaxNodesPerRead;

        for (var offset = 0; offset < nodeIds.Count; offset += chunkSize)
        {
            ct.ThrowIfCancellationRequested();

            var count = Math.Min(chunkSize, nodeIds.Count - offset);
            var request = new ReadValueIdCollection(count);
            for (var i = 0; i < count; i++)
            {
                request.Add(new ReadValueId
                {
                    NodeId = nodeIds[offset + i],
                    AttributeId = Attributes.Value
                });
            }

            var response = await session.ReadAsync(null, 0, TimestampsToReturn.Both, request, ct);
            var values = response?.Results;

            for (var i = 0; i < count; i++)
            {
                results[offset + i] = values is not null && i < values.Count && values[i] is not null
                    ? values[i]
                    : new DataValue(StatusCodes.BadUnexpectedError);
            }
        }

        return results;
    }

    /// <summary>
    /// The server's declared <c>MaxNodesPerRead</c>, or <see cref="DefaultMaxNodesPerRead"/>
    /// when it declares none.
    /// </summary>
    public static async Task<int> GetMaxNodesPerReadAsync(Session session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        try
        {
            var request = new ReadValueIdCollection
            {
                new ReadValueId
                {
                    NodeId = VariableIds.Server_ServerCapabilities_OperationLimits_MaxNodesPerRead,
                    AttributeId = Attributes.Value
                }
            };

            var response = await session.ReadAsync(null, 0, TimestampsToReturn.Neither, request, ct);
            var value = response?.Results?[0];

            if (value is not null && StatusCode.IsGood(value.StatusCode) && value.Value is uint limit && limit > 0)
                return (int)Math.Min(limit, int.MaxValue);
        }
        catch (Exception)
        {
            // Not every server exposes the OperationLimits object. Falling back to a
            // conservative default is correct here -- the read still works, it is just
            // chunked more cautiously than the server could actually handle.
        }

        return DefaultMaxNodesPerRead;
    }

    /// <summary>Seed values for a freshly created subscription, so a table is never blank.</summary>
    public static async Task<List<TagReading>> ReadCurrentValuesAsync(Session session, IEnumerable<string> nodeIds, CancellationToken ct)
    {
        var ids = nodeIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<TagReading>(ids.Count);

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();

            var row = new TagReading
            {
                NodeId = id,
                DisplayName = id
            };

            try
            {
                var nodeId = NodeId.Parse(id);
                var readValueIdCollection = new ReadValueIdCollection
                {
                    new ReadValueId
                    {
                        NodeId = nodeId,
                        AttributeId = Attributes.Value
                    }
                };

                var readResponse = await session.ReadAsync(null, 0, TimestampsToReturn.Both, readValueIdCollection, ct);
                var value = readResponse?.Results?[0];
                row.Value = value?.Value;
                row.Quality = value?.StatusCode.ToString();
                row.Timestamp = value?.SourceTimestamp.ToString("o");

                var node = await session.ReadNodeAsync(nodeId, ct);
                row.DisplayName = node?.DisplayName?.Text ?? id;
                row.DataType = OpcUaDataTypes.RefineDataTypeFromValue(await OpcUaDataTypes.TryGetDataTypeName(node, session), row.Value);
            }
            catch (Exception ex)
            {
                row.Error = ex.Message;
            }

            rows.Add(row);
        }

        return rows;
    }
}
