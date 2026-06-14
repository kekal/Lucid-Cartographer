using FluentAssertions;
using LucidCartographer.Services.Trip;

namespace LucidCartographer.Tests;

/// <summary>
/// TRIP-TSP-01 (Story 3.1, AR-6/D5): pure unit tests for the nearest-neighbour +
/// 2-opt heuristic. Operating only on a cost matrix lets us assert the algorithm's
/// behaviour (untangling, pin honouring, roundtrip vs open path, never-worse,
/// asymmetric correctness) with no database.
/// </summary>
public class TspSolverTests
{
    // Builds a symmetric matrix from 1-D positions on a line: cost = |xi - xj|.
    private static double[][] LineMatrix(params double[] xs)
    {
        var n = xs.Length;
        var m = new double[n][];
        for (var i = 0; i < n; i++)
        {
            m[i] = new double[n];
            for (var j = 0; j < n; j++)
            {
                m[i][j] = Math.Abs(xs[i] - xs[j]);
            }
        }
        return m;
    }

    [Fact]
    public void Solve_UntanglesAZigZag_IntoMonotonicOrder()
    {
        // Points on a line at positions 0,1,2,3,4 presented in a zig-zag index order.
        // Optimal open path visits them in spatial order. Indices: 0->pos0, 1->pos4,
        // 2->pos1, 3->pos3, 4->pos2.
        var m = LineMatrix(0, 4, 1, 3, 2);

        var tour = TspSolver.Solve(m, 5, startIndex: null, finishIndex: null, roundtrip: false);

        // Cost of the result must equal the optimal monotonic traversal cost.
        // Optimal open-path cost over 0..4 on a line = span = 4.
        TspSolver.TourCost(tour, m, roundtrip: false).Should().BeApproximately(4.0, 1e-9);
        tour.Should().HaveCount(5).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void Solve_PinsStartAtFront_AndFinishAtEnd()
    {
        var m = LineMatrix(0, 4, 1, 3, 2);

        // Pin index 1 (pos 4) as Start and index 0 (pos 0) as Finish.
        var tour = TspSolver.Solve(m, 5, startIndex: 1, finishIndex: 0, roundtrip: false);

        tour[0].Should().Be(1, "the pinned Start stays at the front");
        tour[^1].Should().Be(0, "the pinned Finish stays at the end");
        tour.Should().HaveCount(5).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void Solve_Roundtrip_CostIncludesClosingEdge()
    {
        var m = LineMatrix(0, 1, 2, 3);
        var tour = TspSolver.Solve(m, 4, startIndex: 0, finishIndex: null, roundtrip: true);

        // Roundtrip over a line 0-1-2-3-(back to 0): optimal = 2 * span = 6.
        TspSolver.TourCost(tour, m, roundtrip: true).Should().BeApproximately(6.0, 1e-9);
        tour[0].Should().Be(0);
    }

    [Fact]
    public void Solve_NeverWorse_ThanSeedOrder()
    {
        // A crafted matrix; whatever NN+2-opt returns must not exceed the identity
        // tour cost (the seed). 2-opt accepts only strictly improving swaps.
        var m = LineMatrix(0, 10, 1, 9, 2, 8);
        var identity = Enumerable.Range(0, 6).ToList();
        var seedCost = TspSolver.TourCost(identity, m, roundtrip: false);

        var tour = TspSolver.Solve(m, 6, null, null, roundtrip: false);

        TspSolver.TourCost(tour, m, roundtrip: false).Should().BeLessThanOrEqualTo(seedCost + 1e-9);
    }

    [Fact]
    public void Solve_HandlesAsymmetricMatrix_WithoutIncreasingCost()
    {
        // Directional matrix ([TRIP-CACHE-01]): A->B != B->A. The full-cost 2-opt
        // must never accept a reversal that raises the true total.
        var n = 5;
        var m = new double[n][];
        for (var i = 0; i < n; i++)
        {
            m[i] = new double[n];
            for (var j = 0; j < n; j++)
            {
                // Cheap going "forward" (i<j), dear going "backward".
                m[i][j] = i == j ? 0 : (i < j ? 1.0 : 5.0);
            }
        }

        var identity = Enumerable.Range(0, n).ToList();
        var seedCost = TspSolver.TourCost(identity, m, roundtrip: false);
        var tour = TspSolver.Solve(m, n, null, null, roundtrip: false);

        TspSolver.TourCost(tour, m, roundtrip: false).Should().BeLessThanOrEqualTo(seedCost + 1e-9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Solve_TrivialSizes_ReturnIdentity(int n)
    {
        var m = LineMatrix(Enumerable.Range(0, n).Select(i => (double)i).ToArray());
        var tour = TspSolver.Solve(m, n, null, null, roundtrip: true);
        tour.Should().Equal(Enumerable.Range(0, n));
    }

    [Fact]
    public void Solve_TwoNodes_IsAlreadyOptimal()
    {
        var m = LineMatrix(0, 5);
        var tour = TspSolver.Solve(m, 2, startIndex: 0, finishIndex: 1, roundtrip: false);
        tour.Should().Equal(0, 1);
    }

    [Fact]
    public void Solve_N30_CompletesWellWithinTheInteractiveBudget()
    {
        // AC5 / NFR1 guardrail: N=30 warm matrix must complete interactively (p95 ≤ 3s).
        // Deterministic positions (no RNG — Math.Random is unavailable and would flake);
        // a generous 3s ceiling keeps this a guardrail, not a benchmark.
        const int n = 30;
        var xs = Enumerable.Range(0, n).Select(i => (double)((i * 37) % n)).ToArray(); // scrambled line
        var m = LineMatrix(xs);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tour = TspSolver.Solve(m, n, startIndex: 0, finishIndex: n - 1, roundtrip: false);
        sw.Stop();

        tour.Should().HaveCount(n).And.OnlyHaveUniqueItems();
        sw.ElapsedMilliseconds.Should().BeLessThan(3000, "TSP-Sort for N≤30 must stay interactive (NFR1)");
    }
}
