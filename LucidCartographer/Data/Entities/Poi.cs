using System.ComponentModel.DataAnnotations;
namespace LucidCartographer.Data.Entities;

public class Poi
{
    public int Id { get; set; }

    public required string Name { get; set; }

    [Range(-90.0, 90.0)]
    public double? Latitude { get; set; }

    [Range(-180.0, 180.0)]
    public double? Longitude { get; set; }

    public string? GoogleMapsUrl { get; set; }

    public string? Address { get; set; }

    public string? Category { get; set; }

    public string? Notes { get; set; }

    [Range(1, 5)]
    public int? Rating { get; set; } // personal 1-5

    [Range(1.0, 5.0)]
    public double? GoogleRating { get; set; } // Google rating e.g. 4.3

    public int? ReviewCount { get; set; }

    public string? Website { get; set; }

    public string? Phone { get; set; }

    public string? ImageUrl { get; set; }

    // Binary image data is stored in a separate PoiImage table so that
    // default Poi queries don't drag BLOBs across the wire. This nav
    // property is NOT auto-loaded — include it explicitly only when
    // serving /api/poi-image/{id}. The presence of the image for a POI
    // should be inferred from ImageUrl, not from this nav.
    public PoiImage? Image { get; set; }

    public string? Country { get; set; }

    public string? Region { get; set; }

    public DateTime AddedDate { get; set; }

    /// <summary>
    /// False means the background enrichment service should fetch
    /// detail-page data (address / website / phone) for this POI.
    /// File imports (KML/GPX/CSV/GeoJSON) set this to true because
    /// the source file already carries whatever metadata exists.
    /// Google-scraped rows start as false — the scraper captures only
    /// what the list card exposes and the background service fills
    /// the rest by opening each place URL in a headless tab. If
    /// enrichment fails, the row is left as-is and will be retried
    /// on the next poll cycle (no retry cap, per design).
    /// </summary>
    public bool IsEnriched { get; set; }

    /// <summary>
    /// Consecutive enrichment failures. Incremented by the background
    /// enrichment service when a POI fails to enrich.
    /// </summary>
    public int EnrichmentFailureCount { get; set; }

    /// <summary>
    /// UTC timestamp of the last enrichment attempt.
    /// </summary>
    public DateTime? LastEnrichmentAttemptAt { get; set; }

    /// <summary>
    /// True when the BG service ran without errors but the place panel
    /// produced no useful data (typical SERP / no-unique-match outcome).
    /// Retrying with the same query won't help, so the row is removed
    /// from the queue and the UI prompts the user for a manual Google
    /// Maps URL via the EnrichFallback dialog. Cleared on the next
    /// successful enrichment or when the user supplies a URL.
    /// </summary>
    public bool EnrichmentNeedsManualUrl { get; set; }

    [ConcurrencyCheck]
    public int Version { get; set; }

    public List<PoiCollectionItem> CollectionItems { get; set; } = [];
    public List<PoiTag> PoiTags { get; set; } = [];
}
