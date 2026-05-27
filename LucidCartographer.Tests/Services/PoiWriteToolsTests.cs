using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests;

public class PoiWriteToolsTests
{
    private static async Task<(IPoiService Service, IDbContextFactory<AppDbContext> Factory)> CreateServiceWithPoiAsync(
        Action<Poi> configure)
    {
        var factory = TestDbHelper.CreateFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var poi = new Poi
            {
                Id = 1,
                Name = "Original Name",
                Notes = "Original notes",
                Latitude = 54.7099,
                Longitude = 18.4373,
                Address = "Rzucewo 1, 84-100 Rzucewo",
                Category = "attraction",
                GoogleMapsUrl = "https://www.google.com/maps/place/X/@54.7,18.4,17z",
                ImageUrl = "https://lh3.googleusercontent.com/p/x",
                IsEnriched = true,
                AddedDate = DateTime.UtcNow
            };
            configure(poi);
            db.Pois.Add(poi);
            db.PoiCollections.Add(new PoiCollection { Id = 1, Name = "Col", Color = "#005bbf", CreatedDate = DateTime.UtcNow });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 1, PoiCollectionId = 1 });
            await db.SaveChangesAsync();
        }

        var service = new PoiService(factory, NullLoggerFactory.Instance.CreateLogger<PoiService>());
        return (service, factory);
    }

    [Fact]
    public async Task EditPoi_ChangesNameAndNotes_PreservingOtherFields()
    {
        var (service, factory) = await CreateServiceWithPoiAsync(_ => { });

        var result = await PoiWriteTools.EditPoi(service, factory, poiId: 1,
            name: "New Name", description: "New notes");

        result.Should().NotBeNull();
        result!.Name.Should().Be("New Name");
        result.Notes.Should().Be("New notes");
        // Everything else is carried through untouched.
        result.Address.Should().Be("Rzucewo 1, 84-100 Rzucewo");
        result.Category.Should().Be("attraction");
        result.Latitude.Should().Be(54.7099);
        result.GoogleMapsUrl.Should().Be("https://www.google.com/maps/place/X/@54.7,18.4,17z");
        result.IsEnriched.Should().BeTrue();
        result.Collections.Should().Contain("Col");
    }

    [Fact]
    public async Task EditPoi_NullArguments_LeaveFieldsUnchanged()
    {
        var (service, factory) = await CreateServiceWithPoiAsync(_ => { });

        var result = await PoiWriteTools.EditPoi(service, factory, poiId: 1,
            name: null, description: null);

        result!.Name.Should().Be("Original Name");
        result.Notes.Should().Be("Original notes");
    }

    [Fact]
    public async Task EditPoi_OnlyName_LeavesNotesUntouched()
    {
        var (service, factory) = await CreateServiceWithPoiAsync(_ => { });

        var result = await PoiWriteTools.EditPoi(service, factory, poiId: 1,
            name: "Renamed");

        result!.Name.Should().Be("Renamed");
        result.Notes.Should().Be("Original notes");
    }

    [Fact]
    public async Task EditPoi_EmptyDescription_ClearsNotes()
    {
        var (service, factory) = await CreateServiceWithPoiAsync(_ => { });

        var result = await PoiWriteTools.EditPoi(service, factory, poiId: 1,
            description: "");

        result!.Notes.Should().Be("");
        result.Name.Should().Be("Original Name");
    }

    [Fact]
    public async Task EditPoi_BlankName_Throws()
    {
        var (service, factory) = await CreateServiceWithPoiAsync(_ => { });

        var act = () => PoiWriteTools.EditPoi(service, factory, poiId: 1, name: "   ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task EditPoi_UnknownPoi_ReturnsNull()
    {
        var (service, factory) = await CreateServiceWithPoiAsync(_ => { });

        var result = await PoiWriteTools.EditPoi(service, factory, poiId: 999, name: "X");

        result.Should().BeNull();
    }
}
