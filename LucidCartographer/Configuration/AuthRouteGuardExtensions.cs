using System.Net;
using System.Net.Sockets;
using System.Security.Claims;

namespace LucidCartographer.Configuration;

public static class AuthRouteGuardExtensions
{
    /// <summary>
    /// Bypasses authentication for local-network requests when enabled,
    /// otherwise redirects unauthenticated requests to /login.
    /// </summary>
    public static IApplicationBuilder UseLanBypassOrAuth(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";

            if (path == "/login" ||
                // The login form POSTs here; an unauthenticated POST must not be
                // bounced to /login (that would drop the credentials).
                path == "/auth/login" ||
                path.StartsWith("/_framework", StringComparison.Ordinal) ||
                path.StartsWith("/css", StringComparison.Ordinal) ||
                path.StartsWith("/js", StringComparison.Ordinal) ||
                path.StartsWith("/lib", StringComparison.Ordinal) ||
                path == "/health" ||
                // The MCP endpoint is an API for non-browser clients (Claude Code).
                // It must NOT be 302-redirected to /login; its own endpoint filter
                // (McpApiKeyFilter) enforces loopback/LAN / API-key / OAuth auth instead.
                path.StartsWith("/mcp", StringComparison.Ordinal) ||
                // OAuth frontdoor: authorization/token/registration endpoints and the
                // discovery/metadata documents must be reachable without the cookie
                // redirect. /connect/authorize challenges the cookie scheme itself when
                // an interactive login is required.
                path.StartsWith("/connect", StringComparison.Ordinal) ||
                path.StartsWith("/.well-known", StringComparison.Ordinal) ||
                path.StartsWith("/_blazor", StringComparison.Ordinal))
            {
                await next();
                return;
            }

            var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
            var bypassLocalAddresses = configuration.GetValue<bool>("Auth:BypassLocalAddresses");
            if (bypassLocalAddresses && IsLocalNetwork(context.Connection.RemoteIpAddress))
            {
                context.User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        [new Claim(ClaimTypes.Name, "lan")],
                        "lan-bypass"));
                await next();
                return;
            }

            if (!(context.User.Identity?.IsAuthenticated ?? false))
            {
                context.Response.Redirect("/login");
                return;
            }

            await next();
        });
        return app;
    }

    /// <summary>
    /// True when the address is loopback or in an RFC 1918 private range.
    /// Exposed internally so the MCP endpoint filter reuses the exact same
    /// definition of "local" rather than duplicating the byte checks.
    /// </summary>
    internal static bool IsLocalNetwork(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // Loopback (127.0.0.0/8) is already handled above by
            // IPAddress.IsLoopback; the remaining clauses cover RFC 1918
            // private ranges only.
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (address.Equals(IPAddress.IPv6Loopback))
        {
            return true;
        }

        var v6 = address.GetAddressBytes();
        return v6[0] == 0xFE && (v6[1] & 0xC0) == 0x80;
    }
}
