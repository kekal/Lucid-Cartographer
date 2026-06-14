namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-TSP-01 (Story 3.1, AR-6/D5): the pure, in-process Traveling-Salesman
/// heuristic — nearest-neighbour construction followed by 2-opt local search.
/// No OR-Tools, no I/O: it operates only on a pre-built N×N cost matrix (travel
/// time in SECONDS) and returns a tour as a permutation of the matrix indices,
/// so it is fully unit-testable without a database.
///
/// Pinning (AC3): a pinned Start is fixed at tour position 0 and a pinned Finish
/// at the last position; 2-opt only ever reverses segments inside the movable
/// window, so the endpoints never move. A Roundtrip (<paramref name="roundtrip"/>
/// true) includes the closing edge last→first in the cost; an open path does not.
/// The solver never produces a worse-than-seed tour — 2-opt accepts only strictly
/// improving swaps — but the caller still guards "≤ pre-sort" explicitly (AC4).
/// </summary>
internal static class TspSolver
{
    // A swap must beat the incumbent by more than this (seconds) to be accepted,
    // so floating-point noise in equal-cost reversals can never cause churn or a
    // non-terminating 2-opt loop.
    private const double ImprovementEpsilon = 1e-6;

    // Defensive cap on full 2-opt sweeps. Convergence is normally a handful of
    // sweeps; the cap guarantees termination for a pathological matrix without
    // affecting the N≤30 interactive target.
    private const int MaxSweeps = 64;

    /// <summary>
    /// Solves for a low-cost tour over <paramref name="n"/> nodes.
    /// <paramref name="startIndex"/>/<paramref name="finishIndex"/> are matrix
    /// indices to pin to the first / last tour position, or null when unpinned.
    /// Returns a permutation of <c>0..n-1</c>.
    /// </summary>
    public static IReadOnlyList<int> Solve(
        double[][] matrix, int n, int? startIndex, int? finishIndex, bool roundtrip)
    {
        if (n <= 1)
        {
            return Enumerable.Range(0, Math.Max(0, n)).ToList();
        }

        var tour = NearestNeighbour(matrix, n, startIndex, finishIndex);
        TwoOpt(matrix, tour, startIndex, finishIndex, roundtrip);
        return tour;
    }

    /// <summary>
    /// Total cost of a tour: the sum of consecutive-edge costs, plus the closing
    /// edge back to the first node when <paramref name="roundtrip"/> is set.
    /// </summary>
    public static double TourCost(IReadOnlyList<int> tour, double[][] matrix, bool roundtrip)
    {
        if (tour.Count < 2)
        {
            return 0.0;
        }

        var cost = 0.0;
        for (var k = 0; k < tour.Count - 1; k++)
        {
            cost += matrix[tour[k]][tour[k + 1]];
        }
        if (roundtrip)
        {
            cost += matrix[tour[^1]][tour[0]];
        }
        return cost;
    }

    private static List<int> NearestNeighbour(double[][] matrix, int n, int? startIndex, int? finishIndex)
    {
        var visited = new bool[n];
        var tour = new List<int>(n);

        // Seed from the pinned Start (else node 0). The pinned Finish is held back
        // and appended last so it always occupies the final position.
        var begin = startIndex ?? 0;
        tour.Add(begin);
        visited[begin] = true;
        if (finishIndex is { } fix && fix != begin)
        {
            visited[fix] = true; // reserved for the last slot
        }

        var current = begin;
        // Greedily extend through every interior node.
        for (var placed = tour.Count; placed < (finishIndex is { } f && f != begin ? n - 1 : n); placed++)
        {
            var next = -1;
            var best = double.PositiveInfinity;
            for (var j = 0; j < n; j++)
            {
                if (visited[j])
                {
                    continue;
                }
                if (matrix[current][j] < best)
                {
                    best = matrix[current][j];
                    next = j;
                }
            }
            if (next < 0)
            {
                break;
            }
            tour.Add(next);
            visited[next] = true;
            current = next;
        }

        if (finishIndex is { } fin && fin != begin)
        {
            tour.Add(fin);
        }

        return tour;
    }

    private static void TwoOpt(
        double[][] matrix, List<int> tour, int? startIndex, int? finishIndex, bool roundtrip)
    {
        var n = tour.Count;
        if (n < 4)
        {
            // No interior segment long enough for a meaningful 2-opt reversal.
            return;
        }

        // The movable window excludes a pinned Start (position 0) and a pinned
        // Finish (last position). With no Start pin we still anchor position 0:
        // for a Roundtrip the cycle cost is rotation-invariant, and for an open
        // path the "≤ pre-sort" guard (caller) protects the result either way.
        var lo = 1;
        var hi = finishIndex is not null ? n - 2 : n - 1;
        _ = startIndex; // documented: position 0 is always anchored

        // Full-cost evaluation per trial. The cache key is DIRECTIONAL
        // ([TRIP-CACHE-01]) so the matrix may be asymmetric (A→B ≠ B→A); a
        // reversal flips every internal edge's direction, so the cheap
        // boundary-edge delta used for symmetric 2-opt is INVALID here. Comparing
        // the full <see cref="TourCost"/> before/after keeps the search correct
        // for an asymmetric matrix and guarantees a reversal is accepted only when
        // it genuinely lowers total cost. O(n) per trial → O(n^3) per sweep, well
        // inside the N≤30 interactive target; larger N still terminates.
        var currentCost = TourCost(tour, matrix, roundtrip);
        for (var sweep = 0; sweep < MaxSweeps; sweep++)
        {
            var improved = false;
            for (var i = lo; i <= hi - 1; i++)
            {
                for (var j = i + 1; j <= hi; j++)
                {
                    tour.Reverse(i, j - i + 1);
                    var trialCost = TourCost(tour, matrix, roundtrip);
                    if (trialCost < currentCost - ImprovementEpsilon)
                    {
                        currentCost = trialCost;
                        improved = true;
                    }
                    else
                    {
                        tour.Reverse(i, j - i + 1); // revert
                    }
                }
            }
            if (!improved)
            {
                break;
            }
        }
    }
}
