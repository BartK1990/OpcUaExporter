using Microsoft.Extensions.Options;
using OpcUaBridge.Certificates;
using OpcUaBridge.Configuration;
using OpcUaBridge.Namespaces;
using OpcUaBridge.Tags;
using OpcUaExporter.Abstractions;

namespace OpcUaBridge.Host;

/// <summary>
/// One-shot commands that run instead of the service.
/// </summary>
/// <remarks>
/// Deployment diagnostics, mostly. A gateway is usually installed by someone who is not
/// the person who configured it, on a machine with no interactive desktop, and "it will
/// not connect" is otherwise a slow thing to diagnose remotely.
/// </remarks>
public static class BridgeCommands
{
    /// <summary>Runs a command if one was given. Returns whether the process should now exit.</summary>
    public static async Task<bool> TryRunAsync(IServiceProvider services, string[] args)
    {
        var command = args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant();

        switch (command)
        {
            case "--check-config":
                CheckConfiguration(services);
                return true;

            case "--check-pki":
                CheckPki(services);
                return true;

            case "--import-exporter-certificates":
                ImportCertificates(services);
                return true;

            case "--show-namespace":
                await ShowNamespaceAsync(services);
                return true;

            case "--help":
                ShowHelp();
                return true;

            default:
                return false;
        }
    }

    private static void ShowHelp()
    {
        Console.WriteLine("""
            OPC UA Bridge

              (no arguments)                     Run the gateway.
              --check-config                     Print the resolved configuration and paths.
              --check-pki                        Report both certificate stores and what is in them.
              --import-exporter-certificates     Copy %LocalAppData%\OpcUaExporter\pki into the bridge.
              --show-namespace                   Summarise the captured namespace snapshot.
              --help                             Show this text.
            """);
    }

    private static void CheckConfiguration(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<BridgeOptions>>().Value;
        var paths = services.GetRequiredService<BridgePaths>();

        Console.WriteLine("Settings files, in the order they override one another:");

        foreach (var file in BridgeConfiguration.DescribeFiles(paths.BaseDirectory))
        {
            var state = file.Exists
                ? "loaded"
                : file.Required ? "MISSING" : "not present";

            Console.WriteLine($"  {file.Role,-20}{state,-13}{file.Path}");
        }

        Console.WriteLine($"  {"Environment",-20}{"applied",-13}OPCUABRIDGE_* variables, then command-line switches");
        Console.WriteLine();

        Console.WriteLine($"""
            Paths
              Install directory   {paths.BaseDirectory}
              Logs                {paths.LogDirectory}
              Configuration       {paths.ConfigDirectory}
              Client certificates {paths.ClientPkiDirectory}
              Server certificates {paths.ServerPkiDirectory}

            Upstream (the server being fronted)
              Endpoint            {options.Upstream.EndpointUrl}
              Security            {options.Upstream.SecurityMode} / {options.Upstream.SecurityPolicy}
              Authentication      {options.Upstream.AuthenticationType}
              Client identity     {options.Upstream.ClientApplicationName}
              Keep-alive          {options.Upstream.KeepAliveIntervalMs}ms
              Reconnect backoff   {options.Upstream.ReconnectMinDelayMs}ms to {options.Upstream.ReconnectMaxDelayMs}ms

            Acquisition
              Mode                {options.Acquisition.Mode}
              Publishing interval {options.Acquisition.PublishingIntervalMs}ms
              Polling interval    {options.Acquisition.PollingIntervalMs}ms
              Items/subscription  {options.Acquisition.MaxItemsPerSubscription}

            Mirrored server (what downstream clients connect to)
              Endpoint            {options.Server.BuildEndpointUrl(Opc.Ua.Utils.GetHostName())}
              Writes forwarded    {options.Server.AllowWrites}
              Unsecured endpoint  {options.Server.AllowNoSecurity}

            Dashboard
              Listening on        {options.Web.Urls}
              Admin token set     {!string.IsNullOrWhiteSpace(options.Web.AdminToken)}
            """);
    }

    private static void CheckPki(IServiceProvider services)
    {
        var paths = services.GetRequiredService<BridgePaths>();
        var identity = services.GetRequiredService<OpcUaExporter.Configuration.OpcUaApplicationIdentity>();

        Console.WriteLine($"""
            Client identity (used to connect to the upstream server)
              Application name    {identity.ApplicationName}
              Application URI     {identity.ApplicationUri}
              Certificate subject {identity.SubjectName}
            """);

        DescribeStore("Client", paths.ClientPkiDirectory);
        DescribeStore("Server", paths.ServerPkiDirectory);

        Console.WriteLine($"""

            To reuse the certificates OPC UA Exporter already had working, run:
              OpcUaBridge.exe --import-exporter-certificates

            The certificate is located by subject name, so 'Bridge:Upstream:ClientApplicationName'
            must match the name it was issued to -- which is why it defaults to OpcUaExporter.
            The certificate also names the machine it was created on, so it will only be
            accepted on that machine.
            """);
    }

    private static void DescribeStore(string role, string root)
    {
        Console.WriteLine($"\n{role} certificate store: {root}");

        foreach (var store in new[] { "own", "trusted", "issuer", "rejected" })
        {
            var certs = Path.Combine(root, store, "certs");
            var count = Directory.Exists(certs) ? Directory.EnumerateFiles(certs).Count() : 0;

            var privateKeys = Path.Combine(root, store, "private");
            var keyCount = Directory.Exists(privateKeys) ? Directory.EnumerateFiles(privateKeys).Count() : 0;

            Console.WriteLine($"  {store,-9} {count} certificate(s)" + (keyCount > 0 ? $", {keyCount} private key(s)" : string.Empty));
        }
    }

    private static void ImportCertificates(IServiceProvider services)
    {
        var importer = services.GetRequiredService<ExporterCertificateImporter>();
        var result = importer.Import();

        Console.WriteLine($"""
            Imported from {result.SourceDirectory}
                       to {result.TargetDirectory}

              Files copied                 {result.FilesCopied}
              Client certificate and key   {(result.FoundOwnCertificate ? "yes" : "no")}
              Trusted server certificates  {result.TrustedCertificateCount}
            """);

        if (!result.ImportedAnything)
            Console.WriteLine("\nNothing was found to import. Check the path, or copy the certificate store across by hand.");
    }

    private static async Task ShowNamespaceAsync(IServiceProvider services)
    {
        var store = services.GetRequiredService<INamespaceSnapshotStore>();
        var snapshot = await store.LoadAsync();

        if (snapshot is null)
        {
            Console.WriteLine($"No namespace snapshot at {store.SnapshotPath}. Capture one from the dashboard.");
            return;
        }

        var registry = TagRegistry.FromSnapshot(snapshot);

        Console.WriteLine($"""
            Namespace snapshot  {store.SnapshotPath}
              Captured          {snapshot.CapturedUtc:u}
              From              {snapshot.Source.EndpointUrl} ({snapshot.Source.ServerName})
              SHA-256           {await store.ComputeHashAsync()}
              Objects           {snapshot.Stats.ObjectCount}
              Variables         {snapshot.Stats.VariableCount}
              Writable tags     {registry.Tags.Count(t => t.IsWritable)}

            Namespaces
            {string.Join(Environment.NewLine, snapshot.NamespaceUris.Select((uri, i) => $"  [{i}] {uri}"))}
            """);
    }
}
