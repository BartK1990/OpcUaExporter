using Opc.Ua;
using Opc.Ua.Configuration;
using OpcUaExporter.SimServer;

// Usage: dotnet run --project tools/OpcUaExporter.SimServer [-- --port 4840]
var port = 4840;
for (var i = 0; i + 1 < args.Length; i++)
{
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var p))
        port = p;
}

var config = await SimServerConfiguration.CreateAsync(port);
var application = new ApplicationInstance(config.CreateMessageContext().Telemetry)
{
    ApplicationName = config.ApplicationName,
    ApplicationType = ApplicationType.Server,
    ApplicationConfiguration = config
};

if (!await application.CheckApplicationInstanceCertificatesAsync(true, 2048, ct: default))
    throw new InvalidOperationException("Unable to create the simulation server certificate.");

var server = new SimServer();
await application.StartAsync(server);

foreach (var endpoint in server.GetEndpoints().Select(e => $"{e.EndpointUrl} [{e.SecurityPolicyUri.Split('#').Last()}, {e.SecurityMode}]").Distinct())
    Console.WriteLine($"Listening: {endpoint}");
Console.WriteLine("Press Ctrl+C to stop.");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
await stop.Task;

server.Stop();
