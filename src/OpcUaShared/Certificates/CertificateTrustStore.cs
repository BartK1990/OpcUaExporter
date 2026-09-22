using Opc.Ua;
using OpcUaExporter.Models;
using OpcUaExporter.Services;
using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;

namespace OpcUaExporter.Certificates;

/// <summary>
/// Decides which OPC UA server certificates this client will talk to.
/// </summary>
/// <remarks>
/// Untrusted certificates are neither auto-accepted nor silently dropped: they are
/// captured here so a front end can show them to a human, who trusts or rejects them.
/// One instance backs one <see cref="ApplicationConfiguration"/>, because trusting a
/// certificate writes it into that configuration's trusted-peer store.
/// </remarks>
public sealed class CertificateTrustStore(DiagnosticsLogService diagnostics)
{
    private readonly ConcurrentDictionary<string, X509Certificate2> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _trustedThumbprints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records a certificate the validator rejected, so a front end can offer to trust it.</summary>
    public void AddPending(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var key = certificate.Thumbprint ?? certificate.GetCertHashString();
        _pending[key] = certificate;
        diagnostics.Add($"Untrusted certificate pending approval: {certificate.Subject} ({key})");
    }

    /// <summary>Whether this certificate was trusted during the current process lifetime.</summary>
    public bool IsExplicitlyTrusted(string? thumbprint)
        => !string.IsNullOrWhiteSpace(thumbprint) && _trustedThumbprints.ContainsKey(thumbprint);

    /// <summary>Certificates awaiting a trust decision, ordered by subject.</summary>
    public List<PendingCertificateInfo> GetPending()
    {
        return _pending.Values
            .Select(c => new PendingCertificateInfo
            {
                Thumbprint = c.Thumbprint ?? string.Empty,
                Subject = c.Subject,
                Issuer = c.Issuer,
                ValidFrom = c.NotBefore,
                ValidTo = c.NotAfter
            })
            .OrderBy(c => c.Subject, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Moves a pending certificate into the configuration's trusted-peer store.</summary>
    public async Task<bool> TrustAsync(ApplicationConfiguration configuration, string thumbprint, CancellationToken ct = default)
    {
        if (!_pending.TryRemove(thumbprint, out var certificate))
            return false;

        ct.ThrowIfCancellationRequested();

        var trustedStore = configuration.SecurityConfiguration.TrustedPeerCertificates.OpenStore(null);
        await trustedStore.AddAsync(certificate, null, ct);

        if (!string.IsNullOrWhiteSpace(certificate.Thumbprint))
            _trustedThumbprints[certificate.Thumbprint] = 0;

        diagnostics.Add($"Trusted certificate: {certificate.Subject} ({certificate.Thumbprint})");
        return true;
    }

    /// <summary>Drops a pending certificate without trusting it.</summary>
    public bool Reject(string thumbprint)
    {
        if (!_pending.TryRemove(thumbprint, out var certificate))
            return false;

        diagnostics.Add($"Rejected certificate: {certificate.Subject} ({certificate.Thumbprint})");
        return true;
    }

    /// <summary>Whether the certificate is already present in the configuration's trusted-peer store.</summary>
    public static bool IsTrustedPeerCertificate(SecurityConfiguration securityConfiguration, X509Certificate2 certificate)
    {
        if (string.IsNullOrWhiteSpace(certificate.Thumbprint))
            return false;

        try
        {
            var trustedStore = securityConfiguration.TrustedPeerCertificates.OpenStore(null);
            var trusted = trustedStore
                .FindByThumbprintAsync(certificate.Thumbprint, default)
                .GetAwaiter()
                .GetResult();

            return trusted is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Public key size in bits, or 0 when it cannot be determined.</summary>
    public static int GetCertificateKeySize(X509Certificate2? certificate)
    {
        if (certificate is null)
            return 0;

        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is not null)
            return rsa.KeySize;

        using var ecdsa = certificate.GetECDsaPublicKey();
        if (ecdsa is not null)
            return ecdsa.KeySize;

        using var dsa = certificate.GetDSAPublicKey();
        if (dsa is not null)
            return dsa.KeySize;

        return 0;
    }
}
