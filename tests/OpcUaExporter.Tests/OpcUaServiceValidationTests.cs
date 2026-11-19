using Microsoft.Extensions.Logging.Abstractions;
using OpcUaExporter.Models;
using OpcUaExporter.Services;
using Xunit;

namespace OpcUaExporter.Tests;

public class OpcUaServiceValidationTests
{
    private static OpcUaService CreateService()
        => new(
            new OpcUaClientService(NullLogger<OpcUaClientService>.Instance, new DiagnosticsLogService()),
            NullLogger<OpcUaService>.Instance,
            new DiagnosticsLogService());

    private static OpcUaService WithUserAuth(string username, string password)
    {
        var svc = CreateService();
        svc.Profile.EndpointUrl = "opc.tcp://localhost:4840";
        svc.Profile.SecurityPolicy = "None";
        svc.Profile.AuthenticationType = AuthenticationType.UsernamePassword;
        svc.Profile.Username = username;
        svc.Profile.Password = password;
        return svc;
    }

    [Fact]
    public void UsernameWithEmptyPassword_IsValid()
    {
        Assert.Empty(WithUserAuth("operator", string.Empty).GetConnectionValidationWarnings());
    }

    [Fact]
    public void EmptyUsername_IsStillRejected()
    {
        var warnings = WithUserAuth(string.Empty, "secret").GetConnectionValidationWarnings();

        Assert.Contains(warnings, w => w.Contains("Username is required"));
    }
}
