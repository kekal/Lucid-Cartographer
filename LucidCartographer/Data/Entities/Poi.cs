using System.ComponentModel.DataAnnotations;

namespace LucidCartographer.Data.Entities
{
    public class Poi
    {
        public int Id { get; set; }

        public required string Name { get; set; }

        [Range(-90.0, 90.0)]
        public double Latitude { get; set; }

        [Range(-180.0, 180.0)]
        public double Longitude { get; set; }

        public string? GoogleMapsUrl { get; set; }

        public string? Address { get; set; }

        public string? Category { get; set; }

        public string? Status { get; set; } // Use PoiStatus constants

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
        public DateTime? VisitedDate { get; set; }

        [ConcurrencyCheck]
        public int Version { get; set; }

        public List<PoiCollectionItem> CollectionItems { get; set; } = new();
        public List<PoiTag> PoiTags { get; set; } = new();
    }
}
