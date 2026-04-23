using System.Security.Cryptography;
using System.Text;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Services.Auth
{
    /// <summary>
    /// Persists and validates authentication sessions.
    /// </summary>
    public sealed class SessionStore
    {
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
        private readonly IDbContextFactory<AppDbContext> _factory;

        public SessionStore(IDbContextFactory<AppDbContext> factory)
        {
            _factory = factory;
        }

        /// <summary>
        /// Creates a new login session and returns the raw token value.
        /// </summary>
        public async Task<string> CreateAsync(CancellationToken cancellationToken)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var now = DateTime.UtcNow;

            db.Sessions.Add(new Session
            {
                TokenHash = ComputeTokenHash(token),
                CreatedAt = now,
                ExpiresAt = now.Add(SessionLifetime)
            });
            await db.SaveChangesAsync(cancellationToken);
            return token;
        }

        /// <summary>
        /// Validates whether a session token maps to an active session.
        /// </summary>
        public async Task<bool> IsActiveAsync(string token, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var hash = ComputeTokenHash(token);
            var now = DateTime.UtcNow;

            return await db.Sessions.AnyAsync(
                s => s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now,
                cancellationToken);
        }

        /// <summary>
        /// Revokes a session by token if it exists.
        /// </summary>
        public async Task RevokeAsync(string token, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(token))
                return;

            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var hash = ComputeTokenHash(token);
            var session = await db.Sessions.FirstOrDefaultAsync(s => s.TokenHash == hash, cancellationToken);
            if (session == null || session.RevokedAt != null)
                return;

            session.RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        private static string ComputeTokenHash(string token)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
