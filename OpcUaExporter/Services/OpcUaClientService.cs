using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using OpcUaExporter.Models;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace OpcUaExporter.Services;

/// <summary>
/// Native OPC UA client service implemented using OPCFoundation UA-.NETStandard.
/// </summary>
public class OpcUaClientService
{
    private const int BrowseProgressLogInterval = 1000;
    private const int BrowseVariableProgressReportInterval = 100;

    /// <summary>Ports commonly used by OPC UA servers, checked before the rest of the range.</summary>
    public static readonly IReadOnlyList<int> WellKnownOpcUaPorts =
    [
        4840, 4841, 4842, 4843, 4844, 4845, 4850, 4860, 4870,
        48010, 48020, 48030,
        51210, 51211,
        53000, 53530,
        62541, 62542
    ];

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

    public async Task<List<OpcTag>> BrowseAsync(
        ConnectionProfile profile,
        Action<List<OpcTag>>? onTopStructureReady = null,
        Action<int>? onVariableCountChanged = null,
        CancellationToken ct = default)
    {
        using var session = await CreateSessionAsync(profile, ct);

        var rootNodeId = ObjectIds.ObjectsFolder;
        var progress = new BrowseProgressState();
        progress.TryVisitNode(rootNodeId.ToString());

        _diagnostics.Add("Browse started.");
        var references = await session.FetchReferencesAsync(rootNodeId, ct: ct);
        var topLevelChildren = references
            .Where(r => r.IsForward)
            .Select(r => (Reference: r, NodeId: ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris)))
            .Where(x => x.NodeId is not null)
            .Select(x => (x.Reference, NodeId: x.NodeId!))
            .ToList();

        var variableDataTypes = await ReadVariableDataTypesAsync(session, topLevelChildren, ct);

        var tags = new List<OpcTag>(topLevelChildren.Count);
        foreach (var (reference, childNodeId) in topLevelChildren)
        {
            ct.ThrowIfCancellationRequested();

            var tag = new OpcTag
            {
                NodeId = childNodeId.ToString(),
                BrowseName = reference.BrowseName?.ToString() ?? string.Empty,
                DisplayName = reference.DisplayName?.Text ?? childNodeId.ToString(),
                NodeClass = reference.NodeClass.ToString()
            };

            if (reference.NodeClass == NodeClass.Variable &&
                variableDataTypes.TryGetValue(tag.NodeId, out var dataTypeId) &&
                dataTypeId is not null)
            {
                tag.DataType = await GetDataTypeNameCachedAsync(dataTypeId, session, progress);
            }

            if (reference.NodeClass == NodeClass.Variable)
                progress.IncrementVariableCount();

            tags.Add(tag);
        }

        onTopStructureReady?.Invoke(tags);
        onVariableCountChanged?.Invoke(progress.VariableCount);

        if (profile.EnableParallelBrowse)
        {
            var maxDegree = Math.Clamp(profile.ParallelBrowseMaxDegree, 1, 32);
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegree,
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(tags, options, async (tag, token) =>
            {
                await BrowseTopLevelTagAsync(session, tag, progress, onVariableCountChanged, token);
            });
        }
        else
        {
            foreach (var tag in tags)
            {
                ct.ThrowIfCancellationRequested();
                await BrowseTopLevelTagAsync(session, tag, progress, onVariableCountChanged, ct);
            }
        }

        _diagnostics.Add($"Browse completed. Scanned {progress.ScannedNodes} node(s). Found {CountVariables(tags)} variable tag(s).");
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

                var readResponse = await session.ReadAsync(null, 0, TimestampsToReturn.Both, readValueIdCollection, ct);
                var value = readResponse?.Results?[0];
                row.Value = value?.Value;
                row.Quality = value?.StatusCode.ToString();
                row.Timestamp = value?.SourceTimestamp.ToString("o");

                var node = await session.ReadNodeAsync(NodeId.Parse(id), ct);
                row.DisplayName = node?.DisplayName?.Text ?? id;
                row.DataType = await TryGetDataTypeName(node, session);
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

    public async Task<(IAsyncDisposable Handle, List<TagReading> InitialReadings)> SubscribeAsync(
        ConnectionProfile profile,
        IEnumerable<string> nodeIds,
        Action<TagReading> onUpdate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(onUpdate);

        var ids = nodeIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count == 0)
            throw new InvalidOperationException("No tags were selected for subscription.");

        var session = await CreateSessionAsync(profile, ct);

        try
        {
            var initialReadings = await ReadCurrentValuesAsync(session, ids, ct);

            var subscription = new Subscription(session.DefaultSubscription)
            {
                DisplayName = "OpcUaExporter Live Subscription",
                PublishingEnabled = true,
                PublishingInterval = 1000,
                KeepAliveCount = 10,
                LifetimeCount = 30,
                MaxNotificationsPerPublish = 0,
                Priority = 0
            };

            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();

                var monitoredItem = new MonitoredItem(subscription.DefaultItem)
                {
                    DisplayName = id,
                    StartNodeId = NodeId.Parse(id),
                    AttributeId = Attributes.Value,
                    SamplingInterval = 1000,
                    QueueSize = 100,
                    DiscardOldest = true
                };

                monitoredItem.Notification += (_, e) =>
                {
                    if (e.NotificationValue is not MonitoredItemNotification notification)
                        return;

                    var value = notification.Value;
                    var update = new TagReading
                    {
                        NodeId = monitoredItem.DisplayName,
                        DisplayName = monitoredItem.DisplayName,
                        Value = value.WrappedValue.Value,
                        Quality = value.StatusCode.ToString(),
                        Timestamp = value.SourceTimestamp.ToString("o")
                    };

                    onUpdate(update);
                };

                subscription.AddItem(monitoredItem);
            }

            session.AddSubscription(subscription);
            await subscription.CreateAsync(ct);

            _diagnostics.Add($"Subscription started. Monitoring {ids.Count} tag(s).");

            var handle = new SessionSubscriptionHandle(session, subscription, _diagnostics);
            return (handle, initialReadings);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public async Task<ServerCapabilitiesInfo> GetServerCapabilitiesAsync(string endpointUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl))
            throw new InvalidOperationException("Endpoint URL is required.");

        ct.ThrowIfCancellationRequested();

        var config = await _configuration.Value;
        var discoveryUrl = CoreClientUtils.GetDiscoveryUrl(endpointUrl);
        using var discoveryClient = await DiscoveryClient.CreateAsync(config, discoveryUrl, ct: ct);

        var endpointDescriptions = await discoveryClient.GetEndpointsAsync([], ct);
        if (endpointDescriptions is null || endpointDescriptions.Count == 0)
            throw new InvalidOperationException("No OPC UA endpoints were returned by the server.");

        var serverName = endpointDescriptions
            .Select(e => e.Server?.ApplicationName?.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
            ?? endpointUrl;

        var options = endpointDescriptions
            .GroupBy(e => new
            {
                Mode = ToConnectionSecurityMode(e.SecurityMode),
                Policy = NormalizeSecurityPolicy(e.SecurityPolicyUri)
            })
            .Select(g => new ServerSecurityOption
            {
                SecurityMode = g.Key.Mode,
                SecurityPolicy = g.Key.Policy,
                SupportsAnonymous = g.Any(e => e.UserIdentityTokens.Any(t => t.TokenType == UserTokenType.Anonymous)),
                SupportsUsernamePassword = g.Any(e => e.UserIdentityTokens.Any(t => t.TokenType == UserTokenType.UserName))
            })
            .OrderBy(o => o.SecurityMode)
            .ThenBy(o => o.SecurityPolicy, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _diagnostics.Add($"Discovered {options.Count} security option(s) on server '{serverName}'.");

        return new ServerCapabilitiesInfo
        {
            ServerName = serverName,
            SecurityOptions = options
        };
    }

    /// <summary>
    /// Scans the given ports on a host for OPC UA servers. Ports are attempted in the order
    /// supplied by the caller (well-known ports first, then the rest of the range), but
    /// <paramref name="onServerFound"/> fires whenever a probe completes since probes run concurrently.
    /// </summary>
    public async Task ScanForServersAsync(
        string host,
        IReadOnlyList<int> ports,
        int maxDegreeOfParallelism,
        int tcpProbeTimeoutMs,
        Action<int, int>? onProgress,
        Action<DiscoveredServerInfo>? onServerFound,
        CancellationToken ct = default)
    {
        var total = ports.Count;
        var scanned = 0;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(maxDegreeOfParallelism, 1, 500),
            CancellationToken = ct
        };

        _diagnostics.Add($"Port scan started for {host}: {total} port(s).");

        await Parallel.ForEachAsync(ports, options, async (port, token) =>
        {
            var info = await ProbePortAsync(host, port, tcpProbeTimeoutMs, token);
            if (info is { HandshakeConfirmed: true })
                onServerFound?.Invoke(info);

            var count = Interlocked.Increment(ref scanned);
            onProgress?.Invoke(count, total);
        });

        _diagnostics.Add($"Port scan of {host} completed. Scanned {total} port(s).");
    }

    private async Task<DiscoveredServerInfo?> ProbePortAsync(string host, int port, int tcpProbeTimeoutMs, CancellationToken ct)
    {
        using (var tcp = new TcpClient())
        {
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(tcpProbeTimeoutMs);
                await tcp.ConnectAsync(host, port, connectCts.Token);
            }
            catch
            {
                return null;
            }

            if (!tcp.Connected)
                return null;
        }

        var endpointUrl = $"opc.tcp://{host}:{port}";
        var info = new DiscoveredServerInfo { Port = port, EndpointUrl = endpointUrl };

        try
        {
            var config = await _configuration.Value;
            var discoveryUrl = CoreClientUtils.GetDiscoveryUrl(endpointUrl);
            using var discoveryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            discoveryCts.CancelAfter(3000);
            using var discoveryClient = await DiscoveryClient.CreateAsync(config, discoveryUrl, ct: discoveryCts.Token);
            var endpoints = await discoveryClient.GetEndpointsAsync([], discoveryCts.Token);

            info.HandshakeConfirmed = endpoints is { Count: > 0 };
            info.ApplicationName = endpoints?
                .Select(e => e.Server?.ApplicationName?.Text)
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        }
        catch (Exception ex)
        {
            info.Error = ex.Message;
        }

        return info;
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
        var trustedStore = config.SecurityConfiguration.TrustedPeerCertificates.OpenStore(null);
        await trustedStore.AddAsync(certificate, null, ct);

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
                "OpcUaExporter",
                60000,
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
            _diagnostics.Add($"Endpoint server certificate: Subject='{cert.Subject}', Issuer='{cert.Issuer}', Thumbprint={cert.Thumbprint}, KeySize={cert.PublicKey?.Key?.KeySize}");
        }
        catch (Exception ex)
        {
            _diagnostics.Add($"Endpoint server certificate parse warning: {ex.Message}");
        }
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

            _diagnostics.Add($"Client application certificate: Subject='{appCertificate.Subject}', Thumbprint={appCertificate.Thumbprint}, KeySize={GetCertificateKeySize(appCertificate)}, HasPrivateKey={appCertificate.HasPrivateKey}");
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

    private async Task<EndpointDescription> SelectEndpointAsync(string endpointUrl, ConnectionProfile profile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var config = await _configuration.Value;
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

    private static ConnectionSecurityMode ToConnectionSecurityMode(MessageSecurityMode mode)
    {
        return mode switch
        {
            MessageSecurityMode.Sign => ConnectionSecurityMode.Sign,
            MessageSecurityMode.SignAndEncrypt => ConnectionSecurityMode.SignAndEncrypt,
            _ => ConnectionSecurityMode.None
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
        var ownStorePath = Path.Combine(pkiRoot, "own");

        Directory.CreateDirectory(pkiRoot);
        Directory.CreateDirectory(trustedPeerStorePath);
        Directory.CreateDirectory(trustedIssuerStorePath);
        Directory.CreateDirectory(rejectedStorePath);
        Directory.CreateDirectory(ownStorePath);

        var config = new ApplicationConfiguration
        {
            ApplicationName = "OpcUaExporter",
            ApplicationType = ApplicationType.Client,
            ApplicationUri = $"urn:{Utils.GetHostName()}:OpcUaExporter",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = ownStorePath,
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

        await config.ValidateAsync(ApplicationType.Client);

        var appInstance = new ApplicationInstance(config.CreateMessageContext().Telemetry)
        {
            ApplicationName = config.ApplicationName,
            ApplicationType = config.ApplicationType,
            ApplicationConfiguration = config
        };

        var hasAppCertificate = await appInstance.CheckApplicationInstanceCertificatesAsync(true, 2048, ct: default);
        if (!hasAppCertificate)
            throw new InvalidOperationException("Unable to create or load OPC UA application certificate.");

        var clientCertificate = await config.SecurityConfiguration.ApplicationCertificate.FindAsync(true, "", null, default);
        var clientKeySize = GetCertificateKeySize(clientCertificate);
        if (clientKeySize < 2048)
        {
            throw new InvalidOperationException(
                $"Client application certificate key size is {clientKeySize}. Basic256Sha256 typically requires at least 2048. Delete '%LocalAppData%\\OpcUaExporter\\pki\\own' and restart the app to regenerate a stronger certificate.");
        }

        config.CertificateValidator.CertificateValidation += (_, e) =>
        {
            if (e.Certificate is not null &&
                !string.IsNullOrWhiteSpace(e.Certificate.Thumbprint) &&
                _trustedThumbprints.ContainsKey(e.Certificate.Thumbprint))
            {
                e.Accept = true;
                return;
            }

            if (e.Certificate is not null && IsTrustedPeerCertificate(config.SecurityConfiguration, e.Certificate))
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

    private static bool IsTrustedPeerCertificate(SecurityConfiguration securityConfiguration, X509Certificate2 certificate)
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

    private static int GetCertificateKeySize(X509Certificate2? certificate)
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

    private async Task<List<OpcTag>> BrowseNodeRecursiveAsync(
        Session session,
        NodeId nodeId,
        BrowseProgressState progress,
        Action<int>? onVariableCountChanged,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var references = await session.FetchReferencesAsync(nodeId, ct: ct);
        var forwardChildren = references
            .Where(r => r.IsForward)
            .Select(r => (Reference: r, NodeId: ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris)))
            .Where(x => x.NodeId is not null)
            .Select(x => (x.Reference, NodeId: x.NodeId!))
            .ToList();

        var variableDataTypes = await ReadVariableDataTypesAsync(session, forwardChildren, ct);
        var children = new List<OpcTag>();

        foreach (var (reference, childNodeId) in forwardChildren)
        {
            ct.ThrowIfCancellationRequested();

            var childNodeIdText = childNodeId.ToString();
            var isFirstVisit = progress.TryVisitNode(childNodeIdText);

            var scannedNodes = progress.IncrementScannedNodes();
            if (scannedNodes % BrowseProgressLogInterval == 0)
            {
                _diagnostics.Add($"Browse in progress: scanned {scannedNodes} node(s). Latest node: {childNodeId}");
            }

            var tag = new OpcTag
            {
                NodeId = childNodeIdText,
                BrowseName = reference.BrowseName?.ToString() ?? string.Empty,
                DisplayName = reference.DisplayName?.Text ?? childNodeId.ToString(),
                NodeClass = reference.NodeClass.ToString()
            };

            if (reference.NodeClass == NodeClass.Variable &&
                variableDataTypes.TryGetValue(tag.NodeId, out var dataTypeId) &&
                dataTypeId is not null)
            {
                tag.DataType = await GetDataTypeNameCachedAsync(dataTypeId, session, progress);
            }

            if (reference.NodeClass == NodeClass.Variable)
            {
                var variableCount = progress.IncrementVariableCount();
                if (variableCount % BrowseVariableProgressReportInterval == 0)
                    onVariableCountChanged?.Invoke(variableCount);
            }

            if (isFirstVisit && reference.NodeClass is NodeClass.Object or NodeClass.Variable)
            {
                try
                {
                    var nested = await BrowseNodeRecursiveAsync(session, childNodeId, progress, onVariableCountChanged, ct);
                    tag.Children = nested;
                }
                catch
                {
                    tag.Children = [];
                }
            }

            children.Add(tag);
        }

        return children;
    }

    private async Task BrowseTopLevelTagAsync(
        Session session,
        OpcTag tag,
        BrowseProgressState progress,
        Action<int>? onVariableCountChanged,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var childNodeId = NodeId.Parse(tag.NodeId);
        var isFirstVisit = progress.TryVisitNode(tag.NodeId);
        if (isFirstVisit && (tag.NodeClass == NodeClass.Object.ToString() || tag.NodeClass == NodeClass.Variable.ToString()))
        {
            try
            {
                var nested = await BrowseNodeRecursiveAsync(session, childNodeId, progress, onVariableCountChanged, ct);
                tag.Children = nested;
            }
            catch
            {
                tag.Children = [];
            }
        }

        onVariableCountChanged?.Invoke(progress.VariableCount);
    }

    private static async Task<Dictionary<string, NodeId>> ReadVariableDataTypesAsync(
        Session session,
        List<(ReferenceDescription Reference, NodeId NodeId)> children,
        CancellationToken ct)
    {
        var variableChildren = children
            .Where(c => c.Reference.NodeClass == NodeClass.Variable)
            .ToList();

        if (variableChildren.Count == 0)
            return new Dictionary<string, NodeId>(StringComparer.Ordinal);

        var requests = new ReadValueIdCollection(variableChildren.Count);
        foreach (var child in variableChildren)
        {
            requests.Add(new ReadValueId
            {
                NodeId = child.NodeId,
                AttributeId = Attributes.DataType
            });
        }

        var response = await session.ReadAsync(
            null,
            0,
            TimestampsToReturn.Neither,
            requests,
            ct);

        var map = new Dictionary<string, NodeId>(StringComparer.Ordinal);
        var results = response?.Results;
        if (results is null)
            return map;

        for (var i = 0; i < variableChildren.Count && i < results.Count; i++)
        {
            var result = results[i];
            if (result is null || StatusCode.IsBad(result.StatusCode))
                continue;

            var dataTypeId = result.Value as NodeId;
            if (dataTypeId is null)
                continue;

            map[variableChildren[i].NodeId.ToString()] = dataTypeId;
        }

        return map;
    }

    private static async Task<string?> GetDataTypeNameCachedAsync(NodeId dataTypeId, Session session, BrowseProgressState progress)
    {
        var key = dataTypeId.ToString();
        if (progress.DataTypeNameCache.TryGetValue(key, out var cached))
            return cached;

        var resolved = await GetDataTypeName(dataTypeId, session);
        progress.DataTypeNameCache.TryAdd(key, resolved);
        return resolved;
    }

    private static async Task<string?> TryGetDataTypeName(Node? node, Session session)
    {
        if (node is VariableNode variable)
            return await GetDataTypeName(variable.DataType, session);

        return null;
    }

    private static async Task<string?> GetDataTypeName(NodeId dataTypeId, Session session)
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
            var dataTypeNode = await session.ReadNodeAsync(dataTypeId);
            return dataTypeNode?.DisplayName?.Text ?? dataTypeId.ToString();
        }
        catch
        {
            return dataTypeId.ToString();
        }
    }

    private static int CountVariables(IEnumerable<OpcTag> tags)
        => Flatten(tags).Count(t => string.Equals(t.NodeClass, "Variable", StringComparison.OrdinalIgnoreCase));

    private static async Task<List<TagReading>> ReadCurrentValuesAsync(Session session, IEnumerable<string> nodeIds, CancellationToken ct)
    {
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
                NodeId = id,
                DisplayName = id
            };

            try
            {
                var nodeId = NodeId.Parse(id);
                var readValueIdCollection = new ReadValueIdCollection
                {
                    new ReadValueId
                    {
                        NodeId = nodeId,
                        AttributeId = Attributes.Value
                    }
                };

                var readResponse = await session.ReadAsync(null, 0, TimestampsToReturn.Both, readValueIdCollection, ct);
                var value = readResponse?.Results?[0];
                row.Value = value?.Value;
                row.Quality = value?.StatusCode.ToString();
                row.Timestamp = value?.SourceTimestamp.ToString("o");

                var node = await session.ReadNodeAsync(nodeId, ct);
                row.DisplayName = node?.DisplayName?.Text ?? id;
                row.DataType = await TryGetDataTypeName(node, session);
            }
            catch (Exception ex)
            {
                row.Error = ex.Message;
            }

            rows.Add(row);
        }

        return rows;
    }

    private static IEnumerable<OpcTag> Flatten(IEnumerable<OpcTag> tags)
    {
        foreach (var tag in tags)
        {
            yield return tag;
            foreach (var child in Flatten(tag.Children))
                yield return child;
        }
    }

    private sealed class BrowseProgressState
    {
        private int _scannedNodes;
        private int _variableCount;

        public int ScannedNodes => Volatile.Read(ref _scannedNodes);
        public int VariableCount => Volatile.Read(ref _variableCount);

        public ConcurrentDictionary<string, string?> DataTypeNameCache { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, byte> VisitedNodeIds { get; } = new(StringComparer.Ordinal);

        public int IncrementScannedNodes()
            => Interlocked.Increment(ref _scannedNodes);

        public int IncrementVariableCount()
            => Interlocked.Increment(ref _variableCount);

        public bool TryVisitNode(string nodeId)
            => VisitedNodeIds.TryAdd(nodeId, 0);
    }

    private sealed class SessionSubscriptionHandle : IAsyncDisposable
    {
        private readonly Session _session;
        private readonly Subscription _subscription;
        private readonly DiagnosticsLogService _diagnostics;
        private bool _disposed;

        public SessionSubscriptionHandle(Session session, Subscription subscription, DiagnosticsLogService diagnostics)
        {
            _session = session;
            _subscription = subscription;
            _diagnostics = diagnostics;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            try
            {
                if (_session.Connected)
                {
                    await _subscription.DeleteAsync(true);
                    await _session.RemoveSubscriptionAsync(_subscription);
                }
            }
            catch
            {
                // best effort cleanup
            }

            _session.Dispose();
            _disposed = true;
            _diagnostics.Add("Subscription stopped.");
        }
    }
}
