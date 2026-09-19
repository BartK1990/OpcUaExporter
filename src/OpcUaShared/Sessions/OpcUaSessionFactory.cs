using Opc.Ua;
using Opc.Ua.Client;
using OpcUaExporter.Certificates;
using OpcUaExporter.Models;
using OpcUaExporter.Services;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace OpcUaExporter.Sessions;

/// <summary>
/// Opens OPC UA sessions and selects the endpoint each one connects to.
/// </summary>
/// <param name="configurationAccessor">
/// Supplies the (lazily built, then cached) application configuration. Taking an
/// accessor rather than the configuration itself keeps certificate creation off the
/// constructor, which callers resolve from DI.
/// </param>
/// <param name="sessionName">Name this client reports in the server's session list.</param>
public sealed class OpcUaSessionFactory(
    Func<Task<ApplicationConfiguration>> configurationAccessor,
    string sessionName,
    DiagnosticsLogService diagnostics)
{
    private readonly Func<Task<ApplicationConfiguration>> _configurationAccessor = configurationAccessor;
    private readonly string _sessionName = sessionName;
    private readonly DiagnosticsLogService _diagnostics = diagnostics;

    /// <summary>Session timeout the exporter has always used, and the default for new callers.</summary>
    public const int DefaultSessionTimeoutMs = 60_000;

    /// <summary>
    /// Opens a session against the endpoint described by <paramref name="profile"/>.
    /// </summary>
    /// <remarks>
    /// The caller owns the returned session and must dispose it. Callers that need the
    /// session to survive network trouble should hand it to a keep-alive/reconnect
    /// supervisor rather than treating a dropped session as a failed operation.
    /// </remarks>
    public async Task<Session> CreateAsync(
        ConnectionProfile profile,
        int sessionTimeoutMs = DefaultSessionTimeoutMs,
        CancellationToken ct = default)
    {
        var config = await _configurationAccessor();

        var endpointUrl = profile.EndpointUrl;

        var selectedEndpoint = await SelectEndpointAsync(endpointUrl, profile, ct);

        var endpointConfiguration = EndpointConfiguration.Create(config);
        var endpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfiguration);
        var userIdentity = BuildUserIdentity(profile);

        _diagnostics.Add($"Connecting to OPC UA endpoint: {endpointUrl} | SecurityMode={selectedEndpoint.SecurityMode} | SecurityPolicy={selectedEndpoint.SecurityPolicyUri} | Auth={profile.AuthenticationType}");
        LogSelectedEndpointDiagnostics(selectedEndpoint);
        await LogClientCertificateDiagnosticsAsync(config);

        Session session;
        try
        {
            var sessionFactory = new DefaultSessionFactory(config.CreateMessageContext().Telemetry);
            var createdSession = await sessionFactory.CreateAsync(
                config,
                endpoint,
                true,
                _sessionName,
                (uint)sessionTimeoutMs,
                userIdentity,
                null,
                ct);

            session = createdSession as Session
                ?? throw new InvalidOperationException("Session factory returned an unsupported session implementation.");
        }
        catch (Exception ex)
        {
            _diagnostics.Add($"OpenSecureChannel failed: {ex.Message}");
            _diagnostics.Add($"Selected endpoint details: Url={selectedEndpoint.EndpointUrl}, Mode={selectedEndpoint.SecurityMode}, Policy={selectedEndpoint.SecurityPolicyUri}, Auth={profile.AuthenticationType}");
            throw;
        }

        _diagnostics.Add("OPC UA session connected.");
        return session;
    }

    private void LogSelectedEndpointDiagnostics(EndpointDescription endpoint)
    {
        var tokenPolicies = endpoint.UserIdentityTokens?
            .Select(t => $"{t.TokenType} ({NormalizeSecurityPolicy(t.SecurityPolicyUri)})")
            .ToList() ?? new List<string>();

        _diagnostics.Add($"Endpoint token policies: {(tokenPolicies.Count == 0 ? "none" : string.Join(", ", tokenPolicies))}");

        if (endpoint.ServerCertificate is null || endpoint.ServerCertificate.Length == 0)
        {
            _diagnostics.Add("Endpoint server certificate: not provided by endpoint discovery.");
            return;
        }

        try
        {
            var cert = new X509Certificate2(endpoint.ServerCertificate);
            _diagnostics.Add($"Endpoint server certificate: Subject='{cert.Subject}', Issuer='{cert.Issuer}', Thumbprint={cert.Thumbprint}, KeySize={DescribePublicKeySize(cert)}");
        }
        catch (Exception ex)
        {
            _diagnostics.Add($"Endpoint server certificate parse warning: {ex.Message}");
        }
    }

    /// <summary>
    /// Public key size of <paramref name="certificate"/>, for diagnostics.
    /// Uses the per-algorithm accessors rather than the obsolete
    /// <c>PublicKey.Key</c>, which only ever understood RSA and DSA.
    /// </summary>
    private static string DescribePublicKeySize(X509Certificate2 certificate)
    {
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is not null)
            return rsa.KeySize.ToString();

        using var ecdsa = certificate.GetECDsaPublicKey();
        return ecdsa is not null ? ecdsa.KeySize.ToString() : "unknown";
    }

    private async Task LogClientCertificateDiagnosticsAsync(ApplicationConfiguration config)
    {
        try
        {
            var appCertificate = await config.SecurityConfiguration.ApplicationCertificate.FindAsync(true, "", null, default);
            if (appCertificate is null)
            {
                _diagnostics.Add("Client application certificate: not found.");
                return;
            }

            _diagnostics.Add($"Client application certificate: Subject='{appCertificate.Subject}', Thumbprint={appCertificate.Thumbprint}, KeySize={CertificateTrustStore.GetCertificateKeySize(appCertificate)}, HasPrivateKey={appCertificate.HasPrivateKey}");
        }
        catch (Exception ex)
        {
            _diagnostics.Add($"Client application certificate diagnostics failed: {ex.Message}");
        }
    }

    private static UserIdentity BuildUserIdentity(ConnectionProfile profile)
    {
        if (profile.AuthenticationType == AuthenticationType.UsernamePassword)
        {
            var userName = profile.Username ?? string.Empty;
            var password = profile.Password ?? string.Empty;
            return new UserIdentity(userName, Encoding.UTF8.GetBytes(password));
        }

        return new UserIdentity(new AnonymousIdentityToken());
    }

    /// <summary>
    /// Picks the endpoint matching the profile's security mode, policy and authentication
    /// type, failing with a message naming the mismatch rather than silently downgrading.
    /// </summary>
    public async Task<EndpointDescription> SelectEndpointAsync(string endpointUrl, ConnectionProfile profile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var config = await _configurationAccessor();
        var discoveryUrl = CoreClientUtils.GetDiscoveryUrl(endpointUrl);
        using var discoveryClient = await DiscoveryClient.CreateAsync(config, discoveryUrl, ct: ct);
        var endpointDescriptions = await discoveryClient.GetEndpointsAsync([], ct);
        if (endpointDescriptions is null || endpointDescriptions.Count == 0)
            throw new InvalidOperationException("No OPC UA endpoints were returned by the server.");

        var preferredMode = profile.SecurityMode switch
        {
            ConnectionSecurityMode.Sign => MessageSecurityMode.Sign,
            ConnectionSecurityMode.SignAndEncrypt => MessageSecurityMode.SignAndEncrypt,
            _ => MessageSecurityMode.None
        };

        var normalizedPolicy = NormalizeSecurityPolicy(profile.SecurityPolicy);

        var candidates = endpointDescriptions
            .Where(e => e.SecurityMode == preferredMode)
            .Where(e => string.Equals(NormalizeSecurityPolicy(e.SecurityPolicyUri), normalizedPolicy, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No endpoint matches SecurityMode='{profile.SecurityMode}' and SecurityPolicy='{profile.SecurityPolicy}'.");
        }

        var requiredTokenType = profile.AuthenticationType == AuthenticationType.UsernamePassword
            ? UserTokenType.UserName
            : UserTokenType.Anonymous;

        var selected = candidates.FirstOrDefault(e => e.UserIdentityTokens.Any(t => t.TokenType == requiredTokenType));
        if (selected is null)
        {
            throw new InvalidOperationException(
                $"Endpoint does not support the selected authentication type '{profile.AuthenticationType}'.");
        }

        if (selected.SecurityMode != MessageSecurityMode.None &&
            (selected.ServerCertificate is null || selected.ServerCertificate.Length == 0))
        {
            throw new InvalidOperationException(
                $"Selected secure endpoint '{selected.EndpointUrl}' did not provide a server certificate in discovery. Try using the exact endpoint URL returned by Discover Modes or reconfigure the server endpoint.");
        }

        return selected;
    }

    /// <summary>Expands a shorthand policy name (e.g. "Basic256Sha256") to its full policy URI.</summary>
    public static string NormalizeSecurityPolicy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return SecurityPolicies.None;

        return value.Trim() switch
        {
            "None" => SecurityPolicies.None,
            "Basic128Rsa15" => SecurityPolicies.Basic128Rsa15,
            "Basic256" => SecurityPolicies.Basic256,
            "Basic256Sha256" => SecurityPolicies.Basic256Sha256,
            "Aes128_Sha256_RsaOaep" => SecurityPolicies.Aes128_Sha256_RsaOaep,
            "Aes256_Sha256_RsaPss" => SecurityPolicies.Aes256_Sha256_RsaPss,
            var v => v
        };
    }

    /// <summary>Maps the SDK security mode onto this application's own enum.</summary>
    public static ConnectionSecurityMode ToConnectionSecurityMode(MessageSecurityMode mode)
    {
        return mode switch
        {
            MessageSecurityMode.Sign => ConnectionSecurityMode.Sign,
            MessageSecurityMode.SignAndEncrypt => ConnectionSecurityMode.SignAndEncrypt,
            _ => ConnectionSecurityMode.None
        };
    }
}
