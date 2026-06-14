using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    // Image-download is never exercised by these tests (imageUrl omitted or "").
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static readonly IHttpClientFactory Http = new StubHttpClientFactory();

    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    private sealed class ImageStubHandler(HttpStatusCode status, byte[]? body, string? contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(status);
            if (body is not null)
            {
                var content = new ByteArrayContent(body);
                if (contentType is not null) content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                resp.Content = content;
            }
            return Task.FromResult(resp);
        }
    }

    private sealed class HandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private static IHttpClientFactory PngHttp() =>
        new HandlerHttpClientFactory(new ImageStubHandler(HttpStatusCode.OK, PngBytes, "image/png"));

    private static IHttpClientFactory ErrorHttp(HttpStatusCode status) =>
        new HandlerHttpClientFactory(new ImageStubHandler(status, null, null));

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

        var service = new PoiService(factory, TestDbHelper.CreateInvalidationService(factory), NullLoggerFactory.Instance.CreateLogger<PoiService>());
        return (service, factory);
    }

    [Fact]
    public async Task EditPoi_ChangesNameAndNotes_PreservingOtherFields()
    {
        var (service, factory) = await CreateServiceWithPoiAsync(_ => { });

        var result = await PoiWriteTools.EditPoi(service, factory, Http, TestDbHelper.CreateInvalidationService(factory), poiId: 1,
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

        var result = await PoiWriteTools.EditPoi(service, factory, Http, TestDbHelper.CreateInvalidationService(factory), poiId: 1,
            name: null, description: null);

        result!.Name.Should().Be("Original Name");
        result.Notes.Should().Be("Original notes");
    }

    [Fact]
    public async Task EditPoi_OnlyName_LeavesNotesUntouched()
    {
        var (service, factory) = await CreateServiceWithPoiAsync(_ => { });

        var result = await PoiWriteTools.EditPoi(service, factory, Http, TestDbHelper.CreateInvalidationService(factory), poiId: 1,
            name: "Renamed");

        result!.Name.Should().Be("Renamed");
        result.Notes.Should().Be("Original notes");
    }

    [Fact]
    public async Task EditPoi_EmptyDescription_ClearsNotes()
    {
        var (service, factory) = await CreateServiceWithPoiAsync(_ => { });

        var result = await PoiWriteTools.EditPoi(service, factory, Http, TestDbHelper.CreateInvalidationService(factory), poiId: 1,
            description: "");

        // "" clears a text field to null (consistent across all editable strings).
        result!.Notes.Should().BeNull();
        result.Name.Should().Be("Original Name");
    }

    [Fact]
    public async Task EditPoi_BlankName_Throws()
    {
        var (service, factory) = await CreateServiceWithPoiAsync(_ => { });

        var act = () => PoiWriteTools.EditPoi(service, factory, Http, TestDbHelper.CreateInvalidationService(factory), poiId: 1, name: "   ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task EditPoi_UnknownPoi_ReturnsNull()
    {
        var (service, factory) = await CreateServiceWithPoiAsync(_ => { });

        var result = await PoiWriteTools.EditPoi(service, factory, Http, TestDbHelper.CreateInvalidationService(factory), poiId: 999, name: "X");

        result.Should().BeNull();
    }

    [Fact]
    public async Task EditPoi_UpdatesBroadenedFields()
    {
        var (service, factory) = await CreateServiceWithPoiAsync(_ => { });

        var result = await PoiWriteTools.EditPoi(service, factory, Http, TestDbHelper.CreateInvalidationService(factory), poiId: 1,
            address: "New address 5", category: "cafe", website: "https://example.org",
            phone: "+48 100 200 300", latitude: 50.0, longitude: 19.0, rating: 4,
            country: "Poland", region: "Lesser Poland");

        result!.Address.Should().Be("New address 5");
        result.Category.Should().Be("cafe");
        result.Website.Should().Be("https://example.org");
        result.Phone.Should().Be("+48 100 200 300");
        result.Latitude.Should().Be(50.0);
        result.Longitude.Should().Be(19.0);
        result.Rating.Should().Be(4);
        result.Country.Should().Be("Poland");
        result.Region.Should().Be("Lesser Poland");
    }

    [Fact]
    public async Task EditPoi_PreservesGoogleRatingAndReviewCount()
    {
        // Regression: UpdatePoiAsync copies GoogleRating/ReviewCount from the
        // entity, so edit_poi must load the full entity and never reset them.
        var (service, factory) = await CreateServiceWithPoiAsync(p =>
        {
            p.GoogleRating = 4.3;
            p.ReviewCount = 120;
        });

        var result = await PoiWriteTools.EditPoi(service, factory, Http, TestDbHelper.CreateInvalidationService(factory), poiId: 1,
            address: "Changed");

        result!.Address.Should().Be("Changed");
        result.GoogleRating.Should().Be(4.3);
        result.ReviewCount.Should().Be(120);
    }

    [Fact]
    public async Task EditPoi_EmptyImageUrl_ClearsPhoto()
    {
        var (service, factory) = await CreateServiceWithPoiAsync(_ => { });
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.PoiImages.Add(new PoiImage { PoiId = 1, Data = [1, 2, 3], ContentType = "image/png" });
            await db.SaveChangesAsync();
        }

        var result = await PoiWriteTools.EditPoi(service, factory, Http, TestDbHelper.CreateInvalidationService(factory), poiId: 1, imageUrl: "");

        result!.ImageUrl.Should().BeNull();
        result.HasStoredImage.Should().BeFalse();
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.PoiImages.AnyAsync(i => i.PoiId == 1)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task CreatePoi_DoesNotEnqueueEnrichment()
    {
        // Decoupling: creation must NOT request enrichment.
        var factory = TestDbHelper.CreateFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.PoiCollections.Add(new PoiCollection { Id = 1, Name = "Col", Color = "#005bbf", CreatedDate = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var service = new PoiService(factory, TestDbHelper.CreateInvalidationService(factory), NullLoggerFactory.Instance.CreateLogger<PoiService>());

        var dto = await PoiWriteTools.CreatePoi(service, factory, Http,
            collectionId: 1, name: "2026-07-18 Â· Reenactment", latitude: 53.488, longitude: 20.087,
            category: "other");

        await using (var db = await factory.CreateDbContextAsync())
        {
            var poi = await db.Pois.FindAsync(dto.Id);
            poi!.EnrichmentRequested.Should().BeFalse();
            poi.IsEnriched.Should().BeFalse();
        }
    }

    [Fact]
    public async Task CreatePoi_WithImageUrl_DownloadsAndStoresPhoto()
    {
        var factory = TestDbHelper.CreateFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.PoiCollections.Add(new PoiCollection { Id = 1, Name = "Col", Color = "#005bbf", CreatedDate = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var service = new PoiService(factory, TestDbHelper.CreateInvalidationService(factory), NullLoggerFactory.Instance.CreateLogger<PoiService>());

        var dto = await PoiWriteTools.CreatePoi(service, factory, PngHttp(),
            collectionId: 1, name: "WithPhoto", imageUrl: "https://example.com/p.png");

        await using (var db = await factory.CreateDbContextAsync())
        {
            var poi = await db.Pois.FindAsync(dto.Id);
            poi!.ImageUrl.Should().Be("https://example.com/p.png");
            (await db.PoiImages.AnyAsync(i => i.PoiId == dto.Id)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task CreatePoi_BadImageUrl_CreatesNoPoi()
    {
        // Download is validated BEFORE the row is created, so a bad URL must not
        // leave an orphan POI (which would invite a duplicate retry).
        var factory = TestDbHelper.CreateFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.PoiCollections.Add(new PoiCollection { Id = 1, Name = "Col", Color = "#005bbf", CreatedDate = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var service = new PoiService(factory, TestDbHelper.CreateInvalidationService(factory), NullLoggerFactory.Instance.CreateLogger<PoiService>());

        var act = () => PoiWriteTools.CreatePoi(service, factory, ErrorHttp(HttpStatusCode.NotFound),
            collectionId: 1, name: "ShouldNotExist", imageUrl: "https://example.com/missing.png");

        await act.Should().ThrowAsync<ArgumentException>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.Pois.AnyAsync()).Should().BeFalse("no POI should be created when the image download fails");
        }
    }

    [Fact]
    public async Task EditPoi_WithImageUrl_DownloadsAndStoresPhoto()
    {
        var (service, factory) = await CreateServiceWithPoiAsync(p => p.ImageUrl = null);

        var result = await PoiWriteTools.EditPoi(service, factory, PngHttp(), TestDbHelper.CreateInvalidationService(factory), poiId: 1,
            imageUrl: "https://example.com/p.png");

        result!.ImageUrl.Should().Be("https://example.com/p.png");
        result.HasStoredImage.Should().BeTrue();
        await using var db = await factory.CreateDbContextAsync();
        (await db.PoiImages.AnyAsync(i => i.PoiId == 1)).Should().BeTrue();
    }

    [Fact]
    public async Task EditPoi_BadImageUrl_ThrowsAndPersistsNoFieldChange()
    {
        // Image is downloaded BEFORE field save, so a failed download leaves the
        // edited fields unpersisted (atomic-ish): the address must NOT change.
        var (service, factory) = await CreateServiceWithPoiAsync(_ => { });

        var act = () => PoiWriteTools.EditPoi(service, factory, ErrorHttp(HttpStatusCode.NotFound), TestDbHelper.CreateInvalidationService(factory),
            poiId: 1, address: "Changed Address", imageUrl: "https://example.com/missing.png");

        await act.Should().ThrowAsync<ArgumentException>();
        await using var db = await factory.CreateDbContextAsync();
        (await db.Pois.FindAsync(1))!.Address.Should().Be("Rzucewo 1, 84-100 Rzucewo");
    }

    [Theory]
    [InlineData("address")]
    [InlineData("website")]
    [InlineData("phone")]
    [InlineData("googleMapsUrl")]
    [InlineData("category")]
    [InlineData("country")]
    [InlineData("region")]
    public async Task EditPoi_EmptyString_ClearsField(string field)
    {
        var (service, factory) = await CreateServiceWithPoiAsync(p =>
        {
            p.Website = "https://w.example";
            p.Phone = "+48 100 200 300";
            p.Country = "Poland";
            p.Region = "Pomerania";
            p.Category = "attraction";
        });

        var result = field switch
        {
            "address" => await PoiWriteTools.EditPoi(service, factory, Http, TestDbHelper.CreateInvalidationService(factory), poiId: 1, address: ""),
            "website" => await PoiWriteTools.EditPoi(service, factory, Http, TestDbHelper.CreateInvalidationService(factory), poiId: 1, website: ""),
            "phone" => await PoiWriteTools.EditPoi(service, factory, Http, TestDbHelper.CreateInvalidationService(factory), poiId: 1, phone: ""),
            "googleMapsUrl" => await PoiWriteTools.EditPoi(service, factory, Http, TestDbHelper.CreateInvalidationService(factory), poiId: 1, googleMapsUrl: ""),
            "category" => await PoiWriteTools.EditPoi(service, factory, Http, TestDbHelper.CreateInvalidationService(factory), poiId: 1, category: ""),
            "country" => await PoiWriteTools.EditPoi(service, factory, Http, TestDbHelper.CreateInvalidationService(factory), poiId: 1, country: ""),
            "region" => await PoiWriteTools.EditPoi(service, factory, Http, TestDbHelper.CreateInvalidationService(factory), poiId: 1, region: ""),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var actual = field switch
        {
            "address" => result!.Address,
            "website" => result!.Website,
            "phone" => result!.Phone,
            "googleMapsUrl" => result!.GoogleMapsUrl,
            "category" => result!.Category,
            "country" => result!.Country,
            "region" => result!.Region,
            _ => "unreachable",
        };
        actual.Should().BeNull();
        // A different field stays populated (proves "" clears only the named field).
        result!.Name.Should().Be("Original Name");
    }

    // === Story 3.2 (A5, TRIP-INVALIDATE-01): MCP coordinate edits invalidate legs ===

    // Adds a neighbour POI + cached RouteSegment rows touching POI 1 (one Estimated
    // each direction + one Manual) so an invalidation can be observed.
    private static async Task SeedSegmentsTouchingPoi1Async(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.Pois.Add(new Poi { Id = 2, Name = "Neighbour", Latitude = 55.0, Longitude = 19.0, AddedDate = DateTime.UtcNow });
        db.RouteSegments.Add(new RouteSegment { FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.Drive, DurationSeconds = 600, DistanceMeters = 5000, Fidelity = Fidelity.Estimated, Source = "Mock", ComputedAt = DateTime.UtcNow });
        db.RouteSegments.Add(new RouteSegment { FromPoiId = 2, ToPoiId = 1, TravelMode = TravelMode.Drive, DurationSeconds = 600, DistanceMeters = 5000, Fidelity = Fidelity.Estimated, Source = "Mock", ComputedAt = DateTime.UtcNow });
        db.RouteSegments.Add(new RouteSegment { FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.AnyAir, DurationSeconds = 300, DistanceMeters = 5000, Fidelity = Fidelity.Manual, Source = "Manual", ComputedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task EditPoi_CoordinateChange_InvalidatesCachedLegs_KeepingManual()
    {
        var (service, factory) = await CreateServiceWithPoiAsync(_ => { });
        await SeedSegmentsTouchingPoi1Async(factory);

        await PoiWriteTools.EditPoi(service, factory, Http, TestDbHelper.CreateInvalidationService(factory),
            poiId: 1, latitude: 54.80, longitude: 18.50);

        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.RouteSegments.AsNoTracking().ToListAsync();
        rows.Should().NotContain(r => r.Fidelity == Fidelity.Estimated, "moving the POI invalidates its non-Manual legs");
        rows.Should().ContainSingle(r => r.Fidelity == Fidelity.Manual, "a user's Manual leg is never invalidated");
    }

    [Fact]
    public async Task EditPoi_NoCoordinateChange_LeavesLegsIntact()
    {
        var (service, factory) = await CreateServiceWithPoiAsync(_ => { });
        await SeedSegmentsTouchingPoi1Async(factory);

        // Edit a non-coordinate field only.
        await PoiWriteTools.EditPoi(service, factory, Http, TestDbHelper.CreateInvalidationService(factory),
            poiId: 1, name: "Renamed");

        await using var db = await factory.CreateDbContextAsync();
        (await db.RouteSegments.CountAsync()).Should().Be(3, "an edit that doesn't touch coordinates invalidates nothing");
    }

    [Fact]
    public async Task EditPoi_SameCoordinateValue_IsNotAChange()
    {
        var (service, factory) = await CreateServiceWithPoiAsync(_ => { });
        await SeedSegmentsTouchingPoi1Async(factory);

        // Pass the POI's existing coordinates verbatim — not a real change.
        await PoiWriteTools.EditPoi(service, factory, Http, TestDbHelper.CreateInvalidationService(factory),
            poiId: 1, latitude: 54.7099, longitude: 18.4373);

        await using var db = await factory.CreateDbContextAsync();
        (await db.RouteSegments.CountAsync()).Should().Be(3, "re-supplying identical coordinates invalidates nothing");
    }
}
