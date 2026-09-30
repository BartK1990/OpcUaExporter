using OpcUaExporter.Models;

namespace OpcUaExporter.Services;

/// <summary>A live OPC UA subscription whose monitored tags can be changed in place. Disposing it closes the session.</summary>
public interface ILiveSubscription : IAsyncDisposable
{
    /// <summary>Node IDs currently monitored.</summary>
    IReadOnlyCollection<string> NodeIds { get; }

    /// <summary>
    /// Adds/removes monitored items so exactly <paramref name="nodeIds"/> are monitored, and returns a current
    /// reading for each newly added tag. Cancellation leaves the subscription consistent: the next call
    /// completes any change that was interrupted.
    /// </summary>
    Task<List<TagReading>> UpdateAsync(IReadOnlyCollection<string> nodeIds, CancellationToken ct = default);
}
