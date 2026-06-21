using System.Security.Claims;
using LucidCartographer.Data;
using LucidCartographer.Services.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Mapped at /auth/login (not /login) to avoid conflicting with routable
        // Login.razor page, which would cause AmbiguousMatchException.
        endpoints.MapPost("/auth/login", async context =>
        {
            // Antiforgery validation surfaces as 400 (not redirect) to let
            // anomaly detection distinguish CSRF abuse from typo'd passwords.
            var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException ex)
            {
                var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
                loggerFactory.CreateLogger("AuthEndpoints").LogWarning(
                    ex,
                    "Antiforgery validation failed for {Path} from {RemoteIp}",
                    context.Request.Path,
                    context.Connection.RemoteIpAddress);
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("CSRF validation failed");
                return;
            }

            var form = await context.Request.ReadFormAsync();
            var username = form["username"].ToString();
            var password = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();
            var dbFactory = context.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>();

            await using var db = await dbFactory.CreateDbContextAsync(context.RequestAborted);
            var user = await db.Users.FirstOrDefaultAsync(
                u => u.Username == username,
                context.RequestAborted);

            var passwordMatch = user is not null && PasswordHasher.Verify(password, user.PasswordHash);

            if (passwordMatch)
            {
                user!.LastLoginAt = DateTime.UtcNow;
                await db.SaveChangesAsync(context.RequestAborted);

                var store = context.RequestServices.GetRequiredService<SessionStore>();
                var sessionToken = await store.CreateAsync(context.RequestAborted);

                List<Claim> claims =
                [
                    new(ClaimTypes.Name, user.Username),
                    new("session_token", sessionToken)
                ];
                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var principal = new ClaimsPrincipal(identity);

                await context.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    principal,
                    new AuthenticationProperties
                    {
                        IsPersistent = true,
                        ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
                    });

                // Only local URLs to prevent open-redirect; used by OAuth /connect/authorize.
                var redirectTo = IsLocalUrl(returnUrl) ? returnUrl : "/";
                context.Response.Redirect(redirectTo);
            }
            else
            {
                context.Response.Redirect("/login?error=1");
            }
        }).RequireRateLimiting("login");

        // Revoke and sign-out are independent: failed revoke must not leave
        // client cookie valid, so SignOutAsync runs unconditionally.
        endpoints.MapGet("/logout", async context =>
        {
            var sessionToken = context.User.FindFirstValue("session_token");
            if (!string.IsNullOrWhiteSpace(sessionToken))
            {
                try
                {
                    var store = context.RequestServices.GetRequiredService<SessionStore>();
                    await store.RevokeAsync(sessionToken, context.RequestAborted);
                }
                catch (Exception ex)
                {
                    var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
                    loggerFactory.CreateLogger("AuthEndpoints").LogWarning(
                        ex,
                        "Server-side session revoke failed; signing out the client cookie anyway");
                }
            }

            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            context.Response.Redirect("/login");
        });

        return endpoints;
    }

    /// <summary>
    /// True only for same-site relative URLs ("/path..."). Rejects absolute URLs
    /// and protocol-relative ("//host", "/\host") forms to prevent open redirects.
    /// </summary>
    private static bool IsLocalUrl(string? url)
        => !string.IsNullOrEmpty(url)
           && url[0] == '/'
           && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}
