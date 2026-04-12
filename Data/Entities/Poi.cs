using System.ComponentModel.DataAnnotations;

namespace LucidCartographer.Data.Entities
{
    public class Poi
    {
        public int Id { get; set; }

        [MaxLength(500)]
        public string Name { get; set; } = string.Empty;

        [Range(-90.0, 90.0)]
        public double Latitude { get; set; }

        [Range(-180.0, 180.0)]
        public double Longitude { get; set; }

        [MaxLength(2048)]
        public string? GoogleMapsUrl { get; set; }

        [MaxLength(1000)]
        public string? Address { get; set; }

        [MaxLength(100)]
        public string? Category { get; set; }

        [MaxLength(50)]
        public string? Status { get; set; } // Use PoiStatus constants

        [MaxLength(2000)]
        public string? Tags { get; set; } // comma-separated

        [MaxLength(10000)]
        public string? Notes { get; set; }

        [Range(1, 5)]
        public int? Rating { get; set; } // personal 1-5

        [Range(1.0, 5.0)]
        public double? GoogleRating { get; set; } // Google rating e.g. 4.3

        public int? ReviewCount { get; set; }

        [MaxLength(2048)]
        public string? Website { get; set; }

        [MaxLength(50)]
        public string? Phone { get; set; }

        [MaxLength(2048)]
        public string? ImageUrl { get; set; }

        [MaxLength(200)]
        public string? Country { get; set; }

        [MaxLength(200)]
        public string? Region { get; set; }

        public DateTime AddedDate { get; set; }
        public DateTime? VisitedDate { get; set; }

        [ConcurrencyCheck]
        public int Version { get; set; }

        public List<PoiCollectionItem> CollectionItems { get; set; } = new();
    }
}
