namespace OpcUaExporter.Models;

/// <summary>Represents a single OPC UA node/tag discovered during browsing.</summary>
public class OpcTag
{
    public string NodeId      { get; set; } = string.Empty;
    public string BrowseName  { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string NodeClass   { get; set; } = string.Empty;
    public string? DataType   { get; set; }
    public object? Value      { get; set; }
    public string? Quality    { get; set; }
    public bool   IsSelected  { get; set; }
    public List<OpcTag> Children { get; set; } = [];

    /// <summary>Whether this node can be checked/selected (only Variable nodes carry a value to export).</summary>
    public bool IsSelectable => NodeClass == "Variable";

    /// <summary>Flattens this node and all descendant Variable nodes.</summary>
    public IEnumerable<OpcTag> Flatten()
    {
        if (NodeClass == "Variable")
            yield return this;
        foreach (var child in Children)
            foreach (var t in child.Flatten())
                yield return t;
    }
}

/// <summary>Full attribute + reference details for a single node, shown in the Node Properties panel.</summary>
public class NodeDetails
{
    public string NodeId      { get; set; } = string.Empty;
    public string BrowseName  { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string NodeClass   { get; set; } = string.Empty;
    public List<NodeAttributeInfo> Attributes { get; set; } = [];
    public List<NodeReferenceInfo> References { get; set; } = [];
}

/// <summary>A single OPC UA attribute (e.g. DataType, AccessLevel) read from a node.</summary>
public class NodeAttributeInfo
{
    public string Name  { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>A single reference to/from a node, e.g. HasComponent, Organizes, HasSubtype.</summary>
public class NodeReferenceInfo
{
    public string ReferenceTypeName  { get; set; } = string.Empty;
    public bool   IsForward          { get; set; }
    public string TargetNodeId       { get; set; } = string.Empty;
    public string TargetBrowseName   { get; set; } = string.Empty;
    public string TargetDisplayName  { get; set; } = string.Empty;
    public string TargetNodeClass    { get; set; } = string.Empty;
}

/// <summary>A single tag value reading.</summary>
public class TagReading
{
    public string  NodeId      { get; set; } = string.Empty;
    public string  DisplayName { get; set; } = string.Empty;
    public object? Value       { get; set; }
    public string? DataType    { get; set; }
    public string? Quality     { get; set; }
    public string? Timestamp   { get; set; }
    public string? Error       { get; set; }
}

/// <summary>Connection settings for an OPC UA server.</summary>
public class ConnectionProfile
{
    public string Name        { get; set; } = "New Server";
    public string EndpointUrl { get; set; } = "opc.tcp://localhost:4840";
    public ConnectionSecurityMode SecurityMode { get; set; } = ConnectionSecurityMode.None;
    public string SecurityPolicy { get; set; } = "http://opcfoundation.org/UA/SecurityPolicy#None";
    public AuthenticationType AuthenticationType { get; set; } = AuthenticationType.Anonymous;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool EnableParallelBrowse { get; set; } = true;
    public int ParallelBrowseMaxDegree { get; set; } = 10;
}

/// <summary>Export options.</summary>
public class ExportOptions
{
    public string OutputPath  { get; set; } = string.Empty;
    public ExportFormat Format { get; set; } = ExportFormat.Csv;
}

public class PendingCertificateInfo
{
    public string Thumbprint { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public DateTime ValidFrom { get; set; }
    public DateTime ValidTo { get; set; }
}

public class ServerCapabilitiesInfo
{
    public string ServerName { get; set; } = string.Empty;
    public List<ServerSecurityOption> SecurityOptions { get; set; } = new();
}

public class ServerSecurityOption
{
    public ConnectionSecurityMode SecurityMode { get; set; }
    public string SecurityPolicy { get; set; } = string.Empty;
    public bool SupportsAnonymous { get; set; }
    public bool SupportsUsernamePassword { get; set; }
}

/// <summary>An OPC UA server found while scanning a host's ports.</summary>
public class DiscoveredServerInfo
{
    public int Port { get; set; }
    public string EndpointUrl { get; set; } = string.Empty;
    public string? ApplicationName { get; set; }
    public bool HandshakeConfirmed { get; set; }
    public string? Error { get; set; }
}

public enum ConnectionSecurityMode
{
    None,
    Sign,
    SignAndEncrypt
}

public enum AuthenticationType
{
    Anonymous,
    UsernamePassword
}

public enum ExportFormat { Csv, Json }
