using System.ComponentModel;
using LucidCartographer.Services.Enrichment;
using ModelContextProtocol.Server;

namespace LucidCartographer.Services.Mcp;

/// <summary>
/// MCP enrichment tools — queue POIs/collections for background enrichment and
/// poll progress. Delegates to <see cref="IPoiService"/> + the singleton
/// <see cref="EnrichmentTrigger"/> / <see cref="EnrichmentProgressService"/>
/// shared with the background worker.
/// </summary>
[McpServerToolType]
public static class EnrichmentTools
{
    [McpServerTool(Name = "enrich_poi")]
    [Description("Queue a single POI for background enrichment (re-scrapes address, website, phone and photo) and wake the worker. Poll get_enrichment_status to watch progress.")]
    public static async Task<string> EnrichPoi(
        IPoiService poiService,
        EnrichmentTrigger enrichmentTrigger,
        [Description("POI id.")] int poiId,
        CancellationToken ct)
    {
        await poiService.MarkPoiForReEnrichmentAsync(poiId, ct);
        enrichmentTrigger.Signal();
        return $"Queued POI {poiId} for enrichment.";
    }

    [McpServerTool(Name = "enrich_collection")]
    [Description("Queue every POI in a collection for background enrichment and wake the worker. Returns the number queued.")]
    public static async Task<string> EnrichCollection(
        IPoiService poiService,
        EnrichmentTrigger enrichmentTrigger,
        [Description("Collection id.")] int collectionId,
        CancellationToken ct)
    {
        var count = await poiService.MarkCollectionForReEnrichmentAsync(collectionId, ct);
        if (count > 0)
        {
            enrichmentTrigger.Signal();
        }
        return $"Queued {count} POIs in collection {collectionId} for enrichment.";
    }

    [McpServerTool(Name = "set_poi_google_maps_url")]
    [Description(
        "Fix a POI whose enrichment couldn't find the right place by giving it the correct Google Maps " +
        "place URL (e.g. https://www.google.com/maps/place/... or https://maps.app.goo.gl/...). Clears the " +
        "POI's stale coordinates/photo and queues a fresh enrichment sourced from this URL. This is the " +
        "headless equivalent of pasting a link into the app's manual-URL dialog.")]
    public static async Task<string> SetPoiGoogleMapsUrl(
        IPoiService poiService,
        EnrichmentTrigger enrichmentTrigger,
        [Description("POI id.")] int poiId,
        [Description("Google Maps URL of the place.")] string googleMapsUrl,
        CancellationToken ct)
    {
        await poiService.ReplacePoiGoogleMapsUrlAsync(poiId, googleMapsUrl, ct);
        enrichmentTrigger.Signal();
        return $"Set Google Maps URL for POI {poiId} and queued re-enrichment from it.";
    }

    [McpServerTool(Name = "get_enrichment_status")]
    [Description("Get the current background-enrichment queue status. Remaining reaches 0 when the queue drains.")]
    public static EnrichmentStatusDto GetEnrichmentStatus(EnrichmentProgressService progress)
        => new(progress.Total, progress.Remaining, progress.Fetched);
}
