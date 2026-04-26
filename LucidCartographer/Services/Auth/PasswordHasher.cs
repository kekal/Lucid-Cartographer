using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace LucidCartographer.Services.Auth;

/// <summary>
/// Verifies and creates PBKDF2 password hashes using the format
/// pbkdf2$iterations$saltBase64$hashBase64.
/// </summary>
public static class PasswordHasher
{
    private const string Scheme = "pbkdf2";
    private const int DefaultIterations = 100000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    /// <summary>
    /// Creates a PBKDF2 password hash using the encoded format
    /// pbkdf2$iterations$saltBase64$hashBase64.
    /// </summary>
    public static string HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            DefaultIterations,
            HashAlgorithmName.SHA256,
            HashSize);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Scheme}${DefaultIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    /// <summary>
    /// Verifies a plaintext password against a PBKDF2 encoded password hash.
    /// </summary>
    public static bool Verify(string password, string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(passwordHash))
        {
            return false;
        }

        return passwordHash.StartsWith($"{Scheme}$", StringComparison.OrdinalIgnoreCase)
            && VerifyPbkdf2(password, passwordHash);
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
