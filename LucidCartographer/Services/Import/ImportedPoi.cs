namespace LucidCartographer.Services.Import
{
    public record ImportedPoi(
        string Name,
        double Latitude,
        double Longitude,
        string? GoogleMapsUrl = null,
        string? Address = null,
        string? Category = null,
        string? Description = null,
        double? Rating = null,
        int? ReviewCount = null,
        string? Website = null,
        string? Phone = null,
        string? ImageUrl = null,
        byte[]? ImageData = null,
        string? ImageContentType = null
    );
}
