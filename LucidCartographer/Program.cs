using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Coravel;
using LucidCartographer.Components;
using LucidCartographer.Data;
using LucidCartographer.Services;
using LucidCartographer.Services.Enrichment;
using LucidCartographer.Services.Export;
using LucidCartographer.Services.Import;
using LucidCartographer.Services.Operations;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.Retry;

// DIAG: tee Console to a log file so scrape logs are readable from outside preview_start
try
{
    var _diagLog = new StreamWriter(@"C:\backup\maps_editor\LucidCartographer\scrape-diag.log", append: false) { AutoFlush = true };
    Console.SetOut(new MultiTextWriter(Console.Out, _diagLog));
    Console.SetError(new MultiTextWriter(Console.Error, _diagLog));
}
catch { }

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
// Background import pipeline (Coravel). User clicks Import -> job is enqueued
// via IImportJobQueue -> Coravel's scheduler runs it on a background thread
// inside its own DI scope, decoupled from the Blazor circuit. The user is
// free to navigate away; ImportJobStatusService publishes lifecycle events
// the UI subscribes to.
builder.Services.AddQueue();
builder.Services.AddSingleton<ImportJobStatusService>();
builder.Services.AddTransient<ImportInvocable>();
builder.Services.AddSingleton<IImportJobQueue, CoravelImportJobQueue>();
builder.Services.AddSingleton<IFileExporter, KmlExporter>();
builder.Services.AddSingleton<IFileExporter, GpxExporter>();
// ARCH-HIGH-01: Removed duplicate concrete KmlExporter registration — use IEnumerable<IFileExporter> instead
builder.Services.AddScoped<IPoiMatcher, PoiMatcher>();
builder.Services.AddScoped<ISetOperationService, SetOperationService>();
// HIGH-07: Scraper registered as Singleton with internal SemaphoreSlim to limit concurrency
builder.Services.AddSingleton<IGoogleMapsListScraper, GoogleMapsListScraper>();
// Background enrichment: fills address/website/phone for Google-scraped Pois
// by opening each place URL in a headless tab. Runs continuously, polling the
// DB for IsEnriched=false rows. Progress service is a singleton the MapPage
// subscribes to for its "N pending" counter.
builder.Services.AddSingleton<EnrichmentProgressService>();
builder.Services.AddSingleton<EnrichmentTrigger>();
builder.Services.AddHttpClient();
// Tunable via the "Enrichment" section of appsettings.json — Concurrency,
// BatchSize, IdlePollSeconds. Defaults match the hard-coded values the
// service used before extraction, so an upgrade without config changes
// behaves identically.
builder.Services.Configure<EnrichmentOptions>(builder.Configuration.GetSection("Enrichment"));
builder.Services.AddHostedService<PoiEnrichmentBackgroundService>();
builder.Services.AddScoped<IMapService, LeafletMapService>();

// Polly v8 resilience pipelines.
// Replaces hand-rolled SemaphoreSlim in GoogleMapsListScraper and adds
// retry/timeout to Playwright-based scraping + enrichment. Pipelines are
// registered by name and resolved via ResiliencePipelineProvider<string>.
//   - "scraper": single-flight (concurrency=1) + timeout + retry. Used for
//     list scrapes so at most one Chromium instance runs at a time.
//   - "enrichment": retry + timeout for per-POI enrichment work.
builder.Services.AddResiliencePipeline("scraper", pipeline =>
{
    pipeline
        .AddConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = 1,
            QueueLimit = int.MaxValue
        })
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 2,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromSeconds(2)
        })
        .AddTimeout(TimeSpan.FromMinutes(10));
});

builder.Services.AddResiliencePipeline("enrichment", pipeline =>
{
    pipeline
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromSeconds(1)
        })
        .AddTimeout(TimeSpan.FromMinutes(2));
});

// BCL rate limiter — replaces hand-rolled ConcurrentDictionary counter.
// 5 attempts per minute per client IP, partitioned fixed window.
// Semantic drift: counts ALL attempts (success + fail), not just failures.
// Acceptable for a single-user app — brute-force protection still holds.
builder.Services.AddRateLimiter(options =>
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

// Sweep orphaned lucid-import-* temp files left by a previous crash that
// died between "file streamed to disk" and "Coravel invocable ran + deleted
// it in finally". Cheap and safe: only files matching the specific pattern
// we wrote ourselves, older than 1h, are removed.
{
    var sweepLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TempFileSweep");
    try
    {
        var tempRoot = Path.GetTempPath();
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
        int swept = 0;
        foreach (var path in Directory.EnumerateFiles(tempRoot, "lucid-import-*"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                    swept++;
                }
            }
            catch (Exception ex)
            {
                sweepLogger.LogDebug(ex, "Could not remove orphaned temp file {Path}", path);
            }
        }
        if (swept > 0)
            sweepLogger.LogInformation("Removed {Count} orphaned lucid-import-* temp files from {Path}", swept, tempRoot);
    }
    catch (Exception ex)
    {
        sweepLogger.LogWarning(ex, "Startup temp-file sweep failed; continuing");
    }
}

// NEW-02: Warn when Auth:Password is empty — authentication is silently disabled
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<Program>();
    if (string.IsNullOrEmpty(app.Configuration["Auth:Password"]))
        logger.LogWarning("Auth:Password not set — authentication is DISABLED");
}

// ARCH-CRIT-01: Use MigrateAsync instead of EnsureCreatedAsync to support schema evolution.
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

// ARCH-HIGH-06: Response compression placed AFTER security headers to avoid BREACH issues.
// Skipped in Development so `dotnet watch` browser-refresh and BrowserLink can inject
// their hot-reload script into uncompressed HTML responses — compressed responses cause
// "Unable to configure browser refresh script injection ... Content-Encoding: 'br'" warnings.
if (!app.Environment.IsDevelopment())
{
    app.UseResponseCompression();
}

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

app.UseRateLimiter();

app.UseStaticFiles();

// Health check endpoint (MED-01)
app.MapHealthChecks("/health");

// ARCH-CRIT-03: Login endpoint with rate limiting (BCL Microsoft.AspNetCore.RateLimiting),
// CSRF validation, constant-time comparison, Secure cookie.
// Rate limit: 5 attempts/min/IP via the "login" policy registered above.
// Counts ALL attempts (successful + failed) — acceptable semantic drift for
// a single-user app; brute-force protection still holds.
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
        context.Response.Redirect("/login?error=1");
    }
}).RequireRateLimiting("login");

// CRIT-03: Logout endpoint
app.MapGet("/logout", (HttpContext context) =>
{
    context.Response.Cookies.Delete("cartographer_auth");
    context.Response.Redirect("/login");
});

// Serves image bytes stored in the Poi.ImageData column. Used by
// PoiDetailPane to render scraped Google Maps thumbnails — we persist the
// bytes rather than hotlinking the signed googleusercontent URLs (which
// Google blocks cross-origin and expires in ~minutes). Auth middleware
// above gates this endpoint behind the same cookie as the rest of the app.
app.MapGet("/api/poi-image/{id:int}", async (int id, IDbContextFactory<AppDbContext> dbFactory) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var image = await db.PoiImages
        .AsNoTracking()
        .FirstOrDefaultAsync(i => i.PoiId == id);
    if (image is null || image.Data.Length == 0)
        return Results.NotFound();
    return Results.File(image.Data, image.ContentType ?? "image/jpeg");
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

internal sealed class MultiTextWriter : System.IO.TextWriter
{
    private readonly System.IO.TextWriter[] _writers;
    public MultiTextWriter(params System.IO.TextWriter[] writers) { _writers = writers; }
    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    public override void Write(char value) { foreach (var w in _writers) w.Write(value); }
    public override void Write(string? value) { foreach (var w in _writers) w.Write(value); }
    public override void WriteLine(string? value) { foreach (var w in _writers) w.WriteLine(value); }
    public override void Flush() { foreach (var w in _writers) w.Flush(); }
}
