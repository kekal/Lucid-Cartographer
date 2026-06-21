using System.ComponentModel;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace LucidCartographer.Services.Mcp;

/// <summary>
/// MCP write tools for managing collections and POIs; all operations delegate to <see cref="IPoiService"/>.
/// </summary>
[McpServerToolType]
public static class PoiWriteTools
{
    [McpServerTool(Name = "create_collection")]
    [Description("Create a new POI collection. Returns the created collection.")]
    public static async Task<CollectionDto> CreateCollection(
        IPoiService poiService,
        [Description("Collection name.")] string name,
        [Description("Optional hex color in #RRGGBB format (e.g. #005bbf). Defaults to the app default if omitted.")] string? color = null,
        CancellationToken ct = default)
    {
        var collection = await poiService.CreateCollectionAsync(name, ct);
        if (!string.IsNullOrWhiteSpace(color))
        {
            await poiService.UpdateCollectionColorAsync(collection.Id, color, ct);
            collection.Color = color;
        }
        return CollectionDto.From(collection);
    }

    [McpServerTool(Name = "create_poi")]
    [Description(
        "Create a new POI in a collection. Creation does NOT enrich the POI: the fields you pass are " +
        "stored exactly as given, with no Google Maps lookup and no fuzzy-merge into a nearby place. " +
        "This is the right tool for events, custom waypoints, and anything that is not its own Google " +
        "Place. To fetch address/photo/website/phone for a real place afterwards, call enrich_poi on " +
        "the returned id. Category must be one of: restaurant, cafe, bar, hotel, attraction, shopping, " +
        "nature, other (or omitted). Optionally pass imageUrl (http/https) to download and store a photo " +
        "now. Before creating, search_pois the name (one name per call) to avoid duplicates; a POI can " +
        "live in several collections, so prefer copy_poi/move_poi over re-creating an existing one.")]
    public static async Task<PoiSummaryDto> CreatePoi(
        IPoiService poiService,
        IDbContextFactory<AppDbContext> dbFactory,
        IHttpClientFactory httpFactory,
        [Description("Target collection id.")] int collectionId,
        [Description("POI name (required).")] string name,
        [Description("Latitude in [-90, 90].")] double? latitude = null,
        [Description("Longitude in [-180, 180].")] double? longitude = null,
        [Description("Google Maps URL (stored as-is; used as the enrichment source if you later call enrich_poi).")] string? googleMapsUrl = null,
        [Description("Street address.")] string? address = null,
        [Description("Category (see allowed values).")] string? category = null,
        [Description("Free-text notes.")] string? notes = null,
        [Description("Website URL.")] string? website = null,
        [Description("Phone number.")] string? phone = null,
        [Description("Personal rating 1-5.")] int? rating = null,
        [Description("Image URL (http/https) to download and store as the POI photo now. Omit to add no photo.")] string? imageUrl = null,
        CancellationToken ct = default)
    {
        var poi = new Poi
        {
            Name = name,
            Latitude = latitude,
            Longitude = longitude,
            GoogleMapsUrl = googleMapsUrl,
            Address = address,
            Category = category,
            Notes = notes,
            Website = website,
            Phone = phone,
            Rating = rating,
            AddedDate = DateTime.UtcNow,
            // Creation is decoupled from enrichment — call enrich_poi explicitly.
            IsEnriched = false,
            EnrichmentRequested = false
        };

        // Validate image URL before creating POI to avoid orphans.
        DownloadedImage? image = null;
        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            image = await ImageDownloadHelper.DownloadAsync(httpFactory, imageUrl, ct);
        }

        var created = await poiService.CreatePoiAsync(poi, collectionId, ct);

        if (image is not null)
        {
            created.ImageUrl = await ImageDownloadHelper.ApplyAsync(dbFactory, created.Id, image, ct);
        }

        return PoiSummaryDto.From(created);
    }

    [McpServerTool(Name = "edit_poi")]
    [Description(
        "Edit a POI's user-facing fields. Editable: name, description (notes), latitude, longitude, " +
        "googleMapsUrl, address, category, website, phone, rating, country, region, and imageUrl. " +
        "Omit a parameter (or pass null) to leave that field unchanged; pass an empty string to CLEAR a " +
        "text field (description/address/website/phone/googleMapsUrl/category/country/region). " +
        "Coordinates can be set but not cleared to null here. Enrichment state and the Google-derived " +
        "googleRating/reviewCount are preserved untouched. imageUrl downloads (http/https) and stores a " +
        "new photo, replacing any existing one; pass \"\" to remove the photo. Editing does not trigger " +
        "enrichment. Returns the updated POI.")]
    public static async Task<PoiDetailDto?> EditPoi(
        IPoiService poiService,
        IDbContextFactory<AppDbContext> dbFactory,
        IHttpClientFactory httpFactory,
        IRouteSegmentInvalidationService routeSegmentInvalidation,
        [Description("POI id.")] int poiId,
        [Description("New name. Omit to leave unchanged.")] string? name = null,
        [Description("New description/notes. Omit to leave unchanged; pass \"\" to clear.")] string? description = null,
        [Description("New latitude in [-90, 90]. Omit to leave unchanged.")] double? latitude = null,
        [Description("New longitude in [-180, 180]. Omit to leave unchanged.")] double? longitude = null,
        [Description("New Google Maps URL. Omit to leave unchanged; pass \"\" to clear.")] string? googleMapsUrl = null,
        [Description("New street address. Omit to leave unchanged; pass \"\" to clear.")] string? address = null,
        [Description("New category (allowed values). Omit to leave unchanged; pass \"\" to clear.")] string? category = null,
        [Description("New website URL. Omit to leave unchanged; pass \"\" to clear.")] string? website = null,
        [Description("New phone. Omit to leave unchanged; pass \"\" to clear.")] string? phone = null,
        [Description("New personal rating 1-5. Omit to leave unchanged.")] int? rating = null,
        [Description("New country. Omit to leave unchanged; pass \"\" to clear.")] string? country = null,
        [Description("New region. Omit to leave unchanged; pass \"\" to clear.")] string? region = null,
        [Description("Image URL (http/https) to download and store as the photo, replacing any existing one. Pass \"\" to remove the photo. Omit to leave the photo unchanged.")] string? imageUrl = null,
        CancellationToken ct = default)
    {
        // Load full entity to round-trip enrichment-owned fields (GoogleRating, ReviewCount) untouched.
        var poi = await poiService.GetPoiAsync(poiId, ct);
        if (poi is null)
        {
            return null;
        }

        if (name is not null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Name cannot be blank.", nameof(name));
            }
            poi.Name = name;
        }

        // null = leave unchanged; "" = clear (for the nullable string fields).
        if (description is not null) poi.Notes = NullIfEmpty(description);
        if (googleMapsUrl is not null) poi.GoogleMapsUrl = NullIfEmpty(googleMapsUrl);
        if (address is not null) poi.Address = NullIfEmpty(address);
        if (category is not null) poi.Category = NullIfEmpty(category);
        if (website is not null) poi.Website = NullIfEmpty(website);
        if (phone is not null) poi.Phone = NullIfEmpty(phone);
        if (country is not null) poi.Country = NullIfEmpty(country);
        if (region is not null) poi.Region = NullIfEmpty(region);
        // Detect genuine coordinate change to invalidate cached legs (supplied-but-identical is not a change).
        var coordsChanged = (latitude is not null && latitude != poi.Latitude)
            || (longitude is not null && longitude != poi.Longitude);
        if (latitude is not null) poi.Latitude = latitude;
        if (longitude is not null) poi.Longitude = longitude;
        if (rating is not null) poi.Rating = rating;

        // Validate image URL before persisting to prevent orphans; write image only after save succeeds.
        var changeImage = imageUrl is not null;
        DownloadedImage? image = changeImage
            ? await ImageDownloadHelper.DownloadAsync(httpFactory, imageUrl, ct) // null => clear ("")
            : null;

        await poiService.UpdatePoiAsync(poi, ct);

        // Coordinate changes invalidate cached RouteSegment legs; background compute refills them.
        if (coordsChanged)
        {
            await routeSegmentInvalidation.InvalidateForPoiAsync(poiId, ct);
        }

        if (changeImage)
        {
            poi.ImageUrl = await ImageDownloadHelper.ApplyAsync(dbFactory, poiId, image, ct);
        }

        var namesById = await poiService.GetPoiCollectionNamesAsync([poiId], ct);
        var collections = namesById.TryGetValue(poiId, out var names) ? names : [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var hasImage = await db.PoiImages.AsNoTracking()
            .AnyAsync(i => i.PoiId == poiId && i.Data.Length > 0, ct);

        return PoiDetailDto.From(poi, collections, hasImage);
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrEmpty(value) ? null : value;

    [McpServerTool(Name = "move_poi")]
    [Description("Move a POI from one collection to another (adds to target, then removes from source).")]
    public static async Task<string> MovePoi(
        IPoiService poiService,
        [Description("POI id.")] int poiId,
        [Description("Source collection id to remove from.")] int fromCollectionId,
        [Description("Target collection id to add to.")] int toCollectionId,
        CancellationToken ct)
    {
        if (fromCollectionId == toCollectionId)
        {
            return $"Source and target collection are the same ({toCollectionId}); nothing to do.";
        }
        await poiService.AddPoiToCollectionAsync(poiId, toCollectionId, ct);
        await poiService.RemovePoiFromCollectionAsync(poiId, fromCollectionId, ct);
        return $"Moved POI {poiId} from collection {fromCollectionId} to {toCollectionId}.";
    }

    [McpServerTool(Name = "copy_poi")]
    [Description("Copy a POI into another collection (the POI stays in its current collections too).")]
    public static async Task<string> CopyPoi(
        IPoiService poiService,
        [Description("POI id.")] int poiId,
        [Description("Target collection id.")] int toCollectionId,
        CancellationToken ct)
    {
        await poiService.AddPoiToCollectionAsync(poiId, toCollectionId, ct);
        return $"Copied POI {poiId} into collection {toCollectionId}.";
    }

    [McpServerTool(Name = "delete_poi")]
    [Description("Remove a POI from a collection (same as the delete button in the POI table). If the POI ends up in no collection, it is deleted entirely.")]
    public static async Task<string> DeletePoi(
        IPoiService poiService,
        [Description("POI id.")] int poiId,
        [Description("Collection id to remove the POI from.")] int collectionId,
        CancellationToken ct)
    {
        await poiService.RemovePoiFromCollectionAsync(poiId, collectionId, ct);
        return $"Removed POI {poiId} from collection {collectionId}.";
    }
}
