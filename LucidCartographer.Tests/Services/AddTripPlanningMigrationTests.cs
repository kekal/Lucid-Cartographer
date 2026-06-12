using FluentAssertions;
using LucidCartographer.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LucidCartographer.Tests;

/// <summary>
/// Exercises the AddTripPlanning migration against a REAL SQLite database. The in-memory EF
/// provider builds schema from the model and never runs migration Up(), so the OrderIndex /
/// TravelMode backfills and the CK_* enum check constraints can only be verified here.
/// Mirrors <see cref="MigrationBackfillTests"/>.
/// </summary>
public class AddTripPlanningMigrationTests
{
    private const string PrevMigration = "20260531173259_AddPoiEnrichmentRequested";

    private static DbContextOptions<AppDbContext> OptionsFor(SqliteConnection conn) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;

    [Fact]
    public async Task AddTripPlanning_AppliesCleanly_AndExposesNewColumnsAndTable()
    {
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var options = OptionsFor(conn);

        await using (var db = new AppDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync();
        }

        // New columns/table are readable and round-trip through EF.
        await using (var db = new AppDbContext(options))
        {
            var poi = new Data.Entities.Poi { Name = "anchor" };
            db.Pois.Add(poi);
            await db.SaveChangesAsync();

            var collection = new Data.Entities.PoiCollection
            {
                Name = "trip",
                Color = "#005bbf",
                TravelMode = Data.Entities.TravelMode.Drive,
                StartPoiId = poi.Id,
                TripStartTime = new DateTime(2026, 6, 11, 9, 0, 0, DateTimeKind.Utc),
                TimeBudgetMinutes = 480,
                TripViewEnabled = true,
            };
            db.PoiCollections.Add(collection);
            await db.SaveChangesAsync();

            db.PoiCollectionItems.Add(new Data.Entities.PoiCollectionItem
            {
                PoiId = poi.Id,
                PoiCollectionId = collection.Id,
                OrderIndex = 1,
                DwellMinutes = 90,
            });
            db.RouteSegments.Add(new Data.Entities.RouteSegment
            {
                FromPoiId = poi.Id,
                ToPoiId = poi.Id,
                TravelMode = Data.Entities.TravelMode.Drive,
                DurationSeconds = 600,
                DistanceMeters = 12000.5,
                Fidelity = Data.Entities.Fidelity.Estimated,
                Source = "Mock",
                ComputedAt = new DateTime(2026, 6, 11, 9, 0, 0, DateTimeKind.Utc),
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options))
        {
            var collection = await db.PoiCollections.SingleAsync();
            collection.TravelMode.Should().Be(Data.Entities.TravelMode.Drive);
            collection.TripViewEnabled.Should().BeTrue();
            collection.TimeBudgetMinutes.Should().Be(480);

            var item = await db.PoiCollectionItems.SingleAsync();
            item.OrderIndex.Should().Be(1);
            item.DwellMinutes.Should().Be(90);

            var seg = await db.RouteSegments.SingleAsync();
            seg.DurationSeconds.Should().Be(600);
            seg.DistanceMeters.Should().Be(12000.5);
            seg.Fidelity.Should().Be(Data.Entities.Fidelity.Estimated);
        }
    }

    [Fact]
    public async Task AddTripPlanning_BackfillsOrderIndex_OneBasedContiguousByAddedDate()
    {
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var options = OptionsFor(conn);

        await using (var db = new AppDbContext(options))
        {
            var migrator = db.GetService<IMigrator>();
            // Migrate to BEFORE OrderIndex / trip columns exist.
            await migrator.MigrateAsync(PrevMigration);

            // Seed pre-migration data via raw SQL (trip columns don't exist yet). Two
            // collections; AddedDate intentionally out of insertion order to prove ordering.
            // p1..p6 are PLACEABLE (lat+lon present) so the backfill numbers them; p7 is
            // non-placeable (NULL coords) to prove it is left out of the Stop Order (==0).
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Pois (Id, Name, Latitude, Longitude, IsEnriched, EnrichmentRequested, EnrichmentFailureCount, EnrichmentNeedsManualUrl, Version, AddedDate) VALUES " +
                "(1, 'p1', 50.0, 20.0, 0, 0, 0, 0, 0, '2026-01-03')," +
                "(2, 'p2', 50.0, 20.0, 0, 0, 0, 0, 0, '2026-01-01')," +
                "(3, 'p3', 50.0, 20.0, 0, 0, 0, 0, 0, '2026-01-02')," +
                "(4, 'p4', 50.0, 20.0, 0, 0, 0, 0, 0, '2026-02-01')," +
                // p5 and p6 share an identical AddedDate to exercise the PoiId tie-break.
                "(5, 'p5', 50.0, 20.0, 0, 0, 0, 0, 0, '2026-03-01')," +
                "(6, 'p6', 50.0, 20.0, 0, 0, 0, 0, 0, '2026-03-01')," +
                // p7 is non-placeable: it must receive OrderIndex 0 ('not a stop').
                "(7, 'p7', NULL, NULL, 0, 0, 0, 0, 0, '2026-01-04');");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO PoiCollections (Id, Name, Color, IsVisible, Version, CreatedDate) VALUES " +
                "(10, 'A', '#005bbf', 1, 0, CURRENT_TIMESTAMP)," +
                "(20, 'B', '#005bbf', 1, 0, CURRENT_TIMESTAMP)," +
                "(30, 'C', '#005bbf', 1, 0, CURRENT_TIMESTAMP);");
            // Collection 10 holds p1,p2,p3 (placeable, AddedDate order ⇒ p2,p3,p1 ⇒ 1,2,3) plus
            // the non-placeable p7 (⇒ 0, must not consume a Stop number).
            // Collection 20 holds p4 only ⇒ 1.
            // Collection 30 holds p5,p6 with equal AddedDate ⇒ tie-break by PoiId ⇒ p5=1, p6=2.
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO PoiCollectionItems (PoiId, PoiCollectionId) VALUES " +
                "(1, 10),(2, 10),(3, 10),(7, 10),(4, 20),(6, 30),(5, 30);");

            // Apply AddTripPlanning (adds columns + runs the backfills).
            await migrator.MigrateAsync();
        }

        await using (var db = new AppDbContext(options))
        {
            var byKey = await db.PoiCollectionItems
                .ToDictionaryAsync(i => (i.PoiCollectionId, i.PoiId), i => i.OrderIndex);

            // Collection 10 ordered by AddedDate asc over PLACEABLE items only:
            // p2(01-01)=1, p3(01-02)=2, p1(01-03)=3; the non-placeable p7 is 0 and does
            // not shift the placeable numbering (stays contiguous 1..3).
            byKey[(10, 2)].Should().Be(1);
            byKey[(10, 3)].Should().Be(2);
            byKey[(10, 1)].Should().Be(3);
            byKey[(10, 7)].Should().Be(0, "non-placeable members are not Stops");
            // Collection 20: single member ⇒ 1 (contiguous, per-collection, gap-free).
            byKey[(20, 4)].Should().Be(1);
            // Collection 30: equal AddedDate ⇒ deterministic PoiId tie-break (p5 before p6),
            // independent of the reversed insertion order.
            byKey[(30, 5)].Should().Be(1);
            byKey[(30, 6)].Should().Be(2);
        }
    }

    [Fact]
    public async Task AddTripPlanning_EnumCheckConstraints_RejectBogusValues()
    {
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var options = OptionsFor(conn);

        await using (var db = new AppDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Pois (Id, Name, IsEnriched, EnrichmentRequested, EnrichmentFailureCount, EnrichmentNeedsManualUrl, Version, AddedDate) " +
                "VALUES (1, 'p1', 0, 0, 0, 0, 0, CURRENT_TIMESTAMP);");
        }

        await using (var db = new AppDbContext(options))
        {
            // Positive control: VALID enum values insert cleanly. This proves the bogus-value
            // failures below come from the CHECK constraints specifically, not from some unrelated
            // INSERT error (NOT NULL, FK, etc.) that would also surface as SqliteException.
            var goodCollection = async () => await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO PoiCollections (Name, Color, IsVisible, Version, CreatedDate, TravelMode, TripViewEnabled) " +
                "VALUES ('good', '#005bbf', 1, 0, CURRENT_TIMESTAMP, 'Drive', 0);");
            await goodCollection.Should().NotThrowAsync();

            var goodSegment = async () => await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO RouteSegments (FromPoiId, ToPoiId, TravelMode, DurationSeconds, DistanceMeters, Fidelity, Source, ComputedAt, Version) " +
                "VALUES (1, 1, 'Drive', 1, 1.0, 'Estimated', 'Mock', CURRENT_TIMESTAMP, 0);");
            await goodSegment.Should().NotThrowAsync();

            // Bogus PoiCollection.TravelMode ⇒ CK_PoiCollection_TravelMode failure.
            var badCollection = async () => await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO PoiCollections (Name, Color, IsVisible, Version, CreatedDate, TravelMode, TripViewEnabled) " +
                "VALUES ('bad', '#005bbf', 1, 0, CURRENT_TIMESTAMP, 'Bogus', 0);");
            await badCollection.Should().ThrowAsync<SqliteException>();

            // Bogus RouteSegment.TravelMode ⇒ CK_RouteSegment_TravelMode failure.
            var badMode = async () => await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO RouteSegments (FromPoiId, ToPoiId, TravelMode, DurationSeconds, DistanceMeters, Fidelity, Source, ComputedAt, Version) " +
                "VALUES (1, 1, 'Bogus', 1, 1.0, 'Estimated', 'Mock', CURRENT_TIMESTAMP, 0);");
            await badMode.Should().ThrowAsync<SqliteException>();

            // Bogus RouteSegment.Fidelity ⇒ CK_RouteSegment_Fidelity failure. Uses TravelMode='Walk'
            // so its PK (1,1,'Walk') differs from the valid positive-control row (1,1,'Drive') —
            // guaranteeing the rejection is the Fidelity CHECK, not a primary-key collision.
            var badFidelity = async () => await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO RouteSegments (FromPoiId, ToPoiId, TravelMode, DurationSeconds, DistanceMeters, Fidelity, Source, ComputedAt, Version) " +
                "VALUES (1, 1, 'Walk', 1, 1.0, 'Bogus', 'Mock', CURRENT_TIMESTAMP, 0);");
            await badFidelity.Should().ThrowAsync<SqliteException>();
        }
    }

    [Fact]
    public async Task AddTripPlanning_DirectionalKey_AllowsBothPairOrders()
    {
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var options = OptionsFor(conn);

        await using (var db = new AppDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync();
            db.Pois.Add(new Data.Entities.Poi { Id = 1, Name = "a" });
            db.Pois.Add(new Data.Entities.Poi { Id = 2, Name = "b" });
            await db.SaveChangesAsync();

            // A→B and B→A under the same mode are distinct rows (directional key).
            db.RouteSegments.Add(new Data.Entities.RouteSegment
            {
                FromPoiId = 1, ToPoiId = 2, TravelMode = Data.Entities.TravelMode.Drive,
                DurationSeconds = 100, DistanceMeters = 1000, Fidelity = Data.Entities.Fidelity.Estimated,
                Source = "Mock", ComputedAt = DateTime.UtcNow,
            });
            db.RouteSegments.Add(new Data.Entities.RouteSegment
            {
                FromPoiId = 2, ToPoiId = 1, TravelMode = Data.Entities.TravelMode.Drive,
                DurationSeconds = 200, DistanceMeters = 1000, Fidelity = Data.Entities.Fidelity.Estimated,
                Source = "Mock", ComputedAt = DateTime.UtcNow,
            });

            var act = async () => await db.SaveChangesAsync();
            await act.Should().NotThrowAsync();
        }

        await using (var db = new AppDbContext(options))
        {
            (await db.RouteSegments.CountAsync()).Should().Be(2);

            // Negative control: a SECOND row on the same (From, To, Mode) triple must be rejected,
            // proving the composite key is actually enforced (not just that both directions fit).
            var duplicate = async () => await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO RouteSegments (FromPoiId, ToPoiId, TravelMode, DurationSeconds, DistanceMeters, Fidelity, Source, ComputedAt, Version) " +
                "VALUES (1, 2, 'Drive', 999, 1.0, 'Estimated', 'Mock', CURRENT_TIMESTAMP, 0);");
            await duplicate.Should().ThrowAsync<SqliteException>();
        }
    }
}
