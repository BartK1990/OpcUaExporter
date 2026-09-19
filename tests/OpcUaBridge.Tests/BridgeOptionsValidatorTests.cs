using OpcUaBridge.Configuration;
using Xunit;

namespace OpcUaBridge.Tests;

public class BridgeOptionsValidatorTests
{
    private static readonly BridgeOptionsValidator Validator = new();

    private static BridgeOptions Valid() => new();

    private static IEnumerable<string> Failures(BridgeOptions options)
        => Validator.Validate(name: null, options).Failures ?? [];

    [Fact]
    public void Validate_AcceptsTheShippedDefaults()
    {
        Assert.True(Validator.Validate(null, Valid()).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://plant:4840")]
    public void Validate_RejectsAnEndpointThatIsNotAnOpcTcpUrl(string endpointUrl)
    {
        var options = Valid();
        options.Upstream.EndpointUrl = endpointUrl;

        Assert.Contains(Failures(options), f => f.Contains("EndpointUrl"));
    }

    [Fact]
    public void Validate_RequiresAUsernameWhenUsernameAuthenticationIsSelected()
    {
        var options = Valid();
        options.Upstream.AuthenticationType = OpcUaExporter.Models.AuthenticationType.UsernamePassword;

        Assert.Contains(Failures(options), f => f.Contains("Username"));
    }

    [Fact]
    public void Validate_RejectsABackoffWindowThatRunsBackwards()
    {
        var options = Valid();
        options.Upstream.ReconnectMinDelayMs = 60_000;
        options.Upstream.ReconnectMaxDelayMs = 30_000;

        Assert.Contains(Failures(options), f => f.Contains("ReconnectMinDelayMs"));
    }

    [Fact]
    public void Validate_RefusesToExposeTheDashboardOffTheMachineWithoutAToken()
    {
        // Fail closed. A gateway that silently published an unauthenticated admin UI to
        // the plant network would be a far worse outcome than a service that won't start.
        var options = Valid();
        options.Web.Urls = "http://0.0.0.0:5080";

        Assert.Contains(Failures(options), f => f.Contains("AdminToken"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5080")]
    [InlineData("http://localhost:5080")]
    [InlineData("http://[::1]:5080")]
    public void Validate_AllowsLoopbackBindingsWithoutAToken(string urls)
    {
        var options = Valid();
        options.Web.Urls = urls;

        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validate_AllowsANetworkBindingOnceATokenIsSet()
    {
        var options = Valid();
        options.Web.Urls = "http://0.0.0.0:5080";
        options.Web.AdminToken = "a-long-random-token";

        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("http://*:5080")]
    [InlineData("http://+:5080")]
    [InlineData("http://192.168.1.10:5080")]
    public void IsLoopback_TreatsWildcardAndExternalHostsAsReachable(string url)
    {
        Assert.False(BridgeOptionsValidator.IsLoopback(url));
    }

    [Fact]
    public void Validate_RejectsAServerThatNoClientCouldConnectTo()
    {
        var options = Valid();
        options.Server.AllowNoSecurity = false;
        options.Server.AllowAnonymous = false;

        Assert.Contains(Failures(options), f => f.Contains("AllowNoSecurity"));
    }

    [Fact]
    public void Validate_RejectsANegativeSamplingIntervalOtherThanTheFollowPublishingSentinel()
    {
        var options = Valid();
        options.Acquisition.SamplingIntervalMs = -5;

        Assert.Contains(Failures(options), f => f.Contains("SamplingIntervalMs"));

        options.Acquisition.SamplingIntervalMs = -1;
        Assert.DoesNotContain(Failures(options), f => f.Contains("SamplingIntervalMs"));
    }
}
