using Microsoft.Extensions.Options;

namespace OpcUaBridge.Configuration;

/// <summary>
/// Rejects configurations that would start but misbehave.
/// </summary>
/// <remarks>
/// A gateway runs unattended, so a setting that is merely wrong must stop the service at
/// startup with a message naming the key, rather than surfacing hours later as data that
/// does not arrive or a dashboard reachable from the plant network.
/// </remarks>
public sealed class BridgeOptionsValidator : IValidateOptions<BridgeOptions>
{
    public ValidateOptionsResult Validate(string? name, BridgeOptions options)
    {
        var failures = new List<string>();

        ValidateUpstream(options.Upstream, failures);
        ValidateAcquisition(options.Acquisition, failures);
        ValidateServer(options.Server, failures);
        ValidateWeb(options.Web, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateUpstream(UpstreamOptions upstream, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(upstream.EndpointUrl))
        {
            failures.Add("Bridge:Upstream:EndpointUrl is required.");
        }
        else if (!Uri.TryCreate(upstream.EndpointUrl, UriKind.Absolute, out var uri) ||
                 !string.Equals(uri.Scheme, "opc.tcp", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"Bridge:Upstream:EndpointUrl '{upstream.EndpointUrl}' must be an absolute opc.tcp:// URL.");
        }

        if (upstream.AuthenticationType == OpcUaExporter.Models.AuthenticationType.UsernamePassword &&
            string.IsNullOrWhiteSpace(upstream.Username))
        {
            failures.Add("Bridge:Upstream:Username is required when AuthenticationType is UsernamePassword.");
        }

        if (upstream.ReconnectMinDelayMs > upstream.ReconnectMaxDelayMs)
        {
            failures.Add(
                $"Bridge:Upstream:ReconnectMinDelayMs ({upstream.ReconnectMinDelayMs}) must not exceed " +
                $"ReconnectMaxDelayMs ({upstream.ReconnectMaxDelayMs}).");
        }

        if (string.IsNullOrWhiteSpace(upstream.ClientApplicationName))
            failures.Add("Bridge:Upstream:ClientApplicationName is required.");
    }

    private static void ValidateAcquisition(AcquisitionOptions acquisition, List<string> failures)
    {
        if (!Enum.IsDefined(acquisition.Mode))
            failures.Add($"Bridge:Acquisition:Mode '{acquisition.Mode}' is not a known mode.");

        if (acquisition.SamplingIntervalMs is not -1 and < 0)
        {
            failures.Add(
                "Bridge:Acquisition:SamplingIntervalMs must be positive, or -1 to follow the publishing interval.");
        }

        // Zero is legal OPC UA and means "as fast as this server can manage". Set by hand
        // it reads like a default, and on a few thousand tags it asks the plant server --
        // the one already struggling, or this gateway would not exist -- for its fastest
        // possible sampling rate. -1 is the setting somebody reaching for "don't care"
        // actually wants.
        if (acquisition.SamplingIntervalMs == 0)
        {
            failures.Add(
                "Bridge:Acquisition:SamplingIntervalMs is 0, which asks the upstream server to sample every " +
                "tag as fast as it can rather than at a rate you chose. Set a millisecond interval, or -1 to " +
                "follow the publishing interval.");
        }

        if (acquisition.PublishingIntervalMs <= 0)
        {
            failures.Add(
                $"Bridge:Acquisition:PublishingIntervalMs ({acquisition.PublishingIntervalMs}) must be positive.");
        }

        if (acquisition.Mode == AcquisitionMode.Polling && acquisition.PollingIntervalMs <= 0)
        {
            failures.Add(
                $"Bridge:Acquisition:PollingIntervalMs ({acquisition.PollingIntervalMs}) must be positive.");
        }
    }

    private static void ValidateServer(MirrorServerOptions server, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(server.ApplicationName))
            failures.Add("Bridge:Server:ApplicationName is required.");

        if (!server.AllowAnonymous)
        {
            // Anonymous is the only user token policy the bridge offers, so turning it off
            // leaves the endpoints advertising none at all and no downstream client able
            // to activate a session. The failure would look like a certificate problem
            // rather than a configuration one, so reject it here instead.
            failures.Add(
                "Bridge:Server:AllowAnonymous is false, but anonymous is the only user token policy " +
                "OPC UA Bridge offers, so no downstream client could activate a session. Leave it enabled, " +
                "and restrict access with Bridge:Server:AllowNoSecurity and the server certificate trust list.");
        }
    }

    private static void ValidateWeb(WebOptions web, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(web.Urls))
        {
            failures.Add("Bridge:Web:Urls is required.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(web.AdminToken))
            return;

        foreach (var url in web.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsLoopback(url))
                continue;

            failures.Add(
                $"Bridge:Web:Urls binds '{url}', which is reachable from the network, but no " +
                "Bridge:Web:AdminToken is set. Set a token, or bind to 127.0.0.1 only.");
        }
    }

    /// <summary>Whether a Kestrel URL is confined to this machine.</summary>
    internal static bool IsLoopback(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        // Kestrel's wildcard hosts bind every interface; Uri maps them to a literal host name.
        if (uri.Host is "*" or "+" or "0.0.0.0" or "[::]")
            return false;

        return uri.IsLoopback;
    }
}
