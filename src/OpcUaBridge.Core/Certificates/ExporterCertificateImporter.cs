using Microsoft.Extensions.Logging;

namespace OpcUaBridge.Certificates;

/// <summary>What an import found and copied.</summary>
/// <param name="SourceDirectory">Where the certificates were read from.</param>
/// <param name="TargetDirectory">The bridge's client certificate store.</param>
/// <param name="FilesCopied">How many files were copied.</param>
/// <param name="FoundOwnCertificate">Whether a client certificate with its private key came across.</param>
/// <param name="TrustedCertificateCount">How many already-trusted server certificates came across.</param>
public sealed record CertificateImportResult(
    string SourceDirectory,
    string TargetDirectory,
    int FilesCopied,
    bool FoundOwnCertificate,
    int TrustedCertificateCount)
{
    public bool ImportedAnything => FilesCopied > 0;
}

/// <summary>
/// Copies an existing OPC UA Exporter certificate store into the bridge's.
/// </summary>
/// <remarks>
/// <para>
/// Worth automating because doing it by hand goes wrong in a way that is genuinely hard to
/// diagnose. Two things have to arrive for the bridge to connect without touching the
/// plant server at all: the client certificate the server already trusts, and the server's
/// own certificate that the exporter already trusted. Miss either and the failure looks
/// like a connection problem rather than a missing file.
/// </para>
/// <para>
/// This only moves files. The identity the certificate was issued to still has to match
/// <c>Bridge:Upstream:ClientApplicationName</c>, which is why that setting defaults to
/// <c>OpcUaExporter</c>.
/// </para>
/// </remarks>
public sealed class ExporterCertificateImporter(BridgePaths paths, ILogger<ExporterCertificateImporter> logger)
{
    /// <summary>The exporter's per-user certificate store, <c>%LocalAppData%\OpcUaExporter\pki</c>.</summary>
    public static string DefaultSourceDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpcUaExporter",
        "pki");

    /// <summary>Whether there is anything to import from the default location.</summary>
    public bool DefaultSourceExists => Directory.Exists(DefaultSourceDirectory);

    /// <summary>Copies the four certificate stores across, without overwriting what is already there.</summary>
    public CertificateImportResult Import(string? sourceDirectory = null)
    {
        var source = sourceDirectory ?? DefaultSourceDirectory;
        var target = paths.ClientPkiDirectory;

        if (!Directory.Exists(source))
        {
            logger.LogWarning("No certificate store to import at {SourceDirectory}.", source);
            return new CertificateImportResult(source, target, 0, false, 0);
        }

        var copied = 0;
        foreach (var store in new[] { "own", "trusted", "issuer", "rejected" })
        {
            var storeSource = Path.Combine(source, store);
            if (Directory.Exists(storeSource))
                copied += CopyTree(storeSource, Path.Combine(target, store));
        }

        var foundOwn = Directory.Exists(Path.Combine(target, "own", "private"))
            && Directory.EnumerateFiles(Path.Combine(target, "own", "private")).Any();

        var trustedDirectory = Path.Combine(target, "trusted", "certs");
        var trustedCount = Directory.Exists(trustedDirectory)
            ? Directory.EnumerateFiles(trustedDirectory).Count()
            : 0;

        logger.LogInformation(
            "Imported {FileCount} certificate file(s) from {SourceDirectory}. " +
            "Client certificate with private key: {FoundOwn}. Trusted server certificates: {TrustedCount}.",
            copied, source, foundOwn, trustedCount);

        if (!foundOwn)
        {
            logger.LogWarning(
                "No client certificate private key was imported, so the bridge will generate its own. " +
                "The upstream server will reject it until that new certificate is trusted there.");
        }

        return new CertificateImportResult(source, target, copied, foundOwn, trustedCount);
    }

    /// <summary>
    /// Copies a directory tree, leaving any file that already exists alone.
    /// </summary>
    /// <remarks>
    /// Never overwriting means running an import twice is harmless, and an operator who
    /// has already set the bridge up cannot destroy it by clicking the button again.
    /// </remarks>
    private int CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        var copied = 0;

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(destination))
                continue;

            File.Copy(file, destination);
            copied++;
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
            copied += CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));

        return copied;
    }
}
