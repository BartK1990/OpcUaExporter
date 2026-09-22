using Opc.Ua;
using Opc.Ua.Client;
using OpcUaExporter.Models;

namespace OpcUaBridge.Namespaces;

/// <summary>Turns a browse result into a snapshot that can outlive the session it came from.</summary>
internal static class SnapshotFactory
{
    /// <summary>
    /// Converts the browsed tree, replacing every session-scoped namespace index with the
    /// namespace URI it stood for.
    /// </summary>
    /// <remarks>
    /// This translation has to happen while the session is still open, because
    /// <see cref="ISession.NamespaceUris"/> is the only thing that knows what the indexes
    /// in those NodeIds meant. Persisting them untranslated is what would let a later
    /// server restart silently point the mirror at different tags.
    /// </remarks>
    public static NamespaceSnapshot FromBrowseResult(
        IReadOnlyList<OpcTag> tags,
        ISession session,
        string endpointUrl)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(session);

        var namespaceUris = session.NamespaceUris.ToArray().ToList();

        var snapshot = new NamespaceSnapshot
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            NamespaceUris = namespaceUris,
            Source = new SnapshotSource
            {
                EndpointUrl = endpointUrl,
                ApplicationUri = session.Endpoint?.Server?.ApplicationUri,
                ServerName = session.Endpoint?.Server?.ApplicationName?.Text
            }
        };

        var objectCount = 0;
        var variableCount = 0;

        foreach (var tag in tags)
        {
            var node = Convert(tag, namespaceUris, ref objectCount, ref variableCount);
            if (node is not null)
                snapshot.Nodes.Add(node);
        }

        snapshot.Stats = new SnapshotStats { ObjectCount = objectCount, VariableCount = variableCount };
        return snapshot;
    }

    private static SnapshotNode? Convert(OpcTag tag, List<string> namespaceUris, ref int objectCount, ref int variableCount)
    {
        if (!NodeId.TryParse(tag.NodeId, out var nodeId))
            return null;

        var isVariable = string.Equals(tag.NodeClass, "Variable", StringComparison.OrdinalIgnoreCase);

        if (isVariable)
            variableCount++;
        else
            objectCount++;

        var node = new SnapshotNode
        {
            NamespaceIndex = EnsureNamespace(nodeId.NamespaceIndex, namespaceUris),
            Identifier = FormatIdentifier(nodeId),
            BrowseName = StripNamespacePrefix(tag.BrowseName),
            DisplayName = string.Equals(tag.DisplayName, tag.BrowseName, StringComparison.Ordinal) ? null : tag.DisplayName,
            IsVariable = isVariable,
            DataType = isVariable ? tag.DataType : null,
            // Left read-only here. The browse does not report AccessLevel, and guessing
            // "writable" would let a downstream client attempt writes the plant server was
            // never going to accept. NamespaceCaptureService reads the real value.
            AccessLevel = isVariable ? AccessLevels.CurrentRead : (byte)0
        };

        foreach (var child in tag.Children)
        {
            var converted = Convert(child, namespaceUris, ref objectCount, ref variableCount);
            if (converted is not null)
                node.Children.Add(converted);
        }

        return node;
    }

    /// <summary>The namespace table can be shorter than an index the server used; keep the snapshot self-consistent.</summary>
    private static int EnsureNamespace(ushort namespaceIndex, List<string> namespaceUris)
    {
        while (namespaceUris.Count <= namespaceIndex)
            namespaceUris.Add($"urn:opcuabridge:unknown-namespace-{namespaceUris.Count}");

        return namespaceIndex;
    }

    /// <summary>The NodeId without its <c>ns=N;</c> prefix, e.g. <c>s=Line1.Speed</c>.</summary>
    private static string FormatIdentifier(NodeId nodeId)
    {
        var text = nodeId.ToString()!;
        var separator = text.IndexOf(';');

        return separator >= 0 && text.StartsWith("ns=", StringComparison.Ordinal)
            ? text[(separator + 1)..]
            : text;
    }

    /// <summary>Browse names arrive as <c>2:Speed</c>; the mirror re-applies its own namespace.</summary>
    private static string StripNamespacePrefix(string browseName)
    {
        var separator = browseName.IndexOf(':');
        return separator > 0 && int.TryParse(browseName[..separator], out _)
            ? browseName[(separator + 1)..]
            : browseName;
    }
}
