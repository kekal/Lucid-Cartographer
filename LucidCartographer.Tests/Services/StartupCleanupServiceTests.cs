using FluentAssertions;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Tests;

public class StartupCleanupServiceTests
{
    [Fact]
    public async Task ReviveCore_RequeuesStuckCohorts_AndClearsUntrustworthyPhotos()
    {
        var factory = TestDbHelper.CreateFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            // (a) failure-capped row with valid coords.
            db.Pois.Add(new Poi { Id = 1, Name = "Failed", Latitude = 52, Longitude = 21, IsEnriched = false, EnrichmentFailureCount = 5, EnrichmentRequested = false, AddedDate = DateTime.UtcNow });
            // (b) pseudo-enriched: IsEnriched=true but no /maps/place/ URL, with a (untrustworthy) photo.
            db.Pois.Add(new Poi { Id = 2, Name = "Pseudo", Latitude = 50, Longitude = 19, IsEnriched = true, GoogleMapsUrl = "https://maps.google.com/search?q=x", ImageUrl = "https://lh3.googleusercontent.com/x", EnrichmentRequested = false, AddedDate = DateTime.UtcNow });
            db.PoiImages.Add(new PoiImage { PoiId = 2, Data = [9, 9, 9], ContentType = "image/jpeg" });
            await db.SaveChangesAsync();
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var (revived, cleared) = await StartupCleanupService.ReviveStuckImportedPoisCoreAsync(db, CancellationToken.None);
            revived.Should().Be(2);
            cleared.Should().Be(1);
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var a = await db.Pois.FindAsync(1);
            a!.EnrichmentRequested.Should().BeTrue();
            a.IsEnriched.Should().BeFalse();
            a.EnrichmentFailureCount.Should().Be(0);

            var b = await db.Pois.FindAsync(2);
            b!.EnrichmentRequested.Should().BeTrue();
            b.IsEnriched.Should().BeFalse();
            b.ImageUrl.Should().BeNull();                  // untrustworthy photo cleared
            (await db.PoiImages.AnyAsync(i => i.PoiId == 2)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task ReviveCore_LeavesGenuinelyEnrichedRowWithEditedUrlUntouched()
    {
        // A genuinely-enriched POI (Address/Website/Phone populated, real photo
        // in PoiImages) whose canonical /maps/place/ URL was later edited to a
        // maps.app.goo.gl shortlink. UpdatePoiAsync leaves IsEnriched=true. The
        // empty-text guard must spare it: it is NOT pseudo-enriched and its real
        // blob must survive the revive sweep, with no needless re-enqueue.
        var factory = TestDbHelper.CreateFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Pois.Add(new Poi
            {
                Id = 1,
                Name = "Real place, shortlink URL",
                Latitude = 52.2297,
                Longitude = 21.0122,
                IsEnriched = true,
                EnrichmentRequested = false,
                EnrichmentNeedsManualUrl = false,
                GoogleMapsUrl = "https://maps.app.goo.gl/abc123",
                Address = "Plac Defilad 1, 00-901 Warszawa",
                Website = "https://example.com",
                Phone = "+48 22 123 45 67",
                ImageUrl = "https://lh3.googleusercontent.com/real-photo",
                AddedDate = DateTime.UtcNow
            });
            db.PoiImages.Add(new PoiImage { PoiId = 1, Data = [1, 2, 3, 4], ContentType = "image/jpeg" });
            await db.SaveChangesAsync();
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var (revived, cleared) = await StartupCleanupService.ReviveStuckImportedPoisCoreAsync(db, CancellationToken.None);
            revived.Should().Be(0, "a genuinely-enriched row with populated fields is not pseudo-enriched");
            cleared.Should().Be(0);
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var p = await db.Pois.FindAsync(1);
            p!.IsEnriched.Should().BeTrue("the row was left untouched");
            p.EnrichmentRequested.Should().BeFalse("it must not be re-enqueued every boot");
            p.GoogleMapsUrl.Should().Be("https://maps.app.goo.gl/abc123");
            p.Address.Should().Be("Plac Defilad 1, 00-901 Warszawa");
            p.Website.Should().Be("https://example.com");
            p.Phone.Should().Be("+48 22 123 45 67");
            p.ImageUrl.Should().Be("https://lh3.googleusercontent.com/real-photo");
            var img = await db.PoiImages.SingleOrDefaultAsync(i => i.PoiId == 1);
            img.Should().NotBeNull("the real served blob must not be destroyed");
            img!.Data.Should().Equal([1, 2, 3, 4]);
        }
    }

    // --- Story 3.2 (FR-16, AD-6): one-time OSRM cache-row purge ---

    private static RouteSegment Segment(int from, int to, string fidelity, string source) => new()
    {
        FromPoiId = from,
        ToPoiId = to,
        TravelMode = TravelMode.Drive,
        DurationSeconds = 600,
        DistanceMeters = 5000,
        Fidelity = fidelity,
        Source = source,
        ComputedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task PurgeOsrmCore_DeletesOsrmRows_KeepsManualAndOtherSources()
    {
        var factory = TestDbHelper.CreateFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.RouteSegments.Add(Segment(1, 2, Fidelity.Measured, "OSRM"));        // purged
            db.RouteSegments.Add(Segment(2, 1, Fidelity.Measured, "OSRM"));        // purged
            db.RouteSegments.Add(Segment(3, 4, Fidelity.Manual, "Manual"));        // survives
            db.RouteSegments.Add(Segment(4, 3, Fidelity.Estimated, "Mock"));       // survives
            db.RouteSegments.Add(Segment(5, 6, Fidelity.Measured, "Valhalla"));    // survives
            await db.SaveChangesAsync();
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var purged = await StartupCleanupService.PurgeOsrmCacheRowsCoreAsync(db, CancellationToken.None);
            purged.Should().Be(2, "only the two Source=OSRM rows are stale");
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.RouteSegments.AnyAsync(r => r.Source == "OSRM")).Should().BeFalse();
            (await db.RouteSegments.CountAsync()).Should().Be(3, "Manual, Mock, and Valhalla rows survive");
            (await db.RouteSegments.AnyAsync(r => r.Source == "Manual")).Should().BeTrue();
            (await db.RouteSegments.AnyAsync(r => r.Source == "Valhalla")).Should().BeTrue();
        }
    }

    [Fact]
    public async Task PurgeOsrmCore_NeverDeletesManualRow_EvenIfMislabeledOsrm()
    {
        // Defensive belt: even a Manual-fidelity row stamped Source="OSRM" must survive —
        // [TRIP-MANUAL-01] never lets a user-entered time be touched.
        var factory = TestDbHelper.CreateFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.RouteSegments.Add(Segment(1, 2, Fidelity.Manual, "OSRM"));
            await db.SaveChangesAsync();
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var purged = await StartupCleanupService.PurgeOsrmCacheRowsCoreAsync(db, CancellationToken.None);
            purged.Should().Be(0, "a Manual row is never touched");
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.RouteSegments.CountAsync()).Should().Be(1);
        }
    }

    [Fact]
    public async Task PurgeOsrmCore_IsIdempotent_SecondRunIsNoOp()
    {
        var factory = TestDbHelper.CreateFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.RouteSegments.Add(Segment(1, 2, Fidelity.Measured, "OSRM"));
            await db.SaveChangesAsync();
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            (await StartupCleanupService.PurgeOsrmCacheRowsCoreAsync(db, CancellationToken.None)).Should().Be(1);
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            (await StartupCleanupService.PurgeOsrmCacheRowsCoreAsync(db, CancellationToken.None))
                .Should().Be(0, "the rows are already gone — the migration self-retires");
        }
    }

    [Fact]
    public async Task ReviveCore_LeavesDormantManualPoiUntouched()
    {
        // The load-bearing decoupling invariant: a freshly created manual POI
        // (Requested=false, IsEnriched=false, FailureCount=0) is NOT revived.
        var factory = TestDbHelper.CreateFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Pois.Add(new Poi { Id = 1, Name = "Manual event", Latitude = 53.488, Longitude = 20.087, IsEnriched = false, EnrichmentFailureCount = 0, EnrichmentRequested = false, AddedDate = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var (revived, _) = await StartupCleanupService.ReviveStuckImportedPoisCoreAsync(db, CancellationToken.None);
            revived.Should().Be(0);
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.Pois.FindAsync(1))!.EnrichmentRequested.Should().BeFalse("creation stays decoupled from enrichment");
        }
    }
}
