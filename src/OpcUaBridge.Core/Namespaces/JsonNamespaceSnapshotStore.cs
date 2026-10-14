using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpcUaBridge.Namespaces;

/// <summary>Stores the snapshot as JSON beside the executable.</summary>
public sealed class JsonNamespaceSnapshotStore(
    string snapshotPath,
    Func<DateTimeOffset, string> backupPathFactory,
    ILogger<JsonNamespaceSnapshotStore> logger) : INamespaceSnapshotWriter
{
    private readonly Func<DateTimeOffset, string> _backupPathFactory = backupPathFactory;
    private readonly ILogger<JsonNamespaceSnapshotStore> _logger = logger;

    public string SnapshotPath { get; } = Path.GetFullPath(snapshotPath);

    public bool Exists => File.Exists(SnapshotPath);

    public async Task<NamespaceSnapshot?> LoadAsync(CancellationToken ct = default)
    {
        if (!Exists)
            return null;

        await using var stream = File.OpenRead(SnapshotPath);
        var snapshot = await JsonSerializer.DeserializeAsync(stream, SnapshotJsonContext.Default.NamespaceSnapshot, ct)
            ?? throw new InvalidDataException($"Namespace snapshot '{SnapshotPath}' is empty.");

        if (snapshot.SchemaVersion > NamespaceSnapshot.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Namespace snapshot '{SnapshotPath}' has schema version {snapshot.SchemaVersion}, but this " +
                $"build understands at most {NamespaceSnapshot.CurrentSchemaVersion}. Upgrade OPC UA Bridge, " +
                "or capture the namespace again.");
        }

        return snapshot;
    }

    public async Task<string?> SaveAsync(NamespaceSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);

        string? backupPath = null;
        if (Exists)
        {
            backupPath = _backupPathFactory(DateTimeOffset.UtcNow);
            File.Copy(SnapshotPath, backupPath, overwrite: true);
            _logger.LogInformation("Backed up the previous namespace snapshot to {BackupPath}.", backupPath);
        }

        // Write to a temporary file and move it into place, so a crash or a full disk
        // mid-write cannot leave a truncated snapshot -- which would strand the service
        // on its next start, unable to serve anything.
        var tempPath = SnapshotPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, SnapshotJsonContext.Default.NamespaceSnapshot, ct);
        }

        File.Move(tempPath, SnapshotPath, overwrite: true);

        _logger.LogInformation(
            "Namespace snapshot written to {SnapshotPath}: {ObjectCount} object(s), {VariableCount} variable(s).",
            SnapshotPath, snapshot.Stats.ObjectCount, snapshot.Stats.VariableCount);

        return backupPath;
    }

    public async Task<string?> ComputeHashAsync(CancellationToken ct = default)
    {
        if (!Exists)
            return null;

        await using var stream = File.OpenRead(SnapshotPath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
