using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using OpcUaExporter.Models;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace OpcUaExporter.Services;

/// <summary>
/// Native OPC UA client service implemented using OPCFoundation UA-.NETStandard.
/// </summary>
public class OpcUaClientService
{
    private readonly ILogger<OpcUaClientService> _logger;
    private readonly DiagnosticsLogService _diagnostics;
    private readonly Lazy<Task<ApplicationConfiguration>> _configuration;
    private readonly ConcurrentDictionary<string, X509Certificate2> _pendingCertificates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _trustedThumbprints = new(StringComparer.OrdinalIgnoreCase);

    public OpcUaClientService(ILogger<OpcUaClientService> logger, DiagnosticsLogService diagnostics)
    {
        _logger = logger;
        _diagnostics = diagnostics;
        _configuration = new Lazy<Task<ApplicationConfiguration>>(BuildConfigurationAsync);
    }

    public async Task<List<OpcTag>> BrowseAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        using var session = await CreateSessionAsync(profile, ct);

        var rootNodeId = ObjectIds.ObjectsFolder;
        var tags = await BrowseNodeRecursiveAsync(session, rootNodeId, ct);

        _diagnostics.Add($"Browse completed. Found {CountVariables(tags)} variable tag(s).");
        return tags;
    }

    public async Task<List<TagReading>> ReadAsync(ConnectionProfile profile, IEnumerable<string> nodeIds, CancellationToken ct = default)
    {
        using var session = await CreateSessionAsync(profile, ct);

        var ids = nodeIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<TagReading>(ids.Count);

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();

            var row = new TagReading
            {
                NodeId = id
            };

            try
            {
                var readValueIdCollection = new ReadValueIdCollection
                {
                    new ReadValueId
                    {
                        NodeId = NodeId.Parse(id),
                        AttributeId = Attributes.Value
                    }
                };

                session.Read(
                    null,
                    0,
                    TimestampsToReturn.Both,
                    readValueIdCollection,
                    out var dataValues,
                    out _);

                var value = dataValues?[0];
                row.Value = value?.Value;
                row.Quality = value?.StatusCode.ToString();
                row.Timestamp = value?.SourceTimestamp.ToString("o");

                var node = session.ReadNode(NodeId.Parse(id));
                row.DisplayName = node?.DisplayName?.Text ?? id;
                row.DataType = TryGetDataTypeName(node, session);
            }
            catch (Exception ex)
            {
                row.DisplayName = id;
                row.Error = ex.Message;
            }

            rows.Add(row);
        }

        _diagnostics.Add($"Read completed. Returned {rows.Count} row(s).");
        return rows;
    }

    public async Task TestConnectionAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        using var session = await CreateSessionAsync(profile, ct);
        _diagnostics.Add("Connection test completed successfully.");
    }

    public List<PendingCertificateInfo> GetPendingCertificates()
    {
        return _pendingCertificates.Values
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

    public async Task<bool> TrustPendingCertificateAsync(string thumbprint, CancellationToken ct = default)
    {
        if (!_pendingCertificates.TryRemove(thumbprint, out var certificate))
            return false;

        ct.ThrowIfCancellationRequested();

        var config = await _configuration.Value;
        var trustedStorePath = config.SecurityConfiguration.TrustedPeerCertificates.StorePath;
        if (string.IsNullOrWhiteSpace(trustedStorePath))
            throw new InvalidOperationException("Trusted peer certificate store path is not configured.");

        Directory.CreateDirectory(trustedStorePath);

        var certBytes = certificate.Export(X509ContentType.Cert);
        var filePath = Path.Combine(trustedStorePath, $"{certificate.Thumbprint}.der");
        await File.WriteAllBytesAsync(filePath, certBytes, ct);

        if (!string.IsNullOrWhiteSpace(certificate.Thumbprint))
            _trustedThumbprints[certificate.Thumbprint] = 0;

        _diagnostics.Add($"Trusted certificate: {certificate.Subject} ({certificate.Thumbprint})");
        return true;
    }

    public bool RejectPendingCertificate(string thumbprint)
    {
        if (!_pendingCertificates.TryRemove(thumbprint, out var certificate))
            return false;

        _diagnostics.Add($"Rejected certificate: {certificate.Subject} ({certificate.Thumbprint})");
        return true;
    }

    private async Task<Session> CreateSessionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var config = await _configuration.Value;

        var endpointUrl = profile.EndpointUrl;

        var selectedEndpoint = await SelectEndpointAsync(endpointUrl, profile, ct);

        var endpointConfiguration = EndpointConfiguration.Create(config);
        var endpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfiguration);
        var userIdentity = BuildUserIdentity(profile);

        _diagnostics.Add($"Connecting to OPC UA endpoint: {endpointUrl} | SecurityMode={selectedEndpoint.SecurityMode} | SecurityPolicy={selectedEndpoint.SecurityPolicyUri} | Auth={profile.AuthenticationType}");

        var session = await Session.Create(
            config,
            endpoint,
            false,
            "OpcUaExporter",
            60000,
            userIdentity,
            null,
            ct);

        _diagnostics.Add("OPC UA session connected.");
        return session;
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

    private async Task<EndpointDescription> SelectEndpointAsync(string endpointUrl, ConnectionProfile profile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var discoveryUrl = CoreClientUtils.GetDiscoveryUrl(endpointUrl);
        using var discoveryClient = DiscoveryClient.Create(discoveryUrl);
        var endpointDescriptions = await discoveryClient.GetEndpointsAsync(new StringCollection(), ct);
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

        return selected;
    }

    private static string NormalizeSecurityPolicy(string? value)
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

    private async Task<ApplicationConfiguration> BuildConfigurationAsync()
    {
        var pkiRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpcUaExporter",
            "pki");

        var trustedPeerStorePath = Path.Combine(pkiRoot, "trusted");
        var trustedIssuerStorePath = Path.Combine(pkiRoot, "issuer");
        var rejectedStorePath = Path.Combine(pkiRoot, "rejected");

        Directory.CreateDirectory(pkiRoot);
        Directory.CreateDirectory(trustedPeerStorePath);
        Directory.CreateDirectory(trustedIssuerStorePath);
        Directory.CreateDirectory(rejectedStorePath);

        var config = new ApplicationConfiguration
        {
            ApplicationName = "OpcUaExporter",
            ApplicationType = ApplicationType.Client,
            ApplicationUri = $"urn:{Utils.GetHostName()}:OpcUaExporter",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.X509Store,
                    StorePath = "CurrentUser\\My",
                    SubjectName = "CN=OpcUaExporter"
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
                MinimumCertificateKeySize = 1024
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

        await config.Validate(ApplicationType.Client);

        var appInstance = new ApplicationInstance
        {
            ApplicationName = config.ApplicationName,
            ApplicationType = config.ApplicationType,
            ApplicationConfiguration = config
        };

        var hasAppCertificate = await appInstance.CheckApplicationInstanceCertificates(false, 2048);
        if (!hasAppCertificate)
            throw new InvalidOperationException("Unable to create or load OPC UA application certificate.");

        config.CertificateValidator.CertificateValidation += (_, e) =>
        {
            if (e.Certificate is not null &&
                !string.IsNullOrWhiteSpace(e.Certificate.Thumbprint) &&
                _trustedThumbprints.ContainsKey(e.Certificate.Thumbprint))
            {
                e.Accept = true;
                return;
            }

            if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted)
            {
                if (e.Certificate is not null)
                {
                    var key = e.Certificate.Thumbprint ?? e.Certificate.GetCertHashString();
                    _pendingCertificates[key] = e.Certificate;
                    _diagnostics.Add($"Untrusted certificate pending approval: {e.Certificate.Subject} ({key})");
                }

                e.Accept = false;
                return;
            }

            e.Accept = false;
        };

        _diagnostics.Add("OPC UA client configuration initialized.");
        return config;
    }

    private async Task<List<OpcTag>> BrowseNodeRecursiveAsync(Session session, NodeId nodeId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var references = session.FetchReferences(nodeId);
        var children = new List<OpcTag>();

        foreach (var reference in references)
        {
            ct.ThrowIfCancellationRequested();

            if (!reference.IsForward)
                continue;

            var childNodeId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
            if (childNodeId is null)
                continue;

            Node? node;
            try
            {
                node = session.ReadNode(childNodeId);
            }
            catch
            {
                continue;
            }

            if (node is null)
                continue;

            var tag = new OpcTag
            {
                NodeId = childNodeId.ToString(),
                BrowseName = node.BrowseName?.ToString() ?? string.Empty,
                DisplayName = node.DisplayName?.Text ?? childNodeId.ToString(),
                NodeClass = node.NodeClass.ToString()
            };

            if (node is VariableNode variable)
            {
                tag.DataType = GetDataTypeName(variable.DataType, session);
            }

            if (node.NodeClass is NodeClass.Object or NodeClass.Variable)
            {
                var nested = await BrowseNodeRecursiveAsync(session, childNodeId, ct);
                tag.Children = nested;
            }

            children.Add(tag);
        }

        return children;
    }

    private static string? TryGetDataTypeName(Node? node, Session session)
    {
        if (node is VariableNode variable)
            return GetDataTypeName(variable.DataType, session);

        return null;
    }

    private static string? GetDataTypeName(NodeId dataTypeId, Session session)
    {
        if (dataTypeId.IsNullNodeId)
            return null;

        var numericTypeId = dataTypeId.IdType == IdType.Numeric && dataTypeId.Identifier is not null
            ? Convert.ToUInt32(dataTypeId.Identifier)
            : 0u;

        var builtInType = TypeInfo.GetBuiltInType(numericTypeId);
        if (builtInType != BuiltInType.Null)
            return builtInType.ToString();

        try
        {
            var dataTypeNode = session.ReadNode(dataTypeId);
            return dataTypeNode?.DisplayName?.Text ?? dataTypeId.ToString();
        }
        catch
        {
            return dataTypeId.ToString();
        }
    }

    private static int CountVariables(IEnumerable<OpcTag> tags)
        => Flatten(tags).Count(t => string.Equals(t.NodeClass, "Variable", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<OpcTag> Flatten(IEnumerable<OpcTag> tags)
    {
        foreach (var tag in tags)
        {
            yield return tag;
            foreach (var child in Flatten(tag.Children))
                yield return child;
        }
    }
}
