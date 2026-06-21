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
    const double DefaultToleranceMeters = 100;

    const double DefaultNameSimilarityThreshold = 0.6;

    /// <summary>Matches if name similarity exceeds threshold and distance is below tolerance; placeholder coordinates always return false.</summary>
    bool IsMatch(Poi a, Poi b, double toleranceMeters = DefaultToleranceMeters, double nameSimilarityThreshold = DefaultNameSimilarityThreshold);

    /// <summary>Returns the closest matching candidate by Haversine distance; ties break to nearest row.</summary>
    Poi? FindMatch(Poi poi, IEnumerable<Poi> candidates, double toleranceMeters = DefaultToleranceMeters, double nameSimilarityThreshold = DefaultNameSimilarityThreshold);

    /// <summary>Groups transitive duplicates via union-find; returns groups of 2+ POIs (e.g. if A~B and B~C, all three group together).</summary>
    List<List<Poi>> FindDuplicateGroups(List<Poi> pois, double toleranceMeters = DefaultToleranceMeters, double nameSimilarityThreshold = DefaultNameSimilarityThreshold, CancellationToken cancellationToken = default);
}