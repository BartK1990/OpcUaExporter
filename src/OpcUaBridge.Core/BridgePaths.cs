namespace OpcUaBridge;

/// <summary>
/// Every file the bridge reads or writes, resolved relative to the executable.
/// </summary>
/// <remarks>
/// <para>
/// A Windows service starts with its working directory set to
/// <c>C:\Windows\System32</c>, not its install folder. Anything resolved from
/// <see cref="Directory.GetCurrentDirectory"/> would therefore land in the system
/// directory -- where it will usually fail to write, and where it would be wrong
/// even if it succeeded. So every path here hangs off
/// <see cref="AppContext.BaseDirectory"/>, and nothing in the bridge composes a
/// path any other way.
/// </para>
/// <para>
/// Keeping logs, configuration and certificates beside the executable also makes
/// the whole installation one portable folder, which is what lets an operator copy
/// a working PKI directory in from elsewhere.
/// </para>
/// </remarks>
public sealed class BridgePaths(string baseDirectory)
{
    /// <summary>Paths rooted at the running executable's directory.</summary>
    public static BridgePaths Default { get; } = new(AppContext.BaseDirectory);

    /// <summary>The installation folder: where OpcUaBridge.exe lives.</summary>
    public string BaseDirectory { get; } = !string.IsNullOrWhiteSpace(baseDirectory)
        ? Path.GetFullPath(baseDirectory)
        : throw new ArgumentException("Base directory must not be empty.", nameof(baseDirectory));

    /// <summary>Serilog's rolling log files.</summary>
    public string LogDirectory => Path.Combine(BaseDirectory, "Logs");

    /// <summary>Operator-editable state: the namespace snapshot and its backups.</summary>
    public string ConfigDirectory => Path.Combine(BaseDirectory, "config");

    /// <summary>Root of both certificate stores.</summary>
    public string PkiDirectory => Path.Combine(BaseDirectory, "pki");

    /// <summary>
    /// Certificate stores for the bridge acting as a <em>client</em> of the upstream
    /// server. Copy an existing working PKI directory here to reuse its identity.
    /// </summary>
    public string ClientPkiDirectory => Path.Combine(PkiDirectory, "client");

    /// <summary>
    /// Certificate stores for the bridge acting as a <em>server</em> to downstream
    /// clients. A separate identity from the client one: different key, different
    /// subject, trusted by different peers.
    /// </summary>
    public string ServerPkiDirectory => Path.Combine(PkiDirectory, "server");

    /// <summary>The captured upstream address space. Never rewritten without an explicit command.</summary>
    public string NamespaceSnapshotFile => Path.Combine(ConfigDirectory, "namespace.json");

    /// <summary>Timestamped backup written before a capture replaces the snapshot.</summary>
    public string NamespaceBackupFile(DateTimeOffset timestamp)
        => Path.Combine(ConfigDirectory, $"namespace.{timestamp:yyyyMMdd-HHmmss}.json");

    /// <summary>Creates the directories the bridge writes to. Safe to call repeatedly.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(ClientPkiDirectory);
        Directory.CreateDirectory(ServerPkiDirectory);
    }
}
