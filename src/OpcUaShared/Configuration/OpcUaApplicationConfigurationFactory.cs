using Opc.Ua;
using Opc.Ua.Configuration;
using OpcUaExporter.Abstractions;
using OpcUaExporter.Certificates;
using OpcUaExporter.Services;

namespace OpcUaExporter.Configuration;

/// <summary>
/// Builds the OPC UA <see cref="ApplicationConfiguration"/> this process uses.
/// </summary>
/// <remarks>
/// Everything here was previously hard-coded inside the client service. The only
/// parts that genuinely vary between deployments are the identity presented to the
/// server and where the certificate stores live, so those are parameters and the
/// rest -- transport quotas, key sizes, store layout -- is shared policy.
/// </remarks>
public static class OpcUaApplicationConfigurationFactory
{
    /// <summary>
    /// Builds, validates and returns a client <see cref="ApplicationConfiguration"/>,
    /// creating the application certificate on first use.
    /// </summary>
    /// <remarks>
    /// The returned configuration's validator routes untrusted server certificates into
    /// <paramref name="trustStore"/> instead of accepting or discarding them, so a front
    /// end can put the decision to a human.
    /// </remarks>
    public static async Task<ApplicationConfiguration> CreateClientAsync(
        OpcUaApplicationIdentity identity,
        IOpcUaPkiLocation pki,
        CertificateTrustStore trustStore,
        DiagnosticsLogService diagnostics,
        CancellationToken ct = default)
    {
        var pkiRoot = pki.PkiRoot;

        var trustedPeerStorePath = Path.Combine(pkiRoot, "trusted");
        var trustedIssuerStorePath = Path.Combine(pkiRoot, "issuer");
        var rejectedStorePath = Path.Combine(pkiRoot, "rejected");
        var ownStorePath = Path.Combine(pkiRoot, "own");

        Directory.CreateDirectory(pkiRoot);
        Directory.CreateDirectory(trustedPeerStorePath);
        Directory.CreateDirectory(trustedIssuerStorePath);
        Directory.CreateDirectory(rejectedStorePath);
        Directory.CreateDirectory(ownStorePath);

        var config = new ApplicationConfiguration
        {
            ApplicationName = identity.ApplicationName,
            ApplicationType = ApplicationType.Client,
            ApplicationUri = identity.ApplicationUri,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = ownStorePath,
                    SubjectName = identity.SubjectName
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = trustedPeerStorePath
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = trustedIssuerStorePath
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = rejectedStorePath
                },
                AutoAcceptUntrustedCertificates = false,
                AddAppCertToTrustedStore = false,
                RejectSHA1SignedCertificates = false,
                MinimumCertificateKeySize = 2048
            },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = 15000,
                MaxStringLength = 1024 * 1024,
                MaxByteStringLength = 1024 * 1024,
                MaxArrayLength = 65535,
                MaxMessageSize = 4 * 1024 * 1024,
                MaxBufferSize = 64 * 1024,
                ChannelLifetime = 300000,
                SecurityTokenLifetime = 3600000
            },
            ClientConfiguration = new ClientConfiguration
            {
                DefaultSessionTimeout = 60000
            },
            DisableHiResClock = false
        };

        config.SecurityConfiguration.TrustedPeerCertificates ??= new CertificateTrustList { StoreType = CertificateStoreType.Directory };
        config.SecurityConfiguration.TrustedIssuerCertificates ??= new CertificateTrustList { StoreType = CertificateStoreType.Directory };
        config.SecurityConfiguration.RejectedCertificateStore ??= new CertificateTrustList { StoreType = CertificateStoreType.Directory };

        config.SecurityConfiguration.TrustedPeerCertificates.StorePath ??= trustedPeerStorePath;
        config.SecurityConfiguration.TrustedIssuerCertificates.StorePath ??= trustedIssuerStorePath;
        config.SecurityConfiguration.RejectedCertificateStore.StorePath ??= rejectedStorePath;

        config.SecurityConfiguration.TrustedPeerCertificates.StoreType = CertificateStoreType.Directory;
        config.SecurityConfiguration.TrustedIssuerCertificates.StoreType = CertificateStoreType.Directory;
        config.SecurityConfiguration.RejectedCertificateStore.StoreType = CertificateStoreType.Directory;

        await config.ValidateAsync(ApplicationType.Client, ct);

        var appInstance = new ApplicationInstance(config.CreateMessageContext().Telemetry)
        {
            ApplicationName = config.ApplicationName,
            ApplicationType = config.ApplicationType,
            ApplicationConfiguration = config
        };

        var hasAppCertificate = await appInstance.CheckApplicationInstanceCertificatesAsync(true, 2048, ct: ct);
        if (!hasAppCertificate)
            throw new InvalidOperationException("Unable to create or load OPC UA application certificate.");

        var clientCertificate = await config.SecurityConfiguration.ApplicationCertificate.FindAsync(true, "", null, ct);
        var clientKeySize = CertificateTrustStore.GetCertificateKeySize(clientCertificate);
        if (clientKeySize < 2048)
        {
            throw new InvalidOperationException(
                $"Client application certificate key size is {clientKeySize}. Basic256Sha256 typically requires at least 2048. Delete '{ownStorePath}' and restart the app to regenerate a stronger certificate.");
        }

        config.CertificateValidator.CertificateValidation += (_, e) =>
        {
            if (e.Certificate is not null &&
                !string.IsNullOrWhiteSpace(e.Certificate.Thumbprint) &&
                trustStore.IsExplicitlyTrusted(e.Certificate.Thumbprint))
            {
                e.Accept = true;
                return;
            }

            if (e.Certificate is not null && CertificateTrustStore.IsTrustedPeerCertificate(config.SecurityConfiguration, e.Certificate))
            {
                e.Accept = true;
                return;
            }

            if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted)
            {
                if (e.Certificate is not null)
                    trustStore.AddPending(e.Certificate);

                e.Accept = false;
                return;
            }

            e.Accept = false;
        };

        diagnostics.Add("OPC UA client configuration initialized.");
        return config;
    }
}
