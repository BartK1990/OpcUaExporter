using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OpcUaBridge.Configuration;

namespace OpcUaBridge.Host;

/// <summary>
/// Requires <c>Bridge:Web:AdminToken</c> before the dashboard will answer.
/// </summary>
/// <remarks>
/// <para>
/// The options validator refuses to start when the dashboard is bound to anything but
/// loopback without a token. That check is only meaningful if the token is actually
/// enforced -- a setting that reads like protection and grants none is worse than no
/// setting at all, because an operator who sets it stops looking.
/// </para>
/// <para>
/// This is a shared secret, not a user system, which is proportionate for a single-operator
/// service. It is accepted from the <c>X-Admin-Token</c> header, from a <c>token</c> query
/// parameter (so a link can be opened in a browser), or from a cookie set on the first
/// accepted request, so that the Blazor circuit's later requests carry it too.
/// </para>
/// </remarks>
public sealed class AdminTokenMiddleware(RequestDelegate next, IOptions<BridgeOptions> options)
{
    private const string CookieName = "opcua-bridge-admin";
    private const string HeaderName = "X-Admin-Token";
    private const string QueryName = "token";

    public async Task InvokeAsync(HttpContext context)
    {
        var expected = options.Value.Web.AdminToken;

        // No token configured means loopback-only, which the validator has already
        // enforced. Nothing to check.
        if (string.IsNullOrWhiteSpace(expected))
        {
            await next(context);
            return;
        }

        if (TryGetPresentedToken(context, out var presented) && IsMatch(presented, expected))
        {
            // Re-issue on every accepted request so the cookie tracks a rotated token.
            context.Response.Cookies.Append(CookieName, presented, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = context.Request.IsHttps,
                IsEssential = true
            });

            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync(
            "OPC UA Bridge: an admin token is required. Supply it as an X-Admin-Token header " +
            "or a ?token= query parameter.");
    }

    private static bool TryGetPresentedToken(HttpContext context, out string token)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var header) && !string.IsNullOrEmpty(header))
        {
            token = header.ToString();
            return true;
        }

        if (context.Request.Query.TryGetValue(QueryName, out var query) && !string.IsNullOrEmpty(query))
        {
            token = query.ToString();
            return true;
        }

        if (context.Request.Cookies.TryGetValue(CookieName, out var cookie) && !string.IsNullOrEmpty(cookie))
        {
            token = cookie;
            return true;
        }

        token = string.Empty;
        return false;
    }

    /// <summary>Compares in fixed time, so a wrong token reveals nothing about the right one.</summary>
    private static bool IsMatch(string presented, string expected)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(expected));
}
