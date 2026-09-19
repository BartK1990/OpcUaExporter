namespace OpcUaBridge.Namespaces;

/// <summary>
/// Read access to the captured address space.
/// </summary>
/// <remarks>
/// The whole runtime pipeline -- the connection manager, both acquisition engines and the
/// mirror server -- is injected this interface and not <see cref="INamespaceSnapshotWriter"/>.
/// That is the enforcement of "the saved namespace never changes without an explicit
/// command": the code paths that run continuously have no method that could write the file,
/// so no amount of reconnecting, re-browsing or error recovery can rewrite it.
/// </remarks>
public interface INamespaceSnapshotStore
{
    /// <summary>Absolute path of the snapshot file.</summary>
    string SnapshotPath { get; }

    /// <summary>Whether a snapshot has been captured.</summary>
    bool Exists { get; }

    /// <summary>Loads the snapshot, or null when none has been captured.</summary>
    Task<NamespaceSnapshot?> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// SHA-256 of the snapshot's bytes, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Logged at load so that a file edited outside the bridge shows up in the log as a
    /// changed hash, rather than as unexplained behaviour.
    /// </remarks>
    Task<string?> ComputeHashAsync(CancellationToken ct = default);
}

/// <summary>
/// Write access, deliberately separated from <see cref="INamespaceSnapshotStore"/>.
/// </summary>
/// <remarks>
/// Only the capture service takes this interface, and only an explicit operator action
/// reaches the capture service.
/// </remarks>
public interface INamespaceSnapshotWriter : INamespaceSnapshotStore
{
    /// <summary>
    /// Replaces the snapshot, backing up the previous one first.
    /// </summary>
    /// <returns>Path of the backup, or null when there was nothing to back up.</returns>
    Task<string?> SaveAsync(NamespaceSnapshot snapshot, CancellationToken ct = default);
}
