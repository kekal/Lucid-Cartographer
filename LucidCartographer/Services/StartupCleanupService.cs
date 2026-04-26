using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Auth;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace LucidCartographer.Services;

/// <summary>
/// Runs once on app startup, in order:
///   1. Sweep orphaned lucid-import-* temp files older than 1h.
///   2. Apply EF Core migrations (ARCH-CRIT-01: MigrateAsync, not EnsureCreatedAsync).
///   3. Bootstrap an initial admin user when the Users table is empty.
/// </summary>
public sealed class StartupCleanupService(
    IServiceProvider services,
    ILoggerFactory loggerFactory)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        SweepOrphanedTempFiles();
        await ApplyMigrationsAsync(cancellationToken);
        await EnsureAdminUserAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void SweepOrphanedTempFiles()
    {
        // Sweep orphaned lucid-import-* temp files left by a previous crash that
        // died between "file streamed to disk" and "Coravel invocable ran +
        // deleted it in finally". Cheap and safe: only files matching the specific
        // pattern we wrote ourselves, older than 1h, are removed.
        var logger = loggerFactory.CreateLogger("TempFileSweep");
        try
        {
            var tempRoot = Path.GetTempPath();
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            var swept = 0;
            foreach (var path in Directory.EnumerateFiles(tempRoot, "lucid-import-*"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                        swept++;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not remove orphaned temp file {Path}", path);
                }
            }
            if (swept > 0)
            {
                logger.LogInformation("Removed {Count} orphaned lucid-import-* temp files from {Path}", swept, tempRoot);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Startup temp-file sweep failed; continuing");
        }
    }

    private async Task ApplyMigrationsAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
    }

    private async Task EnsureAdminUserAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        if (await db.Users.AnyAsync(cancellationToken))
        {
            return;
        }

        var initialPassword = GenerateUrlSafePassword(24);
        var now = DateTime.UtcNow;

        db.Users.Add(new User
        {
            Username = "admin",
            PasswordHash = PasswordHasher.HashPassword(initialPassword),
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);

        var logger = loggerFactory.CreateLogger<StartupCleanupService>();
        logger.LogWarning(
            "════════════════════════════════════════════════════════\n" +
            "  INITIAL ADMIN USER CREATED\n" +
            "      Username: admin\n" +
            "      Password: {InitialPassword}\n" +
            "  Save this password - it will not be shown again.\n" +
            "════════════════════════════════════════════════════════",
            initialPassword);
    }

    private static string GenerateUrlSafePassword(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var buffer = RandomNumberGenerator.GetBytes(length);
        var chars = new char[length];

        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[buffer[i] % alphabet.Length];
        }

        return new string(chars);
    }
}
