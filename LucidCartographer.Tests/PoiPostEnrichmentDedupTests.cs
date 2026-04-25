using FluentAssertions;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Enrichment;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Tests;

/// <summary>
/// Verifies the post-enrichment dedup helper fired by
/// <c>PoiEnrichmentBackgroundService</c> once a row finishes
/// enrichment. Exercises the helper directly against an in-memory
/// SQLite database so we don't need to spin up Playwright.
/// </summary>
public class PoiPostEnrichmentDedupTests
{
    private static Poi NewEnriched(string name, double lat, double lon, string? url)
        => new()
        {
            Name = name,
            Latitude = lat,
            Longitude = lon,
            GoogleMapsUrl = url,
            IsEnriched = true,
            AddedDate = DateTime.UtcNow,
            Status = "imported"
        };

    [Fact]
    public async Task MergeIfDuplicate_UrlMatch_SingleCollection_FoldsDuplicateLinkOntoCanonical()
    {
        var factory = TestDbHelper.CreateFactory();
        int collectionId, canonicalId, duplicateId;

        await using (var seed = await factory.CreateDbContextAsync())
        {
            var col = new PoiCollection { Name = "C", Color = "#000", CreatedDate = DateTime.UtcNow };
            seed.PoiCollections.Add(col);
            var a = NewEnriched("Place", 52.0, 21.0, "https://maps.google.com/place/X");
            var b = NewEnriched("Place", 52.0, 21.0, "https://maps.google.com/place/X");
            seed.Pois.AddRange(a, b);
            await seed.SaveChangesAsync();
            seed.PoiCollectionItems.AddRange(
                new PoiCollectionItem { PoiCollectionId = col.Id, PoiId = a.Id },
                new PoiCollectionItem { PoiCollectionId = col.Id, PoiId = b.Id });
            await seed.SaveChangesAsync();
            collectionId = col.Id;
            canonicalId = a.Id;
            duplicateId = b.Id;
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var duplicate = await db.Pois.FirstAsync(p => p.Id == duplicateId);
            var merged = await PoiPostEnrichmentDedup.MergeIfDuplicateAsync(db, duplicate, CancellationToken.None);
            merged.Should().BeTrue();
        }

        await using (var check = await factory.CreateDbContextAsync())
        {
            var pois = await check.Pois.ToListAsync();
            pois.Should().HaveCount(1);
            pois.Single().Id.Should().Be(canonicalId);

            var links = await check.PoiCollectionItems
                .Where(ci => ci.PoiCollectionId == collectionId)
                .ToListAsync();
            links.Should().HaveCount(1, "the duplicate link must be dropped so the managed-sources counter stays truthful");
            links.Single().PoiId.Should().Be(canonicalId);
        }
    }

    [Fact]
    public async Task MergeIfDuplicate_UrlMatch_MultipleCollections_CanonicalPicksUpAllLinks()
    {
        var factory = TestDbHelper.CreateFactory();
        int c1, c2, canonicalId, duplicateId;

        await using (var seed = await factory.CreateDbContextAsync())
        {
            var col1 = new PoiCollection { Name = "Old", Color = "#000", CreatedDate = DateTime.UtcNow };
            var col2 = new PoiCollection { Name = "New", Color = "#111", CreatedDate = DateTime.UtcNow };
            seed.PoiCollections.AddRange(col1, col2);
            var a = NewEnriched("Same Place", 50.0, 20.0, "https://maps.google.com/place/Y");
            var b = NewEnriched("Same Place", 50.0, 20.0, "https://maps.google.com/place/Y");
            seed.Pois.AddRange(a, b);
            await seed.SaveChangesAsync();
            seed.PoiCollectionItems.AddRange(
                new PoiCollectionItem { PoiCollectionId = col1.Id, PoiId = a.Id },
                new PoiCollectionItem { PoiCollectionId = col2.Id, PoiId = b.Id });
            await seed.SaveChangesAsync();
            c1 = col1.Id;
            c2 = col2.Id;
            canonicalId = a.Id;
            duplicateId = b.Id;
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var duplicate = await db.Pois.FirstAsync(p => p.Id == duplicateId);
            (await PoiPostEnrichmentDedup.MergeIfDuplicateAsync(db, duplicate, CancellationToken.None))
                .Should().BeTrue();
        }

        await using (var check = await factory.CreateDbContextAsync())
        {
            (await check.Pois.CountAsync()).Should().Be(1);
            (await check.PoiCollectionItems.CountAsync(ci => ci.PoiCollectionId == c1 && ci.PoiId == canonicalId)).Should().Be(1);
            (await check.PoiCollectionItems.CountAsync(ci => ci.PoiCollectionId == c2 && ci.PoiId == canonicalId)).Should().Be(1,
                "the duplicate's link should be redirected onto the canonical row, not deleted");
        }
    }

    [Fact]
    public async Task MergeIfDuplicate_ProximityMatch_NoUrl_SameName_MergesWithin100m()
    {
        var factory = TestDbHelper.CreateFactory();
        int canonicalId, duplicateId;

        await using (var seed = await factory.CreateDbContextAsync())
        {
            // Two points ~1m apart, same name, no URL — this is the
            // "enrichment returned coords but no URL" shape.
            var a = NewEnriched("Bieszczadzkie Drezyny", 49.3000, 22.5000, null);
            var b = NewEnriched("Bieszczadzkie Drezyny", 49.30001, 22.50001, null);
            seed.Pois.AddRange(a, b);
            await seed.SaveChangesAsync();
            canonicalId = a.Id;
            duplicateId = b.Id;
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var duplicate = await db.Pois.FirstAsync(p => p.Id == duplicateId);
            (await PoiPostEnrichmentDedup.MergeIfDuplicateAsync(db, duplicate, CancellationToken.None))
                .Should().BeTrue();
        }

        await using (var check = await factory.CreateDbContextAsync())
        {
            var remaining = await check.Pois.ToListAsync();
            remaining.Should().HaveCount(1);
            remaining.Single().Id.Should().Be(canonicalId);
        }
    }

    [Fact]
    public async Task MergeIfDuplicate_SameName_FarApart_IsNotMerged()
    {
        var factory = TestDbHelper.CreateFactory();
        int duplicateId;

        await using (var seed = await factory.CreateDbContextAsync())
        {
            // Two "Plac zabaw" (playground) entries in different Polish
            // cities — distinct places that should NOT be collapsed.
            var warsaw = NewEnriched("Plac zabaw", 52.2297, 21.0122, null);
            var krakow = NewEnriched("Plac zabaw", 50.0647, 19.9450, null); // 250+ km away
            seed.Pois.AddRange(warsaw, krakow);
            await seed.SaveChangesAsync();
            duplicateId = krakow.Id;
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var kr = await db.Pois.FirstAsync(p => p.Id == duplicateId);
            (await PoiPostEnrichmentDedup.MergeIfDuplicateAsync(db, kr, CancellationToken.None))
                .Should().BeFalse("distance >> 100m — these are different playgrounds");
        }

        await using (var check = await factory.CreateDbContextAsync())
        {
            (await check.Pois.CountAsync()).Should().Be(2);
        }
    }

    [Fact]
    public async Task MergeIfDuplicate_CalledOnSmallerIdRow_IsNoOp()
    {
        // Deterministic race guard: only the worker holding the larger
        // Id acts. Calling the helper on the older row finds no
        // smaller-Id canonical and returns false.
        var factory = TestDbHelper.CreateFactory();
        int canonicalId;

        await using (var seed = await factory.CreateDbContextAsync())
        {
            var a = NewEnriched("X", 1.0, 1.0, "https://maps.google.com/place/Z");
            var b = NewEnriched("X", 1.0, 1.0, "https://maps.google.com/place/Z");
            seed.Pois.AddRange(a, b);
            await seed.SaveChangesAsync();
            canonicalId = a.Id;
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var older = await db.Pois.FirstAsync(p => p.Id == canonicalId);
            (await PoiPostEnrichmentDedup.MergeIfDuplicateAsync(db, older, CancellationToken.None))
                .Should().BeFalse();
        }

        await using (var check = await factory.CreateDbContextAsync())
        {
            (await check.Pois.CountAsync()).Should().Be(2,
                "helper must be a no-op when invoked on the canonical (smaller-Id) row");
        }
    }

    [Fact]
    public async Task MergeIfDuplicate_UnenrichedRow_IsNoOp()
    {
        var factory = TestDbHelper.CreateFactory();
        int rowId;

        await using (var seed = await factory.CreateDbContextAsync())
        {
            var a = NewEnriched("Y", 2.0, 2.0, "https://maps.google.com/place/A");
            var pending = new Poi
            {
                Name = "Y",
                Latitude = 0,
                Longitude = 0,
                IsEnriched = false,
                Status = "imported",
                AddedDate = DateTime.UtcNow
            };
            seed.Pois.AddRange(a, pending);
            await seed.SaveChangesAsync();
            rowId = pending.Id;
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var pending = await db.Pois.FirstAsync(p => p.Id == rowId);
            (await PoiPostEnrichmentDedup.MergeIfDuplicateAsync(db, pending, CancellationToken.None))
                .Should().BeFalse("unenriched rows are never touched — they'll be checked when they themselves finish enrichment");
        }

        await using (var check = await factory.CreateDbContextAsync())
        {
            (await check.Pois.CountAsync()).Should().Be(2);
        }
    }

    [Fact]
    public async Task MergeIfDuplicate_OlderRowNotEnrichedButLocated_MergesIntoOlderCanonical()
    {
        var factory = TestDbHelper.CreateFactory();
        int olderId, newerId;

        await using (var seed = await factory.CreateDbContextAsync())
        {
            var older = new Poi
            {
                Name = "Alpaki Fajne Sprawy Habdzin",
                Latitude = 52.091133,
                Longitude = 21.161223,
                IsEnriched = false,
                Status = "imported",
                AddedDate = DateTime.UtcNow,
                Address = null,
                Phone = null
            };
            var newer = NewEnriched(
                "Alpaki Fajne Sprawy Habdzin",
                52.091133,
                21.161223,
                "https://www.google.com/maps/place/alpaki");
            newer.Address = "Habdzin 61a";
            newer.Phone = "503 302 302";

            seed.Pois.AddRange(older, newer);
            await seed.SaveChangesAsync();
            olderId = older.Id;
            newerId = newer.Id;
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var newer = await db.Pois.FirstAsync(p => p.Id == newerId);
            (await PoiPostEnrichmentDedup.MergeIfDuplicateAsync(db, newer, CancellationToken.None))
                .Should().BeTrue();
        }

        await using (var check = await factory.CreateDbContextAsync())
        {
            var rows = await check.Pois.ToListAsync();
            rows.Should().HaveCount(1);
            rows.Single().Id.Should().Be(olderId);
            rows.Single().Address.Should().Be("Habdzin 61a");
            rows.Single().Phone.Should().Be("503 302 302");
        }
    }

    [Fact]
    public async Task MergeIfDuplicate_NoCollision_IsNoOp()
    {
        var factory = TestDbHelper.CreateFactory();
        int rowId;

        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Pois.Add(NewEnriched("Alone", 3.0, 3.0, "https://maps.google.com/place/Solo"));
            await seed.SaveChangesAsync();
            rowId = seed.Pois.Single().Id;
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = await db.Pois.FirstAsync(p => p.Id == rowId);
            (await PoiPostEnrichmentDedup.MergeIfDuplicateAsync(db, row, CancellationToken.None))
                .Should().BeFalse();
        }

        await using (var check = await factory.CreateDbContextAsync())
        {
            (await check.Pois.CountAsync()).Should().Be(1);
        }
    }

    [Fact]
    public async Task MergeIfDuplicate_CanonicalHasNoImage_ReassignsDuplicatesImage()
    {
        var factory = TestDbHelper.CreateFactory();
        int canonicalId, duplicateId;
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header-ish

        await using (var seed = await factory.CreateDbContextAsync())
        {
            var a = NewEnriched("Gallery", 51.5, -0.1, "https://maps.google.com/place/gal");
            var b = NewEnriched("Gallery", 51.5, -0.1, "https://maps.google.com/place/gal");
            seed.Pois.AddRange(a, b);
            await seed.SaveChangesAsync();
            canonicalId = a.Id;
            duplicateId = b.Id;

            // Only the duplicate (newer, larger Id) carries an image.
            seed.PoiImages.Add(new PoiImage
            {
                PoiId = duplicateId,
                Data = imageBytes,
                ContentType = "image/png"
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var duplicate = await db.Pois.FirstAsync(p => p.Id == duplicateId);
            (await PoiPostEnrichmentDedup.MergeIfDuplicateAsync(db, duplicate, CancellationToken.None))
                .Should().BeTrue();
        }

        await using (var check = await factory.CreateDbContextAsync())
        {
            // Image survived the merge by being reassigned to the canonical row.
            var images = await check.PoiImages.ToListAsync();
            images.Should().HaveCount(1);
            images.Single().PoiId.Should().Be(canonicalId);
            images.Single().Data.Should().Equal(imageBytes);
            images.Single().ContentType.Should().Be("image/png");
        }
    }

    [Fact]
    public async Task MergeIfDuplicate_CanonicalMissingMetadata_BackfillsFromDuplicate()
    {
        var factory = TestDbHelper.CreateFactory();
        int canonicalId, duplicateId;

        await using (var seed = await factory.CreateDbContextAsync())
        {
            var canonical = NewEnriched("Farm", 50.1, 20.1, null);
            canonical.Address = null;
            canonical.Phone = null;
            canonical.Website = null;
            canonical.ImageUrl = null;

            var duplicate = NewEnriched("Farm", 50.1, 20.1, "https://www.google.com/maps/place/farm");
            duplicate.Address = "Habdzin 1";
            duplicate.Phone = "+48 123 456 789";
            duplicate.Website = "https://example.test";
            duplicate.ImageUrl = "https://lh3.googleusercontent.com/photo=w1024";

            seed.Pois.AddRange(canonical, duplicate);
            await seed.SaveChangesAsync();
            canonicalId = canonical.Id;
            duplicateId = duplicate.Id;
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var duplicate = await db.Pois.FirstAsync(p => p.Id == duplicateId);
            (await PoiPostEnrichmentDedup.MergeIfDuplicateAsync(db, duplicate, CancellationToken.None))
                .Should().BeTrue();
        }

        await using (var check = await factory.CreateDbContextAsync())
        {
            var canonical = await check.Pois.SingleAsync(p => p.Id == canonicalId);
            canonical.Address.Should().Be("Habdzin 1");
            canonical.Phone.Should().Be("+48 123 456 789");
            canonical.Website.Should().Be("https://example.test");
            canonical.ImageUrl.Should().Be("https://lh3.googleusercontent.com/photo=w1024");
            canonical.GoogleMapsUrl.Should().Be("https://www.google.com/maps/place/farm");
        }
    }

    [Fact]
    public async Task MergeIfDuplicate_NameWithPolishDiacritics_StillMergesViaBboxPreFilter()
    {
        // Regression: SQLite's built-in LOWER() is ASCII-only, so the
        // previous name-based SQL pre-filter missed candidates whose
        // names contained characters like Ż, Ś, Ł, Ą. Those same
        // characters lowercase correctly via C#'s Unicode-aware
        // ToLowerInvariant, producing "wieża" on the incoming side
        // and "wieŻa" on the SQL side — mismatch, no merge. The fix
        // is a coordinate-bbox pre-filter, so Unicode has nothing to
        // do with candidate discovery any more.
        var factory = TestDbHelper.CreateFactory();
        int canonicalId, duplicateId;

        await using (var seed = await factory.CreateDbContextAsync())
        {
            // Exact same name, both enriched, same coords — the case
            // the original bug hit on re-import of a Polish list.
            var a = NewEnriched("WIEŻA WIDOKOWA SKY WALK", 50.9, 15.3, null);
            var b = NewEnriched("WIEŻA WIDOKOWA SKY WALK", 50.9, 15.3, null);
            seed.Pois.AddRange(a, b);
            await seed.SaveChangesAsync();
            canonicalId = a.Id;
            duplicateId = b.Id;
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var duplicate = await db.Pois.FirstAsync(p => p.Id == duplicateId);
            (await PoiPostEnrichmentDedup.MergeIfDuplicateAsync(db, duplicate, CancellationToken.None))
                .Should().BeTrue("the bbox pre-filter must find the canonical regardless of SQLite's ASCII-only LOWER()");
        }

        await using (var check = await factory.CreateDbContextAsync())
        {
            var remaining = await check.Pois.ToListAsync();
            remaining.Should().HaveCount(1);
            remaining.Single().Id.Should().Be(canonicalId);
        }
    }

    [Fact]
    public async Task MergeIfDuplicate_BothHaveImages_DropsDuplicatesImage()
    {
        var factory = TestDbHelper.CreateFactory();
        int canonicalId, duplicateId;

        await using (var seed = await factory.CreateDbContextAsync())
        {
            var a = NewEnriched("Museum", 48.86, 2.35, "https://maps.google.com/place/mus");
            var b = NewEnriched("Museum", 48.86, 2.35, "https://maps.google.com/place/mus");
            seed.Pois.AddRange(a, b);
            await seed.SaveChangesAsync();
            canonicalId = a.Id;
            duplicateId = b.Id;

            seed.PoiImages.AddRange(
                new PoiImage { PoiId = canonicalId, Data = [0x01], ContentType = "image/jpeg" },
                new PoiImage { PoiId = duplicateId, Data = [0x02], ContentType = "image/png" });
            await seed.SaveChangesAsync();
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var duplicate = await db.Pois.FirstAsync(p => p.Id == duplicateId);
            (await PoiPostEnrichmentDedup.MergeIfDuplicateAsync(db, duplicate, CancellationToken.None))
                .Should().BeTrue();
        }

        await using (var check = await factory.CreateDbContextAsync())
        {
            // Canonical keeps its own image; the duplicate's image is dropped.
            var images = await check.PoiImages.ToListAsync();
            images.Should().HaveCount(1);
            images.Single().PoiId.Should().Be(canonicalId);
            images.Single().Data.Should().Equal(new byte[] { 0x01 });
            images.Single().ContentType.Should().Be("image/jpeg");
        }
    }

    [Fact]
    public async Task MergeIfDuplicate_CanonicalHasTileImage_ReplacesWithDuplicatesPhoto()
    {
        var factory = TestDbHelper.CreateFactory();
        int canonicalId, duplicateId;

        await using (var seed = await factory.CreateDbContextAsync())
        {
            var canonical = NewEnriched("Farm", 48.0, 21.0, "https://www.google.com/maps/place/farm");
            canonical.ImageUrl = "https://tile.openstreetmap.org/1/2/3.png";
            var duplicate = NewEnriched("Farm", 48.0, 21.0, "https://www.google.com/maps/place/farm");
            duplicate.ImageUrl = "https://lh3.googleusercontent.com/photo=w1024";
            seed.Pois.AddRange(canonical, duplicate);
            await seed.SaveChangesAsync();
            canonicalId = canonical.Id;
            duplicateId = duplicate.Id;

            seed.PoiImages.AddRange(
                new PoiImage { PoiId = canonicalId, Data = [0x10], ContentType = "image/png" },
                new PoiImage { PoiId = duplicateId, Data = [0x20, 0x21], ContentType = "image/jpeg" });
            await seed.SaveChangesAsync();
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var duplicate = await db.Pois.FirstAsync(p => p.Id == duplicateId);
            (await PoiPostEnrichmentDedup.MergeIfDuplicateAsync(db, duplicate, CancellationToken.None))
                .Should().BeTrue();
        }

        await using (var check = await factory.CreateDbContextAsync())
        {
            var canonical = await check.Pois.SingleAsync(p => p.Id == canonicalId);
            canonical.ImageUrl.Should().Be("https://lh3.googleusercontent.com/photo=w1024");

            var img = await check.PoiImages.SingleAsync(i => i.PoiId == canonicalId);
            img.Data.Should().Equal(new byte[] { 0x20, 0x21 });
            img.ContentType.Should().Be("image/jpeg");
        }
    }
}