using System.Security.Cryptography;
using System.Text;

namespace LucidCartographer.Services.Auth
{
    /// <summary>
    /// Verifies and creates PBKDF2 password hashes using the format
    /// pbkdf2$iterations$saltBase64$hashBase64.
    /// </summary>
    public static class PasswordHasher
    {
        private const string Scheme = "pbkdf2";
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int DefaultIterations = 100_000;

        /// <summary>
        /// Hashes a plaintext password using PBKDF2-SHA256.
        /// </summary>
        public static string HashPassword(string password, int iterations = DefaultIterations)
        {
            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentException("Password cannot be empty.", nameof(password));

            if (iterations <= 0)
                throw new ArgumentOutOfRangeException(nameof(iterations));

            Span<byte> salt = stackalloc byte[SaltSize];
            RandomNumberGenerator.Fill(salt);
            var hash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                HashSize);

            return $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        /// <summary>
        /// Verifies a plaintext password against configured hash input.
        /// Accepts either pbkdf2 hash format or legacy plaintext value.
        /// </summary>
        public static bool VerifyConfiguredSecret(string password, string configuredSecret)
        {
            if (string.IsNullOrEmpty(configuredSecret))
                return false;

            if (configuredSecret.StartsWith($"{Scheme}$", StringComparison.OrdinalIgnoreCase))
                return VerifyPbkdf2(password, configuredSecret);

            var inputBytes = Encoding.UTF8.GetBytes(password);
            var expectedBytes = Encoding.UTF8.GetBytes(configuredSecret);
            return inputBytes.Length == expectedBytes.Length
                && CryptographicOperations.FixedTimeEquals(inputBytes, expectedBytes);
        }

        private static bool VerifyPbkdf2(string password, string encoded)
        {
            var parts = encoded.Split('$');
            if (parts.Length != 4)
                return false;

            if (!int.TryParse(parts[1], out var iterations) || iterations <= 0)
                return false;

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
}
