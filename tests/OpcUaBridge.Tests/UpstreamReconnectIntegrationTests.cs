using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Configuration;
using OpcUaBridge.Configuration;
using OpcUaBridge.Namespaces;
using OpcUaBridge.Server;
using OpcUaBridge.Tags;
using OpcUaBridge.Upstream;
using OpcUaExporter.Abstractions;
using OpcUaExporter.Certificates;
using OpcUaExporter.Configuration;
using OpcUaExporter.Services;
using OpcUaExporter.Sessions;
using Xunit;

namespace OpcUaBridge.Tests;

/// <summary>
/// Drives <see cref="UpstreamConnectionManager"/> through the SDK's real reconnect handler,
/// against a real server it can take away and give back.
/// </summary>
/// <remarks>
/// Both cases here once left a production bridge in <c>Reconnecting</c> for hours, with no
/// values flowing and nothing in the log, until the service was restarted by hand. The
/// fault was in how the manager read the handler's outcome, so only the real handler can
/// show it.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class UpstreamReconnectIntegrationTests : IAsyncLifetime
{
    private const int UpstreamPort = 48431;

    private readonly string _pkiRoot = Directory.CreateTempSubdirectory("opcua-bridge-reconnect").FullName;
    private readonly List<UpstreamConnectionState> _states = [];
    private readonly RecordingLogger _log = new();

    private ApplicationInstance? _upstream;
    private UpstreamConnectionManager _manager = null!;

    public async Task InitializeAsync()
    {
        await StartUpstreamAsync();

        var options = new BridgeOptions
        {
            Upstream = new UpstreamOptions
            {
                EndpointUrl = $"opc.tcp://localhost:{UpstreamPort}/Plant",
                ClientApplicationName = "ReconnectTestClient",
                KeepAliveIntervalMs = 1_000,
                SessionTimeoutMs = 10_000,
                ReconnectMinDelayMs = 500,
                ReconnectMaxDelayMs = 2_000,
                ReconnectResumeTimeoutMs = 10_000
            }
        };

        var diagnostics = new DiagnosticsLogService();
        var identity = OpcUaApplicationIdentity.ForApplication("ReconnectTestClient");
        var pki = new OpcUaPkiLocation(Path.Combine(_pkiRoot, "client"));
        var configuration = new Lazy<Task<ApplicationConfiguration>>(() =>
            OpcUaApplicationConfigurationFactory.CreateClientAsync(
                identity, pki, new CertificateTrustStore(diagnostics), diagnostics));
        var sessionFactory = new OpcUaSessionFactory(() => configuration.Value, identity.ApplicationName, diagnostics);

        _manager = new UpstreamConnectionManager(
            new StaticOptionsMonitor(options), sessionFactory, _log);
        _manager.StateChanged += (_, status) => { lock (_states) _states.Add(status.State); };

        await _manager.StartAsync(CancellationToken.None);
        await WaitForAsync(() => _manager.State == UpstreamConnectionState.Connected, TimeSpan.FromSeconds(30));
    }

    public async Task DisposeAsync()
    {
        await _manager.StopAsync(CancellationToken.None);
        _manager.Dispose();
        await StopUpstreamAsync();
        Directory.Delete(_pkiRoot, recursive: true);
    }

    [Fact]
    public async Task AKeepAliveFailureThatRecoversByItself_ReturnsToConnectedOnTheSameSession()
    {
        var session = _manager.Session!;
        var epoch = _manager.Status.SessionEpoch;

        // What a server that answers one keep-alive late looks like: the bridge starts a
        // reconnect, and by the time the handler runs the session is healthy again. The
        // handler then aborts and reports back with no session -- which the manager used
        // to read as "still trying", and so waited for a callback that never came.
        _manager.BeginReconnect(session);
        Assert.Equal(UpstreamConnectionState.Reconnecting, _manager.State);

        await WaitForAsync(() => _manager.State == UpstreamConnectionState.Connected, TimeSpan.FromSeconds(15));

        Assert.Same(session, _manager.Session);
        Assert.Equal(epoch, _manager.Status.SessionEpoch);
        Assert.Equal(1, _manager.Status.ReconnectCount);
        Assert.Null(_manager.Status.LastError);
    }

    [Fact]
    public async Task AReconnectThatDoesNotResumeInTime_IsAbandonedForAFreshSession()
    {
        var epoch = _manager.Status.SessionEpoch;

        await StopUpstreamAsync();
        await WaitForAsync(() => _manager.State == UpstreamConnectionState.Reconnecting, TimeSpan.FromSeconds(15));
        lock (_states) _states.Clear();

        // With the server gone the SDK handler can only keep retrying. Past the resume
        // deadline the manager must stop waiting on it and fall back to opening sessions
        // itself -- the same path a service restart takes.
        await WaitForAsync(
            () => { lock (_states) return _states.Contains(UpstreamConnectionState.Connecting); },
            TimeSpan.FromSeconds(30));
        Assert.Contains(_log.Messages, m => m.Contains("did not resume", StringComparison.Ordinal));

        await StartUpstreamAsync();
        await WaitForAsync(() => _manager.State == UpstreamConnectionState.Connected, TimeSpan.FromSeconds(45));

        Assert.True(_manager.Status.SessionEpoch > epoch);
    }

    private async Task StartUpstreamAsync()
    {
        var options = new MirrorServerOptions
        {
            Host = "localhost",
            Port = UpstreamPort,
            EndpointPath = "/Plant",
            ApplicationName = "ReconnectPlantStub",
            AllowNoSecurity = true,
            AllowAnonymous = true
        };

        var snapshot = new NamespaceSnapshot
        {
            NamespaceUris = [Opc.Ua.Namespaces.OpcUa, "urn:plant:Server", "http://plant/line1"],
            Nodes =
            [
                new SnapshotNode
                {
                    NamespaceIndex = 2, Identifier = "s=Speed", BrowseName = "Speed",
                    IsVariable = true, DataType = "Double", AccessLevel = 1
                }
            ]
        };

        var registry = TagRegistry.FromSnapshot(snapshot);
        var server = new BridgeServer(
            registry, new TagValueStore(registry.Count), options, new NullWriteRouter(), NullLoggerFactory.Instance);

        var configuration = await BridgeServerConfigurationFactory.CreateAsync(options, Path.Combine(_pkiRoot, "plant"));
        var application = new ApplicationInstance(configuration.CreateMessageContext().Telemetry)
        {
            ApplicationName = configuration.ApplicationName,
            ApplicationType = ApplicationType.Server,
            ApplicationConfiguration = configuration
        };

        await application.CheckApplicationInstanceCertificatesAsync(silent: true, 2048);
        await application.StartAsync(server);
        _upstream = application;
    }

    private async Task StopUpstreamAsync()
    {
        var application = Interlocked.Exchange(ref _upstream, null);
        if (application is not null)
            await application.StopAsync();
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Condition not met within {timeout.TotalSeconds:F0}s.");
            await Task.Delay(100);
        }
    }

    private sealed class RecordingLogger : ILogger<UpstreamConnectionManager>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages { get { lock (_messages) return [.. _messages]; } }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_messages) _messages.Add(formatter(state, exception));
        }
    }

    private sealed class NullWriteRouter : IUpstreamWriteRouter
    {
        public StatusCode Write(MirrorTag tag, object? value) => StatusCodes.Good;
    }

    private sealed class StaticOptionsMonitor(BridgeOptions options) : IOptionsMonitor<BridgeOptions>
    {
        public BridgeOptions CurrentValue => options;
        public BridgeOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<BridgeOptions, string?> listener) => null;
    }
}
