using Opc.Ua;
using OpcUaBridge.Upstream;
using Xunit;

namespace OpcUaBridge.Tests;

public class ExponentialBackoffTests
{
    private static ExponentialBackoff WithoutJitter(double minSeconds = 1, double maxSeconds = 30) =>
        new(TimeSpan.FromSeconds(minSeconds), TimeSpan.FromSeconds(maxSeconds), jitterFraction: 0);

    [Fact]
    public void Next_DoublesTheDelayUntilItReachesTheCap()
    {
        var backoff = WithoutJitter();

        var delays = Enumerable.Range(0, 7).Select(_ => backoff.Next().TotalSeconds).ToArray();

        Assert.Equal([1, 2, 4, 8, 16, 30, 30], delays);
    }

    [Fact]
    public void Next_NeverExceedsTheCapEvenAfterManyFailures()
    {
        // An outage lasting days must not overflow the doubling into a negative or
        // absurd delay -- the bridge has to still be trying when the server comes back.
        var backoff = WithoutJitter();

        for (var i = 0; i < 500; i++)
            backoff.Next();

        Assert.Equal(TimeSpan.FromSeconds(30), backoff.Next());
    }

    [Fact]
    public void Reset_ReturnsToTheShortestDelay()
    {
        var backoff = WithoutJitter();
        backoff.Next();
        backoff.Next();

        backoff.Reset();

        Assert.Equal(0, backoff.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(1), backoff.Next());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Next_SpreadsDelaysWithinTheJitterBand(double randomValue)
    {
        // Jitter keeps several bridges restarting after one power blip from arriving at
        // the recovering server in lockstep.
        var backoff = new ExponentialBackoff(
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), jitterFraction: 0.2, randomSource: () => randomValue);

        var delay = backoff.Next().TotalSeconds;

        Assert.InRange(delay, 8, 12);
    }

    [Fact]
    public void Next_StaysPositiveWhenJitterWouldPushTheDelayBelowZero()
    {
        var backoff = new ExponentialBackoff(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), jitterFraction: 2.0, randomSource: () => 0.0);

        Assert.True(backoff.Next() > TimeSpan.Zero);
    }
}

public class UpstreamFailureClassificationTests
{
    public static TheoryData<uint> Unrecoverable =>
    [
        StatusCodes.BadCertificateUntrusted,
        StatusCodes.BadCertificateTimeInvalid,
        StatusCodes.BadCertificateHostNameInvalid,
        StatusCodes.BadCertificateUriInvalid,
        StatusCodes.BadSecurityChecksFailed,
        StatusCodes.BadUserAccessDenied,
        StatusCodes.BadIdentityTokenRejected
    ];

    public static TheoryData<uint> Transient =>
    [
        StatusCodes.BadNotConnected,
        StatusCodes.BadTimeout,
        StatusCodes.BadCommunicationError,
        StatusCodes.BadServerNotConnected,
        StatusCodes.BadTooManySessions
    ];

    [Theory]
    [MemberData(nameof(Unrecoverable))]
    public void IsUnrecoverable_IsTrueWhenTheServerHasRefusedUsOutright(uint statusCode)
    {
        // Retrying a rejected certificate every second forever buries the real problem in
        // log noise and loads a server that has already said no.
        Assert.True(UpstreamConnectionManager.IsUnrecoverable(new ServiceResultException(statusCode)));
    }

    [Theory]
    [MemberData(nameof(Transient))]
    public void IsUnrecoverable_IsFalseForAnythingThatMightRecoverOnItsOwn(uint statusCode)
    {
        Assert.False(UpstreamConnectionManager.IsUnrecoverable(new ServiceResultException(statusCode)));
    }

    [Fact]
    public void IsUnrecoverable_TreatsAnUnmatchedEndpointAsAConfigurationError()
    {
        // Endpoint selection throws this when nothing matches the configured security
        // mode or policy. No amount of waiting will make a matching endpoint appear.
        Assert.True(UpstreamConnectionManager.IsUnrecoverable(
            new InvalidOperationException("No endpoint matches SecurityMode='SignAndEncrypt'.")));
    }

    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(IOException))]
    public void IsUnrecoverable_IsFalseForOrdinaryNetworkFailures(Type exceptionType)
    {
        Assert.False(UpstreamConnectionManager.IsUnrecoverable((Exception)Activator.CreateInstance(exceptionType)!));
    }
}
