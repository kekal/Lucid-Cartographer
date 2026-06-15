using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LucidCartographer.Tests;

/// <summary>
/// Exercises the AddOutgoingTravelMode migration (Story 3.1, TRIP-LEGMODE-01) against a REAL
/// SQLite database. The in-memory EF provider builds schema from the model and never runs
/// migration Up(), so the nullable CK_PoiCollectionItem_OutgoingTravelMode check constraint can
/// only be verified here. Mirrors <see cref="AddTripPlanningMigrationTests"/>.
/// </summary>
public class AddOutgoingTravelModeMigrationTests
{
    private static DbContextOptions<AppDbContext> OptionsFor(SqliteConnection conn) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;

    /// <summary>
    /// Seeds one Poi + one PoiCollection and returns their ids, after applying ALL migrations.
    /// </summary>
    private static async Task<(int PoiId, int CollectionId)> SeedAnchorAsync(DbContextOptions<AppDbContext> options)
    {
        await using var db = new AppDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync();

        var poi = new Poi { Name = "anchor" };
        db.Pois.Add(poi);
        await db.SaveChangesAsync();

        var collection = new PoiCollection { Name = "trip", Color = "#005bbf" };
        db.PoiCollections.Add(collection);
        await db.SaveChangesAsync();

        return (poi.Id, collection.Id);
    }

    [Theory]
    [InlineData(TravelMode.AnyAir)]
    [InlineData(TravelMode.Drive)]
    [InlineData(TravelMode.Walk)]
    [InlineData(TravelMode.Cycle)]
    [InlineData(null)]
    public async Task OutgoingTravelMode_RoundTrips_ForEachAllowedValueAndNull(string? mode)
    {
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var options = OptionsFor(conn);

        var (poiId, collectionId) = await SeedAnchorAsync(options);

        await using (var db = new AppDbContext(options))
        {
            db.PoiCollectionItems.Add(new PoiCollectionItem
            {
                PoiId = poiId,
                PoiCollectionId = collectionId,
                OrderIndex = 1,
                OutgoingTravelMode = mode,
            });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().NotThrowAsync();
        }

        await using (var db = new AppDbContext(options))
        {
            var item = await db.PoiCollectionItems.SingleAsync();
            item.OutgoingTravelMode.Should().Be(mode);
        }
    }

    [Fact]
    public async Task OutgoingTravelMode_RejectsBogusValue_ViaCheckConstraint()
    {
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var options = OptionsFor(conn);

        var (poiId, collectionId) = await SeedAnchorAsync(options);

        await using var db = new AppDbContext(options);

        // Positive control: a VALID value inserts cleanly, proving the bogus-value failure
        // below comes from CK_PoiCollectionItem_OutgoingTravelMode specifically and not from
        // some unrelated INSERT error (NOT NULL, FK, PK collision, etc.).
        var good = async () => await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO PoiCollectionItems (PoiId, PoiCollectionId, OrderIndex, OutgoingTravelMode) " +
            "VALUES (" + poiId + ", " + collectionId + ", 1, 'Drive');");
        await good.Should().NotThrowAsync();

        // Bogus OutgoingTravelMode ⇒ CK_PoiCollectionItem_OutgoingTravelMode failure. Uses a
        // distinct collection row would be ideal, but the composite PK (PoiId, PoiCollectionId)
        // is already taken; delete the good row first so the rejection is the CHECK, not the PK.
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM PoiCollectionItems WHERE PoiId = " + poiId + " AND PoiCollectionId = " + collectionId + ";");

        var bad = async () => await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO PoiCollectionItems (PoiId, PoiCollectionId, OrderIndex, OutgoingTravelMode) " +
            "VALUES (" + poiId + ", " + collectionId + ", 1, 'Bogus');");
        await bad.Should().ThrowAsync<SqliteException>();
    }
}
