using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Tests;

public class SetOperationServiceTests
{
    private static (IDbContextFactory<AppDbContext> factory, int colAId, int colBId) SeedDatabase(
        Action<AppDbContext, int, int>? additionalSeed = null)
    {
        var dbName = Guid.NewGuid().ToString();
        var factory = TestDbHelper.CreateFactory(dbName);

        int colAId, colBId;
        using (var db = factory.CreateDbContext())
        {
            var colA = new PoiCollection { Color = "#005bbf", Name = "A", PoiCount = 0 };
            var colB = new PoiCollection { Color = "#005bbf", Name = "B", PoiCount = 0 };
            db.PoiCollections.AddRange(colA, colB);
            db.SaveChanges();
            colAId = colA.Id;
            colBId = colB.Id;

            additionalSeed?.Invoke(db, colAId, colBId);
        }

        return (factory, colAId, colBId);
    }

    private static SetOperationService CreateService(IDbContextFactory<AppDbContext> factory)
    {
        return new SetOperationService(factory, new PoiMatcher());
    }

    // --- Subtract ---

    [Fact]
    public async Task Subtract_ReturnsOnlyPoisFromAThatAreNotInB()
    {
        var (factory, colAId, colBId) = SeedDatabase((db, aId, bId) =>
        {
            var shared = new Poi { Name = "Shared Place", Latitude = 50.0, Longitude = 19.0, GoogleMapsUrl = "https://maps.google.com/shared" };
            var onlyA = new Poi { Name = "Only In A", Latitude = 51.0, Longitude = 20.0, GoogleMapsUrl = "https://maps.google.com/onlyA" };
            var onlyB = new Poi { Name = "Only In B", Latitude = 52.0, Longitude = 21.0, GoogleMapsUrl = "https://maps.google.com/onlyB" };
            db.Pois.AddRange(shared, onlyA, onlyB);
            db.SaveChanges();

            // Shared exists in both with same URL
            var sharedInB = new Poi { Name = "Shared Place", Latitude = 50.0, Longitude = 19.0, GoogleMapsUrl = "https://maps.google.com/shared" };
            db.Pois.Add(sharedInB);
            db.SaveChanges();

            db.PoiCollectionItems.AddRange(
                new PoiCollectionItem { PoiId = shared.Id, PoiCollectionId = aId },
                new PoiCollectionItem { PoiId = onlyA.Id, PoiCollectionId = aId },
                new PoiCollectionItem { PoiId = sharedInB.Id, PoiCollectionId = bId },
                new PoiCollectionItem { PoiId = onlyB.Id, PoiCollectionId = bId }
            );
            db.SaveChanges();
        });

        var service = CreateService(factory);
        var result = await service.ExecuteAsync(SetOperation.Subtract, colAId, colBId);

        result.Pois.Should().HaveCount(1);
        result.Pois.Should().Contain(p => p.Name == "Only In A");
    }

    [Fact]
    public async Task Subtract_WithNoOverlap_ReturnsAllFromA()
    {
        var (factory, colAId, colBId) = SeedDatabase((db, aId, bId) =>
        {
            var a1 = new Poi { Name = "A1", Latitude = 50.0, Longitude = 19.0, GoogleMapsUrl = "https://maps.google.com/a1" };
            var a2 = new Poi { Name = "A2", Latitude = 51.0, Longitude = 20.0, GoogleMapsUrl = "https://maps.google.com/a2" };
            var b1 = new Poi { Name = "B1", Latitude = 60.0, Longitude = 30.0, GoogleMapsUrl = "https://maps.google.com/b1" };
            db.Pois.AddRange(a1, a2, b1);
            db.SaveChanges();

            db.PoiCollectionItems.AddRange(
                new PoiCollectionItem { PoiId = a1.Id, PoiCollectionId = aId },
                new PoiCollectionItem { PoiId = a2.Id, PoiCollectionId = aId },
                new PoiCollectionItem { PoiId = b1.Id, PoiCollectionId = bId }
            );
            db.SaveChanges();
        });

        var service = CreateService(factory);
        var result = await service.ExecuteAsync(SetOperation.Subtract, colAId, colBId);

        result.Pois.Should().HaveCount(2);
    }

    // --- Intersect ---

    [Fact]
    public async Task Intersect_ReturnsOnlyPoisPresentInBothAAndB()
    {
        var (factory, colAId, colBId) = SeedDatabase((db, aId, bId) =>
        {
            var sharedA = new Poi { Name = "Shared", Latitude = 50.0, Longitude = 19.0, GoogleMapsUrl = "https://maps.google.com/shared" };
            var onlyA = new Poi { Name = "Only A", Latitude = 51.0, Longitude = 20.0, GoogleMapsUrl = "https://maps.google.com/onlyA" };
            var sharedB = new Poi { Name = "Shared", Latitude = 50.0, Longitude = 19.0, GoogleMapsUrl = "https://maps.google.com/shared" };
            var onlyB = new Poi { Name = "Only B", Latitude = 52.0, Longitude = 21.0, GoogleMapsUrl = "https://maps.google.com/onlyB" };
            db.Pois.AddRange(sharedA, onlyA, sharedB, onlyB);
            db.SaveChanges();

            db.PoiCollectionItems.AddRange(
                new PoiCollectionItem { PoiId = sharedA.Id, PoiCollectionId = aId },
                new PoiCollectionItem { PoiId = onlyA.Id, PoiCollectionId = aId },
                new PoiCollectionItem { PoiId = sharedB.Id, PoiCollectionId = bId },
                new PoiCollectionItem { PoiId = onlyB.Id, PoiCollectionId = bId }
            );
            db.SaveChanges();
        });

        var service = CreateService(factory);
        var result = await service.ExecuteAsync(SetOperation.Intersect, colAId, colBId);

        result.Pois.Should().HaveCount(1);
        result.Pois.Should().Contain(p => p.Name == "Shared");
    }

    [Fact]
    public async Task Intersect_WithNoOverlap_ReturnsEmpty()
    {
        var (factory, colAId, colBId) = SeedDatabase((db, aId, bId) =>
        {
            var a1 = new Poi { Name = "A1", Latitude = 50.0, Longitude = 19.0, GoogleMapsUrl = "https://maps.google.com/a1" };
            var b1 = new Poi { Name = "B1", Latitude = 60.0, Longitude = 30.0, GoogleMapsUrl = "https://maps.google.com/b1" };
            db.Pois.AddRange(a1, b1);
            db.SaveChanges();

            db.PoiCollectionItems.AddRange(
                new PoiCollectionItem { PoiId = a1.Id, PoiCollectionId = aId },
                new PoiCollectionItem { PoiId = b1.Id, PoiCollectionId = bId }
            );
            db.SaveChanges();
        });

        var service = CreateService(factory);
        var result = await service.ExecuteAsync(SetOperation.Intersect, colAId, colBId);

        result.Pois.Should().BeEmpty();
    }

    // --- Union ---

    [Fact]
    public async Task Union_ReturnsAllUniquePoisFromBoth()
    {
        var (factory, colAId, colBId) = SeedDatabase((db, aId, bId) =>
        {
            var a1 = new Poi { Name = "A1", Latitude = 50.0, Longitude = 19.0, GoogleMapsUrl = "https://maps.google.com/a1" };
            var b1 = new Poi { Name = "B1", Latitude = 60.0, Longitude = 30.0, GoogleMapsUrl = "https://maps.google.com/b1" };
            db.Pois.AddRange(a1, b1);
            db.SaveChanges();

            db.PoiCollectionItems.AddRange(
                new PoiCollectionItem { PoiId = a1.Id, PoiCollectionId = aId },
                new PoiCollectionItem { PoiId = b1.Id, PoiCollectionId = bId }
            );
            db.SaveChanges();
        });

        var service = CreateService(factory);
        var result = await service.ExecuteAsync(SetOperation.Union, colAId, colBId);

        result.Pois.Should().HaveCount(2);
    }

    [Fact]
    public async Task Union_DeduplicatesMatchingPois()
    {
        var (factory, colAId, colBId) = SeedDatabase((db, aId, bId) =>
        {
            var a1 = new Poi { Name = "Shared", Latitude = 50.0, Longitude = 19.0, GoogleMapsUrl = "https://maps.google.com/shared" };
            var a2 = new Poi { Name = "Only A", Latitude = 51.0, Longitude = 20.0, GoogleMapsUrl = "https://maps.google.com/onlyA" };
            var b1 = new Poi { Name = "Shared", Latitude = 50.0, Longitude = 19.0, GoogleMapsUrl = "https://maps.google.com/shared" };
            var b2 = new Poi { Name = "Only B", Latitude = 60.0, Longitude = 30.0, GoogleMapsUrl = "https://maps.google.com/onlyB" };
            db.Pois.AddRange(a1, a2, b1, b2);
            db.SaveChanges();

            db.PoiCollectionItems.AddRange(
                new PoiCollectionItem { PoiId = a1.Id, PoiCollectionId = aId },
                new PoiCollectionItem { PoiId = a2.Id, PoiCollectionId = aId },
                new PoiCollectionItem { PoiId = b1.Id, PoiCollectionId = bId },
                new PoiCollectionItem { PoiId = b2.Id, PoiCollectionId = bId }
            );
            db.SaveChanges();
        });

        var service = CreateService(factory);
        var result = await service.ExecuteAsync(SetOperation.Union, colAId, colBId);

        // A has 2, B has 2, but 1 is shared => 3 unique
        result.Pois.Should().HaveCount(3);
    }

    // --- Dedup ---

    [Fact]
    public async Task Dedup_FindsAndRemovesDuplicates()
    {
        var (factory, colAId, _) = SeedDatabase((db, aId, bId) =>
        {
            var p1 = new Poi { Name = "Coffee Shop", Latitude = 50.0, Longitude = 19.0, GoogleMapsUrl = "https://maps.google.com/coffee" };
            var p2 = new Poi { Name = "Coffee Shop", Latitude = 50.0, Longitude = 19.0, GoogleMapsUrl = "https://maps.google.com/coffee" };
            var p3 = new Poi { Name = "Unique Place", Latitude = 60.0, Longitude = 30.0, GoogleMapsUrl = "https://maps.google.com/unique" };
            db.Pois.AddRange(p1, p2, p3);
            db.SaveChanges();

            db.PoiCollectionItems.AddRange(
                new PoiCollectionItem { PoiId = p1.Id, PoiCollectionId = aId },
                new PoiCollectionItem { PoiId = p2.Id, PoiCollectionId = aId },
                new PoiCollectionItem { PoiId = p3.Id, PoiCollectionId = aId }
            );
            db.SaveChanges();
        });

        var service = CreateService(factory);
        var result = await service.ExecuteAsync(SetOperation.Dedup, colAId, null);

        result.Pois.Should().HaveCount(2);
        result.DuplicateGroups.Should().NotBeNull();
        result.DuplicateGroups.Should().HaveCount(1);
    }

    [Fact]
    public async Task Dedup_WithNoDuplicates_ReturnsAllPois()
    {
        var (factory, colAId, _) = SeedDatabase((db, aId, bId) =>
        {
            var p1 = new Poi { Name = "Place A", Latitude = 50.0, Longitude = 19.0, GoogleMapsUrl = "https://maps.google.com/a" };
            var p2 = new Poi { Name = "Place B", Latitude = 60.0, Longitude = 30.0, GoogleMapsUrl = "https://maps.google.com/b" };
            db.Pois.AddRange(p1, p2);
            db.SaveChanges();

            db.PoiCollectionItems.AddRange(
                new PoiCollectionItem { PoiId = p1.Id, PoiCollectionId = aId },
                new PoiCollectionItem { PoiId = p2.Id, PoiCollectionId = aId }
            );
            db.SaveChanges();
        });

        var service = CreateService(factory);
        var result = await service.ExecuteAsync(SetOperation.Dedup, colAId, null);

        result.Pois.Should().HaveCount(2);
        result.DuplicateGroups.Should().BeEmpty();
    }

    // --- CommitResultAsync ---

    [Fact]
    public async Task CommitResultAsync_SavesResultAsNewCollection()
    {
        var dbName = Guid.NewGuid().ToString();
        var factory = TestDbHelper.CreateFactory(dbName);

        // Seed some POIs
        Poi poi1, poi2;
        await using (var db = await factory.CreateDbContextAsync())
        {
            poi1 = new Poi { Name = "P1", Latitude = 1, Longitude = 1 };
            poi2 = new Poi { Name = "P2", Latitude = 2, Longitude = 2 };
            db.Pois.AddRange(poi1, poi2);
            await db.SaveChangesAsync();
        }

        var service = CreateService(factory);
        var collection = await service.CommitResultAsync([poi1, poi2], "Result Set");

        collection.Name.Should().Be("Result Set");
        collection.SourceType.Should().Be("operation_result");
        collection.PoiCount.Should().Be(2);

        await using (var db = await factory.CreateDbContextAsync())
        {
            var saved = await db.PoiCollections.FirstOrDefaultAsync(c => c.Name == "Result Set");
            saved.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task CommitResultAsync_LinksExistingPoisToNewCollection()
    {
        var dbName = Guid.NewGuid().ToString();
        var factory = TestDbHelper.CreateFactory(dbName);

        Poi poi1, poi2;
        await using (var db = await factory.CreateDbContextAsync())
        {
            poi1 = new Poi { Name = "P1", Latitude = 1, Longitude = 1 };
            poi2 = new Poi { Name = "P2", Latitude = 2, Longitude = 2 };
            db.Pois.AddRange(poi1, poi2);
            await db.SaveChangesAsync();
        }

        var service = CreateService(factory);
        var collection = await service.CommitResultAsync([poi1, poi2], "Linked Set");

        await using (var db = await factory.CreateDbContextAsync())
        {
            var items = await db.PoiCollectionItems
                .Where(ci => ci.PoiCollectionId == collection.Id)
                .ToListAsync();
            items.Should().HaveCount(2);
            items.Select(i => i.PoiId).Should().Contain(poi1.Id);
            items.Select(i => i.PoiId).Should().Contain(poi2.Id);
        }
    }

    [Fact]
    public async Task CommitResultAsync_SkipsDanglingPoiIds_OverRealFkConstraint()
    {
        // The in-memory EF provider does NOT enforce foreign keys, so this
        // scenario (a previewed Poi physically deleted by a whole-DB dedup
        // pass before commit) is only reproducible against real SQLite.
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        var factory = new SqliteContextFactory(options);

        Poi survivor, deleted;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            survivor = new Poi { Name = "Survivor", Latitude = 1, Longitude = 1 };
            deleted = new Poi { Name = "Deleted", Latitude = 2, Longitude = 2 };
            db.Pois.AddRange(survivor, deleted);
            await db.SaveChangesAsync();

            // Simulate the whole-DB dedup removing the previewed row.
            db.Pois.Remove(deleted);
            await db.SaveChangesAsync();
        }

        var service = CreateService(factory);

        // The stale 'deleted' row would violate the real FK on Poi.Id; the
        // commit must drop it instead of throwing and aborting.
        var commit = () => service.CommitResultAsync([survivor, deleted], "Result Set");
        var collection = await commit.Should().NotThrowAsync();
        collection.Which.PoiCount.Should().Be(1);

        await using (var db = factory.CreateDbContext())
        {
            var items = await db.PoiCollectionItems
                .Where(ci => ci.PoiCollectionId == collection.Which.Id)
                .ToListAsync();
            items.Should().ContainSingle().Which.PoiId.Should().Be(survivor.Id);
        }
    }

    private sealed class SqliteContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    // --- Error handling ---

    [Fact]
    public async Task ExecuteAsync_ThrowsWhenCollectionBIsNullForBinaryOp()
    {
        var (factory, colAId, _) = SeedDatabase((db, aId, bId) =>
        {
            var p = new Poi { Name = "P", Latitude = 1, Longitude = 1 };
            db.Pois.Add(p);
            db.SaveChanges();
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = p.Id, PoiCollectionId = aId });
            db.SaveChanges();
        });

        var service = CreateService(factory);

        var act = () => service.ExecuteAsync(SetOperation.Subtract, colAId, null);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // --- Proximity-based matching ---

    [Fact]
    public async Task Intersect_MatchesByNameAndProximity()
    {
        var (factory, colAId, colBId) = SeedDatabase((db, aId, bId) =>
        {
            // Same name, very close coordinates, no URL => should match by proximity
            var a1 = new Poi { Name = "Central Park", Latitude = 50.00000, Longitude = 19.00000 };
            var b1 = new Poi { Name = "Central Park", Latitude = 50.00001, Longitude = 19.00001 };
            var a2 = new Poi { Name = "Unique A", Latitude = 60.0, Longitude = 30.0 };
            db.Pois.AddRange(a1, b1, a2);
            db.SaveChanges();

            db.PoiCollectionItems.AddRange(
                new PoiCollectionItem { PoiId = a1.Id, PoiCollectionId = aId },
                new PoiCollectionItem { PoiId = a2.Id, PoiCollectionId = aId },
                new PoiCollectionItem { PoiId = b1.Id, PoiCollectionId = bId }
            );
            db.SaveChanges();
        });

        var service = CreateService(factory);
        var result = await service.ExecuteAsync(SetOperation.Intersect, colAId, colBId);

        result.Pois.Should().HaveCount(1);
        result.Pois.Should().Contain(p => p.Name == "Central Park");
    }
}