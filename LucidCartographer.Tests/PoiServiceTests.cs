using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests
{
    public class PoiServiceTests
    {
        private static readonly ILogger<PoiService> NullLogger = NullLoggerFactory.Instance.CreateLogger<PoiService>();

        private static async Task<(PoiService Service, Microsoft.EntityFrameworkCore.IDbContextFactory<AppDbContext> Factory)> CreateServiceAsync(Action<AppDbContext> seed)
        {
            var factory = TestDbHelper.CreateFactory();
            await using var db = factory.CreateDbContext();
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
            result.Select(p => p.Name).Should().Contain(new[] { "Poi1", "Poi2" });
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

            await using var db = factory.CreateDbContext();
            var col = await db.PoiCollections.FindAsync(1);
            col!.IsVisible.Should().BeFalse();

            // Toggle again
            await service.ToggleVisibilityAsync(1);
            await using var db2 = factory.CreateDbContext();
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

            await using var db = factory.CreateDbContext();
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

            await using var db = factory.CreateDbContext();
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
    }
}
