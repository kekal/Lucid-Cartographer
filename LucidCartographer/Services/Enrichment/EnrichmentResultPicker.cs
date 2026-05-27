using LucidCartographer.Services.Operations;

namespace LucidCartographer.Services.Enrichment;

/// <summary>
/// Pure decision logic for the "smart" name-search fallback. When a Google
/// Maps name search does not redirect straight to a single canonical place
/// but lands on a results list (e.g. searching "Park Dzikich Zwierząt
/// Kadzidłowo" returns both "Park Dzikich Zwierząt" and "Kasa Parku Dzikich
/// Zwierząt"), this picks the one result whose name unambiguously matches the
/// POI we were enriching — but only when exactly one candidate clears the bar.
///
/// Conservative on purpose: zero matches OR two-or-more matches both return
/// null, which routes the caller to the manual-URL fallback dialog. Auto-
/// selecting the wrong place is worse than asking the user, so the threshold
/// is deliberately higher than the dedup default (0.6).
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
                    // A second qualifying card makes the choice ambiguous —
                    // bail out so the caller shows the manual-URL dialog.
                    return null;
                }

                matchIndex = i;
            }
        }

        return matchIndex;
    }
}
