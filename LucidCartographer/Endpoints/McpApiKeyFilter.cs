using System.Security.Cryptography;
using System.Text;
using LucidCartographer.Configuration;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Validation.AspNetCore;

namespace LucidCartographer.Endpoints;

/// <summary>
/// Endpoint filter guarding the MCP endpoint. A request is allowed if any of:
///   1. it comes from loopback/LAN and the bypass is enabled
///      (<c>Mcp:AllowLocalNetworkBypass</c>, off in Production);
///   2. it presents the static API key (<c>Authorization: Bearer &lt;key&gt;</c> or
///      <c>X-Api-Key</c>) — for Claude Code / scripts;
///   3. it presents a valid OAuth access token issued by this app's frontdoor —
///      for Claude.ai connectors (validated in-process by OpenIddict).
/// Otherwise it returns 401 with a <c>WWW-Authenticate</c> header pointing at the
/// protected-resource metadata so an OAuth-capable client can start the flow.
/// </summary>
public sealed class McpApiKeyFilter(IConfiguration configuration, ILogger<McpApiKeyFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // 1. Local machine / LAN — trusted, no credential needed, unless the bypass
        //    is disabled (Production), where RFC1918 peers are the proxy/tunnel.
        var allowLocalBypass = configuration.GetValue("Mcp:AllowLocalNetworkBypass", true);
        if (allowLocalBypass && AuthRouteGuardExtensions.IsLocalNetwork(http.Connection.RemoteIpAddress))
        {
            return await next(context);
        }

        // 2. Static API key. Env var MCP_API_KEY wins, then the Mcp:ApiKey config.
        var configuredKey = Environment.GetEnvironmentVariable("MCP_API_KEY");
        if (string.IsNullOrEmpty(configuredKey))
        {
            configuredKey = configuration["Mcp:ApiKey"];
        }
        if (!string.IsNullOrEmpty(configuredKey))
        {
            var presented = ExtractKey(http);
            if (presented is not null && FixedTimeEquals(presented, configuredKey))
            {
                return await next(context);
            }
        }

        // 3. OAuth bearer token, validated in-process by the OpenIddict frontdoor.
        //    Skipped cleanly when the frontdoor is disabled (scheme not registered)
        //    or in unit tests (no request services).
        if (await TryAuthenticateOAuthAsync(http))
        {
            return await next(context);
        }

        // 4. Unauthorized. Point OAuth-capable clients at the protected-resource
        //    metadata so they can discover the authorization server and sign in.
        logger.LogWarning(
            "Rejected MCP request from {RemoteIp}: no LAN bypass, API key, or OAuth token.",
            http.Connection.RemoteIpAddress);

        http.Response.Headers.WWWAuthenticate =
            $"Bearer resource_metadata=\"{http.Request.Scheme}://{http.Request.Host}/.well-known/oauth-protected-resource\"";
        return Results.Text(
            "Unauthorized: present a valid MCP API key or OAuth access token.",
            statusCode: StatusCodes.Status401Unauthorized);
    }

    private async Task<bool> TryAuthenticateOAuthAsync(HttpContext http)
    {
        var schemeProvider = http.RequestServices?.GetService<IAuthenticationSchemeProvider>();
        if (schemeProvider is null ||
            await schemeProvider.GetSchemeAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme) is null)
        {
            return false;
        }

        var hasBearer = http.Request.Headers.Authorization.ToString()
            .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

        var result = await http.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        if (result.Succeeded && result.Principal is not null)
        {
            http.User = result.Principal;
            return true;
        }

        // Diagnostics: distinguish "client sent no token" (token exchange likely
        // failed upstream) from "token present but rejected" (and why).
        if (hasBearer)
        {
            logger.LogWarning(
                "OAuth bearer token present but validation failed: {Reason}",
                result.Failure?.Message ?? "no principal returned");
        }
        else
        {
            logger.LogInformation("No OAuth bearer token on the MCP request.");
        }

        return false;
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
