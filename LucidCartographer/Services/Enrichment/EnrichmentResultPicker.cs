using LucidCartographer.Services.Operations;

namespace LucidCartographer.Services.Enrichment;

/// <summary>
/// Decision logic to pick the one unambiguous match from a search-results list
/// by name similarity. Returns null if zero or multiple candidates match, routing
/// to manual-URL fallback — auto-selecting the wrong place is worse than asking.
/// </summary>
public static class EnrichmentResultPicker
{
    /// <summary>
    /// Minimum name similarity (0.0 - 1.0) for a search-result card to count
    /// as a match. Higher than <see cref="Operations.IPoiMatcher.DefaultNameSimilarityThreshold"/>
    /// because an automatic pick must be confident — a borderline match should
    /// defer to the human via the manual fallback.
    /// </summary>
    public const double AutoSelectNameThreshold = 0.8;

    /// <summary>
    /// Returns the index of the single search-result card whose name matches
    /// <paramref name="targetName"/>, or null when there is no match or more
    /// than one (ambiguous). Blank candidate names are ignored.
    /// </summary>
    public static int? PickUnambiguousMatch(
        string targetName,
        IReadOnlyList<string> candidateNames,
        double threshold = AutoSelectNameThreshold)
    {
        if (string.IsNullOrWhiteSpace(targetName) || candidateNames is null)
        {
            return null;
        }

        int? matchIndex = null;
        for (var i = 0; i < candidateNames.Count; i++)
        {
            var name = candidateNames[i];
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (PoiMatcher.NameSimilarity(targetName, name) >= threshold)
            {
                if (matchIndex is not null)
                {
                    // Multiple matches = ambiguous; defer to manual fallback.
                    return null;
                }

                matchIndex = i;
            }
        }

        return matchIndex;
    }
}
