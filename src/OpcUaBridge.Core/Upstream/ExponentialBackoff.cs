namespace OpcUaBridge.Upstream;

/// <summary>
/// Retry delays that grow with each consecutive failure.
/// </summary>
/// <remarks>
/// Jitter is not decoration. Several bridges restarting after the same plant power blip
/// would otherwise synchronise their retries and arrive at the recovering server together,
/// repeatedly, at exactly the moment it can least afford it.
/// </remarks>
public sealed class ExponentialBackoff(
    TimeSpan minDelay,
    TimeSpan maxDelay,
    double jitterFraction = 0.2,
    Func<double>? randomSource = null)
{
    private readonly Func<double> _random = randomSource ?? Random.Shared.NextDouble;
    private int _attempt;

    /// <summary>Consecutive failures since the last <see cref="Reset"/>.</summary>
    public int Attempt => _attempt;

    /// <summary>The delay before the next attempt, advancing the sequence.</summary>
    public TimeSpan Next()
    {
        var exponent = Math.Min(_attempt, 30);                  // 2^31 ms would overflow the doubling
        var scaled = minDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(scaled, maxDelay.TotalMilliseconds);

        _attempt++;

        // Spread by +/- jitterFraction around the capped delay.
        var jitter = capped * jitterFraction * (2 * _random() - 1);
        var withJitter = Math.Max(minDelay.TotalMilliseconds * 0.5, capped + jitter);

        return TimeSpan.FromMilliseconds(withJitter);
    }

    /// <summary>Returns to the shortest delay, after a successful connection.</summary>
    public void Reset() => _attempt = 0;
}
