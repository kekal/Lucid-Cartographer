using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Mcp;

/// <summary>MCP-facing DTOs returned instead of EF entities to control JSON shape and avoid reference cycles.</summary>
public record CollectionDto(
    int Id,
    string Name,
    string? Description,
    string Color,
    bool IsVisible,
    int PoiCount,
    string? SourceType,
    string? SourceFileName,
    DateTime CreatedDate)
{
    public static CollectionDto From(PoiCollection c) => new(
        c.Id, c.Name, c.Description, c.Color, c.IsVisible, c.PoiCount,
        c.SourceType, c.SourceFileName, c.CreatedDate);
}

/// <summary>Compact POI shape for list/search results.</summary>
/// <remarks>
/// <see cref="HasPlaceUrl"/> reports whether the POI carries a canonical Google
/// Maps <c>/maps/place/</c> URL — the trustworthy signal that enrichment
/// actually resolved a place. Together with <see cref="IsEnriched"/> and
/// <see cref="EnrichmentNeedsManualUrl"/> this lets callers spot
/// "enriched but without a real Google link" rows (IsEnriched &amp;&amp;
/// !HasPlaceUrl) straight from a list, without fetching each POI's detail.
/// </remarks>
public record PoiSummaryDto(
    int Id,
    string Name,
    double? Latitude,
    double? Longitude,
    string? Category,
    string? Address,
    bool IsEnriched,
    bool HasPlaceUrl,
    bool EnrichmentNeedsManualUrl)
{
    public static PoiSummaryDto From(Poi p) => new(
        p.Id, p.Name, p.Latitude, p.Longitude, p.Category, p.Address, p.IsEnriched,
        p.GoogleMapsUrl is not null && p.GoogleMapsUrl.Contains("/maps/place/"),
        p.EnrichmentNeedsManualUrl);
}

/// <summary>Full POI shape; <see cref="HasStoredImage"/> indicates a photo is stored in the DB—fetch via get_poi_image tool or <see cref="ImageEndpoint"/> HTTP endpoint.</summary>
public record PoiDetailDto(
    int Id,
    string Name,
    double? Latitude,
    double? Longitude,
    string? GoogleMapsUrl,
    string? Address,
    string? Category,
    string? Notes,
    int? Rating,
    double? GoogleRating,
    int? ReviewCount,
    string? Website,
    string? Phone,
    string? Country,
    string? Region,
    string? ImageUrl,
    bool HasStoredImage,
    string? ImageEndpoint,
    DateTime AddedDate,
    bool IsEnriched,
    bool EnrichmentRequested,
    bool EnrichmentNeedsManualUrl,
    int EnrichmentFailureCount,
    IReadOnlyList<string> Collections)
{
    public static PoiDetailDto From(Poi p, IReadOnlyList<string> collections, bool hasStoredImage) => new(
        p.Id,
        p.Name,
        p.Latitude,
        p.Longitude,
        p.GoogleMapsUrl,
        p.Address,
        p.Category,
        p.Notes,
        p.Rating,
        p.GoogleRating,
        p.ReviewCount,
        p.Website,
        p.Phone,
        p.Country,
        p.Region,
        p.ImageUrl,
        hasStoredImage,
        hasStoredImage ? $"/api/poi-image/{p.Id}" : null,
        p.AddedDate,
        p.IsEnriched,
        p.EnrichmentRequested,
        p.EnrichmentNeedsManualUrl,
        p.EnrichmentFailureCount,
        collections);
}

/// <summary>Background enrichment queue status snapshot.</summary>
public record EnrichmentStatusDto(int Total, int Remaining, int Fetched);

/// <summary>Trip as seen by MCP agent: ordered Stops plus cached Legs. Travel mode is per-leg (see <see cref="TripLegDto.TravelMode"/>). Units: durations in seconds, distances in meters, dwell in minutes, OrderIndex 1-based.</summary>
public record TripDto(
    int CollectionId,
    IReadOnlyList<TripStopDto> Stops,
    IReadOnlyList<TripLegDto> Legs);

/// <summary>One ordered Stop: 1-based OrderIndex, pin flags, optional dwell.</summary>
public record TripStopDto(
    int PoiId,
    string Name,
    int OrderIndex,
    bool IsStart,
    bool IsFinish,
    int? DwellMinutes);

/// <summary>Cached directional Leg (From → To) under per-leg travel mode. <see cref="TravelMode"/> is the From-stop's OutgoingTravelMode (null normalized to AnyAir). Duration/distance/fidelity come from the cached RouteSegment for that (From, To, Mode) combination; null if no cache row exists yet (e.g., Any/Air legs have no ground cache row).</summary>
public record TripLegDto(
    int FromPoiId,
    int ToPoiId,
    string TravelMode,
    int? DurationSeconds,
    double? DistanceMeters,
    string? Fidelity);
