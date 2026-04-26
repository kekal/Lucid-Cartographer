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
        // Login endpoint with rate limiting + CSRF validation.
        endpoints.MapPost("/login", async context =>
        {
            // ARCH-CRIT-03: Validate antiforgery token on login POST
            var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.Redirect("/login?error=1");
                return;
            }

            var form = await context.Request.ReadFormAsync();
            var username = form["username"].ToString();
            var password = form["password"].ToString();
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
                context.Response.Redirect("/");
            }
            else
            {
                context.Response.Redirect("/login?error=1");
            }
        }).RequireRateLimiting("login");

        // Logout endpoint
        endpoints.MapGet("/logout", async context =>
        {
            var sessionToken = context.User.FindFirstValue("session_token");
            if (!string.IsNullOrWhiteSpace(sessionToken))
            {
                var store = context.RequestServices.GetRequiredService<SessionStore>();
                await store.RevokeAsync(sessionToken, context.RequestAborted);
            }

            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            context.Response.Redirect("/login");
        });

        return endpoints;
    }
}
