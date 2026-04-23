using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Operations;

namespace LucidCartographer.Services
{
    /// <summary>
    /// Single source of truth for "these two POIs represent the same real
    /// world place". Used by <see cref="Poi"/>'s <see cref="IEquatable{Poi}"/>
    /// implementation, by the import-time dedup in <c>ImportPersister</c>,
    /// by the post-enrichment dedup in <c>PoiPostEnrichmentDedup</c>, and by
    /// the operations-time matcher in <see cref="PoiMatcher"/>.
    ///
    /// Rule: same-looking name (Levenshtein-derived similarity ≥ threshold)
    /// AND geographic proximity (Haversine distance &lt; threshold meters),
    /// ONLY when BOTH rows carry real coordinates. Rows with NULL
    /// coordinates (pending enrichment) are never considered identical —
    /// three distinct playgrounds all called "Plac zabaw" must survive
    /// until enrichment lands real coords, at which point their identity
    /// becomes decidable.
    ///
    /// URLs are deliberately NOT part of the identity: different branches
    /// of a franchise (bank, coffee shop, gas station) can share a corporate
    /// URL, and URL-based dedup would collapse them.
    /// </summary>
    public static class PoiIdentity
    {
        /// <summary>
        /// Maximum distance (meters) at which two same-named POIs are still
        /// considered the same physical place. Calibrated for building-
        /// granularity places rather than neighborhood-granularity ones.
        /// </summary>
        public const double ProximityThresholdMeters = 100;

        /// <summary>
        /// Minimum normalized name similarity (0.0 - 1.0). Below this, the
        /// names are treated as distinct regardless of proximity.
        /// </summary>
        public const double NameSimilarityThreshold = IPoiMatcher.DefaultNameSimilarityThreshold;

        /// <summary>
        /// True if the two (name, lat, lon) triples describe the same real
        /// place. Used by <see cref="Poi.Equals(Poi?)"/> and by the import
        /// and enrichment dedup paths. Optional parameters let callers
        /// (e.g. the operations page's tolerance slider) widen or narrow
        /// the default strictness; defaults match <see cref="Poi.Equals"/>.
        /// </summary>
        public static bool AreSamePlace(
            string nameA, double? latA, double? lonA,
            string nameB, double? latB, double? lonB,
            double toleranceMeters = ProximityThresholdMeters,
            double nameSimilarityThreshold = NameSimilarityThreshold)
        {
            // Unlocated rows can't be compared meaningfully — wait for
            // enrichment to fill real coords before deciding identity.
            if (!latA.HasValue || !lonA.HasValue) return false;
            if (!latB.HasValue || !lonB.HasValue) return false;

            if (GeoUtils.HaversineDistance(latA.Value, lonA.Value, latB.Value, lonB.Value) >= toleranceMeters)
                return false;

            return PoiMatcher.NameSimilarity(nameA, nameB) >= nameSimilarityThreshold;
        }

        /// <summary>
        /// Same as <see cref="AreSamePlace(string,double,double,string,double,double,double,double)"/>
        /// but typed against <see cref="Poi"/> directly. Never dereferences
        /// nulls — two nulls are NOT equal (distinct non-existent rows).
        /// </summary>
        public static bool AreSamePlace(
            Poi? a, Poi? b,
            double toleranceMeters = ProximityThresholdMeters,
            double nameSimilarityThreshold = NameSimilarityThreshold)
        {
            if (a is null || b is null) return false;
            return AreSamePlace(a.Name, a.Latitude, a.Longitude,
                                b.Name, b.Latitude, b.Longitude,
                                toleranceMeters, nameSimilarityThreshold);
        }
    }
}
