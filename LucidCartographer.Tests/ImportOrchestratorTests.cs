using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Enrichment;
using LucidCartographer.Services.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests;

public class ImportOrchestratorTests
{
    private readonly IFileImporter[] _importers =
    [
        new GpxImporter(NullLogger<GpxImporter>.Instance),
        new KmlImporter(NullLogger<KmlImporter>.Instance),
        new GeoJsonImporter(NullLogger<GeoJsonImporter>.Instance),
        new CsvImporter(NullLogger<CsvImporter>.Instance)
    ];

    private ImportOrchestrator CreateOrchestrator(IDbContextFactory<AppDbContext> factory, EnrichmentTrigger? trigger = null) =>
        new(factory, _importers, trigger ?? new EnrichmentTrigger(), NullLogger<ImportOrchestrator>.Instance);

    [Fact]
    public async Task ImportAsync_CreatesCollectionWithCorrectMetadata()
    {
        var factory = TestDbHelper.CreateFactory();
        var orchestrator = CreateOrchestrator(factory);

        await using var stream = File.OpenRead(Path.Combine("TestData", "sample.gpx"));
        var result = await orchestrator.ImportAsync(stream, "sample.gpx", "My Trip", "#ff0000");

        await using var db = await factory.CreateDbContextAsync();
        var collection = await db.PoiCollections.FirstAsync();

        collection.Name.Should().Be("My Trip");
        collection.Color.Should().Be("#ff0000");
        collection.SourceType.Should().Be("gpx_import");
        collection.SourceFileName.Should().Be("sample.gpx");
    }

    [Fact]
    public async Task ImportAsync_CreatesPoisWithCorrectFields()
    {
        var factory = TestDbHelper.CreateFactory();
        var orchestrator = CreateOrchestrator(factory);

        await using var stream = File.OpenRead(Path.Combine("TestData", "sample.gpx"));
        await orchestrator.ImportAsync(stream, "sample.gpx", "Test");

        await using var db = await factory.CreateDbContextAsync();
        var pois = await db.Pois.ToListAsync();

        pois.Should().HaveCount(3);

        var wawel = pois.First(p => p.Name == "Wawel Castle");
        wawel.Latitude.Should().Be(50.0647);
        wawel.Longitude.Should().Be(19.9450);
        wawel.Notes.Should().Be("Historic royal castle in Kraków");
    }

    [Fact]
    public async Task ImportAsync_DeduplicatesByGoogleMapsUrl()
    {
        var factory = TestDbHelper.CreateFactory();
        var orchestrator = CreateOrchestrator(factory);

        // First import
        await using (var stream = File.OpenRead(Path.Combine("TestData", "sample.gpx")))
        {
            await orchestrator.ImportAsync(stream, "sample.gpx", "First Import");
        }

        // Second import of same file
        await using (var stream = File.OpenRead(Path.Combine("TestData", "sample.gpx")))
        {
            var result = await orchestrator.ImportAsync(stream, "sample.gpx", "Second Import");

            // The 2 waypoints with Google Maps URLs should be skipped, the 1 without URL
            // will match by name+proximity since coords are identical
            result.SkippedCount.Should().Be(3);
            result.AddedCount.Should().Be(0);
        }

        await using var db = await factory.CreateDbContextAsync();
        var totalPois = await db.Pois.CountAsync();
        totalPois.Should().Be(3); // No duplicates created
    }

    [Fact]
    public async Task ImportAsync_DeduplicatesByNameAndProximity()
    {
        var factory = TestDbHelper.CreateFactory();
        var orchestrator = CreateOrchestrator(factory);

        // Pre-create a POI with same name and very close coordinates (no Google Maps URL)
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Pois.Add(new Poi
            {
                Name = "Wrocław Market Square",
                Latitude = 51.1079,
                Longitude = 17.0385
            });
            await db.SaveChangesAsync();
        }

        await using var stream = File.OpenRead(Path.Combine("TestData", "sample.gpx"));
        var result = await orchestrator.ImportAsync(stream, "sample.gpx", "Test");

        // "Wrocław Market Square" should be skipped (name + proximity match)
        result.SkippedCount.Should().BeGreaterOrEqualTo(1);

        await using (var db = await factory.CreateDbContextAsync())
        {
            var count = await db.Pois.CountAsync(p => p.Name == "Wrocław Market Square");
            count.Should().Be(1); // Not duplicated
        }
    }

    [Fact]
    public async Task ImportAsync_LinksExistingPoisToNewCollection()
    {
        var factory = TestDbHelper.CreateFactory();
        var orchestrator = CreateOrchestrator(factory);

        // First import
        await using (var stream = File.OpenRead(Path.Combine("TestData", "sample.gpx")))
        {
            await orchestrator.ImportAsync(stream, "sample.gpx", "First");
        }

        // Second import - existing POIs should be linked to the new collection
        await using (var stream = File.OpenRead(Path.Combine("TestData", "sample.gpx")))
        {
            await orchestrator.ImportAsync(stream, "sample.gpx", "Second");
        }

        await using var db = await factory.CreateDbContextAsync();
        var collections = await db.PoiCollections.ToListAsync();
        collections.Should().HaveCount(2);

        // All POIs should be linked to the second collection too
        var secondCollection = collections.First(c => c.Name == "Second");
        var linkedItems = await db.PoiCollectionItems
            .Where(ci => ci.PoiCollectionId == secondCollection.Id)
            .CountAsync();
        linkedItems.Should().Be(3);
    }

    [Fact]
    public async Task ImportAsync_ReturnsCorrectAddedAndSkippedCounts()
    {
        var factory = TestDbHelper.CreateFactory();
        var orchestrator = CreateOrchestrator(factory);

        await using var stream = File.OpenRead(Path.Combine("TestData", "sample.gpx"));
        var result = await orchestrator.ImportAsync(stream, "sample.gpx", "Test");

        result.AddedCount.Should().Be(3);
        result.SkippedCount.Should().Be(0);
        result.TotalParsed.Should().Be(3);
        result.CollectionName.Should().Be("Test");
    }

    [Fact]
    public void CanImport_ReturnsTrueForGpx()
    {
        var factory = TestDbHelper.CreateFactory();
        var orchestrator = CreateOrchestrator(factory);

        orchestrator.CanImport("test.gpx").Should().BeTrue();
    }

    [Fact]
    public void CanImport_ReturnsTrueForKml()
    {
        var factory = TestDbHelper.CreateFactory();
        var orchestrator = CreateOrchestrator(factory);

        orchestrator.CanImport("test.kml").Should().BeTrue();
    }

    [Fact]
    public void CanImport_ReturnsFalseForUnsupportedExtension()
    {
        var factory = TestDbHelper.CreateFactory();
        var orchestrator = CreateOrchestrator(factory);

        orchestrator.CanImport("test.xyz").Should().BeFalse();
    }

    [Fact]
    public async Task ImportAsync_SignalsEnrichmentTriggerAfterSuccessfulImport()
    {
        var factory = TestDbHelper.CreateFactory();
        var trigger = new EnrichmentTrigger();
        var orchestrator = CreateOrchestrator(factory, trigger);

        await using var stream = File.OpenRead(Path.Combine("TestData", "sample.gpx"));
        var result = await orchestrator.ImportAsync(stream, "sample.gpx", "Test");

        result.AddedCount.Should().BeGreaterThan(0);

        // Signal fires via a bounded Channel<Unit>; WaitAsync returns
        // true if a signal was already in the channel. A real consumer
        // would not need this timeout, but the test completes in <100ms.
        var signaled = await trigger.WaitAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);
        signaled.Should().BeTrue("EnrichmentTrigger.Signal() should be fired after a successful import that added rows");

        // Decoupling: import is a pipeline, so it explicitly flags its new rows
        // for the worker (creation alone no longer enqueues).
        await using var db = await factory.CreateDbContextAsync();
        var added = await db.Pois.ToListAsync();
        added.Should().NotBeEmpty();
        added.Should().OnlyContain(p => p.EnrichmentRequested,
            "every imported row should be explicitly requested for enrichment");
    }

    [Fact]
    public async Task ImportAsync_DoesNotSignalEnrichmentTriggerWhenNoPoisAdded()
    {
        var factory = TestDbHelper.CreateFactory();
        var trigger = new EnrichmentTrigger();
        var orchestrator = CreateOrchestrator(factory, trigger);

        await using var stream = File.OpenRead(Path.Combine("TestData", "empty.gpx"));
        var result = await orchestrator.ImportAsync(stream, "empty.gpx", "Empty");

        result.AddedCount.Should().Be(0);

        // No signal should be in the channel — WaitAsync should time
        // out and return false.
        var signaled = await trigger.WaitAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);
        signaled.Should().BeFalse("EnrichmentTrigger.Signal() should NOT fire when the import added zero rows");
    }

    [Fact]
    public async Task ImportFromScrapedAsync_DuplicateWithoutStoredImageBytes_BackfillsImageData()
    {
        var factory = TestDbHelper.CreateFactory();

        int existingPoiId;
        await using (var seed = await factory.CreateDbContextAsync())
        {
            var existing = new Poi
            {
                Name = "Alpaki Fajne Sprawy Habdzin",
                Latitude = 50.0,
                Longitude = 20.0,
                IsEnriched = true,
                AddedDate = DateTime.UtcNow,
                ImageUrl = "https://example.com/old-image.jpg"
            };
            seed.Pois.Add(existing);
            await seed.SaveChangesAsync();
            existingPoiId = existing.Id;
        }

        var orchestrator = CreateOrchestrator(factory);
        List<ImportedPoi> scraped =
        [
            new(
                Name: "Alpaki Fajne Sprawy Habdzin",
                Latitude: 50.0,
                Longitude: 20.0,
                GoogleMapsUrl: "https://www.google.com/maps/place/Alpaki",
                ImageUrl: "https://lh3.googleusercontent.com/photo=w1024",
                ImageData: [1, 2, 3, 4],
                ImageContentType: "image/jpeg")
        ];

        var result = await orchestrator.ImportFromScrapedAsync(scraped, "Reimport");

        result.AddedCount.Should().Be(0);
        result.SkippedCount.Should().Be(1);

        await using var check = await factory.CreateDbContextAsync();
        (await check.Pois.CountAsync()).Should().Be(1);

        var image = await check.PoiImages.FirstOrDefaultAsync(i => i.PoiId == existingPoiId);
        image.Should().NotBeNull();
        image!.Data.Should().Equal(new byte[] { 1, 2, 3, 4 });
    }

    [Fact]
    public async Task ImportFromScrapedAsync_DedupLinkedExistingRow_IsNotRequestedForEnrichment()
    {
        // Decoupling: only newly-added rows are enqueued. A pre-existing row the
        // import dedups against must NOT have its EnrichmentRequested flipped.
        var factory = TestDbHelper.CreateFactory();
        int existingId;
        await using (var seed = await factory.CreateDbContextAsync())
        {
            var existing = new Poi
            {
                Name = "Alpaki Fajne Sprawy Habdzin",
                Latitude = 50.0,
                Longitude = 20.0,
                IsEnriched = true,
                EnrichmentRequested = false,
                AddedDate = DateTime.UtcNow
            };
            seed.Pois.Add(existing);
            await seed.SaveChangesAsync();
            existingId = existing.Id;
        }

        var orchestrator = CreateOrchestrator(factory);
        List<ImportedPoi> scraped =
        [
            new(Name: "Alpaki Fajne Sprawy Habdzin", Latitude: 50.0, Longitude: 20.0,
                GoogleMapsUrl: "https://www.google.com/maps/place/Alpaki"),
            new(Name: "Brand New Place", Latitude: 51.0, Longitude: 21.0,
                GoogleMapsUrl: "https://www.google.com/maps/place/New"),
        ];

        var result = await orchestrator.ImportFromScrapedAsync(scraped, "Reimport");
        result.AddedCount.Should().Be(1);

        await using var check = await factory.CreateDbContextAsync();
        (await check.Pois.FindAsync(existingId))!.EnrichmentRequested.Should().BeFalse("deduped existing row must not be re-enqueued");
        var newRow = await check.Pois.FirstAsync(p => p.Name == "Brand New Place");
        newRow.EnrichmentRequested.Should().BeTrue("the newly-added row is enqueued");
    }

    [Fact]
    public async Task ImportFromScrapedAsync_DuplicateWithLegacySearchGoogleUrl_UpgradesToImportedUrl()
    {
        var factory = TestDbHelper.CreateFactory();

        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Pois.Add(new Poi
            {
                Name = "Centrum Przyrodnicze",
                Latitude = 50.9856105,
                Longitude = 17.7016002,
                GoogleMapsUrl = "https://www.google.com/maps/search/?api=1&query=50.9856105,17.7016002",
                IsEnriched = true,
                AddedDate = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var orchestrator = CreateOrchestrator(factory);
        List<ImportedPoi> scraped =
        [
            new(
                Name: "Centrum Przyrodnicze",
                Latitude: 50.9856105,
                Longitude: 17.7016002,
                GoogleMapsUrl: "https://maps.app.goo.gl/mHCBTX7XAJH3wVeD9")
        ];

        var result = await orchestrator.ImportFromScrapedAsync(scraped, "Reimport");

        result.AddedCount.Should().Be(0);
        result.SkippedCount.Should().Be(1);

        await using var check = await factory.CreateDbContextAsync();
        var poi = await check.Pois.SingleAsync(p => p.Name == "Centrum Przyrodnicze");
        poi.GoogleMapsUrl.Should().Be("https://maps.app.goo.gl/mHCBTX7XAJH3wVeD9");
    }

    [Fact]
    public async Task ImportFromScrapedAsync_DiacriticVariant_DocumentsCandidatePoolExactMatchLimitation()
    {
        // KNOWN LIMITATION: ImportPersister.LoadCandidatePoolAsync uses
        // `Name.ToLower() == nameLower` as its SQL pre-filter, which is
        // byte-exact. PoiMatcher.NameSimilarity would return 1.0 after
        // NFC normalisation on "Café Rio" vs "Cafe Rio" — but the
        // existing row is never pulled into the candidate pool, so the
        // in-memory PoiIdentity.AreSamePlace check never runs, and the
        // incoming row lands as a fresh Poi. If the pre-filter is
        // widened to use NFC-normalised lowercase (or full-text search),
        // this test will start to fail and should flip to assert the
        // correct dedup behaviour.
        var factory = TestDbHelper.CreateFactory();

        // Seed an existing enriched row with an ASCII name at real coords.
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Pois.Add(new Poi
            {
                Name = "Cafe Rio",
                Latitude = 50.0647,
                Longitude = 19.9450,
                IsEnriched = true,
                AddedDate = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var orchestrator = CreateOrchestrator(factory);
        List<ImportedPoi> scraped = [new("Café Rio", 50.0647, 19.9450)];

        var result = await orchestrator.ImportFromScrapedAsync(scraped, "Test");

        result.AddedCount.Should().Be(1,
            "documents the current pre-filter limitation: diacritic-variant names don't hit the candidate pool");
        result.SkippedCount.Should().Be(0);

        await using var check = await factory.CreateDbContextAsync();
        (await check.Pois.CountAsync()).Should().Be(2,
            "both rows exist until the pre-filter is widened to normalise diacritics");
    }

    [Fact]
    public async Task ImportFromScrapedAsync_DistinctPoisSharingANameAtPlaceholderCoordsAreNotDeduped()
    {
        // Regression: a Google Maps list scrape emits cards without
        // href coordinates at placeholder (0,0) until enrichment fills
        // in the real values. Two distinct playgrounds both named
        // "Plac zabaw" must NOT be collapsed into one row just because
        // the (0,0)↔(0,0) Haversine distance trivially satisfies the
        // 100 m proximity threshold of the name-based tier-2 dedup.
        // Original symptom: a 230-item list imported as only 226 rows.
        var factory = TestDbHelper.CreateFactory();
        var orchestrator = CreateOrchestrator(factory);

        List<ImportedPoi> scraped =
        [
            new("Plac zabaw", 0, 0, null, null, "Playground"),
            new("Plac zabaw", 0, 0, null, null, "Playground"),
            new("Plac zabaw", 0, 0, null, null, "Playground"),
            new("Bieszczadzkie Drezyny Rowerowe", 0, 0, null, null, "Tourist attraction"),
            new("Bieszczadzkie Drezyny Rowerowe", 0, 0, null, null, "Tourist attraction"),
            new("Unique Place", 50.0, 20.0, null, null, "Attraction")
        ];

        var result = await orchestrator.ImportFromScrapedAsync(scraped, "Poland with kids");

        result.TotalParsed.Should().Be(6);
        result.AddedCount.Should().Be(6, "distinct POIs at placeholder (0,0) must survive dedup until enrichment lands real coords");
        result.SkippedCount.Should().Be(0);

        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.PoiCollectionItems
            .Where(ci => ci.PoiCollectionId == result.CollectionId)
            .CountAsync();
        rows.Should().Be(6, "the collection should have one PoiCollectionItem per scraped card");

        // And the three "Plac zabaw" rows should be distinct Poi.Id values,
        // not three links to the same row.
        var placZabawIds = await db.PoiCollectionItems
            .Where(ci => ci.PoiCollectionId == result.CollectionId && ci.Poi.Name == "Plac zabaw")
            .Select(ci => ci.PoiId)
            .ToListAsync();
        placZabawIds.Should().HaveCount(3);
        placZabawIds.Distinct().Should().HaveCount(3, "three distinct playgrounds must map to three distinct Poi rows");
    }
}