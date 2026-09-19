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
    }

    private static void ValidateServer(MirrorServerOptions server, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(server.ApplicationName))
            failures.Add("Bridge:Server:ApplicationName is required.");

        if (!server.AllowNoSecurity && !server.AllowAnonymous)
        {
            // Both off leaves no usable way in, and the failure would look like a
            // certificate problem rather than a configuration one.
            failures.Add(
                "Bridge:Server has neither AllowNoSecurity nor AllowAnonymous enabled, so no downstream " +
                "client could authenticate. Enable one, or configure a user token policy.");
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
