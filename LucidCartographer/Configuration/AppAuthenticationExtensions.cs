using System.Security.Claims;
using System.Threading.RateLimiting;
using LucidCartographer.Services.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace LucidCartographer.Configuration;

public static class AppAuthenticationExtensions
{
    /// <summary>
    /// Cookie auth + session-token validation, login rate limiter, and the
    /// startup-time refusal to run with the literal "changeme" secret.
    /// Sliding 30-day expiration; HttpOnly + SameSite=Strict + Secure.
    /// </summary>
    public static IServiceCollection AddAppAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ARCH-CRIT-03: Refuse to start with default insecure password
        var configuredPassword = configuration["Auth:Password"];
        var configuredPasswordHash = configuration["Auth:PasswordHash"];
        if (string.Equals(configuredPassword, "changeme", StringComparison.Ordinal)
            || string.Equals(configuredPasswordHash, "changeme", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Auth:Password/Auth:PasswordHash is still set to 'changeme'. Set a strong secret before starting the application.");
        }

        services.AddSingleton<SessionStore>();

        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/login";
                options.AccessDeniedPath = "/login";
                options.Cookie.Name = "cartographer_auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromDays(30);
                options.Events = new CookieAuthenticationEvents
                {
                    OnValidatePrincipal = async context =>
                    {
                        var sessionToken = context.Principal?.FindFirstValue("session_token");
                        if (string.IsNullOrWhiteSpace(sessionToken))
                        {
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                            return;
                        }

                        var store = context.HttpContext.RequestServices.GetRequiredService<SessionStore>();
                        if (!await store.IsActiveAsync(sessionToken, context.HttpContext.RequestAborted))
                        {
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                        }
                    }
                };
            });
        services.AddAuthorization();

        // BCL rate limiter — replaces hand-rolled ConcurrentDictionary counter.
        // 5 attempts per minute per client IP, partitioned fixed window.
        // Semantic drift: counts ALL attempts (success + fail), not just failures.
        // Acceptable for a single-user app — brute-force protection still holds.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsync(
                    "Too many login attempts. Try again later.", ct);
            };
            options.AddPolicy("login", httpContext =>
            {
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
        });

        return services;
    }
}
