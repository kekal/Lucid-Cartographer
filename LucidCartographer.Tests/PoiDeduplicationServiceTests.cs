using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests;

/// <summary>
/// Exercises the whole-database deduplication engine end to end against an
/// in-memory SQLite DB. The engine combines <see cref="PoiMatcher.FindDuplicateGroups"/>
/// with the shared pair-merge mechanics, so these tests double as a guard on
/// the group-wide behaviour (multi-member groups, place-id-over-coords,
/// link union, idempotence) that the per-row post-enrichment dedup never
/// hits.
/// </summary>
public class PoiDeduplicationServiceTests
{
    // A canonical /maps/place/ URL carrying a stable feature id. Two rows with
    // the same ftid are the same place regardless of coordinate drift.
    private static string PlaceUrl(string ftid, double lat, double lon)
        => $"https://www.google.com/maps/place/X/@{lat},{lon},17z/data=!3m1!4b1!4m6!3m5!1s{ftid}!8m2!3d{lat}!4d{lon}";

    private static Poi Enriched(string name, double lat, double lon, string? url = null)
        => new()
        {
            Name = name,
            Latitude = lat,
            Longitude = lon,
            GoogleMapsUrl = url,
            IsEnriched = true,
            AddedDate = DateTime.UtcNow
        };

    private static PoiDeduplicationService NewService(IDbContextFactory<AppDbContext> factory)
        => new(factory, new PoiMatcher(), new SqliteWriteLock(), NullLogger<PoiDeduplicationService>.Instance);

    [Fact]
    public async Task DeduplicateAll_CleanDatabase_MergesNothing()
    {
        var factory = TestDbHelper.CreateFactory();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Pois.AddRange(
                Enriched("Wawel", 50.0540, 19.9354),
                Enriched("Hala Stulecia", 51.1069, 17.0772));
            await seed.SaveChangesAsync();
        }

        var result = await NewService(factory).DeduplicateAllAsync();

        result.Should().Be(new DedupResult(0, 0));
        await using var check = await factory.CreateDbContextAsync();
        (await check.Pois.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task DeduplicateAll_SamePlaceId_DifferentCoords_MergesIntoSmallerId()
    {
        var factory = TestDbHelper.CreateFactory();
        const string ftid = "0x47045b3f13482675:0xc522afd5119f73c7";
        int canonicalId, duplicateId;

        await using (var seed = await factory.CreateDbContextAsync())
        {
            // Same Google feature id, but coordinates drifted ~50km apart —
            // place id must decide identity, so they still merge.
            var a = Enriched("Place", 52.00, 21.00, PlaceUrl(ftid, 52.00, 21.00));
            var b = Enriched("Place", 52.50, 21.40, PlaceUrl(ftid, 52.50, 21.40));
            seed.Pois.AddRange(a, b);
            await seed.SaveChangesAsync();
            canonicalId = Math.Min(a.Id, b.Id);
            duplicateId = Math.Max(a.Id, b.Id);
        }

        var result = await NewService(factory).DeduplicateAllAsync();

        result.Should().Be(new DedupResult(1, 1));
        await using var check = await factory.CreateDbContextAsync();
        var rows = await check.Pois.ToListAsync();
        rows.Should().ContainSingle().Which.Id.Should().Be(canonicalId);
        rows.Should().NotContain(p => p.Id == duplicateId);
    }

    [Fact]
    public async Task DeduplicateAll_ProximityAndName_NoUrl_Merges()
    {
        var factory = TestDbHelper.CreateFactory();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Pois.AddRange(
                Enriched("Bieszczadzkie Drezyny", 49.30000, 22.50000),
                Enriched("Bieszczadzkie Drezyny", 49.30001, 22.50001));
            await seed.SaveChangesAsync();
        }

        var result = await NewService(factory).DeduplicateAllAsync();

        result.Should().Be(new DedupResult(1, 1));
        await using var check = await factory.CreateDbContextAsync();
        (await check.Pois.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DeduplicateAll_SameNameFarApart_NotMerged()
    {
        var factory = TestDbHelper.CreateFactory();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Pois.AddRange(
                Enriched("Plac zabaw", 52.2297, 21.0122),  // Warsaw
                Enriched("Plac zabaw", 50.0647, 19.9450)); // Kraków, 250+ km
            await seed.SaveChangesAsync();
        }

        var result = await NewService(factory).DeduplicateAllAsync();

        result.Should().Be(new DedupResult(0, 0));
        await using var check = await factory.CreateDbContextAsync();
        (await check.Pois.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task DeduplicateAll_ManualAndEnrichedSamePlace_Merges()
    {
        var factory = TestDbHelper.CreateFactory();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            // A manually-added located POI and an enriched one for the same
            // place — identity ignores IsEnriched, so they still collapse.
            var manual = new Poi
            {
                Name = "Farm", Latitude = 50.10, Longitude = 20.10,
                IsEnriched = false, AddedDate = DateTime.UtcNow
            };
            var enriched = Enriched("Farm", 50.10001, 20.10001, "https://www.google.com/maps/place/farm");
            seed.Pois.AddRange(manual, enriched);
            await seed.SaveChangesAsync();
        }

        var result = await NewService(factory).DeduplicateAllAsync();

        result.Should().Be(new DedupResult(1, 1));
        await using var check = await factory.CreateDbContextAsync();
        (await check.Pois.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DeduplicateAll_ThreeMemberGroup_FoldsAllIntoCanonical_AndUnionsCollectionLinks()
    {
        var factory = TestDbHelper.CreateFactory();
        const string ftid = "0x111111111111aaaa:0x222222222222bbbb";
        int canonicalId, c1, c2, c3;

        await using (var seed = await factory.CreateDbContextAsync())
        {
            var col1 = new PoiCollection { Name = "One", Color = "#001", CreatedDate = DateTime.UtcNow };
            var col2 = new PoiCollection { Name = "Two", Color = "#002", CreatedDate = DateTime.UtcNow };
            var col3 = new PoiCollection { Name = "Three", Color = "#003", CreatedDate = DateTime.UtcNow };
            seed.PoiCollections.AddRange(col1, col2, col3);

            var a = Enriched("Same", 40.0, 10.0, PlaceUrl(ftid, 40.0, 10.0));
            var b = Enriched("Same", 40.0, 10.0, PlaceUrl(ftid, 40.0, 10.0));
            var c = Enriched("Same", 40.0, 10.0, PlaceUrl(ftid, 40.0, 10.0));
            seed.Pois.AddRange(a, b, c);
            await seed.SaveChangesAsync();

            seed.PoiCollectionItems.AddRange(
                new PoiCollectionItem { PoiCollectionId = col1.Id, PoiId = a.Id },
                new PoiCollectionItem { PoiCollectionId = col2.Id, PoiId = b.Id },
                new PoiCollectionItem { PoiCollectionId = col3.Id, PoiId = c.Id });
            await seed.SaveChangesAsync();

            canonicalId = new[] { a.Id, b.Id, c.Id }.Min();
            c1 = col1.Id; c2 = col2.Id; c3 = col3.Id;
        }

        var result = await NewService(factory).DeduplicateAllAsync();

        result.Should().Be(new DedupResult(1, 2), "one place group with two duplicates folded in");
        await using var check = await factory.CreateDbContextAsync();
        var rows = await check.Pois.ToListAsync();
        rows.Should().ContainSingle().Which.Id.Should().Be(canonicalId);

        var links = await check.PoiCollectionItems.Where(ci => ci.PoiId == canonicalId).ToListAsync();
        links.Select(l => l.PoiCollectionId).Should().BeEquivalentTo(new[] { c1, c2, c3 },
            "all three collection memberships must survive on the surviving row");
    }

    [Fact]
    public async Task DeduplicateAll_TwoIndependentGroups_CountsBoth()
    {
        var factory = TestDbHelper.CreateFactory();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            // Group 1: two near Warsaw. Group 2: three near Kraków.
            seed.Pois.AddRange(
                Enriched("Cafe A", 52.2300, 21.0100),
                Enriched("Cafe A", 52.23001, 21.01001),
                Enriched("Bar B", 50.0600, 19.9400),
                Enriched("Bar B", 50.06001, 19.94001),
                Enriched("Bar B", 50.060005, 19.940005),
                Enriched("Lonely", 48.8566, 2.3522)); // Paris, no dup
            await seed.SaveChangesAsync();
        }

        var result = await NewService(factory).DeduplicateAllAsync();

        result.GroupsMerged.Should().Be(2);
        result.PoisMerged.Should().Be(3, "1 from the cafe pair + 2 from the bar trio");
        await using var check = await factory.CreateDbContextAsync();
        (await check.Pois.CountAsync()).Should().Be(3, "one survivor per group + the lonely Paris row");
    }
}
