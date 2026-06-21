namespace LucidCartographer.Data.Entities;

/// <summary>
/// Image bytes for a POI, stored separately to avoid BLOB streaming on routine queries; one image per POI.
/// </summary>
public class PoiImage
{
    public int PoiId { get; set; }

    public required byte[] Data { get; set; }

    public string? ContentType { get; set; }

    public Poi? Poi { get; set; }
}