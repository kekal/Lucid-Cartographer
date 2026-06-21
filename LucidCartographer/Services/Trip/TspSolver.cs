namespace LucidCartographer.Services.Trip;

/// <summary>
/// Pure in-process Traveling-Salesman heuristic: nearest-neighbour construction
/// followed by 2-opt local search. Operates on an N×N cost matrix (travel time in
/// seconds) and returns a tour permutation. A pinned Start is fixed at position 0
/// and a pinned Finish at the last position; 2-opt only reverses segments within
/// the movable window. A roundtrip includes the closing edge; an open path does not.
/// </summary>
internal static class TspSolver
{
    // Prevents churn from floating-point noise in equal-cost reversals.
    private const double ImprovementEpsilon = 1e-6;

    // Cap on full 2-opt sweeps; guarantees termination without affecting N≤30 targets.
    private const int MaxSweeps = 64;

    /// <summary>
    /// Solves for a low-cost tour. <paramref name="startIndex"/> and <paramref name="finishIndex"/>
    /// pin to first/last tour positions, or null when unpinned. Returns a permutation of <c>0..n-1</c>.
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
    /// Sums consecutive-edge costs, plus closing edge when <paramref name="roundtrip"/> is set.
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
            visited[fix] = true;
        }

        var current = begin;
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
            return;
        }

        // The movable window excludes a pinned Start and Finish. Position 0 is
        // always anchored (for Roundtrip, cost is rotation-invariant; for open path,
        // the caller guards "≤ pre-sort").
        var lo = 1;
        var hi = finishIndex is not null ? n - 2 : n - 1;
        _ = startIndex;

        // Matrix may be asymmetric (A→B ≠ B→A), so evaluate full tour cost per trial
        // instead of boundary-edge delta. Guarantees correctness and acceptance only on
        // genuine improvement. O(n³) per sweep but well within N≤30 interactive targets.
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
                        tour.Reverse(i, j - i + 1);
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
