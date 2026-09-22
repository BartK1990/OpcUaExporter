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
    public void Validate_EnforcesRangeAttributesOnTheNestedSections()
    {
        // AddOptions().ValidateDataAnnotations() only inspects BridgeOptions' own
        // properties, every one of which is a nested object with no attribute on it. These
        // bounds are only real because this validator applies them itself.
        var options = Valid();
        options.Acquisition.MaxItemsPerSubscription = 100_000;

        Assert.Contains(Failures(options), f => f.Contains("Bridge:Acquisition:MaxItemsPerSubscription"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70_000)]
    public void Validate_RejectsAServerPortOutsideTheLegalRange(int port)
    {
        var options = Valid();
        options.Server.Port = port;

        Assert.Contains(Failures(options), f => f.Contains("Bridge:Server:Port"));
    }

    [Fact]
    public void Validate_RejectsAKeepAliveIntervalTooShortToBeMeaningful()
    {
        var options = Valid();
        options.Upstream.KeepAliveIntervalMs = 1;

        Assert.Contains(Failures(options), f => f.Contains("Bridge:Upstream:KeepAliveIntervalMs"));
    }

    [Fact]
    public void Validate_NamesTheConfigurationKeyNotTheClrProperty()
    {
        // "The field MaxItemsPerSubscription must be between 1 and 50000" is not something
        // anybody can search an appsettings.json for.
        var options = Valid();
        options.Acquisition.QueueSize = 0;

        var failure = Assert.Single(Failures(options), f => f.Contains("QueueSize"));

        Assert.StartsWith("Bridge:Acquisition:QueueSize", failure);
    }

    [Fact]
    public void Validate_AcceptsALargeButLegalSubscriptionSize()
    {
        // 50000 is the documented ceiling and has to stay usable: at 64000 tags it is the
        // difference between 65 publish pipelines and 2.
        var options = Valid();
        options.Acquisition.MaxItemsPerSubscription = 50_000;

        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validate_RejectsASamplingIntervalOfZero()
    {
        // Legal OPC UA, and it means "as fast as this server can manage". Typed by hand it
        // reads like a default, so it is rejected rather than quietly asking an already
        // struggling plant server for its fastest rate on every tag.
        var options = Valid();
        options.Acquisition.SamplingIntervalMs = 0;

        Assert.Contains(Failures(options), f => f.Contains("SamplingIntervalMs"));
    }

    [Fact]
    public void Validate_AcceptsMinusOneAsTheSamplingInterval()
    {
        var options = Valid();
        options.Acquisition.SamplingIntervalMs = -1;

        Assert.DoesNotContain(Failures(options), f => f.Contains("SamplingIntervalMs"));
    }

    [Fact]
    public void Validate_RejectsANonPositivePublishingInterval()
    {
        var options = Valid();
        options.Acquisition.PublishingIntervalMs = 0;

        Assert.Contains(Failures(options), f => f.Contains("PublishingIntervalMs"));
    }

    [Fact]
    public void Validate_RejectsANonPositivePollingIntervalInEitherMode()
    {
        // The [Range] attribute bounds this at 50ms and says nothing about the mode, so it
        // is rejected whether or not the interval is currently in use. A configuration
        // holding a value that could never work is worth refusing at startup rather than
        // the first time somebody switches mode to diagnose something.
        foreach (var mode in new[] { AcquisitionMode.Polling, AcquisitionMode.Subscription })
        {
            var options = Valid();
            options.Acquisition.Mode = mode;
            options.Acquisition.PollingIntervalMs = 0;

            Assert.Contains(Failures(options), f => f.Contains("PollingIntervalMs"));
        }
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
    public void Validate_RejectsAServerNoClientCouldAuthenticateTo()
    {
        // Anonymous is the only user token policy the bridge offers, so turning it off
        // leaves the endpoints advertising none and every downstream session refused --
        // a failure that would look like a certificate problem.
        var options = Valid();
        options.Server.AllowAnonymous = false;

        Assert.Contains(Failures(options), f => f.Contains("AllowAnonymous"));
    }

    [Fact]
    public void Validate_AllowsASecureOnlyServer()
    {
        var options = Valid();
        options.Server.AllowNoSecurity = false;

        Assert.True(Validator.Validate(null, options).Succeeded);
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
