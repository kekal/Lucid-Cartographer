using LucidCartographer.Components;
using LucidCartographer.Configuration;
using LucidCartographer.Endpoints;
using LucidCartographer.Services;
using LucidCartographer.Services.Diagnostics;

// Diagnostic console tee — defaults under the app's bin directory so the path
// is portable across machines. Override via SCRAPE_DIAG_LOG env var if needed.
DiagnosticLogging.TeeConsoleToFile(
    Environment.GetEnvironmentVariable("SCRAPE_DIAG_LOG")
        ?? Path.Combine(AppContext.BaseDirectory, "scrape-diag.log"));

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddRazorAndCompression()
    .AddAppDatabase(builder.Configuration, builder.Environment)
    .AddPoiServices()
    .AddImportPipeline()
    .AddEnrichmentPipeline(builder.Configuration)
    .AddExportPipeline()
    .AddAppAuthentication(builder.Configuration)
    .AddAppResiliencePipelines()
    .AddPageViewModels()
    .AddMcpServerServices()
    .AddHealthChecks().Services
    .AddHostedService<StartupCleanupService>();

var app = builder.Build();

// ARCH-LOW-07: Log unobserved task exceptions instead of letting them crash the process
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    var logger = app.Services.GetService<ILogger<Program>>();
    logger?.LogError(e.Exception, "Unobserved task exception");
    e.SetObserved();
};

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // HSTS: instruct browsers to only connect over HTTPS. Safe behind the
    // TLS-terminating proxy (Cloudflare / Zero Trust) since the edge is HTTPS.
    app.UseHsts();
    // ARCH-HIGH-05: Defense-in-depth — UseHttpsRedirection ensures
    // direct-access requests are also redirected even though Cloudflare
    // already terminates TLS at the edge.
    app.UseHttpsRedirection();
}

app.UseForwardedHeaders();
app.UseSecurityHeaders();

// ARCH-HIGH-06: Response compression placed AFTER security headers to avoid BREACH issues.
// Skipped in Development so `dotnet watch` browser-refresh / BrowserLink can inject
// their hot-reload script into uncompressed HTML responses.
if (!app.Environment.IsDevelopment())
{
    app.UseResponseCompression();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseLanBypassOrAuth();
app.UseAntiforgery();
app.UseRateLimiter();
app.UseStaticFiles();

app.MapHealthChecks("/health"); // MED-01
app.MapAuthEndpoints();
app.MapPoiImageEndpoints();

// MCP endpoint for external agents (Claude Code). DisableAntiforgery because the
// JSON-RPC POSTs carry no antiforgery token; McpApiKeyFilter enforces auth
// (loopback/LAN bypass, else MCP_API_KEY). /mcp is exempt from UseLanBypassOrAuth's
// cookie redirect (see AuthRouteGuardExtensions).
app.MapMcp("/mcp")
    .DisableAntiforgery()
    .AddEndpointFilter<IEndpointConventionBuilder, McpApiKeyFilter>();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();

// Make Program accessible for WebApplicationFactory in integration tests
public partial class Program;
