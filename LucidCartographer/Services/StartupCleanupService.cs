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
        await ReviveStuckImportedPoisAsync(cancellationToken);
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

    /// <summary>
    /// One-time recovery for the bug where file-imported POIs that hit the
    /// enrichment retry cap (or were marked enriched without any extracted
    /// data, before the success criterion was tightened) ended up hidden
    /// from their collection. Resets the failure counter on rows with valid
    /// coords so the BG service tries them again with the current selectors.
    /// Idempotent — runs every startup but is a no-op once everything's clean.
    /// </summary>
    private async Task ReviveStuckImportedPoisAsync(CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("ReviveStuckImportedPois");
        try
        {
            using var scope = services.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await factory.CreateDbContextAsync(cancellationToken);

            // Two cohorts to revive:
            //  (a) failure cap reached but coords are valid — the row is
            //      visible now but enrichment has given up.
            //  (b) marked enriched yet has no address / website / phone /
            //      canonical place URL — symptom of the old coords-only
            //      success check; flip back to unenriched so BG retries.
            var stuck = await db.Pois
                .Where(p => p.Latitude != null && p.Longitude != null
                            && (
                                (!p.IsEnriched && p.EnrichmentFailureCount > 0)
                                || (p.IsEnriched
                                    && (p.Address == null || p.Address == "")
                                    && (p.Website == null || p.Website == "")
                                    && (p.Phone == null || p.Phone == "")
                                    && (p.GoogleMapsUrl == null || !p.GoogleMapsUrl.Contains("/maps/place/")))
                            ))
                .ToListAsync(cancellationToken);

            if (stuck.Count == 0)
            {
                return;
            }

            foreach (var poi in stuck)
            {
                poi.IsEnriched = false;
                poi.EnrichmentFailureCount = 0;
                poi.LastEnrichmentAttemptAt = null;
            }

            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Revived {Count} stuck POIs (failed enrichment or pseudo-enriched) for re-enrichment", stuck.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ReviveStuckImportedPois failed; continuing startup");
        }
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
