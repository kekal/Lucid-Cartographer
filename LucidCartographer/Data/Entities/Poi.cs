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

    // Binary data in separate table; infer presence from ImageUrl, not this nav.
    public PoiImage? Image { get; set; }

    public string? Country { get; set; }

    public string? Region { get; set; }

    public DateTime AddedDate { get; set; }

    /// <summary>
    /// True after an enrichment attempt completes (regardless of outcome).
    /// Unlike <see cref="EnrichmentRequested"/>, this is not a queue signal.
    /// </summary>
    public bool IsEnriched { get; set; }

    /// <summary>
    /// Queue signal for the background enrichment service. Cleared on terminal
    /// outcomes; creation does not set this (decoupled from enrichment).
    /// </summary>
    public bool EnrichmentRequested { get; set; }

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
    /// True when enrichment ran but found no useful data; retry won't help.
    /// Cleared on successful enrichment or when user supplies a URL.
    /// </summary>
    public bool EnrichmentNeedsManualUrl { get; set; }

    [ConcurrencyCheck]
    public int Version { get; set; }

    public List<PoiCollectionItem> CollectionItems { get; set; } = [];
    public List<PoiTag> PoiTags { get; set; } = [];
}
