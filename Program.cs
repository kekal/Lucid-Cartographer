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

// MED-06: DB path from configuration instead of manual env-var sniffing
var dbPath = builder.Configuration.GetValue<string>("Database:Path")
    ?? (builder.Environment.IsProduction() ? "/data/cartographer.db" : "data/cartographer.db");
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddScoped<IPoiService, PoiService>();
builder.Services.AddScoped<IFileImporter, GpxImporter>();
builder.Services.AddScoped<IFileImporter, KmlImporter>();
builder.Services.AddScoped<IFileImporter, GeoJsonImporter>();
builder.Services.AddScoped<IFileImporter, CsvImporter>();
builder.Services.AddScoped<IImportOrchestrator, ImportOrchestrator>();
builder.Services.AddSingleton<IFileExporter, KmlExporter>();
builder.Services.AddSingleton<IFileExporter, GpxExporter>();
builder.Services.AddSingleton<KmlExporter>();
builder.Services.AddScoped<IPoiMatcher, PoiMatcher>();
builder.Services.AddScoped<ISetOperationService, SetOperationService>();
// HIGH-07: Scraper registered as Singleton with internal SemaphoreSlim to limit concurrency
builder.Services.AddSingleton<IGoogleMapsListScraper, GoogleMapsListScraper>();
builder.Services.AddScoped<IMapService, LeafletMapService>();

// Health checks endpoint
builder.Services.AddHealthChecks();

var app = builder.Build();

// TODO [CRIT-04]: Replace EnsureCreatedAsync with EF Core migrations.
// EnsureCreatedAsync only creates the schema once and silently ignores any subsequent
// schema changes (new columns, indexes, etc.). Since Microsoft.EntityFrameworkCore.Design
// is already referenced, generate migrations with:
//   dotnet ef migrations add InitialCreate
//   dotnet ef database update
// Then replace the block below with: await db.Database.MigrateAsync();
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// MED-04: Enable response compression
app.UseResponseCompression();

// HIGH-06: Content Security Policy header
// NOTE: The Tailwind CDN play script and external CDNs require 'unsafe-eval' and loose
// script-src for now. Once CRIT-01 (self-host Tailwind) and HIGH-05 (self-host Leaflet/fonts)
// are completed, tighten to script-src 'self' only.
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("Content-Security-Policy",
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' 'unsafe-eval' https://cdn.tailwindcss.com https://unpkg.com; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://unpkg.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        "img-src 'self' data: https://*.tile.openstreetmap.org https://*.googleapis.com https://*.gstatic.com; " +
        "connect-src 'self' ws: wss:; " +
        "frame-ancestors 'none';");
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    await next();
});

// CRIT-03: Simple password + cookie authentication middleware
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
        if (cookieValue != expectedHash)
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

// CRIT-03: Login endpoint (handles form POST from login page)
app.MapPost("/login", async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    var password = form["password"].ToString();
    var expectedPassword = context.RequestServices.GetRequiredService<IConfiguration>()["Auth:Password"];

    if (!string.IsNullOrEmpty(expectedPassword) && password == expectedPassword)
    {
        context.Response.Cookies.Append("cartographer_auth", ComputeHash(password), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            MaxAge = TimeSpan.FromDays(30),
            IsEssential = true
        });
        context.Response.Redirect("/");
    }
    else
    {
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
