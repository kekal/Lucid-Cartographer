namespace LucidCartographer.Configuration;

public static class SecurityHeadersExtensions
{
    /// <summary>
    /// Adds CSP, X-Content-Type-Options, X-Frame-Options, Referrer-Policy.
    /// ARCH-CRIT-04: Tightened CSP — removed 'unsafe-eval', specified CDN domains explicitly.
    /// 'unsafe-inline' for script-src is required by Blazor Server — its SignalR bootstrapper
    /// injects inline scripts that cannot use nonces/hashes with the current Blazor runtime.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            // The embedded noVNC remote view (Google session page) is trusted,
            // localhost-proxied content framed same-origin by /google-session.
            // The app's strict CSP + X-Frame-Options: DENY + frame-ancestors 'none'
            // would block the iframe from rendering AND break noVNC's own inline
            // scripts/websocket, so scope those off this subtree and allow only
            // same-origin framing instead.
            var path = context.Request.Path.Value ?? "";
            if (path.StartsWith("/google-session/novnc", StringComparison.Ordinal))
            {
                context.Response.Headers.Append("X-Frame-Options", "SAMEORIGIN");
                await next();
                return;
            }

            context.Response.Headers.Append("Content-Security-Policy",
                "default-src 'self'; " +
                "script-src 'self' 'unsafe-inline'; " +
                "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
                "font-src 'self' https://fonts.gstatic.com; " +
                "img-src 'self' data: https://*.tile.openstreetmap.org https://*.googleapis.com https://*.gstatic.com; " +
                "connect-src 'self' ws: wss:; " +
                "frame-ancestors 'none';");
            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            context.Response.Headers.Append("X-Frame-Options", "DENY");
            context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
            await next();
        });
        return app;
    }
}
