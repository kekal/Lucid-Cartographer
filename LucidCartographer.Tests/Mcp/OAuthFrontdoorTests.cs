using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LucidCartographer.Configuration;
using LucidCartographer.Data;
using LucidCartographer.Endpoints;
using LucidCartographer.Services;
using LucidCartographer.Services.Enrichment;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace LucidCartographer.Tests.Mcp;

/// <summary>
/// Boots a host with the OAuth frontdoor enabled (OpenIddict server + validation,
/// custom DCR, MCP resource-server metadata) and drives it over real HTTP.
/// Verifies the discovery document, the protected-resource metadata, dynamic
/// client registration, and that /mcp challenges unauthenticated callers.
/// Does not attempt the full interactive OAuth dance (that needs a browser/Claude).
/// </summary>
public sealed class OAuthFrontdoorTests : IAsyncLifetime
{
    private const string Issuer = "http://127.0.0.1:53217";
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string? _originalEnvKey;

    public async Task InitializeAsync()
    {
        _originalEnvKey = Environment.GetEnvironmentVariable("MCP_API_KEY");
        Environment.SetEnvironmentVariable("MCP_API_KEY", null);

        _dbPath = Path.Combine(Path.GetTempPath(), $"oauth_test_{Guid.NewGuid():N}.db");

        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = "Development"; // allows http (DisableTransportSecurityRequirement)
        builder.WebHost.UseUrls(Issuer);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OAuth:Issuer"] = Issuer,
            // Require a credential even from loopback so the /mcp challenge path runs.
            ["Mcp:AllowLocalNetworkBypass"] = "false"
        });

        builder.Services.AddSingleton(new Mock<IPoiService>().Object);
        builder.Services.AddSingleton<EnrichmentTrigger>();
        builder.Services.AddSingleton<EnrichmentProgressService>();
        builder.Services.AddDbContextFactory<AppDbContext>(o =>
            o.UseSqlite($"Data Source={_dbPath}").UseOpenIddict());
        builder.Services.AddScoped<AppDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
        builder.Services.AddMcpServerServices();
        builder.Services.AddOAuthFrontdoor(builder.Configuration, builder.Environment);
        // Cookie scheme so /connect/authorize's interactive-login passthrough has a
        // handler to challenge (the real app registers it via AddAppAuthentication).
        builder.Services.AddAuthentication().AddCookie();
        builder.Services.AddAuthorization();

        _app = builder.Build();

        using (var scope = _app.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
        }

        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapMcp("/mcp")
            .DisableAntiforgery()
            .AddEndpointFilter<IEndpointConventionBuilder, McpApiKeyFilter>();
        _app.MapOAuthEndpoints();

        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(Issuer) };
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("MCP_API_KEY", _originalEnvKey);
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        try { File.Delete(_dbPath); } catch { /* best effort */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* best effort */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Discovery_AdvertisesEndpoints_AndCustomRegistration()
    {
        var root = await GetJsonAsync("/.well-known/oauth-authorization-server");

        root.GetProperty("authorization_endpoint").GetString().Should().Contain("/connect/authorize");
        root.GetProperty("token_endpoint").GetString().Should().Contain("/connect/token");
        root.GetProperty("registration_endpoint").GetString().Should().Contain("/connect/register");
        root.GetProperty("code_challenge_methods_supported").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("S256");
    }

    [Fact]
    public async Task ProtectedResourceMetadata_PointsAtAuthorizationServer()
    {
        var root = await GetJsonAsync("/.well-known/oauth-protected-resource");

        // The resource identifier is the canonical /mcp endpoint URL (must match
        // the value registered via options.RegisterResources so OpenIddict accepts
        // the RFC 8707 `resource` parameter Claude derives from this metadata).
        root.GetProperty("resource").GetString().Should().Be(Issuer + "/mcp");
        root.GetProperty("authorization_servers").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain(Issuer);

        // "header" must not be advertised twice (regression: it was set both by
        // the SDK default and explicitly).
        if (root.TryGetProperty("bearer_methods_supported", out var methods))
        {
            methods.EnumerateArray().Select(e => e.GetString()).Should().Equal("header");
        }
    }

    [Fact]
    public async Task Register_CreatesPublicClient_ForDynamicRegistration()
    {
        var resp = await _client.PostAsJsonAsync("/connect/register", new
        {
            redirect_uris = new[] { "https://claude.ai/api/mcp/auth_callback" },
            client_name = "Test Connector",
            token_endpoint_auth_method = "none"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var root = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        root.GetProperty("client_id").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("token_endpoint_auth_method").GetString().Should().Be("none");
    }

    [Fact]
    public async Task Mcp_WithoutToken_Challenges_WithResourceMetadata()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        req.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json");

        using var resp = await _client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resp.Headers.WwwAuthenticate.ToString().Should().Contain("resource_metadata");
    }

    [Fact]
    public async Task Authorize_WithMcpResource_NotRejectedAsInvalidTarget()
    {
        var clientId = await RegisterClientAsync();

        using var resp = await SendAuthorizeAsync(clientId, Issuer + "/mcp");
        var body = await resp.Content.ReadAsStringAsync();

        // A valid resource passes OpenIddict's RFC 8707 validation and falls
        // through to the interactive login challenge (302 to the cookie login
        // path) — it must NOT come back as invalid_target.
        var combined = $"status={(int)resp.StatusCode} location={resp.Headers.Location} body={body}";
        combined.Should().NotContain("invalid_target");
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect, because: combined);
    }

    [Fact]
    public async Task Authorize_WithUnknownResource_RejectedAsInvalidTarget()
    {
        var clientId = await RegisterClientAsync();

        using var resp = await SendAuthorizeAsync(clientId, "https://attacker.example/resource");
        var body = await resp.Content.ReadAsStringAsync();

        // An unregistered resource is rejected as invalid_target (returned directly
        // since it fails request validation before the redirect stage).
        body.Should().Contain("invalid_target");
    }

    private async Task<string> RegisterClientAsync()
    {
        var resp = await _client.PostAsJsonAsync("/connect/register", new
        {
            redirect_uris = new[] { "https://claude.ai/api/mcp/auth_callback" },
            client_name = "Test Connector",
            token_endpoint_auth_method = "none"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("client_id").GetString()!;
    }

    private async Task<HttpResponseMessage> SendAuthorizeAsync(string clientId, string resource)
    {
        using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(Issuer)
        };
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = "https://claude.ai/api/mcp/auth_callback",
            ["scope"] = "openid mcp",
            ["code_challenge"] = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            ["code_challenge_method"] = "S256",
            ["resource"] = resource
        };
        var url = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString("/connect/authorize", query);
        return await noRedirect.GetAsync(url);
    }

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        using var resp = await _client.GetAsync(path);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
