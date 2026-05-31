using System.ComponentModel;
using LucidCartographer.Data;
using LucidCartographer.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LucidCartographer.Services.Mcp;

/// <summary>
/// MCP read tools — surface POIs, collections, every POI field, and photos.
/// All calls delegate to the existing <see cref="IPoiService"/>; no business
/// logic lives here. Service parameters are resolved from the per-request DI
/// scope by the MCP runtime.
/// </summary>
[McpServerToolType]
public static class PoiReadTools
{
    [McpServerTool(Name = "list_collections")]
    [Description("List all POI collections with their id, name, color, visibility and POI count.")]
    public static async Task<IReadOnlyList<CollectionDto>> ListCollections(
        IPoiService poiService,
        CancellationToken ct)
    {
        var collections = await poiService.GetCollectionsAsync(ct);
        return collections.Select(CollectionDto.From).ToList();
    }

    [McpServerTool(Name = "list_pois_in_collection")]
    [Description("List the POIs that belong to a collection (only POIs that have coordinates).")]
    public static async Task<IReadOnlyList<PoiSummaryDto>> ListPoisInCollection(
        IPoiService poiService,
        [Description("The collection id.")] int collectionId,
        CancellationToken ct)
    {
        var pois = await poiService.GetPoisByCollectionAsync(collectionId, ct);
        return pois.Select(PoiSummaryDto.From).ToList();
    }

    [McpServerTool(Name = "search_pois")]
    [Description(
        "Search POIs by ONE distinctive term. The query is matched as a SINGLE string against a " +
        "POI's name, address, notes and tags: every word in the query must occur together in the " +
        "SAME POI (phrase/substring match, NOT OR across words). Returns up to 100 matches.\n" +
        "Do NOT put several different place names in one query: a query like " +
        "\"Energylandia Suntago Mandoria\" returns [] even when all three exist, because no single " +
        "POI contains all those words. To look up multiple places, call this tool once per place.\n" +
        "An empty result means \"this single name was not found\" — NOT \"search is broken\"; if you " +
        "got [] from a multi-name query, retry with one name at a time. Prefer the shortest " +
        "distinctive proper name over a long descriptive phrase. Matching also hits the address " +
        "field, so results may include nearby POIs that share a street/venue name.\n" +
        "A POI can belong to several collections; on a hit call get_poi(id) to read its " +
        "`collections` before deciding whether to create a duplicate. IDs are not persistent " +
        "across imports — resolve collections by name via list_collections.\n" +
        "Examples:\n" +
        "  search_pois(\"Hevelianum\")   -> finds the POI (one proper name)\n" +
        "  search_pois(\"Energylandia\") -> finds it (+ neighbors by address)\n" +
        "  search_pois(\"Energylandia Suntago Mandoria Inwald Twinpigs\")\n" +
        "                              -> [] (five distinct names never co-occur in one POI)\n" +
        "Dedup before create — one call per candidate:\n" +
        "  for name in candidates: if not search_pois(name): create_poi(...)")]
    public static async Task<IReadOnlyList<PoiSummaryDto>> SearchPois(
        IPoiService poiService,
        [Description(
            "ONE distinctive term (usually a single proper name). Matched as a single substring "
            + "across name/address/notes/tags — never pass multiple different place names at once.")]
            string query,
        CancellationToken ct)
    {
        var pois = await poiService.SearchAsync(query, ct);
        return pois.Select(PoiSummaryDto.From).ToList();
    }

    [McpServerTool(Name = "get_poi")]
    [Description("Get the full detail of a single POI, including every field and the collections it belongs to. Returns null if not found.")]
    public static async Task<PoiDetailDto?> GetPoi(
        IPoiService poiService,
        IDbContextFactory<AppDbContext> dbFactory,
        [Description("The POI id.")] int poiId,
        CancellationToken ct)
    {
        var poi = await poiService.GetPoiAsync(poiId, ct);
        if (poi is null)
        {
            return null;
        }

        var namesById = await poiService.GetPoiCollectionNamesAsync([poiId], ct);
        var collections = namesById.TryGetValue(poiId, out var names) ? names : [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var hasImage = await db.PoiImages.AsNoTracking()
            .AnyAsync(i => i.PoiId == poiId && i.Data.Length > 0, ct);

        return PoiDetailDto.From(poi, collections, hasImage);
    }

    [McpServerTool(Name = "get_poi_image")]
    [Description("Get the photo for a POI as a viewable image. If no image bytes are stored, returns the external image URL (if any) as text.")]
    public static async Task<ContentBlock> GetPoiImage(
        IDbContextFactory<AppDbContext> dbFactory,
        IPoiService poiService,
        [Description("The POI id.")] int poiId,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var image = await db.PoiImages.AsNoTracking()
            .FirstOrDefaultAsync(i => i.PoiId == poiId, ct);

        if (image is not null && image.Data.Length > 0)
        {
            return ImageContentBlock.FromBytes(image.Data, image.ContentType ?? "image/jpeg");
        }

        var poi = await poiService.GetPoiAsync(poiId, ct);
        var text = poi?.ImageUrl is { Length: > 0 } url
            ? $"No image bytes stored for POI {poiId}. External image URL: {url}"
            : $"No image available for POI {poiId}.";
        return new TextContentBlock { Text = text };
    }
}
