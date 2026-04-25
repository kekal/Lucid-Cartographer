namespace LucidCartographer.Data.Entities;

/// <summary>
/// Companion table for <see cref="Poi"/> that holds the downloaded image
/// bytes. Kept on its own table (rather than a column on Pois) so that
/// routine Poi queries don't have to stream BLOBs across the wire —
/// detail-pane rendering pulls it explicitly via /api/poi-image/{id}.
/// PoiId is both the primary key and the foreign key, so there is at
/// most one image per POI.
/// </summary>
public class PoiImage
{
    public int PoiId { get; set; }

    public required byte[] Data { get; set; }

    public string? ContentType { get; set; }

    public Poi? Poi { get; set; }
}