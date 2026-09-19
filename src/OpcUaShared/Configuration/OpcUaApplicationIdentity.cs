using Opc.Ua;

namespace OpcUaExporter.Configuration;

/// <summary>
/// The identity this process presents to an OPC UA server: the name shown in the
/// server's session list, the URI that must match the client certificate's SAN,
/// and the subject the certificate is issued to.
/// </summary>
/// <remarks>
/// The SDK locates the application certificate by <see cref="SubjectName"/>, so
/// two applications that want to share a certificate store must agree on all
/// three values. That is what lets OPC UA Bridge reuse a certificate the
/// exporter already had trusted by a server.
/// </remarks>
/// <param name="ApplicationName">Human-readable name, e.g. <c>OpcUaExporter</c>.</param>
/// <param name="ApplicationUri">Application URI, e.g. <c>urn:HOST:OpcUaExporter</c>.</param>
/// <param name="SubjectName">Certificate subject, e.g. <c>CN=OpcUaExporter</c>.</param>
public sealed record OpcUaApplicationIdentity(string ApplicationName, string ApplicationUri, string SubjectName)
{
    /// <summary>The exporter's identity — also the default, so existing certificate stores keep working.</summary>
    public static OpcUaApplicationIdentity Exporter { get; } = ForApplication("OpcUaExporter");

    /// <summary>
    /// Builds the conventional identity for <paramref name="applicationName"/>:
    /// <c>urn:{hostname}:{name}</c> and <c>CN={name}</c>.
    /// </summary>
    public static OpcUaApplicationIdentity ForApplication(string applicationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        return new OpcUaApplicationIdentity(
            applicationName,
            $"urn:{Utils.GetHostName()}:{applicationName}",
            $"CN={applicationName}");
    }
}
