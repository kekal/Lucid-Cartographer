using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Operations
{
    /// <summary>
    /// Defines matching operations for Points of Interest.
    /// Implementations are stateless and safe for concurrent use.
    /// </summary>
    public interface IPoiMatcher
    {
        /// <summary>
        /// Determines if two POIs represent the same place.
        /// Tier 1: Google Maps URL match (normalized).
        /// Tier 2: Within spatial tolerance + name similarity (only when both POIs lack URLs or exactly one has a URL).
        /// If both POIs have URLs that differ, returns false immediately.
        /// </summary>
        /// <param name="a">First POI.</param>
        /// <param name="b">Second POI.</param>
        /// <param name="toleranceMeters">Maximum distance in meters for proximity matching.</param>
        /// <returns>True if the two POIs are considered a match.</returns>
        bool IsMatch(Poi a, Poi b, double toleranceMeters = PoiMatcher.DefaultToleranceMeters);

        /// <summary>
        /// Finds the best match for a POI in a collection of candidates.
        /// Uses URL pre-indexing for O(1) lookup when possible, falling back to proximity matching.
        /// </summary>
        /// <param name="poi">The POI to match.</param>
        /// <param name="candidates">The candidates to search.</param>
        /// <param name="toleranceMeters">Maximum distance in meters for proximity matching.</param>
        /// <returns>The best matching POI, or null if no match found.</returns>
        Poi? FindMatch(Poi poi, IEnumerable<Poi> candidates, double toleranceMeters = PoiMatcher.DefaultToleranceMeters);

        /// <summary>
        /// Finds the best match for a POI using a pre-built URL index and a candidate list for proximity fallback.
        /// </summary>
        /// <param name="poi">The POI to match.</param>
        /// <param name="urlIndex">Pre-built dictionary mapping normalized URLs to POIs.</param>
        /// <param name="candidates">Candidates for proximity-based matching.</param>
        /// <param name="toleranceMeters">Maximum distance in meters for proximity matching.</param>
        /// <returns>The best matching POI, or null if no match found.</returns>
        Poi? FindMatch(Poi poi, Dictionary<string, Poi> urlIndex, IEnumerable<Poi> candidates, double toleranceMeters = PoiMatcher.DefaultToleranceMeters);

        /// <summary>
        /// Finds all duplicate groups within a single list using union-find for transitive grouping.
        /// Returns groups of 2+ POIs that match each other (directly or transitively).
        /// </summary>
        /// <param name="pois">The list of POIs to check for duplicates.</param>
        /// <param name="toleranceMeters">Maximum distance in meters for proximity matching.</param>
        /// <returns>Groups of duplicate POIs (each group has 2+ members).</returns>
        List<List<Poi>> FindDuplicateGroups(List<Poi> pois, double toleranceMeters = PoiMatcher.DefaultToleranceMeters);

        /// <summary>
        /// Builds a dictionary mapping normalized URLs to POIs for O(1) URL-based lookup.
        /// </summary>
        /// <param name="pois">The POIs to index.</param>
        /// <returns>Dictionary from normalized URL to POI.</returns>
        Dictionary<string, Poi> BuildUrlIndex(IEnumerable<Poi> pois);

        /// <summary>
        /// Normalizes a Google Maps URL for comparison purposes.
        /// Handles www vs non-www, http vs https, trailing slashes, fragments, and CID extraction.
        /// </summary>
        /// <param name="url">The URL to normalize.</param>
        /// <returns>The normalized URL string.</returns>
        string NormalizeUrl(string url);
    }
}
