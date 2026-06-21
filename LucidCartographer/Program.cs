using LucidCartographer.Components;
using LucidCartographer.Configuration;
using LucidCartographer.Endpoints;
using LucidCartographer.Services;
using LucidCartographer.Services.Diagnostics;

// Diagnostic console tee: portable path by default; override via SCRAPE_DIAG_LOG.
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
    .AddDeduplicationPipeline(builder.Configuration)
    .AddTripServices(builder.Configuration)
    .AddBrowserSession(builder.Configuration)
    .AddExportPipeline()
    .AddAppAuthentication(builder.Configuration, builder.Environment)
    .AddAppResiliencePipelines()
    .AddPageViewModels()
    .AddMcpServerServices()
    .AddOAuthFrontdoor(builder.Configuration, builder.Environment)
    .AddHealthChecks().Services
    .AddHostedService<StartupCleanupService>();

var app = builder.Build();

// Log unobserved task exceptions to prevent process crashes.
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    var logger = app.Services.GetService<ILogger<Program>>();
    logger?.LogError(e.Exception, "Unobserved task exception");
    e.SetObserved();
};

// Forwarded headers MUST run first — before HTTPS redirection, auth, and the
// OAuth/OpenIddict endpoints — so the request scheme is rewritten to https from
// the tunnel's X-Forwarded-Proto before any middleware reads it. Otherwise
// OpenIddict rejects the (apparently http) request with "only accepts HTTPS".
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // HSTS: instruct browsers to only connect over HTTPS.
    app.UseHsts();
    // Defense-in-depth: redirect direct-access requests to HTTPS.
    app.UseHttpsRedirection();
}

app.UseSecurityHeaders();

// Response compression placed AFTER security headers to avoid BREACH issues.
// Skipped in Development for BrowserLink hot-reload compatibility.
if (!app.Environment.IsDevelopment())
{
    app.UseResponseCompression();
}

// Raw WebSocket support for the noVNC reverse proxy (Google session remote view).
// Harmless when the proxy is disabled; Blazor's SignalR transport is unaffected.
app.UseWebSockets();

app.UseAuthentication();
app.UseAuthorization();
app.UseLanBypassOrAuth();
app.UseAntiforgery();
app.UseRateLimiter();
app.UseStaticFiles();

app.MapHealthChecks("/health");
app.MapAuthEndpoints();
app.MapOAuthEndpoints();
app.MapPoiImageEndpoints();
app.MapDocsEndpoints(); // Includes /docs/osrm.md for Trip View OSRM setup instructions.

// noVNC proxy for Google sign-in remote view; Docker/Linux only; gated by cookie auth.
app.MapNoVncProxy();

// MCP endpoint for external agents. No antiforgery (JSON-RPC); McpApiKeyFilter enforces auth.
// Exempt from UseLanBypassOrAuth cookie redirect; see AuthRouteGuardExtensions.
app.MapMcp("/mcp")
    .DisableAntiforgery()
    .AddEndpointFilter<IEndpointConventionBuilder, McpApiKeyFilter>();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();

// Make Program accessible for WebApplicationFactory in integration tests
public partial class Program;
