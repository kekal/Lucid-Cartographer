using System.Net.Http;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LucidCartographer.Configuration;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Endpoints;
using LucidCartographer.Services;
using LucidCartographer.Services.Enrichment;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace LucidCartographer.Tests.Mcp;

/// <summary>
/// Spins up a minimal host with just the MCP server wiring (mirroring Program.cs:
/// AddMcpServerServices + MapMcp + McpApiKeyFilter) and drives it over real HTTP
/// with JSON-RPC. Requests originate from loopback so the filter allows them
/// without a key. Verifies the tool set is exposed and that a tool call flows
/// through to the underlying service.
/// </summary>
public sealed class McpEndpointTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _baseUrl = null!;
    private readonly Mock<IPoiService> _poiService = new();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddSingleton(_poiService.Object);
        builder.Services.AddSingleton<EnrichmentTrigger>();
        builder.Services.AddSingleton<EnrichmentProgressService>();
        builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseInMemoryDatabase("mcp-endpoint-tests"));
        builder.Services.AddMcpServerServices();

        _app = builder.Build();
        _app.MapMcp("/mcp")
            .DisableAntiforgery()
            .AddEndpointFilter<IEndpointConventionBuilder, McpApiKeyFilter>();

        await _app.StartAsync();
        _baseUrl = _app.Urls.First().TrimEnd('/');
        _client = new HttpClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task ToolsList_ExposesEveryTool()
    {
        var root = await PostRpcAsync("tools/list");

        var names = root.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();

        names.Should().Contain(new[]
        {
            "list_collections", "list_pois_in_collection", "search_pois", "get_poi", "get_poi_image",
            "create_collection", "create_poi", "move_poi", "copy_poi", "delete_poi",
            "enrich_poi", "enrich_collection", "get_enrichment_status", "set_poi_google_maps_url"
        });
    }

    [Fact]
    public async Task CreateCollectionTool_FlowsThroughToService()
    {
        _poiService
            .Setup(s => s.CreateCollectionAsync("Test From MCP", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PoiCollection { Id = 42, Name = "Test From MCP", Color = "#005bbf" });

        var root = await PostRpcAsync("tools/call", new
        {
            name = "create_collection",
            arguments = new { name = "Test From MCP" }
        });

        var result = root.GetProperty("result");
        if (result.TryGetProperty("isError", out var isError))
        {
            isError.GetBoolean().Should().BeFalse();
        }

        _poiService.Verify(s => s.CreateCollectionAsync("Test From MCP", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetEnrichmentStatusTool_ReturnsSuccessfully()
    {
        var root = await PostRpcAsync("tools/call", new
        {
            name = "get_enrichment_status",
            arguments = new { }
        });

        var result = root.GetProperty("result");
        if (result.TryGetProperty("isError", out var isError))
        {
            isError.GetBoolean().Should().BeFalse();
        }
        result.GetProperty("content").GetArrayLength().Should().BeGreaterThan(0);
    }

    /// <summary>
    /// POSTs a JSON-RPC request to /mcp and parses the SSE "data:" payload that
    /// the Streamable-HTTP transport returns.
    /// </summary>
    private async Task<JsonElement> PostRpcAsync(string method, object? @params = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = method
        };
        if (@params is not null)
        {
            payload["params"] = @params;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/mcp");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();

        // Streamable-HTTP returns SSE framing: "event: message\ndata: {json}\n\n".
        var dataLine = body.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal));
        var json = dataLine is null ? body : dataLine["data:".Length..].Trim();
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
