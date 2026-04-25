using System.Security.Cryptography;
using System.Text;

namespace LucidCartographer.Services.Auth;

/// <summary>
/// Verifies and creates PBKDF2 password hashes using the format
/// pbkdf2$iterations$saltBase64$hashBase64.
/// </summary>
public static class PasswordHasher
{
    private const string Scheme = "pbkdf2";

    /// <summary>
    /// Verifies a plaintext password against configured hash input.
    /// Accepts either pbkdf2 hash format or legacy plaintext value.
    /// </summary>
    public static bool VerifyConfiguredSecret(string password, string configuredSecret)
    {
        if (string.IsNullOrEmpty(configuredSecret))
        {
            return false;
        }

        if (configuredSecret.StartsWith($"{Scheme}$", StringComparison.OrdinalIgnoreCase))
        {
            return VerifyPbkdf2(password, configuredSecret);
        }

        var inputBytes = Encoding.UTF8.GetBytes(password);
        var expectedBytes = Encoding.UTF8.GetBytes(configuredSecret);
        return inputBytes.Length == expectedBytes.Length
               && CryptographicOperations.FixedTimeEquals(inputBytes, expectedBytes);
    }

    private static bool VerifyPbkdf2(string password, string encoded)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 4)
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expectedHash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actualHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
