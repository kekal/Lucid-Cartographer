using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests;

/// <summary>
/// Exercises the whole-database deduplication engine end to end against the
/// EF Core InMemory provider (see <see cref="TestDbHelper"/>). The engine
/// combines <see cref="PoiMatcher.FindDuplicateGroups"/> with the shared
/// pair-merge mechanics, so these tests double as a guard on the group-wide
/// behaviour (multi-member groups, place-id-over-coords, field backfill,
/// link union, idempotence) that the per-row post-enrichment dedup never
/// hits.
///
/// NOTE: the InMemory provider is non-relational and does NOT enforce the
/// [ConcurrencyCheck] Version token, so the DbUpdateException abort branch
/// in <see cref="PoiDeduplicationService.DeduplicateAllAsync"/> is NOT
/// covered here — these tests assert field/flag merge outcomes, not
/// concurrency behaviour.
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

    [Fact]
    public async Task DeduplicateAll_RichDuplicateFoldedIntoSparseCanonical_BackfillsAllFields()
    {
        // DEDUP-1: the lowest-Id row is kept as canonical, but it may be an old
        // sparse stub. When a richer higher-Id duplicate is folded in and then
        // hard-deleted, its merge-worthy fields must survive on the canonical —
        // otherwise the richness is lost for good.
        var factory = TestDbHelper.CreateFactory();
        const string ftid = "0x47045b3f13482675:0xc522afd5119f73c7";
        int canonicalId;

        await using (var seed = await factory.CreateDbContextAsync())
        {
            // Sparse canonical (added first → smaller Id). Same place id as the
            // duplicate so they merge regardless of coord drift.
            var sparse = Enriched("Old Stub", 50.00, 20.00, PlaceUrl(ftid, 50.00, 20.00));

            // Rich duplicate (added second → larger Id) carrying all the fields
            // the canonical is missing.
            var rich = Enriched("Old Stub", 50.00001, 20.00001, PlaceUrl(ftid, 50.00001, 20.00001));
            rich.Category = "restaurant";
            rich.Notes = "Great pierogi";
            rich.Rating = 5;
            rich.GoogleRating = 4.6;
            rich.ReviewCount = 1234;
            rich.Country = "Poland";
            rich.Region = "Lesser Poland";

            seed.Pois.AddRange(sparse, rich);
            await seed.SaveChangesAsync();
            canonicalId = Math.Min(sparse.Id, rich.Id);
        }

        var result = await NewService(factory).DeduplicateAllAsync();

        result.Should().Be(new DedupResult(1, 1));
        await using var check = await factory.CreateDbContextAsync();
        var survivor = await check.Pois.SingleAsync();
        survivor.Id.Should().Be(canonicalId, "smaller-Id row stays canonical");
        survivor.Category.Should().Be("restaurant");
        survivor.Notes.Should().Be("Great pierogi");
        survivor.Rating.Should().Be(5);
        survivor.GoogleRating.Should().Be(4.6);
        survivor.ReviewCount.Should().Be(1234);
        survivor.Country.Should().Be("Poland");
        survivor.Region.Should().Be("Lesser Poland");
    }

    [Fact]
    public async Task DeduplicateAll_SoftFailCanonicalGainsPlaceUrl_ClearsNeedsManualUrl()
    {
        // ENR-1: a soft-failed canonical (no place URL, NeedsManualUrl=true) that
        // inherits a /maps/place/ URL from a better-resolved duplicate must have
        // its stale NeedsManualUrl flag reconciled to false — otherwise McpDtos
        // expose HasPlaceUrl=true && NeedsManualUrl=true, baiting an agent into
        // nulling the good coords.
        var factory = TestDbHelper.CreateFactory();
        const string ftid = "0x471111111111aaaa:0x472222222222bbbb";
        int canonicalId;

        await using (var seed = await factory.CreateDbContextAsync())
        {
            // Soft-fail canonical: real coords, IsEnriched, needs manual URL, no
            // GoogleMapsUrl. Added first → smaller Id → canonical.
            var softFail = new Poi
            {
                Name = "Lookout Tower",
                Latitude = 49.5000,
                Longitude = 22.0000,
                GoogleMapsUrl = null,
                IsEnriched = true,
                EnrichmentNeedsManualUrl = true,
                AddedDate = DateTime.UtcNow
            };

            // Newer duplicate that resolved a real /maps/place/ URL. No ftid on
            // the canonical, so these match by name + proximity.
            var resolved = Enriched("Lookout Tower", 49.50001, 22.00001, PlaceUrl(ftid, 49.50001, 22.00001));

            seed.Pois.AddRange(softFail, resolved);
            await seed.SaveChangesAsync();
            canonicalId = Math.Min(softFail.Id, resolved.Id);
        }

        var result = await NewService(factory).DeduplicateAllAsync();

        result.Should().Be(new DedupResult(1, 1));
        await using var check = await factory.CreateDbContextAsync();
        var survivor = await check.Pois.SingleAsync();
        survivor.Id.Should().Be(canonicalId);
        survivor.GoogleMapsUrl.Should().Contain("/maps/place/", "the place URL was backfilled from the duplicate");
        survivor.EnrichmentNeedsManualUrl.Should().BeFalse(
            "a canonical that now has a /maps/place/ URL no longer needs a manual URL");
    }

    [Fact]
    public async Task DeduplicateAll_TransitiveBridgeAcrossDistance_DoesNotMergeFarRow()
    {
        // DEDUP-2: union-find can transitively group A~B (same place id, drifted
        // coords) with B~C (same name, nearby) even when A and C are hundreds of
        // km apart. The merge must re-validate each pair: B (same id as A) still
        // folds in, but the far-apart C must be left untouched, not deleted.
        var factory = TestDbHelper.CreateFactory();
        const string ftid = "0x47abc0000000aaaa:0x47def0000000bbbb";
        int canonicalId, farId;

        await using (var seed = await factory.CreateDbContextAsync())
        {
            // A: Warsaw, carries the place id. Added first → canonical.
            var a = Enriched("Wieża", 52.2297, 21.0122, PlaceUrl(ftid, 52.2297, 21.0122));
            // B: same place id as A but coords drifted ~250km to Kraków — must
            // still merge (place id wins over distance).
            var b = Enriched("Wieża", 50.0647, 19.9450, PlaceUrl(ftid, 50.0647, 19.9450));
            // C: same name, ~1.5m from B, NO place id → bridges to B by
            // name+proximity but is far from A. Must NOT be folded into A.
            var c = Enriched("Wieża", 50.06471, 19.94501);

            seed.Pois.AddRange(a, b, c);
            await seed.SaveChangesAsync();
            canonicalId = a.Id;
            farId = c.Id;
        }

        var result = await NewService(factory).DeduplicateAllAsync();

        result.Should().Be(new DedupResult(1, 1),
            "only B (same place id) folds into A; the far-apart C is skipped");
        await using var check = await factory.CreateDbContextAsync();
        var rows = await check.Pois.ToListAsync();
        rows.Select(p => p.Id).Should().BeEquivalentTo(new[] { canonicalId, farId },
            "the canonical and the far row survive; only the drifted same-id duplicate is merged");
    }

    [Fact]
    public async Task DeduplicateAll_TransitiveChain_SkippedSubsetCollapsesIntoSecondaryCanonical()
    {
        // DEDUP-2 follow-up: the original fix re-validated each member against the
        // single lowest-Id canonical and SKIPPED any that did not match, leaving
        // them "for a future pass". But when the skipped members are a genuine
        // same-place pair that the canonical happens not to match, that future
        // pass re-forms the identical group and re-skips them — they never
        // collapse. This is the A(far) ~ B ~ C(near each other) shape: A is the
        // canonical, the near rows are B and C below (here named near1/near2),
        // and A matches NEITHER of them directly.
        //
        // A can only be unioned into the group through a member it DOES match, so
        // a place-id-drifted bridge (same ftid as A, sitting among the near rows)
        // is what pulls A in — exactly the drift mechanic the DEDUP-2 test uses.
        // The bridge folds into A by place id; the near pair, far from A, must now
        // re-cluster and collapse into a SECONDARY canonical instead of surviving
        // as two stranded singletons. A survives as its own place.
        var factory = TestDbHelper.CreateFactory();
        const string ftid = "0x47abc0000000aaaa:0x47def0000000bbbb";
        int canonicalId, near1Id, near2Id, bridgeId;

        await using (var seed = await factory.CreateDbContextAsync())
        {
            // A: Warsaw, carries the place id. Added first → lowest Id → primary
            // canonical and far-apart survivor.
            var a = Enriched("Wieża", 52.2297, 21.0122, PlaceUrl(ftid, 52.2297, 21.0122));
            // Bridge: same place id as A but coords drifted ~250km to Kraków, and
            // physically among the near pair. Folds into A by place id.
            var bridge = Enriched("Wieża", 50.06400, 19.94500, PlaceUrl(ftid, 50.06400, 19.94500));
            // near1 / near2: same name, no place id, a few metres apart in Kraków.
            // Each bridges to the group by name+proximity but is far from A. They
            // are the genuine same-place pair that must collapse together.
            var near1 = Enriched("Wieża", 50.06401, 19.94501);
            var near2 = Enriched("Wieża", 50.06402, 19.94502);

            seed.Pois.AddRange(a, bridge, near1, near2);
            await seed.SaveChangesAsync();
            canonicalId = a.Id;
            bridgeId = bridge.Id;
            near1Id = near1.Id;
            near2Id = near2.Id;
        }

        var result = await NewService(factory).DeduplicateAllAsync();

        // Two merges: the drifted bridge folds into A, and near2 folds into the
        // secondary canonical near1 — one transitive group, two duplicates gone.
        result.Should().Be(new DedupResult(1, 2),
            "the place-id bridge folds into A AND the far-apart near pair collapses into its own canonical");
        await using var check = await factory.CreateDbContextAsync();
        var rows = await check.Pois.ToListAsync();
        rows.Select(p => p.Id).Should().BeEquivalentTo(new[] { canonicalId, near1Id },
            "A survives as one place; the near pair collapses into the lower-Id near row (secondary canonical), " +
            "while the bridge and the higher-Id near duplicate are removed");
        rows.Select(p => p.Id).Should().NotContain(bridgeId);
        rows.Select(p => p.Id).Should().NotContain(near2Id);
    }
}
