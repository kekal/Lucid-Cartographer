namespace LucidCartographer.Services.Import;

public record ImportedPoi(
    string Name,
    double Latitude,
    double Longitude,
    string? GoogleMapsUrl = null,
    string? Address = null,
    string? Category = null,
    string? Description = null
);
