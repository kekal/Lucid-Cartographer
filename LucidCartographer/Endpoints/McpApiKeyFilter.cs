using System.Security.Cryptography;
using System.Text;
using LucidCartographer.Configuration;

namespace LucidCartographer.Endpoints;

/// <summary>
/// Endpoint filter guarding the MCP endpoint. Loopback/LAN requests are allowed
/// without a key (so local Claude Code works with zero config). Any other origin
/// must present the configured key via <c>Authorization: Bearer &lt;key&gt;</c>
/// or <c>X-Api-Key: &lt;key&gt;</c>. If no key is configured, remote access is
/// refused (fail-closed).
/// </summary>
public sealed class McpApiKeyFilter(IConfiguration configuration, ILogger<McpApiKeyFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // 1. Local machine / LAN — trusted, no key needed.
        if (AuthRouteGuardExtensions.IsLocalNetwork(http.Connection.RemoteIpAddress))
        {
            return await next(context);
        }

        // 2. Remote — require the configured key. Env var MCP_API_KEY wins,
        //    then the Mcp:ApiKey configuration value.
        var configuredKey = Environment.GetEnvironmentVariable("MCP_API_KEY");
        if (string.IsNullOrEmpty(configuredKey))
        {
            configuredKey = configuration["Mcp:ApiKey"];
        }

        if (string.IsNullOrEmpty(configuredKey))
        {
            logger.LogWarning(
                "Rejected remote MCP request from {RemoteIp}: no MCP API key is configured (set MCP_API_KEY).",
                http.Connection.RemoteIpAddress);
            return Results.Text("Unauthorized: MCP API key is not configured; remote access is disabled.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var presented = ExtractKey(http);
        if (presented is not null && FixedTimeEquals(presented, configuredKey))
        {
            return await next(context);
        }

        logger.LogWarning("Rejected remote MCP request from {RemoteIp}: missing or invalid API key.", http.Connection.RemoteIpAddress);
        return Results.Text("Unauthorized: missing or invalid MCP API key.", statusCode: StatusCodes.Status401Unauthorized);
    }

    private static string? ExtractKey(HttpContext http)
    {
        var auth = http.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return auth["Bearer ".Length..].Trim();
        }

        var apiKey = http.Request.Headers["X-Api-Key"].ToString();
        return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b));
}
