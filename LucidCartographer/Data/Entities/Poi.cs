using System.ComponentModel.DataAnnotations;
using LucidCartographer.Services;

namespace LucidCartographer.Data.Entities
{
    public class Poi : IEquatable<Poi>
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

        /// <summary>
        /// False means the background enrichment service should fetch
        /// detail-page data (address / website / phone) for this POI.
        /// File imports (KML/GPX/CSV/GeoJSON) set this to true because
        /// the source file already carries whatever metadata exists.
        /// Google-scraped rows start as false — the scraper captures only
        /// what the list card exposes and the background service fills
        /// the rest by opening each place URL in a headless tab. If
        /// enrichment fails, the row is left as-is and will be retried
        /// on the next poll cycle (no retry cap, per design).
        /// </summary>
        public bool IsEnriched { get; set; }

        [ConcurrencyCheck]
        public int Version { get; set; }

        public List<PoiCollectionItem> CollectionItems { get; set; } = new();
        public List<PoiTag> PoiTags { get; set; } = new();

        // ---- IEquatable<Poi> ------------------------------------------------
        //
        // A Poi's identity is "the real place it represents", not its primary
        // key. Two rows are equal when their name is similar enough AND they
        // sit close enough on the globe. URL is deliberately NOT part of the
        // rule — franchise branches can share a corporate URL and must stay
        // distinct. See Services/PoiIdentity.cs for the single source of
        // truth, used by ImportPersister, PoiPostEnrichmentDedup and
        // PoiMatcher so "same place" means exactly one thing everywhere.
        //
        // GetHashCode is deliberately lossy (always returns 0). Fuzzy
        // equality isn't hash-compatible — two rows at 99m distance are
        // equal but hash-bucketing them together would collide all Pois
        // in a HashSet. Do NOT store Poi instances in HashSet<Poi> /
        // Dictionary<Poi,_>; use IEnumerable.Contains / FirstOrDefault
        // (they fall through to Equals) or key by Id when you need a
        // proper hash-based set.

        public bool Equals(Poi? other) => PoiIdentity.AreSamePlace(this, other);

        public override bool Equals(object? obj) => obj is Poi other && Equals(other);

        public override int GetHashCode() => 0;
    }
}
