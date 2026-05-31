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
