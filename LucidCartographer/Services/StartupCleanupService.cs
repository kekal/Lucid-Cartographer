using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Auth;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Services;

/// <summary>
/// Runs once on app startup, in order:
///   1. Sweep orphaned lucid-import-* temp files older than 1h.
///   2. Apply EF Core migrations (ARCH-CRIT-01: MigrateAsync, not EnsureCreatedAsync).
///   3. Bootstrap an initial admin user when the Users table is empty.
///   4. Revive stuck file-imported POIs (one-time data recovery, idempotent).
///   5. Vacuum expired / long-revoked auth sessions.
///   6. Purge stale OSRM-sourced RouteSegment cache rows (one-time migration, idempotent).
/// </summary>
public sealed class StartupCleanupService(
    IServiceProvider services,
    ILoggerFactory loggerFactory)
    : IHostedService
{
    // Refuse to start after two consecutive ReviveStuckImportedPois failures (transient hiccup vs. real schema problem).
    private static int s_consecutiveReviveFailures;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        SweepOrphanedTempFiles();
        await ApplyMigrationsAsync(cancellationToken);
        await EnsureAdminUserAsync(cancellationToken);
        await ReviveStuckImportedPoisAsync(cancellationToken);
        await VacuumExpiredSessionsAsync(cancellationToken);
        await PurgeOsrmCacheRowsAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void SweepOrphanedTempFiles()
    {
        // Clean up lucid-import-* temp files older than 1h (safe: crashes between stream and cleanup).
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

            var (revived, clearedImages) = await ReviveStuckImportedPoisCoreAsync(db, cancellationToken);

            if (revived > 0)
            {
                logger.LogWarning(
                    "Revived {Count} stuck POIs (failed enrichment or pseudo-enriched) for re-enrichment; cleared {ImageCount} untrustworthy photo(s)",
                    revived, clearedImages);
            }
            Interlocked.Exchange(ref s_consecutiveReviveFailures, 0);
        }
        catch (Exception ex)
        {
            var failures = Interlocked.Increment(ref s_consecutiveReviveFailures);
            if (failures >= 2)
            {
                logger.LogCritical(ex,
                    "ReviveStuckImportedPois has failed {FailureCount} starts in a row; refusing to continue startup so the underlying problem is visible",
                    failures);
                throw;
            }
            logger.LogError(ex,
                "ReviveStuckImportedPois failed (attempt {FailureCount}); continuing startup, will escalate on next failure",
                failures);
        }
    }

    /// <summary>
    /// Pure revive logic, extracted for testability. Revives two cohorts and
    /// re-enqueues them under the explicit-request model:
    ///  (a) failure-capped rows with valid coords (enrichment gave up); and
    ///  (b) rows marked enriched that never resolved a canonical /maps/place/ URL
    ///      AND carry no extracted text (Address/Website/Phone all empty) — the
    ///      pseudo-enriched false positives that may hold a stray SERP photo
    ///      (POI #604). Genuinely-enriched rows whose place URL was later edited
    ///      to a shortlink/website/cleared keep their populated fields, so the
    ///      empty-text guard spares them and their real photo.
    /// For revived rows lacking a place URL the untrustworthy photo is dropped.
    /// Dormant manually-created POIs (EnrichmentRequested=false, IsEnriched=false,
    /// FailureCount=0) match NEITHER cohort, so creation stays decoupled from
    /// enrichment. Returns (revived count, cleared-image count).
    /// </summary>
    internal static async Task<(int Revived, int ClearedImages)> ReviveStuckImportedPoisCoreAsync(
        AppDbContext db, CancellationToken cancellationToken)
    {
        var stuck = await db.Pois
            .Where(p => p.Latitude != null && p.Longitude != null
                        && !p.EnrichmentNeedsManualUrl
                        && (
                            (!p.IsEnriched && p.EnrichmentFailureCount > 0)
                            || (p.IsEnriched
                                && (p.GoogleMapsUrl == null || !p.GoogleMapsUrl.Contains("/maps/place/"))
                                && (p.Address == null || p.Address == "")
                                && (p.Website == null || p.Website == "")
                                && (p.Phone == null || p.Phone == ""))
                        ))
            .ToListAsync(cancellationToken);

        if (stuck.Count == 0)
        {
            return (0, 0);
        }

        // Photos on rows without a canonical place URL can only have come from the
        // buggy SERP grab, so they are untrustworthy — drop them (rows WITH a
        // /maps/place/ URL are never in this cohort, so good photos are untouched).
        var poisedIds = stuck
            .Where(p => p.GoogleMapsUrl == null || !p.GoogleMapsUrl.Contains("/maps/place/"))
            .Select(p => p.Id)
            .ToList();
        var imagesToDrop = await db.PoiImages
            .Where(img => poisedIds.Contains(img.PoiId))
            .ToListAsync(cancellationToken);
        if (imagesToDrop.Count > 0)
        {
            db.PoiImages.RemoveRange(imagesToDrop);
        }

        foreach (var poi in stuck)
        {
            poi.IsEnriched = false;
            poi.EnrichmentFailureCount = 0;
            poi.LastEnrichmentAttemptAt = null;
            poi.EnrichmentNeedsManualUrl = false;
            poi.EnrichmentRequested = true;
            if (poi.GoogleMapsUrl == null || !poi.GoogleMapsUrl.Contains("/maps/place/"))
            {
                poi.ImageUrl = null;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return (stuck.Count, imagesToDrop.Count);
    }

    /// <summary>
    /// Removes session rows that are either past their <c>ExpiresAt</c>
    /// or were revoked more than 30 days ago. Cookie auth never reads
    /// these rows, so the table just grows monotonically without it.
    /// One sweep per startup is enough — sessions accumulate slowly.
    /// </summary>
    private async Task VacuumExpiredSessionsAsync(CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("SessionVacuum");
        try
        {
            using var scope = services.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await factory.CreateDbContextAsync(cancellationToken);

            var now = DateTime.UtcNow;
            var revokedCutoff = now - TimeSpan.FromDays(30);

            var stale = await db.Sessions
                .Where(s => s.ExpiresAt < now || (s.RevokedAt != null && s.RevokedAt < revokedCutoff))
                .ToListAsync(cancellationToken);

            if (stale.Count == 0)
            {
                return;
            }

            db.Sessions.RemoveRange(stale);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Vacuumed {Count} expired/revoked auth sessions", stale.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Session vacuum failed; continuing startup");
        }
    }

    /// <summary>
    /// One-time migration (FR-16, AD-6): the hand-rolled OSRM provider has been retired,
    /// so its cached rows (<c>Source="OSRM"</c>, Fidelity Measured) are stale, un-reproducible
    /// measurements that the never-downgrade-Measured guard (<c>[TRIP-MANUAL-01]</c>) would
    /// otherwise pin forever. Delete them once under the write lock so the active provider
    /// recomputes them via the existing missing-row trigger. Idempotent / self-retiring:
    /// a no-op once the rows are gone. Manual rows are never touched.
    /// </summary>
    private async Task PurgeOsrmCacheRowsAsync(CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("OsrmCachePurge");
        try
        {
            using var scope = services.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            var writeLock = scope.ServiceProvider.GetRequiredService<SqliteWriteLock>();
            await using var db = await factory.CreateDbContextAsync(cancellationToken);

            await writeLock.Gate.WaitAsync(cancellationToken);
            try
            {
                var purged = await PurgeOsrmCacheRowsCoreAsync(db, cancellationToken);
                if (purged > 0)
                {
                    logger.LogWarning(
                        "Purged {Count} stale OSRM-sourced route-segment cache row(s); the active provider will recompute them",
                        purged);
                }
            }
            finally
            {
                writeLock.Gate.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OSRM cache purge failed; continuing startup");
        }
    }

    /// <summary>
    /// Pure purge logic, extracted for testability. Deletes every <see cref="RouteSegment"/>
    /// whose <see cref="RouteSegment.Source"/> equals the literal string <c>"OSRM"</c>, while
    /// never touching a <see cref="Fidelity.Manual"/> row. Returns the deleted-row count.
    /// </summary>
    internal static async Task<int> PurgeOsrmCacheRowsCoreAsync(
        AppDbContext db, CancellationToken cancellationToken)
    {
        // The literal "OSRM" is intentional: the TravelTimeSource.Osrm constant is removed in
        // Story 3.3, so this migration must not depend on it (AD-6). The Fidelity != Manual
        // belt makes the never-touch-Manual guarantee structural even though Manual rows carry
        // Source="Manual" and would not match "OSRM" anyway.
        var stale = await db.RouteSegments
            .Where(r => r.Source == "OSRM" && r.Fidelity != Fidelity.Manual)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return 0;
        }

        db.RouteSegments.RemoveRange(stale);
        await db.SaveChangesAsync(cancellationToken);
        return stale.Count;
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

        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = loggerFactory.CreateLogger<StartupCleanupService>();

        var username = configuration["Auth:InitialAdminUsername"];
        if (string.IsNullOrWhiteSpace(username))
        {
            username = "admin";
        }

        var initialPassword = configuration["Auth:InitialAdminPassword"];
        if (string.IsNullOrWhiteSpace(initialPassword))
        {
            // Fail closed. We never auto-generate or log a credential: an
            // operator must supply the first password explicitly. WebApplication
            // reads this from env vars (Auth__InitialAdminPassword) and the
            // command line (--Auth:InitialAdminPassword=...), so it works for
            // both `docker compose` and `dotnet` launches.
            throw new InvalidOperationException(
                "No users exist yet and no initial admin password was provided. Set the password and " +
                "restart: env var Auth__InitialAdminPassword=... (docker compose: ADMIN_PASSWORD in .env) " +
                "or --Auth:InitialAdminPassword=... on the command line. It is read once to seed the " +
                $"'{username}' account, then can be removed.");
        }

        db.Users.Add(new User
        {
            Username = username,
            PasswordHash = PasswordHasher.HashPassword(initialPassword),
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);

        // Log that the account exists, never the secret itself.
        logger.LogInformation("Created initial admin user '{Username}' from the configured password.", username);
    }
}
