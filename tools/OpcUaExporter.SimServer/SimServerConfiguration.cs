using Opc.Ua;

namespace OpcUaExporter.SimServer;

internal static class SimServerConfiguration
{
    public static async Task<ApplicationConfiguration> CreateAsync(int port)
    {
        // Kept apart from the exporter's own PKI under %LocalAppData%\OpcUaExporter\.
        var pkiRoot = Path.Combine(Path.GetTempPath(), "OpcUaExporter.SimServer", "pki");

        var config = new ApplicationConfiguration
        {
            ApplicationName = "OpcUaExporter Simulation Server",
            ApplicationType = ApplicationType.Server,
            ApplicationUri = $"urn:{Utils.GetHostName()}:OpcUaExporter:SimServer",
            ProductUri = "urn:OpcUaExporter:SimServer",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "own"),
                    SubjectName = "CN=OpcUaExporter SimServer"
                },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pkiRoot, "trusted") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pkiRoot, "issuer") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pkiRoot, "rejected") },
                // A test server: accept any client certificate so secure endpoints work without manual trust.
                AutoAcceptUntrustedCertificates = true,
                MinimumCertificateKeySize = 2048
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000, MaxMessageSize = 4 * 1024 * 1024 },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = [$"opc.tcp://localhost:{port}"],
                SecurityPolicies =
                [
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None },
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.SignAndEncrypt, SecurityPolicyUri = SecurityPolicies.Basic256Sha256 }
                ],
                UserTokenPolicies =
                [
                    new UserTokenPolicy(UserTokenType.Anonymous),
                    new UserTokenPolicy(UserTokenType.UserName)
                ],
                MinRequestThreadCount = 5,
                MaxRequestThreadCount = 100,
                MaxQueuedRequestCount = 200
            },
            TraceConfiguration = new TraceConfiguration()
        };

        await config.ValidateAsync(ApplicationType.Server);
        return config;
    }
}
