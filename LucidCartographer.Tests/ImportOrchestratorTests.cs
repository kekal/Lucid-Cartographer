using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Enrichment;
using LucidCartographer.Services.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests
{
    public class ImportOrchestratorTests
    {
        private readonly IFileImporter[] _importers = new IFileImporter[]
        {
            new GpxImporter(NullLogger<GpxImporter>.Instance),
            new KmlImporter(NullLogger<KmlImporter>.Instance),
            new GeoJsonImporter(NullLogger<GeoJsonImporter>.Instance),
            new CsvImporter(NullLogger<CsvImporter>.Instance)
        };

        private ImportOrchestrator CreateOrchestrator(IDbContextFactory<AppDbContext> factory, EnrichmentTrigger? trigger = null) =>
            new(factory, _importers, trigger ?? new EnrichmentTrigger(), NullLogger<ImportOrchestrator>.Instance);

        [Fact]
        public async Task ImportAsync_CreatesCollectionWithCorrectMetadata()
        {
            var factory = TestDbHelper.CreateFactory();
            var orchestrator = CreateOrchestrator(factory);

            using var stream = File.OpenRead(Path.Combine("TestData", "sample.gpx"));
            var result = await orchestrator.ImportAsync(stream, "sample.gpx", "My Trip", "#ff0000");

            await using var db = factory.CreateDbContext();
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

            using var stream = File.OpenRead(Path.Combine("TestData", "sample.gpx"));
            await orchestrator.ImportAsync(stream, "sample.gpx", "Test");

            await using var db = factory.CreateDbContext();
            var pois = await db.Pois.ToListAsync();

            pois.Should().HaveCount(3);

            var wawel = pois.First(p => p.Name == "Wawel Castle");
            wawel.Latitude.Should().Be(50.0647);
            wawel.Longitude.Should().Be(19.9450);
            wawel.Status.Should().Be("imported");
            wawel.Notes.Should().Be("Historic royal castle in Kraków");
        }

        [Fact]
        public async Task ImportAsync_DeduplicatesByGoogleMapsUrl()
        {
            var factory = TestDbHelper.CreateFactory();
            var orchestrator = CreateOrchestrator(factory);

            // First import
            using (var stream = File.OpenRead(Path.Combine("TestData", "sample.gpx")))
            {
                await orchestrator.ImportAsync(stream, "sample.gpx", "First Import");
            }

            // Second import of same file
            using (var stream = File.OpenRead(Path.Combine("TestData", "sample.gpx")))
            {
                var result = await orchestrator.ImportAsync(stream, "sample.gpx", "Second Import");

                // The 2 waypoints with Google Maps URLs should be skipped, the 1 without URL
                // will match by name+proximity since coords are identical
                result.SkippedCount.Should().Be(3);
                result.AddedCount.Should().Be(0);
            }

            await using var db = factory.CreateDbContext();
            var totalPois = await db.Pois.CountAsync();
            totalPois.Should().Be(3); // No duplicates created
        }

        [Fact]
        public async Task ImportAsync_DeduplicatesByNameAndProximity()
        {
            var factory = TestDbHelper.CreateFactory();
            var orchestrator = CreateOrchestrator(factory);

            // Pre-create a POI with same name and very close coordinates (no Google Maps URL)
            await using (var db = factory.CreateDbContext())
            {
                db.Pois.Add(new Poi
                {
                    Name = "Wrocław Market Square",
                    Latitude = 51.1079,
                    Longitude = 17.0385,
                    Status = "imported"
                });
                await db.SaveChangesAsync();
            }

            using var stream = File.OpenRead(Path.Combine("TestData", "sample.gpx"));
            var result = await orchestrator.ImportAsync(stream, "sample.gpx", "Test");

            // "Wrocław Market Square" should be skipped (name + proximity match)
            result.SkippedCount.Should().BeGreaterOrEqualTo(1);

            await using (var db = factory.CreateDbContext())
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
            using (var stream = File.OpenRead(Path.Combine("TestData", "sample.gpx")))
            {
                await orchestrator.ImportAsync(stream, "sample.gpx", "First");
            }

            // Second import - existing POIs should be linked to the new collection
            using (var stream = File.OpenRead(Path.Combine("TestData", "sample.gpx")))
            {
                await orchestrator.ImportAsync(stream, "sample.gpx", "Second");
            }

            await using var db = factory.CreateDbContext();
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

            using var stream = File.OpenRead(Path.Combine("TestData", "sample.gpx"));
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

            using var stream = File.OpenRead(Path.Combine("TestData", "sample.gpx"));
            var result = await orchestrator.ImportAsync(stream, "sample.gpx", "Test");

            result.AddedCount.Should().BeGreaterThan(0);

            // Signal fires via a bounded Channel<Unit>; WaitAsync returns
            // true if a signal was already in the channel. A real consumer
            // would not need this timeout, but the test completes in <100ms.
            var signaled = await trigger.WaitAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);
            signaled.Should().BeTrue("EnrichmentTrigger.Signal() should be fired after a successful import that added rows");
        }

        [Fact]
        public async Task ImportAsync_DoesNotSignalEnrichmentTriggerWhenNoPoisAdded()
        {
            var factory = TestDbHelper.CreateFactory();
            var trigger = new EnrichmentTrigger();
            var orchestrator = CreateOrchestrator(factory, trigger);

            using var stream = File.OpenRead(Path.Combine("TestData", "empty.gpx"));
            var result = await orchestrator.ImportAsync(stream, "empty.gpx", "Empty");

            result.AddedCount.Should().Be(0);

            // No signal should be in the channel — WaitAsync should time
            // out and return false.
            var signaled = await trigger.WaitAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);
            signaled.Should().BeFalse("EnrichmentTrigger.Signal() should NOT fire when the import added zero rows");
        }
    }
}
