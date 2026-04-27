using FluentAssertions;
using LucidCartographer.Services.Enrichment;

namespace LucidCartographer.Tests;

/// <summary>
/// EnrichmentProgressService — was at 10.5% coverage.
/// Covers the high-water-mark Total + drain-to-zero reset semantics
/// and the BehaviorSubject Changes stream contract.
/// </summary>
public class EnrichmentProgressServiceTests
{
    [Fact]
    public void Set_FirstNonZero_SetsBothRemainingAndTotal()
    {
        var svc = new EnrichmentProgressService();
        svc.Set(10);

        svc.Remaining.Should().Be(10);
        svc.Total.Should().Be(10);
        svc.Fetched.Should().Be(0);
    }

    [Fact]
    public void Set_DecreasingValues_AdvanceFetched_ButTotalStaysAtHighWaterMark()
    {
        var svc = new EnrichmentProgressService();
        svc.Set(22);
        svc.Set(13);

        svc.Total.Should().Be(22);
        svc.Remaining.Should().Be(13);
        svc.Fetched.Should().Be(9);
    }

    [Fact]
    public void Set_IncreasingValue_GrowsTotal()
    {
        var svc = new EnrichmentProgressService();
        svc.Set(5);
        svc.Set(8);

        svc.Total.Should().Be(8);
        svc.Remaining.Should().Be(8);
    }

    [Fact]
    public void Set_Zero_ResetsTotal_SoNextRunStartsFromScratch()
    {
        var svc = new EnrichmentProgressService();
        svc.Set(15);
        svc.Set(0);

        svc.Total.Should().Be(0);
        svc.Remaining.Should().Be(0);
        svc.Fetched.Should().Be(0);

        svc.Set(7);
        svc.Total.Should().Be(7);
    }

    [Fact]
    public void Changes_ReplaysLatestValue_OnSubscribe()
    {
        var svc = new EnrichmentProgressService();
        svc.Set(42);

        int? observed = null;
        using var sub = svc.Changes.Subscribe(v => observed = v);

        observed.Should().Be(42);
    }

    [Fact]
    public void Changes_DoesNotEmit_OnDuplicateValue()
    {
        var svc = new EnrichmentProgressService();
        var emissions = new List<int>();
        using var sub = svc.Changes.Subscribe(emissions.Add);

        svc.Set(0); // duplicate of initial 0
        svc.Set(5);
        svc.Set(5); // duplicate

        // initial replay (0) + Set(5) = 2 distinct emissions.
        emissions.Should().Equal(0, 5);
    }
}
