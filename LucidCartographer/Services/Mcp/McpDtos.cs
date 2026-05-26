using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Mcp;

/// <summary>
/// MCP-facing DTOs. These are returned by the MCP tools instead of EF entities
/// so we control the JSON shape and avoid serializing navigation properties
/// (which would cause reference cycles).
/// </summary>
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
public record PoiSummaryDto(
    int Id,
    string Name,
    double? Latitude,
    double? Longitude,
    string? Category,
    string? Status,
    string? Address,
    bool IsEnriched)
{
    public static PoiSummaryDto From(Poi p) => new(
        p.Id, p.Name, p.Latitude, p.Longitude, p.Category, p.Status, p.Address, p.IsEnriched);
}

/// <summary>
/// Full POI shape with every user-facing field. <see cref="HasStoredImage"/>
/// indicates a photo is stored in the DB — fetch the actual bytes with the
/// <c>get_poi_image</c> tool (or via <see cref="ImageEndpoint"/> over HTTP).
/// </summary>
public record PoiDetailDto(
    int Id,
    string Name,
    double? Latitude,
    double? Longitude,
    string? GoogleMapsUrl,
    string? Address,
    string? Category,
    string? Status,
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
    DateTime? VisitedDate,
    bool IsEnriched,
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
        p.Status,
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
        p.VisitedDate,
        p.IsEnriched,
        p.EnrichmentNeedsManualUrl,
        p.EnrichmentFailureCount,
        collections);
}

/// <summary>Snapshot of the background enrichment queue.</summary>
public record EnrichmentStatusDto(int Total, int Remaining, int Fetched);
