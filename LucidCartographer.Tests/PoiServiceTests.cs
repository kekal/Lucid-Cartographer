using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests;

public class PoiServiceTests
{
    private static readonly ILogger<PoiService> NullLogger = NullLoggerFactory.Instance.CreateLogger<PoiService>();

    private static async Task<(IPoiService Service, Microsoft.EntityFrameworkCore.IDbContextFactory<AppDbContext> Factory)> CreateServiceAsync(Action<AppDbContext> seed)
    {
        var factory = TestDbHelper.CreateFactory();
        await using var db = await factory.CreateDbContextAsync();
        seed(db);
        await db.SaveChangesAsync();
        var service = new PoiService(factory, NullLogger);
        return (service, factory);
    }

    [Fact]
    public async Task GetCollectionsAsync_ReturnsAllCollectionsOrderedByDate()
    {
        var (service, _) = await CreateServiceAsync(db =>
        {
            db.PoiCollections.AddRange(
                new PoiCollection { Color = "#005bbf", Id = 1, Name = "Old", CreatedDate = new DateTime(2024, 1, 1) },
                new PoiCollection { Color = "#005bbf", Id = 2, Name = "New", CreatedDate = new DateTime(2024, 6, 1) },
                new PoiCollection { Color = "#005bbf", Id = 3, Name = "Mid", CreatedDate = new DateTime(2024, 3, 1) }
            );
        });

        var result = await service.GetCollectionsAsync();

        result.Should().HaveCount(3);
        result[0].Name.Should().Be("New");
        result[1].Name.Should().Be("Mid");
        result[2].Name.Should().Be("Old");
    }

    [Fact]
    public async Task GetPoisByCollectionAsync_ReturnsPoisForGivenCollection()
    {
        var (service, _) = await CreateServiceAsync(db =>
        {
            var poi1 = new Poi { Id = 1, Name = "Poi1", Latitude = 52.0, Longitude = 21.0, AddedDate = DateTime.UtcNow };
            var poi2 = new Poi { Id = 2, Name = "Poi2", Latitude = 50.0, Longitude = 19.0, AddedDate = DateTime.UtcNow };
            var poi3 = new Poi { Id = 3, Name = "Poi3", Latitude = 48.0, Longitude = 2.0, AddedDate = DateTime.UtcNow };
            db.Pois.AddRange(poi1, poi2, poi3);

            var col = new PoiCollection { Color = "#005bbf", Id = 1, Name = "Col1", CreatedDate = DateTime.UtcNow };
            db.PoiCollections.Add(col);

            db.PoiCollectionItems.AddRange(
                new PoiCollectionItem { PoiId = 1, PoiCollectionId = 1 },
                new PoiCollectionItem { PoiId = 2, PoiCollectionId = 1 }
            );
        });

        var result = await service.GetPoisByCollectionAsync(1);

        result.Should().HaveCount(2);
        result.Select(p => p.Name).Should().Contain(["Poi1", "Poi2"]);
    }

    [Fact]
    public async Task GetVisiblePoisGroupedAsync_ReturnsOnlyVisibleCollections()
    {
        var (service, _) = await CreateServiceAsync(db =>
        {
            var poi1 = new Poi { Id = 1, Name = "Poi1", Latitude = 52.0, Longitude = 21.0, AddedDate = DateTime.UtcNow, IsEnriched = true };
            var poi2 = new Poi { Id = 2, Name = "Poi2", Latitude = 50.0, Longitude = 19.0, AddedDate = DateTime.UtcNow, IsEnriched = true };
            db.Pois.AddRange(poi1, poi2);

            db.PoiCollections.AddRange(
                new PoiCollection { Color = "#005bbf", Id = 1, Name = "Visible", IsVisible = true, CreatedDate = DateTime.UtcNow },
                new PoiCollection { Color = "#005bbf", Id = 2, Name = "Hidden", IsVisible = false, CreatedDate = DateTime.UtcNow }
            );

            db.PoiCollectionItems.AddRange(
                new PoiCollectionItem { PoiId = 1, PoiCollectionId = 1 },
                new PoiCollectionItem { PoiId = 2, PoiCollectionId = 2 }
            );
        });

        var result = await service.GetVisiblePoisGroupedAsync();

        result.Should().ContainKey(1);
        result.Should().NotContainKey(2);
        result[1].Should().HaveCount(1);
    }

    [Fact]
    public async Task ToggleVisibilityAsync_TogglesIsVisibleFlag()
    {
        var (service, factory) = await CreateServiceAsync(db =>
        {
            db.PoiCollections.Add(new PoiCollection { Color = "#005bbf", Id = 1, Name = "Col", IsVisible = true, CreatedDate = DateTime.UtcNow });
        });

        await service.ToggleVisibilityAsync(1);

        await using var db = await factory.CreateDbContextAsync();
        var col = await db.PoiCollections.FindAsync(1);
        col!.IsVisible.Should().BeFalse();

        // Toggle again
        await service.ToggleVisibilityAsync(1);
        await using var db2 = await factory.CreateDbContextAsync();
        var col2 = await db2.PoiCollections.FindAsync(1);
        col2!.IsVisible.Should().BeTrue();
    }

    [Fact]
    public async Task ToggleVisibilityAsync_ThrowsOnInvalidId()
    {
        var (service, _) = await CreateServiceAsync(db => { });

        var act = () => service.ToggleVisibilityAsync(999);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*999*");
    }

    [Fact]
    public async Task SearchAsync_FindsPoisByName()
    {
        var (service, _) = await CreateServiceAsync(db =>
        {
            db.Pois.AddRange(
                new Poi { Id = 1, Name = "Warsaw Coffee", Latitude = 52.0, Longitude = 21.0, AddedDate = DateTime.UtcNow },
                new Poi { Id = 2, Name = "Krakow Bakery", Latitude = 50.0, Longitude = 19.0, AddedDate = DateTime.UtcNow }
            );
        });

        var result = await service.SearchAsync("coffee");

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Warsaw Coffee");
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyOnNullOrWhitespace()
    {
        var (service, _) = await CreateServiceAsync(db =>
        {
            db.Pois.Add(new Poi { Id = 1, Name = "Test", Latitude = 0, Longitude = 0, AddedDate = DateTime.UtcNow });
        });

        (await service.SearchAsync(null!)).Should().BeEmpty();
        (await service.SearchAsync("")).Should().BeEmpty();
        (await service.SearchAsync("   ")).Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_FindsPoisByAddress()
    {
        var (service, _) = await CreateServiceAsync(db =>
        {
            db.Pois.AddRange(
                new Poi { Id = 1, Name = "Place A", Latitude = 52.0, Longitude = 21.0, Address = "123 Main Street", AddedDate = DateTime.UtcNow },
                new Poi { Id = 2, Name = "Place B", Latitude = 50.0, Longitude = 19.0, Address = "456 Oak Avenue", AddedDate = DateTime.UtcNow }
            );
        });

        var result = await service.SearchAsync("main street");

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Place A");
    }

    [Fact]
    public async Task SearchAsync_FindsPoisByTags()
    {
        var (service, _) = await CreateServiceAsync(db =>
        {
            var tagItalian = new Tag { Id = 1, Name = "italian" };
            var tagFood = new Tag { Id = 2, Name = "food" };
            var tagShopping = new Tag { Id = 3, Name = "shopping" };
            var tagClothes = new Tag { Id = 4, Name = "clothes" };
            db.Tags.AddRange(tagItalian, tagFood, tagShopping, tagClothes);

            var poiA = new Poi { Id = 1, Name = "Place A", Latitude = 52.0, Longitude = 21.0, AddedDate = DateTime.UtcNow };
            var poiB = new Poi { Id = 2, Name = "Place B", Latitude = 50.0, Longitude = 19.0, AddedDate = DateTime.UtcNow };
            db.Pois.AddRange(poiA, poiB);

            db.PoiTags.AddRange(
                new PoiTag { PoiId = 1, TagId = 1 },
                new PoiTag { PoiId = 1, TagId = 2 },
                new PoiTag { PoiId = 2, TagId = 3 },
                new PoiTag { PoiId = 2, TagId = 4 }
            );
        });

        var result = await service.SearchAsync("italian");

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Place A");
    }

    [Fact]
    public async Task DeleteCollectionAsync_RemovesCollectionAndOrphanedPois()
    {
        var (service, factory) = await CreateServiceAsync(db =>
        {
            var poi1 = new Poi { Id = 1, Name = "Orphan", Latitude = 52.0, Longitude = 21.0, AddedDate = DateTime.UtcNow };
            var poi2 = new Poi { Id = 2, Name = "Shared", Latitude = 50.0, Longitude = 19.0, AddedDate = DateTime.UtcNow };
            db.Pois.AddRange(poi1, poi2);

            db.PoiCollections.AddRange(
                new PoiCollection { Color = "#005bbf", Id = 1, Name = "ToDelete", CreatedDate = DateTime.UtcNow },
                new PoiCollection { Color = "#005bbf", Id = 2, Name = "Other", CreatedDate = DateTime.UtcNow }
            );

            db.PoiCollectionItems.AddRange(
                new PoiCollectionItem { PoiId = 1, PoiCollectionId = 1 },
                new PoiCollectionItem { PoiId = 2, PoiCollectionId = 1 },
                new PoiCollectionItem { PoiId = 2, PoiCollectionId = 2 }
            );
        });

        await service.DeleteCollectionAsync(1);

        await using var db = await factory.CreateDbContextAsync();
        db.PoiCollections.Should().HaveCount(1);
        // poi1 was only in deleted collection -> orphaned -> removed
        var remainingPois = db.Pois.ToList();
        remainingPois.Should().HaveCount(1);
        remainingPois[0].Name.Should().Be("Shared");
    }

    [Fact]
    public async Task UpdatePoiAsync_UpdatesPoiFields()
    {
        var (service, factory) = await CreateServiceAsync(db =>
        {
            db.Pois.Add(new Poi { Id = 1, Name = "Original", Latitude = 52.0, Longitude = 21.0, AddedDate = DateTime.UtcNow });
        });

        // Read, modify, update
        var poi = new Poi { Id = 1, Name = "Updated", Latitude = 53.0, Longitude = 22.0, Address = "New Address", AddedDate = DateTime.UtcNow };
        await service.UpdatePoiAsync(poi);

        await using var db = await factory.CreateDbContextAsync();
        var updated = await db.Pois.FindAsync(1);
        updated!.Name.Should().Be("Updated");
        updated.Latitude.Should().Be(53.0);
        updated.Address.Should().Be("New Address");
    }

    [Fact]
    public async Task UpdatePoiAsync_ThrowsOnNonexistentPoi()
    {
        var (service, _) = await CreateServiceAsync(db => { });

        var poi = new Poi { Id = 999, Name = "Ghost", Latitude = 0, Longitude = 0, AddedDate = DateTime.UtcNow };
        var act = () => service.UpdatePoiAsync(poi);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*999*");
    }

    [Fact]
    public async Task UpdateCollectionColorAsync_ValidatesHexFormat()
    {
        var (service, _) = await CreateServiceAsync(db =>
        {
            db.PoiCollections.Add(new PoiCollection { Color = "#005bbf", Id = 1, Name = "Col", CreatedDate = DateTime.UtcNow });
        });

        var act = () => service.UpdateCollectionColorAsync(1, "banana");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*hex color*");
    }

    [Fact]
    public async Task RenameCollectionAsync_UpdatesName_AndTrimsWhitespace()
    {
        var (service, factory) = await CreateServiceAsync(db =>
        {
            db.PoiCollections.Add(new PoiCollection { Color = "#005bbf", Id = 1, Name = "Old", CreatedDate = DateTime.UtcNow });
        });

        await service.RenameCollectionAsync(1, "  Замки 2  ");

        await using var db = await factory.CreateDbContextAsync();
        var col = await db.PoiCollections.FindAsync(1);
        col!.Name.Should().Be("Замки 2");
    }

    [Fact]
    public async Task RenameCollectionAsync_BlankName_Throws()
    {
        var (service, _) = await CreateServiceAsync(db =>
        {
            db.PoiCollections.Add(new PoiCollection { Color = "#005bbf", Id = 1, Name = "Old", CreatedDate = DateTime.UtcNow });
        });

        var act = () => service.RenameCollectionAsync(1, "   ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RenameCollectionAsync_UnknownCollection_Throws()
    {
        var (service, _) = await CreateServiceAsync(_ => { });

        var act = () => service.RenameCollectionAsync(999, "New");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*999*");
    }

    [Fact]
    public async Task GetCollectionsAsync_ComputesPoiCountFromDb()
    {
        var (service, _) = await CreateServiceAsync(db =>
        {
            var poi1 = new Poi { Id = 1, Name = "P1", Latitude = 0, Longitude = 0, AddedDate = DateTime.UtcNow };
            var poi2 = new Poi { Id = 2, Name = "P2", Latitude = 0, Longitude = 0, AddedDate = DateTime.UtcNow };
            db.Pois.AddRange(poi1, poi2);

            // Stored PoiCount is wrong (0) but should be computed correctly
            db.PoiCollections.Add(new PoiCollection { Color = "#005bbf", Id = 1, Name = "Col", PoiCount = 0, CreatedDate = DateTime.UtcNow });

            db.PoiCollectionItems.AddRange(
                new PoiCollectionItem { PoiId = 1, PoiCollectionId = 1 },
                new PoiCollectionItem { PoiId = 2, PoiCollectionId = 1 }
            );
        });

        var result = await service.GetCollectionsAsync();

        result.Should().HaveCount(1);
        result[0].PoiCount.Should().Be(2); // Computed, not the stored 0
    }

    [Fact]
    public async Task MarkPoiForReEnrichmentAsync_KeepsExistingPhoto_UntilEnrichmentReplacesIt()
    {
        var (service, factory) = await CreateServiceAsync(db =>
        {
            db.Pois.Add(new Poi
            {
                Id = 1,
                Name = "Has Photo",
                Latitude = 52.0,
                Longitude = 21.0,
                IsEnriched = true,
                GoogleMapsUrl = "https://www.google.com/maps/place/X/@52,21,17z",
                ImageUrl = "https://lh3.googleusercontent.com/photo=w1024",
                AddedDate = DateTime.UtcNow
            });
            db.PoiImages.Add(new PoiImage { PoiId = 1, Data = [1, 2, 3], ContentType = "image/jpeg" });
        });

        await service.MarkPoiForReEnrichmentAsync(1);

        await using var db = await factory.CreateDbContextAsync();
        var poi = await db.Pois.FindAsync(1);
        poi!.IsEnriched.Should().BeFalse();
        poi.GoogleMapsUrl.Should().BeNull();       // re-search forces a fresh lookup
        poi.ImageUrl.Should().NotBeNull();          // but the photo is kept...
        var image = await db.PoiImages.FindAsync(1);
        image.Should().NotBeNull();                 // ...bytes and all
        image!.Data.Should().Equal([1, 2, 3]);
    }

    [Fact]
    public async Task ReplacePoiGoogleMapsUrlAsync_KeepsExistingPhoto_UntilEnrichmentReplacesIt()
    {
        var (service, factory) = await CreateServiceAsync(db =>
        {
            db.Pois.Add(new Poi
            {
                Id = 1,
                Name = "Has Photo",
                Latitude = 52.0,
                Longitude = 21.0,
                IsEnriched = true,
                ImageUrl = "https://lh3.googleusercontent.com/photo=w1024",
                AddedDate = DateTime.UtcNow
            });
            db.PoiImages.Add(new PoiImage { PoiId = 1, Data = [9, 9], ContentType = "image/jpeg" });
        });

        await service.ReplacePoiGoogleMapsUrlAsync(1, "https://www.google.com/maps/place/Correct/@52,21,17z");

        await using var db = await factory.CreateDbContextAsync();
        var poi = await db.Pois.FindAsync(1);
        poi!.IsEnriched.Should().BeFalse();
        poi.GoogleMapsUrl.Should().Contain("/maps/place/Correct");
        poi.Latitude.Should().BeNull();             // stale coords dropped for the new place
        (await db.PoiImages.FindAsync(1)).Should().NotBeNull();   // photo survives until re-enrich
    }

    [Fact]
    public async Task MarkCollectionForReEnrichmentAsync_KeepsExistingPhotos()
    {
        var (service, factory) = await CreateServiceAsync(db =>
        {
            db.Pois.AddRange(
                new Poi { Id = 1, Name = "A", Latitude = 52.0, Longitude = 21.0, IsEnriched = true, ImageUrl = "https://lh3.googleusercontent.com/a=w1024", AddedDate = DateTime.UtcNow },
                new Poi { Id = 2, Name = "B", Latitude = 50.0, Longitude = 19.0, IsEnriched = true, ImageUrl = "https://lh3.googleusercontent.com/b=w1024", AddedDate = DateTime.UtcNow });
            db.PoiCollections.Add(new PoiCollection { Id = 1, Name = "Col", Color = "#005bbf", CreatedDate = DateTime.UtcNow });
            db.PoiCollectionItems.AddRange(
                new PoiCollectionItem { PoiId = 1, PoiCollectionId = 1 },
                new PoiCollectionItem { PoiId = 2, PoiCollectionId = 1 });
            db.PoiImages.AddRange(
                new PoiImage { PoiId = 1, Data = [1], ContentType = "image/jpeg" },
                new PoiImage { PoiId = 2, Data = [2], ContentType = "image/jpeg" });
        });

        var count = await service.MarkCollectionForReEnrichmentAsync(1);

        count.Should().Be(2);
        await using var db = await factory.CreateDbContextAsync();
        (await db.Pois.FindAsync(1))!.IsEnriched.Should().BeFalse();
        (await db.Pois.FindAsync(2))!.IsEnriched.Should().BeFalse();
        // No photos stripped.
        (await db.PoiImages.FindAsync(1)).Should().NotBeNull();
        (await db.PoiImages.FindAsync(2)).Should().NotBeNull();
    }

    [Fact]
    public async Task CreatePoiAsync_DoesNotRequestEnrichment()
    {
        // Decoupling: creating a POI must not enqueue it for the BG worker.
        var (service, factory) = await CreateServiceAsync(db =>
            db.PoiCollections.Add(new PoiCollection { Id = 1, Name = "Col", Color = "#005bbf", CreatedDate = DateTime.UtcNow }));

        var created = await service.CreatePoiAsync(
            new Poi { Name = "Event", Latitude = 53.0, Longitude = 20.0, AddedDate = DateTime.UtcNow },
            collectionId: 1);

        await using var db = await factory.CreateDbContextAsync();
        var poi = await db.Pois.FindAsync(created.Id);
        poi!.EnrichmentRequested.Should().BeFalse();
        poi.IsEnriched.Should().BeFalse();
    }

    [Fact]
    public async Task MarkPoiForReEnrichmentAsync_RequestsEnrichment()
    {
        var (service, factory) = await CreateServiceAsync(db =>
            db.Pois.Add(new Poi { Id = 1, Name = "A", Latitude = 52.0, Longitude = 21.0, IsEnriched = true, AddedDate = DateTime.UtcNow }));

        await service.MarkPoiForReEnrichmentAsync(1);

        await using var db = await factory.CreateDbContextAsync();
        (await db.Pois.FindAsync(1))!.EnrichmentRequested.Should().BeTrue();
    }

    [Fact]
    public async Task ResetFailedEnrichmentAsync_ResetsCounterAndRequeues()
    {
        var (service, factory) = await CreateServiceAsync(db =>
            db.Pois.Add(new Poi { Id = 1, Name = "Failed", Latitude = 52.0, Longitude = 21.0, IsEnriched = false, EnrichmentFailureCount = 3, EnrichmentRequested = false, AddedDate = DateTime.UtcNow }));

        var count = await service.ResetFailedEnrichmentAsync();

        count.Should().Be(1);
        await using var db = await factory.CreateDbContextAsync();
        var poi = await db.Pois.FindAsync(1);
        poi!.EnrichmentFailureCount.Should().Be(0);
        poi.EnrichmentRequested.Should().BeTrue("reset must re-enqueue or the worker ignores it");
    }

    [Fact]
    public async Task RequestEnrichmentAsync_FlagsOnlyGivenIds_WithoutResettingState()
    {
        var (service, factory) = await CreateServiceAsync(db =>
            db.Pois.AddRange(
                new Poi { Id = 1, Name = "A", Latitude = 52.0, Longitude = 21.0, IsEnriched = true, GoogleMapsUrl = "https://www.google.com/maps/place/A/@52,21,17z", AddedDate = DateTime.UtcNow },
                new Poi { Id = 2, Name = "B", Latitude = 50.0, Longitude = 19.0, IsEnriched = true, AddedDate = DateTime.UtcNow }));

        var count = await service.RequestEnrichmentAsync([1]);

        count.Should().Be(1);
        await using var db = await factory.CreateDbContextAsync();
        var a = await db.Pois.FindAsync(1);
        a!.EnrichmentRequested.Should().BeTrue();
        // Unlike MarkPoiForReEnrichment, no other state is reset.
        a.IsEnriched.Should().BeTrue();
        a.GoogleMapsUrl.Should().Contain("/maps/place/A");
        (await db.Pois.FindAsync(2))!.EnrichmentRequested.Should().BeFalse();
    }
}