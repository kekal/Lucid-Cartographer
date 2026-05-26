using System.Security.Cryptography;
using LucidCartographer.Data;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Authentication;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LucidCartographer.Configuration;

/// <summary>
/// Registers the app's own OAuth 2.1 "frontdoor" so Claude's remote-MCP
/// connector can authenticate against this app directly (the deployment is a
/// plain HTTPS tunnel with no edge auth). OpenIddict is the authorization
/// server (authorization-code + PKCE, refresh tokens, discovery, in-process
/// token validation); a custom RFC 7591 endpoint adds the dynamic client
/// registration OpenIddict itself lacks. The MCP SDK's resource-server handler
/// serves the protected-resource metadata and the WWW-Authenticate challenge.
///
/// The frontdoor is only enabled when <c>OAuth:Issuer</c> (the public https
/// base URL) is set — otherwise the app behaves exactly as before and /mcp is
/// protected by the API key / LAN bypass only.
/// </summary>
public static class OAuthFrontdoorExtensions
{
    /// <summary>OAuth scope advertised for MCP access.</summary>
    public const string McpScope = "mcp";

    /// <summary>Default issuer used in Development when OAuth:Issuer is unset.</summary>
    private const string DevelopmentIssuer = "http://localhost:5087";

    public static IServiceCollection AddOAuthFrontdoor(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var issuer = configuration["OAuth:Issuer"];
        if (string.IsNullOrWhiteSpace(issuer) && environment.IsDevelopment())
        {
            issuer = DevelopmentIssuer;
        }

        if (string.IsNullOrWhiteSpace(issuer))
        {
            // No public issuer configured -> OAuth frontdoor disabled. /mcp stays
            // protected by the API key / LAN bypass. Set OAuth:Issuer to enable
            // browserless OAuth for Claude connectors.
            return services;
        }

        var dataDir = DatabaseServicesExtensions.ResolveDataDirectory(configuration, environment);
        var signingKey = LoadOrCreateRsaKey(Path.Combine(dataDir, "oauth-signing.key"), "sig-1");
        var encryptionKey = LoadOrCreateRsaKey(Path.Combine(dataDir, "oauth-encryption.key"), "enc-1");

        services.AddOpenIddict()
            .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<AppDbContext>())
            .AddServer(options =>
            {
                options.SetAuthorizationEndpointUris("connect/authorize")
                       .SetTokenEndpointUris("connect/token")
                       // Serve the metadata at both the OIDC and the RFC 8414 OAuth
                       // well-known paths so any client's discovery probe resolves.
                       .SetConfigurationEndpointUris(
                           ".well-known/openid-configuration",
                           ".well-known/oauth-authorization-server");

                options.AllowAuthorizationCodeFlow()
                       .RequireProofKeyForCodeExchange()   // mandatory PKCE (S256)
                       .AllowRefreshTokenFlow();

                options.RegisterScopes(Scopes.Email, Scopes.Profile, Scopes.OfflineAccess, McpScope);

                options.SetIssuer(new Uri(issuer));

                // Persistent asymmetric keys (survive restarts; live on the data volume).
                options.AddSigningKey(signingKey);
                options.AddEncryptionKey(encryptionKey);

                // OpenIddict has no built-in Dynamic Client Registration, so advertise
                // our custom /connect/register endpoint in the discovery document.
                options.AddEventHandler<OpenIddictServerEvents.HandleConfigurationRequestContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        var baseUri = context.Issuer ?? new Uri(issuer);
                        context.Metadata["registration_endpoint"] = new Uri(baseUri, "connect/register").AbsoluteUri;
                        return default;
                    }));

                var aspNetCore = options.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough();

                if (environment.IsDevelopment())
                {
                    // Dev runs over plain http://localhost. In production the tunnel
                    // forwards X-Forwarded-Proto: https, so HTTPS is enforced.
                    aspNetCore.DisableTransportSecurityRequirement();
                }
            })
            .AddValidation(options =>
            {
                // Validate access tokens in-process (can decrypt OpenIddict's JWE tokens).
                options.UseLocalServer();
                options.UseAspNetCore();
            });

        // MCP resource-server metadata: the SDK serves /.well-known/oauth-protected-resource
        // and the WWW-Authenticate challenge pointing Claude at our authorization server.
        services.AddAuthentication().AddMcp(options =>
        {
            options.ResourceMetadata = new ProtectedResourceMetadata
            {
                Resource = issuer,
                AuthorizationServers = { issuer },
                ScopesSupported = { McpScope },
                BearerMethodsSupported = { "header" }
            };
        });

        return services;
    }

    /// <summary>
    /// Loads an RSA private key from <paramref name="path"/> (PEM), creating and
    /// persisting a fresh 2048-bit key there on first run. Using raw RSA keys
    /// avoids X.509 store / key-usage pitfalls inside the container.
    /// </summary>
    private static RsaSecurityKey LoadOrCreateRsaKey(string path, string keyId)
    {
        var rsa = RSA.Create(2048);
        if (File.Exists(path))
        {
            rsa.ImportFromPem(File.ReadAllText(path));
        }
        else
        {
            File.WriteAllText(path, rsa.ExportRSAPrivateKeyPem());
        }

        return new RsaSecurityKey(rsa) { KeyId = keyId };
    }
}
