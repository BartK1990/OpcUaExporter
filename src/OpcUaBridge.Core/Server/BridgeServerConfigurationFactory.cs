using Opc.Ua;
using OpcUaBridge.Configuration;

namespace OpcUaBridge.Server;

/// <summary>
/// Builds the <see cref="ApplicationConfiguration"/> for the bridge's own OPC UA server.
/// </summary>
/// <remarks>
/// A second, independent identity from the one the bridge uses as a client. The client
/// certificate is the one the plant server trusts; the server certificate is the one
/// downstream clients trust. Sharing a key pair between the two roles would mean a
/// downstream client's trust decision also granted access to the plant network's identity.
/// </remarks>
public static class BridgeServerConfigurationFactory
{
    public static async Task<ApplicationConfiguration> CreateAsync(
        MirrorServerOptions options,
        string pkiRoot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(pkiRoot);

        var ownStore = Path.Combine(pkiRoot, "own");
        var trustedStore = Path.Combine(pkiRoot, "trusted");
        var issuerStore = Path.Combine(pkiRoot, "issuer");
        var rejectedStore = Path.Combine(pkiRoot, "rejected");

        foreach (var store in new[] { ownStore, trustedStore, issuerStore, rejectedStore })
            Directory.CreateDirectory(store);

        var host = !string.IsNullOrWhiteSpace(options.Host) ? options.Host : Utils.GetHostName();

        var configuration = new ApplicationConfiguration
        {
            ApplicationName = options.ApplicationName,
            ApplicationType = ApplicationType.Server,
            ApplicationUri = $"urn:{host}:{options.ApplicationName}",
            ProductUri = "urn:opcuabridge",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = ownStore,
                    SubjectName = $"CN={options.ApplicationName}, DC={host}"
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = trustedStore
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = issuerStore
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = rejectedStore
                },
                // Downstream clients are surfaced for a trust decision, exactly as the
                // exporter surfaces untrusted servers, rather than being waved through.
                AutoAcceptUntrustedCertificates = false,
                AddAppCertToTrustedStore = false,
                RejectSHA1SignedCertificates = false,
                MinimumCertificateKeySize = 2048
            },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = 30_000,
                MaxStringLength = 4 * 1024 * 1024,
                MaxByteStringLength = 4 * 1024 * 1024,
                // Must comfortably exceed the tag count: a downstream client reading every
                // mirrored tag in one call is a normal thing to do.
                MaxArrayLength = 1 << 20,
                MaxMessageSize = 16 * 1024 * 1024,
                MaxBufferSize = 64 * 1024,
                ChannelLifetime = 600_000,
                SecurityTokenLifetime = 3_600_000
            },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = [options.BuildEndpointUrl(host)],
                SecurityPolicies = BuildSecurityPolicies(options),
                UserTokenPolicies = BuildUserTokenPolicies(options),
                MinRequestThreadCount = 5,
                MaxRequestThreadCount = 100,
                MaxQueuedRequestCount = 2_000,
                MaxSessionCount = 20,
                MinSessionTimeout = 10_000,
                MaxSessionTimeout = 3_600_000,
                MinPublishingInterval = 50,
                MaxPublishingInterval = 3_600_000,
                PublishingResolution = 50,
                MaxSubscriptionCount = 100,
                MaxSubscriptionLifetime = 3_600_000,
                // A downstream client subscribing to every mirrored tag produces a very
                // large first publish. These defaults are sized for hundreds of items,
                // not thousands, and would silently drop notifications.
                MaxNotificationsPerPublish = 0,
                MaxNotificationQueueSize = 10_000,
                MaxMessageQueueSize = 100,
                MaxPublishRequestCount = 100,
                MaxBrowseContinuationPoints = 10,
                MaxRequestAge = 600_000,
                DiagnosticsEnabled = false
            }
        };

        await configuration.ValidateAsync(ApplicationType.Server, ct);
        return configuration;
    }

    private static ServerSecurityPolicyCollection BuildSecurityPolicies(MirrorServerOptions options)
    {
        var policies = new ServerSecurityPolicyCollection
        {
            new()
            {
                SecurityMode = MessageSecurityMode.SignAndEncrypt,
                SecurityPolicyUri = SecurityPolicies.Basic256Sha256
            },
            new()
            {
                SecurityMode = MessageSecurityMode.Sign,
                SecurityPolicyUri = SecurityPolicies.Basic256Sha256
            }
        };

        if (options.AllowNoSecurity)
        {
            policies.Add(new ServerSecurityPolicy
            {
                SecurityMode = MessageSecurityMode.None,
                SecurityPolicyUri = SecurityPolicies.None
            });
        }

        return policies;
    }

    private static UserTokenPolicyCollection BuildUserTokenPolicies(MirrorServerOptions options)
    {
        var policies = new UserTokenPolicyCollection();

        if (options.AllowAnonymous)
            policies.Add(new UserTokenPolicy(UserTokenType.Anonymous));

        return policies;
    }
}
