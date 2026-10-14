using OpcUaExporter.SimServer;

// Usage: dotnet run --project tools/OpcUaExporter.SimServer [-- --port 4840]
var port = 4840;
for (var i = 0; i + 1 < args.Length; i++)
{
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var p))
        port = p;
}

await using var host = await SimServerHost.StartAsync(port);

foreach (var endpoint in host.Endpoints.Select(e => $"{e.EndpointUrl} [{e.SecurityPolicyUri.Split('#').Last()}, {e.SecurityMode}]").Distinct())
    Console.WriteLine($"Listening: {endpoint}");
Console.WriteLine($"Connect the exporter to {host.EndpointUrl}. Press Ctrl+C to stop.");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
await stop.Task;
