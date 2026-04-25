using LucidCartographer.Services.Auth;

namespace LucidCartographer.Configuration;

public static class AuthRouteGuardExtensions
{
    /// <summary>
    /// Redirects unauthenticated requests to /login when an auth secret is
    /// configured. Allowed unauthenticated paths: /login, static assets,
    /// /health, Blazor framework + circuit endpoints.
    /// </summary>
    public static IApplicationBuilder UseAuthRouteGuard(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";

            if (path == "/login" ||
                path.StartsWith("/_framework", StringComparison.Ordinal) ||
                path.StartsWith("/css", StringComparison.Ordinal) ||
                path.StartsWith("/js", StringComparison.Ordinal) ||
                path.StartsWith("/lib", StringComparison.Ordinal) ||
                path == "/health" ||
                path.StartsWith("/_blazor", StringComparison.Ordinal))
            {
                await next();
                return;
            }

            var configuredSecret = AuthSecretReader.GetConfiguredAuthSecret(
                context.RequestServices.GetRequiredService<IConfiguration>());
            if (!string.IsNullOrEmpty(configuredSecret)
                && !(context.User.Identity?.IsAuthenticated ?? false))
            {
                context.Response.Redirect("/login");
                return;
            }

            await next();
        });
        return app;
    }
}
