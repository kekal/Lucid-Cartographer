using FluentAssertions;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Enrichment;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Tests;

public class EnrichmentStateMachineTests
{
    private const int MaxRetries = 5;

    private static Poi Queued() => new()
    {
        Name = "P",
        IsEnriched = false,
        EnrichmentRequested = true,
        EnrichmentFailureCount = 0,
        EnrichmentNeedsManualUrl = false,
    };

    [Fact]
    public void ApplyOutcome_Resolved_MarksEnrichedAndDequeues()
    {
        var poi = Queued();
        EnrichmentStateMachine.ApplyOutcome(poi, EnrichmentOutcome.Resolved, MaxRetries);

        poi.IsEnriched.Should().BeTrue();
        poi.EnrichmentRequested.Should().BeFalse();
        poi.EnrichmentNeedsManualUrl.Should().BeFalse();
        poi.EnrichmentFailureCount.Should().Be(0);
    }

    [Fact]
    public void ApplyOutcome_SoftFailure_MarksEnrichedNeedsManualUrlAndDequeues()
    {
        var poi = Queued();
        EnrichmentStateMachine.ApplyOutcome(poi, EnrichmentOutcome.SoftFailure, MaxRetries);

        poi.IsEnriched.Should().BeTrue();
        poi.EnrichmentNeedsManualUrl.Should().BeTrue();
        poi.EnrichmentRequested.Should().BeFalse();
        poi.EnrichmentFailureCount.Should().Be(0);
    }

    [Fact]
    public void ApplyOutcome_HardFailureBelowCap_KeepsQueuedAndIncrements()
    {
        var poi = Queued();
        EnrichmentStateMachine.ApplyOutcome(poi, EnrichmentOutcome.HardFailure, MaxRetries);

        poi.EnrichmentFailureCount.Should().Be(1);
        poi.EnrichmentRequested.Should().BeTrue("a retryable failure below the cap stays queued");
        poi.IsEnriched.Should().BeFalse();
    }

    [Fact]
    public void ApplyOutcome_HardFailureReachingCap_Dequeues()
    {
        var poi = Queued();
        poi.EnrichmentFailureCount = MaxRetries - 1;

        EnrichmentStateMachine.ApplyOutcome(poi, EnrichmentOutcome.HardFailure, MaxRetries);

        poi.EnrichmentFailureCount.Should().Be(MaxRetries);
        poi.EnrichmentRequested.Should().BeFalse("reaching the retry cap gives up and leaves the queue");
        poi.IsEnriched.Should().BeFalse("a hard failure never marks the row enriched");
    }

    [Fact]
    public async Task QueuePredicate_SelectsOnlyRequestedRowsUnderCap()
    {
        var factory = TestDbHelper.CreateFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Pois.AddRange(
                new Poi { Id = 1, Name = "queued", EnrichmentRequested = true, IsEnriched = false, EnrichmentFailureCount = 0, AddedDate = DateTime.UtcNow },
                // The decoupling invariant: created-but-not-requested is SKIPPED.
                new Poi { Id = 2, Name = "dormant", EnrichmentRequested = false, IsEnriched = false, EnrichmentFailureCount = 0, AddedDate = DateTime.UtcNow },
                new Poi { Id = 3, Name = "exhausted", EnrichmentRequested = true, IsEnriched = false, EnrichmentFailureCount = MaxRetries, AddedDate = DateTime.UtcNow },
                new Poi { Id = 4, Name = "done", EnrichmentRequested = false, IsEnriched = true, EnrichmentFailureCount = 0, AddedDate = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await using var read = await factory.CreateDbContextAsync();
        var matched = await read.Pois
            .Where(EnrichmentStateMachine.QueuePredicate(MaxRetries))
            .Select(p => p.Id)
            .ToListAsync();

        matched.Should().Equal(1);
    }
}
