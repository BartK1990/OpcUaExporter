namespace OpcUaBridge.Host;

/// <summary>
/// Compares what the dashboard was asked to bind with what something else expects it to.
/// </summary>
/// <remarks>
/// <c>Bridge:Web:Urls</c> deliberately wins over <c>ASPNETCORE_URLS</c> and over a
/// <c>launchSettings.json</c> profile, because the options validator's fail-closed check on
/// non-loopback addresses is only meaningful if nothing else can quietly override it. The
/// cost is that a disagreeing profile opens a browser at an address nothing is listening
/// on, so the mismatch is worth reporting -- but only a real one. <c>http://localhost:5080</c>
/// and <c>http://127.0.0.1:5080</c> are the same endpoint, and warning about that pair
/// would be noise on every single run from Visual Studio.
/// </remarks>
public static class WebBinding
{
    /// <summary>Whether two Kestrel URL lists would accept the same requests.</summary>
    public static bool AreEquivalent(string? left, string? right)
        => Normalize(left).SetEquals(Normalize(right));

    private static HashSet<string> Normalize(string? urls)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(urls))
            return normalized;

        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                normalized.Add(url);
                continue;
            }

            // Every loopback spelling collapses to one token; a wildcard bind is its own,
            // because it is emphatically not the same as loopback.
            var host = uri.Host is "*" or "+" or "0.0.0.0" or "[::]"
                ? "any"
                : uri.IsLoopback ? "loopback" : uri.Host;

            normalized.Add($"{uri.Scheme}://{host}:{uri.Port}");
        }

        return normalized;
    }
}
