using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Operations;

/// <summary>
/// Defines matching operations for Points of Interest. All matching
/// delegates to <see cref="PoiIdentity.AreSamePlace(Poi?, Poi?, double, double)"/>
/// — the single source of truth for "same real place" across import,
/// enrichment and set operations. Name similarity + geographic
/// proximity, no URL tier. Implementations are stateless and safe for
/// concurrent use.
/// </summary>
public interface IPoiMatcher
{
    /// <summary>Default spatial tolerance in meters for proximity matching.</summary>
    const double DefaultToleranceMeters = 100;

    /// <summary>Default threshold for name similarity (0.0 - 1.0). Names with similarity below this are not considered matches.</summary>
    const double DefaultNameSimilarityThreshold = 0.6;

    /// <summary>
    /// Determines if two POIs represent the same real place — name
    /// similarity above the threshold AND geographic distance below
    /// the tolerance. Placeholder (0,0) coordinates always return
    /// false (wait for enrichment before deciding identity).
    /// </summary>
    bool IsMatch(Poi a, Poi b, double toleranceMeters = DefaultToleranceMeters, double nameSimilarityThreshold = DefaultNameSimilarityThreshold);

    /// <summary>
    /// Finds the best match for a POI in a collection of candidates.
    /// Scans all candidates and returns the closest passing match by
    /// Haversine distance (so ties break toward the nearest row).
    /// </summary>
    Poi? FindMatch(Poi poi, IEnumerable<Poi> candidates, double toleranceMeters = DefaultToleranceMeters, double nameSimilarityThreshold = DefaultNameSimilarityThreshold);

    /// <summary>
    /// Finds all duplicate groups within a single list using union-find
    /// for transitive grouping — if A~B and B~C but not A~C, all three
    /// still end up in the same group. Returns groups of 2+ POIs.
    /// </summary>
    List<List<Poi>> FindDuplicateGroups(List<Poi> pois, double toleranceMeters = DefaultToleranceMeters, double nameSimilarityThreshold = DefaultNameSimilarityThreshold, CancellationToken cancellationToken = default);
}