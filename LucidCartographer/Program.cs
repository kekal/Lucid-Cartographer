using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LucidCartographer.Components;
using LucidCartographer.Data;
using LucidCartographer.Services;
using LucidCartographer.Services.Export;
using LucidCartographer.Services.Import;
using LucidCartographer.Services.Operations;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// MED-04: Response compression for Blazor SignalR and static files
builder.Services.AddResponseCompression(opts =>
{
    opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        new[] { "application/octet-stream" }); // SignalR uses octet-stream
});

// MED-06 / OS-independent DB path resolution.
// Precedence:
//   1. DB_PATH environment variable (simple override for Docker/cloud)
//   2. Database:Path from configuration (also honours Database__Path env var)
//   3. Default "data/cartographer.db" relative to ContentRootPath
// Relative paths are resolved against ContentRootPath so the process does not depend
// on the current working directory. The containing directory is created if missing.
var dbPath = ResolveDbPath(builder.Configuration, builder.Environment);
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

static string ResolveDbPath(IConfiguration cfg, IHostEnvironment env)
{
    var raw = Environment.GetEnvironmentVariable("DB_PATH");
    if (string.IsNullOrWhiteSpace(raw))
        raw = cfg.GetValue<string>("Database:Path");
    if (string.IsNullOrWhiteSpace(raw))
        raw = Path.Combine("data", "cartographer.db");

    var full = Path.IsPathRooted(raw)
        ? raw
        : Path.GetFullPath(Path.Combine(env.ContentRootPath, raw));

    var dir = Path.GetDirectoryName(full);
    if (!string.IsNullOrEmpty(dir))
        Directory.CreateDirectory(dir);
    return full;
}
builder.Services.AddScoped<IPoiService, PoiService>();
// ARCH-HIGH-02: Importers are stateless parsers — register as Singleton (consistent with exporters)
builder.Services.AddSingleton<IFileImporter, GpxImporter>();
builder.Services.AddSingleton<IFileImporter, KmlImporter>();
builder.Services.AddSingleton<IFileImporter, GeoJsonImporter>();
builder.Services.AddSingleton<IFileImporter, CsvImporter>();
builder.Services.AddScoped<IImportOrchestrator, ImportOrchestrator>();
builder.Services.AddSingleton<IFileExporter, KmlExporter>();
builder.Services.AddSingleton<IFileExporter, GpxExporter>();
// ARCH-HIGH-01: Removed duplicate concrete KmlExporter registration — use IEnumerable<IFileExporter> instead
builder.Services.AddScoped<IPoiMatcher, PoiMatcher>();
builder.Services.AddScoped<ISetOperationService, SetOperationService>();
// HIGH-07: Scraper registered as Singleton with internal SemaphoreSlim to limit concurrency
builder.Services.AddSingleton<IGoogleMapsListScraper, GoogleMapsListScraper>();
builder.Services.AddScoped<IMapService, LeafletMapService>();

// Health checks endpoint
builder.Services.AddHealthChecks();

// ARCH-CRIT-03: Refuse to start with default insecure password
var configuredPassword = builder.Configuration["Auth:Password"];
if (string.Equals(configuredPassword, "changeme", StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "AUTH__PASSWORD is still set to 'changeme'. Set a strong password before starting the application.");
}

var app = builder.Build();

// ARCH-LOW-07: Log unobserved task exceptions instead of letting them crash the process
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    var logger = app.Services.GetService<ILogger<Program>>();
    logger?.LogError(e.Exception, "Unobserved task exception");
    e.SetObserved();
};

// NEW-02: Warn when Auth:Password is empty — authentication is silently disabled
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<Program>();
    if (string.IsNullOrEmpty(app.Configuration["Auth:Password"]))
        logger.LogWarning("Auth:Password not set — authentication is DISABLED");
}

// ARCH-CRIT-01: Use MigrateAsync instead of EnsureCreatedAsync to support schema evolution.
// TODO: Generate initial migration with: dotnet ef migrations add InitialCreate
// If no migrations exist yet, MigrateAsync will create the DB using the model snapshot.
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // ARCH-HIGH-05: Defense-in-depth — app runs behind Cloudflare which terminates TLS,
    // but UseHttpsRedirection ensures direct-access requests are also redirected.
    app.UseHttpsRedirection();
}

// ARCH-CRIT-04: Tightened CSP — removed 'unsafe-eval', specified CDN domains explicitly.
// 'unsafe-inline' for script-src is required by Blazor Server — its SignalR bootstrapper
// injects inline scripts that cannot use nonces/hashes with the current Blazor runtime.
// TODO: Replace 'unsafe-inline' with nonce-based CSP once Blazor Server supports it.
app.Use(async (context, next) =>
{
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

// ARCH-HIGH-06: Response compression placed AFTER security headers to avoid BREACH issues
app.UseResponseCompression();

// ARCH-CRIT-03: Simple password + cookie authentication middleware (hardened).
// LIMITATION: This is homebrew SHA256 cookie auth — no session tokens, no revocation,
// no salting, and no ASP.NET Core Identity. Acceptable for a single-user personal NAS
// tool behind Cloudflare. If this ever becomes multi-user, replace with
// Microsoft.AspNetCore.Authentication.Cookies or ASP.NET Core Identity.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";

    // Allow: login page, static assets, health check, Blazor framework
    if (path == "/login" ||
        path.StartsWith("/_framework") ||
        path.StartsWith("/css") ||
        path.StartsWith("/js") ||
        path.StartsWith("/lib") ||
        path == "/health" ||
        path.StartsWith("/_blazor"))
    {
        await next();
        return;
    }

    // Check auth cookie (skip auth entirely if no password configured)
    var expectedPassword = context.RequestServices.GetRequiredService<IConfiguration>()["Auth:Password"];
    if (!string.IsNullOrEmpty(expectedPassword))
    {
        var cookieValue = context.Request.Cookies["cartographer_auth"];
        var expectedHash = ComputeHash(expectedPassword);
        // ARCH-CRIT-03: Use constant-time comparison to prevent timing attacks
        var cookieBytes = Encoding.UTF8.GetBytes(cookieValue ?? "");
        var expectedBytes = Encoding.UTF8.GetBytes(expectedHash);
        if (cookieBytes.Length != expectedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(cookieBytes, expectedBytes))
        {
            context.Response.Redirect("/login");
            return;
        }
    }

    await next();
});

app.UseAntiforgery();

app.UseStaticFiles();

// Health check endpoint (MED-01)
app.MapHealthChecks("/health");

// ARCH-CRIT-03: Rate limiting for login — in-memory counter per IP (5 attempts per minute)
var _loginAttempts = new ConcurrentDictionary<string, (int count, DateTime windowStart)>();

// ARCH-CRIT-03: Login endpoint with rate limiting, CSRF validation, constant-time comparison, Secure cookie
app.MapPost("/login", async (HttpContext context) =>
{
    // ARCH-CRIT-03: Validate antiforgery token on login POST
    var antiforgery = context.RequestServices.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>();
    try
    {
        await antiforgery.ValidateRequestAsync(context);
    }
    catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException)
    {
        context.Response.Redirect("/login?error=1");
        return;
    }

    // NEW-01: Periodically clean up expired rate-limiter entries to prevent memory leak
    if (_loginAttempts.Count > 1000)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        foreach (var key in _loginAttempts.Keys.ToList())
        {
            if (_loginAttempts.TryGetValue(key, out var entry) && entry.windowStart < cutoff)
                _loginAttempts.TryRemove(key, out _);
        }
    }

    // ARCH-CRIT-03: Rate limiting — block after 5 failed attempts per minute per IP
    var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var now = DateTime.UtcNow;
    var attempt = _loginAttempts.GetOrAdd(clientIp, _ => (0, now));
    if ((now - attempt.windowStart).TotalMinutes >= 1)
    {
        attempt = (0, now);
    }
    if (attempt.count >= 5)
    {
        context.Response.StatusCode = 429;
        await context.Response.WriteAsync("Too many login attempts. Try again later.");
        return;
    }

    var form = await context.Request.ReadFormAsync();
    var password = form["password"].ToString();
    var expectedPassword = context.RequestServices.GetRequiredService<IConfiguration>()["Auth:Password"];

    // ARCH-CRIT-03: Constant-time password comparison
    var passwordBytes = Encoding.UTF8.GetBytes(password);
    var expectedBytes = Encoding.UTF8.GetBytes(expectedPassword ?? "");
    var passwordMatch = !string.IsNullOrEmpty(expectedPassword)
        && passwordBytes.Length == expectedBytes.Length
        && CryptographicOperations.FixedTimeEquals(passwordBytes, expectedBytes);

    if (passwordMatch)
    {
        // Reset rate limit on success
        _loginAttempts.TryRemove(clientIp, out _);

        context.Response.Cookies.Append("cartographer_auth", ComputeHash(password), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            MaxAge = TimeSpan.FromDays(30),
            IsEssential = true,
            Secure = true // ARCH-CRIT-03/HIGH-05: Secure flag — works behind TLS-terminating proxy (Cloudflare)
        });
        context.Response.Redirect("/");
    }
    else
    {
        // Increment failed attempt counter
        _loginAttempts[clientIp] = (attempt.count + 1, attempt.windowStart);
        context.Response.Redirect("/login?error=1");
    }
});

// CRIT-03: Logout endpoint
app.MapGet("/logout", (HttpContext context) =>
{
    context.Response.Cookies.Delete("cartographer_auth");
    context.Response.Redirect("/login");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// CRIT-03: Hash helper for password cookie
static string ComputeHash(string input)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexString(bytes).ToLowerInvariant();
}

// Make Program accessible for WebApplicationFactory in integration tests
public partial class Program { }
