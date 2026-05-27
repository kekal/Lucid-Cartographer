using System.ComponentModel;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Enrichment;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace LucidCartographer.Services.Mcp;

/// <summary>
/// MCP write tools — create collections/POIs and move/copy/delete POIs between
/// collections. All calls delegate to the existing <see cref="IPoiService"/>;
/// validation (name, coordinate ranges, category/status enums) is enforced by
/// the service. No business logic is duplicated here.
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
        "Create a new POI in a collection. The POI is always created unenriched and is automatically " +
        "queued for background enrichment (scrapes address/photo/website/phone from its Google Maps URL " +
        "or name). Category must be one of: restaurant, cafe, bar, hotel, attraction, shopping, nature, " +
        "other (or omitted). Status must be one of: visited, want_to_go, imported (or omitted).")]
    public static async Task<PoiSummaryDto> CreatePoi(
        IPoiService poiService,
        EnrichmentTrigger enrichmentTrigger,
        [Description("Target collection id.")] int collectionId,
        [Description("POI name (required).")] string name,
        [Description("Latitude in [-90, 90].")] double? latitude = null,
        [Description("Longitude in [-180, 180].")] double? longitude = null,
        [Description("Google Maps URL (used as the enrichment source when enrich=true).")] string? googleMapsUrl = null,
        [Description("Street address.")] string? address = null,
        [Description("Category (see allowed values).")] string? category = null,
        [Description("Status (see allowed values).")] string? status = null,
        [Description("Free-text notes.")] string? notes = null,
        [Description("Website URL.")] string? website = null,
        [Description("Phone number.")] string? phone = null,
        [Description("Personal rating 1-5.")] int? rating = null,
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
            Status = status,
            Notes = notes,
            Website = website,
            Phone = phone,
            Rating = rating,
            AddedDate = DateTime.UtcNow,
            // Always unenriched: a freshly created POI is never considered
            // pre-enriched. The background service picks it up and fills
            // address/photo/website/phone (mirrors the import path).
            IsEnriched = false
        };

        var created = await poiService.CreatePoiAsync(poi, collectionId, ct);
        // Wake the worker so enrichment starts promptly instead of waiting for
        // the next idle poll.
        enrichmentTrigger.Signal();
        return PoiSummaryDto.From(created);
    }

    [McpServerTool(Name = "edit_poi")]
    [Description(
        "Edit a POI's name and/or description (notes). Only these two fields can be changed; " +
        "all other fields (coordinates, address, category, photo, enrichment state, etc.) are " +
        "preserved untouched. To change anything else, delete and recreate the POI instead. " +
        "Omit a parameter (or pass null) to leave that field unchanged; pass an empty string " +
        "for description to clear it. Returns the updated POI.")]
    public static async Task<PoiDetailDto?> EditPoi(
        IPoiService poiService,
        IDbContextFactory<AppDbContext> dbFactory,
        [Description("POI id.")] int poiId,
        [Description("New name. Omit to leave the name unchanged.")] string? name = null,
        [Description("New description/notes. Omit to leave unchanged; pass \"\" to clear.")] string? description = null,
        CancellationToken ct = default)
    {
        var poi = await poiService.GetPoiAsync(poiId, ct);
        if (poi is null)
        {
            return null;
        }

        if (name is not null)
        {
            // Name is required; reject a blank rename (the service also validates,
            // but fail early with a clearer message).
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Name cannot be blank.", nameof(name));
            }
            poi.Name = name;
        }

        if (description is not null)
        {
            poi.Notes = description;
        }

        await poiService.UpdatePoiAsync(poi, ct);

        var namesById = await poiService.GetPoiCollectionNamesAsync([poiId], ct);
        var collections = namesById.TryGetValue(poiId, out var names) ? names : [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var hasImage = await db.PoiImages.AsNoTracking()
            .AnyAsync(i => i.PoiId == poiId && i.Data.Length > 0, ct);

        return PoiDetailDto.From(poi, collections, hasImage);
    }

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
