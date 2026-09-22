using Opc.Ua;
using Opc.Ua.Client;
using OpcUaExporter.Models;
using OpcUaExporter.Services;
using OpcUaExporter.Sessions;
using System.Net.Sockets;

namespace OpcUaExporter.Discovery;

/// <summary>Finds OPC UA servers on a host and reports what each endpoint supports.</summary>
public sealed class OpcUaServerScanner(
    Func<Task<ApplicationConfiguration>> configurationAccessor,
    DiagnosticsLogService diagnostics)
{
    private readonly Func<Task<ApplicationConfiguration>> _configurationAccessor = configurationAccessor;
    private readonly DiagnosticsLogService _diagnostics = diagnostics;

    /// <summary>Ports commonly used by OPC UA servers, checked before the rest of the range.</summary>
    public static readonly IReadOnlyList<int> WellKnownOpcUaPorts =
    [
        4840, 4841, 4842, 4843, 4844, 4845, 4850, 4860, 4870,
        48010, 48020, 48030,
        51210, 51211,
        53000, 53530,
        62541, 62542
    ];

    /// <summary>
    /// Parses a comma-delimited list of ports and/or dash-delimited ranges (e.g. "4840,4842,502-520")
    /// into a sorted, de-duplicated port list. Throws <see cref="FormatException"/> on invalid input.
    /// </summary>
    public static List<int> ParsePortSpec(string spec)
    {
        var ports = new SortedSet<int>();

        foreach (var token in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dashIndex = token.IndexOf('-');
            if (dashIndex > 0)
            {
                var startText = token[..dashIndex].Trim();
                var endText = token[(dashIndex + 1)..].Trim();
                if (!int.TryParse(startText, out var start) || !int.TryParse(endText, out var end))
                    throw new FormatException($"Invalid port range '{token}'.");
                if (start < 1 || end > 65535 || start > end)
                    throw new FormatException($"Invalid port range '{token}'. Ports must be between 1 and 65535 with start <= end.");

                for (var port = start; port <= end; port++)
                    ports.Add(port);
            }
            else
            {
                if (!int.TryParse(token, out var port))
                    throw new FormatException($"Invalid port '{token}'.");
                if (port < 1 || port > 65535)
                    throw new FormatException($"Invalid port '{token}'. Ports must be between 1 and 65535.");

                ports.Add(port);
            }
        }

        if (ports.Count == 0)
            throw new FormatException("Enter at least one port or port range, e.g. 4840,4842,502-520.");

        return ports.ToList();
    }

    /// <summary>Probes every port on a host and reports the ones answering an OPC UA handshake.</summary>
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
            var config = await _configurationAccessor();
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

    /// <summary>Lists the security modes, policies and token types an endpoint offers.</summary>
    public async Task<ServerCapabilitiesInfo> GetServerCapabilitiesAsync(string endpointUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl))
            throw new InvalidOperationException("Endpoint URL is required.");

        ct.ThrowIfCancellationRequested();

        var config = await _configurationAccessor();
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
                Mode = OpcUaSessionFactory.ToConnectionSecurityMode(e.SecurityMode),
                Policy = OpcUaSessionFactory.NormalizeSecurityPolicy(e.SecurityPolicyUri)
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
}
