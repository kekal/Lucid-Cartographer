using System.Security.Claims;
using System.Net;
using System.Threading.RateLimiting;
using LucidCartographer.Services.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

namespace LucidCartographer.Configuration;

public static class AppAuthenticationExtensions
{
    /// <summary>
    /// Cookie auth + session-token validation, login rate limiter, and
    /// forwarded-header trust configuration for reverse proxies.
    /// Sliding 30-day expiration; HttpOnly + SameSite=Strict + Secure.
    /// </summary>
    public static IServiceCollection AddAppAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // Persist Data Protection keys to the /data volume. Without this they
        // land in the ephemeral container filesystem (~/.aspnet/DataProtection-Keys),
        // so every redeploy regenerates them — invalidating all auth cookies,
        // antiforgery tokens, and any in-flight OAuth authorization-code state.
        // A fixed application name keeps the key ring stable across instances.
        var keysDir = Path.Combine(
            DatabaseServicesExtensions.ResolveDataDirectory(configuration, environment),
            "dataprotection-keys");
        Directory.CreateDirectory(keysDir);
        services
            .AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
            .SetApplicationName("LucidCartographer");

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();

            var trustedProxies = configuration.GetSection("Auth:TrustedProxies").Get<string[]>() ?? [];
            foreach (var trustedProxy in trustedProxies)
            {
                if (IPAddress.TryParse(trustedProxy, out var parsedAddress))
                {
                    options.KnownProxies.Add(parsedAddress);
                }
            }

            // Trusted networks (CIDR), e.g. "172.16.0.0/12" for the Docker bridge
            // range. Behind a container NAT the immediate peer is a gateway whose
            // exact IP is hard to predict and can change, so trusting the private
            // network is more robust than pinning a single proxy IP. Only matters
            // for which peer may set X-Forwarded-Proto/For; the app is reachable
            // solely via the tunnel and LAN, so trusting RFC1918 here is safe.
            var trustedNetworks = configuration.GetSection("Auth:TrustedNetworks").Get<string[]>() ?? [];
            foreach (var entry in trustedNetworks)
            {
                var parts = entry.Split('/', 2);
                if (parts.Length == 2
                    && IPAddress.TryParse(parts[0], out var prefix)
                    && int.TryParse(parts[1], out var prefixLength))
                {
                    options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, prefixLength));
                }
            }
        });

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
