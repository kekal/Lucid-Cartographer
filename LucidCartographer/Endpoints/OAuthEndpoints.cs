using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using LucidCartographer.Configuration;
using Microsoft.AspNetCore; // OpenIddict HttpContext.GetOpenIddictServerRequest()
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LucidCartographer.Endpoints;

/// <summary>
/// OAuth 2.1 authorization-server endpoints for the MCP frontdoor, built on
/// OpenIddict's pass-through model:
///   - /connect/authorize  — authorization-code + PKCE; reuses the existing
///     cookie login (challenges it when the user isn't signed in).
///   - /connect/token       — code and refresh-token exchange.
///   - /connect/register    — RFC 7591 Dynamic Client Registration (OpenIddict
///     has no built-in DCR), so Claude clients can self-register.
/// These paths are exempt from the cookie LAN-bypass redirect (see
/// AuthRouteGuardExtensions) and from antiforgery (they carry no token).
/// </summary>
public static class OAuthEndpoints
{
    public static IEndpointRouteBuilder MapOAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Cast the single-HttpContext handlers to Delegate so they're treated as
        // route handlers (their Task<IResult> is written to the response) rather
        // than RequestDelegates (which would discard it) — see ASP0016.
        endpoints.MapMethods("/connect/authorize", ["GET", "POST"], (Delegate)AuthorizeAsync).DisableAntiforgery();
        endpoints.MapPost("/connect/token", (Delegate)ExchangeAsync).DisableAntiforgery();
        endpoints.MapPost("/connect/register", RegisterAsync).DisableAntiforgery();
        return endpoints;
    }

    private static async Task<IResult> AuthorizeAsync(HttpContext http)
    {
        var request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        // Require an interactive login via the existing cookie scheme. When the
        // user isn't signed in, bounce through /login and return to /authorize.
        var auth = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (auth is not { Succeeded: true })
        {
            var returnUrl = http.Request.PathBase + http.Request.Path + http.Request.QueryString;
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = returnUrl },
                [CookieAuthenticationDefaults.AuthenticationScheme]);
        }

        var username = auth.Principal!.Identity?.Name
            ?? auth.Principal.FindFirstValue(ClaimTypes.Name)
            ?? "user";

        var identity = new ClaimsIdentity(
            authenticationType: "OpenIddict",
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetClaim(Claims.Subject, username)
                .SetClaim(Claims.Name, username);

        identity.SetScopes(request.GetScopes());
        identity.SetDestinations(GetDestinations);

        return Results.SignIn(
            new ClaimsPrincipal(identity),
            properties: null,
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> ExchangeAsync(HttpContext http)
    {
        var request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            // The principal (with its claim destinations) is restored from the
            // authorization code / refresh token; re-issue tokens from it.
            var result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            if (result.Principal is null)
            {
                return Results.Forbid(
                    authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
            }

            return Results.SignIn(
                result.Principal,
                properties: null,
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        throw new InvalidOperationException("The specified grant type is not supported.");
    }

    private static async Task<IResult> RegisterAsync(
        HttpContext http,
        IOpenIddictApplicationManager applications,
        CancellationToken cancellationToken)
    {
        ClientRegistrationRequest? body;
        try
        {
            body = await http.Request.ReadFromJsonAsync<ClientRegistrationRequest>(cancellationToken);
        }
        catch (Exception)
        {
            return Results.BadRequest(new { error = "invalid_client_metadata" });
        }

        if (body?.RedirectUris is not { Count: > 0 })
        {
            return Results.BadRequest(new
            {
                error = "invalid_redirect_uri",
                error_description = "At least one redirect_uri is required."
            });
        }

        var redirectUris = new List<Uri>(body.RedirectUris.Count);
        foreach (var uri in body.RedirectUris)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                return Results.BadRequest(new
                {
                    error = "invalid_redirect_uri",
                    error_description = $"Invalid redirect_uri: {uri}"
                });
            }
            redirectUris.Add(parsed);
        }

        // Public (PKCE, no secret) unless the client explicitly asks for a secret-based method.
        var isPublic = string.IsNullOrEmpty(body.TokenEndpointAuthMethod)
            || string.Equals(body.TokenEndpointAuthMethod, "none", StringComparison.OrdinalIgnoreCase);

        var clientId = Guid.NewGuid().ToString("N");
        var clientSecret = isPublic ? null : GenerateSecret();

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            ClientType = isPublic ? ClientTypes.Public : ClientTypes.Confidential,
            ConsentType = ConsentTypes.Implicit, // no consent screen; the login is the gate
            DisplayName = body.ClientName,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Email,
                Permissions.Scopes.Profile,
                Permissions.Prefixes.Scope + OAuthFrontdoorExtensions.McpScope
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange }
        };
        foreach (var uri in redirectUris)
        {
            descriptor.RedirectUris.Add(uri);
        }

        await applications.CreateAsync(descriptor, cancellationToken);

        var response = new Dictionary<string, object?>
        {
            ["client_id"] = clientId,
            ["client_id_issued_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["token_endpoint_auth_method"] = isPublic ? "none" : "client_secret_basic",
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["redirect_uris"] = body.RedirectUris,
            ["scope"] = string.Join(' ', "openid", "profile", "email", OAuthFrontdoorExtensions.McpScope)
        };
        if (!string.IsNullOrEmpty(body.ClientName))
        {
            response["client_name"] = body.ClientName;
        }
        if (clientSecret is not null)
        {
            response["client_secret"] = clientSecret;
            response["client_secret_expires_at"] = 0;
        }

        return Results.Json(response, statusCode: StatusCodes.Status201Created);
    }

    private static IEnumerable<string> GetDestinations(Claim claim) => claim.Type switch
    {
        Claims.Name => [Destinations.AccessToken, Destinations.IdentityToken],
        _ => [Destinations.AccessToken]
    };

    private static string GenerateSecret()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private sealed record ClientRegistrationRequest
    {
        [JsonPropertyName("redirect_uris")] public List<string>? RedirectUris { get; init; }
        [JsonPropertyName("client_name")] public string? ClientName { get; init; }
        [JsonPropertyName("token_endpoint_auth_method")] public string? TokenEndpointAuthMethod { get; init; }
        [JsonPropertyName("grant_types")] public List<string>? GrantTypes { get; init; }
        [JsonPropertyName("response_types")] public List<string>? ResponseTypes { get; init; }
        [JsonPropertyName("scope")] public string? Scope { get; init; }
    }
}
