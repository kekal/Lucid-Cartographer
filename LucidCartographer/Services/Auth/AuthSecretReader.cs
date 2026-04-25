namespace LucidCartographer.Services.Auth;

/// <summary>
/// Single source of truth for resolving the configured auth secret.
/// Hash takes precedence over plaintext password.
/// </summary>
public static class AuthSecretReader
{
    public static string? GetConfiguredAuthSecret(IConfiguration configuration)
    {
        var hash = configuration["Auth:PasswordHash"];
        if (!string.IsNullOrWhiteSpace(hash))
            return hash;

        return configuration["Auth:Password"];
    }
}
