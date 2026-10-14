using Opc.Ua;
using OpcUaBridge.Tags;

namespace OpcUaBridge.Server;

/// <summary>Forwards a downstream client's write to the upstream server.</summary>
public interface IUpstreamWriteRouter
{
    /// <summary>
    /// Writes a value upstream and returns the server's own status code.
    /// </summary>
    /// <remarks>
    /// Synchronous because the OPC UA server SDK's write hook is. Implementations must
    /// therefore bound both how long a write may take and how many may run at once.
    /// </remarks>
    StatusCode Write(MirrorTag tag, object? value);
}
