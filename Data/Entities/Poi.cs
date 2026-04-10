namespace LucidCartographer.Data.Entities;

public class Poi
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? GoogleMapsUrl { get; set; }
    public string? Address { get; set; }
    public string? Category { get; set; }
    public string? Status { get; set; } // visited, want_to_go, imported
    public string? Tags { get; set; } // comma-separated
    public string? Notes { get; set; }
    public int? Rating { get; set; } // 1-5
    public string? Country { get; set; }
    public string? Region { get; set; }
    public DateTime AddedDate { get; set; } = DateTime.UtcNow;
    public DateTime? VisitedDate { get; set; }

    public List<PoiCollectionItem> CollectionItems { get; set; } = new();
}
