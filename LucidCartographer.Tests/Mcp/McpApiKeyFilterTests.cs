using System.Net;
using FluentAssertions;
using LucidCartographer.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests.Mcp;

/// <summary>
/// Unit tests for the MCP endpoint auth filter: loopback/LAN is trusted,
/// everything else needs the configured API key. Covers the 401 paths that
/// can't be reproduced over a real loopback HTTP connection.
/// </summary>
public sealed class McpApiKeyFilterTests : IDisposable
{
    private readonly string? _originalEnvKey;

    public McpApiKeyFilterTests()
    {
        // The filter reads MCP_API_KEY first; clear it so tests exercise the
        // configuration path deterministically. Restored in Dispose.
        _originalEnvKey = Environment.GetEnvironmentVariable("MCP_API_KEY");
        Environment.SetEnvironmentVariable("MCP_API_KEY", null);
    }

    public void Dispose() => Environment.SetEnvironmentVariable("MCP_API_KEY", _originalEnvKey);

    [Fact]
    public async Task Loopback_IsAllowed_WithoutKey()
    {
        var (nextCalled, status) = await InvokeAsync(Filter(configuredKey: null), Context("127.0.0.1"));
        nextCalled.Should().BeTrue();
        status.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task LanAddress_IsAllowed_WithoutKey()
    {
        var (nextCalled, _) = await InvokeAsync(Filter(configuredKey: null), Context("192.168.1.50"));
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Remote_WithNoConfiguredKey_Is401()
    {
        var (nextCalled, status) = await InvokeAsync(Filter(configuredKey: null), Context("8.8.8.8"));
        nextCalled.Should().BeFalse();
        status.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Remote_WithCorrectBearerKey_IsAllowed()
    {
        var (nextCalled, _) = await InvokeAsync(
            Filter(configuredKey: "s3cret"),
            Context("8.8.8.8", authHeader: "Bearer s3cret"));
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Remote_WithCorrectApiKeyHeader_IsAllowed()
    {
        var (nextCalled, _) = await InvokeAsync(
            Filter(configuredKey: "s3cret"),
            Context("8.8.8.8", apiKeyHeader: "s3cret"));
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Remote_WithWrongKey_Is401()
    {
        var (nextCalled, status) = await InvokeAsync(
            Filter(configuredKey: "s3cret"),
            Context("8.8.8.8", authHeader: "Bearer nope"));
        nextCalled.Should().BeFalse();
        status.Should().Be(StatusCodes.Status401Unauthorized);
    }

    private static McpApiKeyFilter Filter(string? configuredKey)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Mcp:ApiKey"] = configuredKey })
            .Build();
        return new McpApiKeyFilter(config, NullLogger<McpApiKeyFilter>.Instance);
    }

    private static DefaultHttpContext Context(string ip, string? authHeader = null, string? apiKeyHeader = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        ctx.Response.Body = new MemoryStream();
        if (authHeader is not null)
        {
            ctx.Request.Headers.Authorization = authHeader;
        }
        if (apiKeyHeader is not null)
        {
            ctx.Request.Headers["X-Api-Key"] = apiKeyHeader;
        }
        return ctx;
    }

    private static async Task<(bool nextCalled, int status)> InvokeAsync(McpApiKeyFilter filter, HttpContext ctx)
    {
        var nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>("OK");
        };

        var efic = EndpointFilterInvocationContext.Create(ctx);
        var result = await filter.InvokeAsync(efic, next);

        // On the deny path the filter returns an IResult carrying the status
        // code instead of calling next(). Read the code off the result directly
        // (executing it would require a populated RequestServices).
        if (!nextCalled)
        {
            var status = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
            return (false, status);
        }

        return (true, StatusCodes.Status200OK);
    }
}
