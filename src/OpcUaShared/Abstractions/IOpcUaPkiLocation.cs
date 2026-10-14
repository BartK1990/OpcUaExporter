namespace OpcUaExporter.Abstractions;

/// <summary>
/// Where the OPC UA certificate stores (<c>own</c>, <c>trusted</c>, <c>issuer</c>,
/// <c>rejected</c>) live on disk.
/// <para>
/// This is the one piece of the OPC UA client that is genuinely
/// deployment-specific: the desktop exporter keeps its PKI under
/// <c>%LocalAppData%\OpcUaExporter\pki</c>, while a Windows service keeps
/// everything beside its own executable. Everything else about the client
/// configuration is identical, so only this is abstracted.
/// </para>
/// </summary>
public interface IOpcUaPkiLocation
{
    /// <summary>Root directory holding the four certificate stores. Created on demand.</summary>
    string PkiRoot { get; }
}

/// <summary>An <see cref="IOpcUaPkiLocation"/> pointing at a fixed directory.</summary>
public sealed class OpcUaPkiLocation(string pkiRoot) : IOpcUaPkiLocation
{
    public string PkiRoot { get; } = !string.IsNullOrWhiteSpace(pkiRoot)
        ? pkiRoot
        : throw new ArgumentException("PKI root must not be empty.", nameof(pkiRoot));

    /// <summary>
    /// <c>%LocalAppData%\{applicationName}\pki</c> — the per-user layout the desktop
    /// exporter has always used.
    /// </summary>
    /// <remarks>
    /// This deliberately recomputes the path rather than calling
    /// <c>OpcUaExporter.AppPaths</c>: that type lives in OpcUaExporter.Core, which
    /// references this assembly, so depending on it here would be a cycle.
    /// <c>AppPathsTests</c> pins the two to the same directory.
    /// </remarks>
    public static OpcUaPkiLocation LocalAppData(string applicationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        return new OpcUaPkiLocation(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            applicationName,
            "pki"));
    }

    /// <summary>
    /// <c>{baseDirectory}\pki</c> — the layout a Windows service uses, where everything
    /// the app persists sits beside its own executable.
    /// </summary>
    public static OpcUaPkiLocation BesideExecutable(string baseDirectory, string subdirectory = "pki")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(subdirectory);

        return new OpcUaPkiLocation(Path.Combine(baseDirectory, subdirectory));
    }
}
