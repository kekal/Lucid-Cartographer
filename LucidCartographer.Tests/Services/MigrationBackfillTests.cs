using FluentAssertions;
using LucidCartographer.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LucidCartographer.Tests;

/// <summary>
/// Exercises the raw-SQL data backfill in 20260531173259_AddPoiEnrichmentRequested
/// against a REAL SQLite database. The in-memory EF provider builds schema from the
/// model and never runs migration Up(), so this is the only place the backfill SQL
/// is actually executed.
/// </summary>
public class MigrationBackfillTests
{
    private const string PrevMigration = "20260528123531_RemovePoiStatusAndVisitedDate";

    [Fact]
    public async Task AddPoiEnrichmentRequested_Backfill_FlagsEveryUnenrichedRow()
    {
        // Keep an in-memory SQLite db alive for the duration via an open connection.
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;

        await using (var db = new AppDbContext(options))
        {
            var migrator = db.GetService<IMigrator>();
            // Migrate up to BEFORE the new column exists.
            await migrator.MigrateAsync(PrevMigration);

            // Seed pre-migration rows via raw SQL (the EnrichmentRequested column
            // does not exist yet, so EF can't insert these).
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Pois (Name, IsEnriched, EnrichmentFailureCount, EnrichmentNeedsManualUrl, Version, AddedDate) VALUES " +
                "('unenriched-fresh', 0, 0, 0, 0, CURRENT_TIMESTAMP)," +
                "('unenriched-failed', 0, 4, 0, 0, CURRENT_TIMESTAMP)," +
                "('unenriched-exhausted', 0, 9, 0, 0, CURRENT_TIMESTAMP)," +
                "('already-enriched', 1, 0, 0, 0, CURRENT_TIMESTAMP);");

            // Apply the remaining migrations (adds the column + runs the backfill).
            await migrator.MigrateAsync();
        }

        await using (var db = new AppDbContext(options))
        {
            var byName = await db.Pois.ToDictionaryAsync(p => p.Name, p => p.EnrichmentRequested);

            // Every IsEnriched=0 row is flagged (cap-independent backfill), regardless
            // of failure count; the already-enriched row is not.
            byName["unenriched-fresh"].Should().BeTrue();
            byName["unenriched-failed"].Should().BeTrue();
            byName["unenriched-exhausted"].Should().BeTrue();
            byName["already-enriched"].Should().BeFalse();
        }
    }
}
