using FluentAssertions;
using LucidCartographer.Components.Shared.Trip;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests.ViewModels;

/// <summary>
/// Story 2.6 (TRIP-TIMELINE-01, AC 2/5/6): the ViewModel persists the per-trip
/// TripStartTime + soft TimeBudgetMinutes on <see cref="PoiCollection"/> and round-trips
/// them; range-guards the budget; does NOT signal the travel-time trigger (these don't
/// affect route segments); and exposes a <see cref="TripViewModel.Timeline"/> computed
/// from the seeded stops/dwell/legs.
/// </summary>
public class TripViewModelTimelineTests
{
    private const int CollectionId = 1;

    private static IDbContextFactory<AppDbContext> Seed(int placeable)
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection
        {
            Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = TravelMode.AnyAir,
        });
        for (var i = 1; i <= placeable; i++)
        {
            db.Pois.Add(new Poi { Id = i, Name = $"P{i}", Latitude = 50 + i, Longitude = 20 + i, AddedDate = new DateTime(2025, 1, i) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = i, PoiCollectionId = CollectionId });
        }
        db.SaveChanges();
        return factory;
    }

    private static async Task<(TripViewModel Vm, TravelTimeTrigger Trigger)> EnabledVmAsync(
        IDbContextFactory<AppDbContext> factory, int placeable)
    {
        var writeLock = new SqliteWriteLock();
        var ordering = new TripOrderingService(factory, writeLock, NullLogger<TripOrderingService>.Instance);
        var trigger = new TravelTimeTrigger();
        var vm = new TripViewModel(
            ordering, factory, writeLock,
            trigger, new TravelTimeProgressService(),
            TestDbHelper.CreateInvalidationService(factory),
            NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, placeable);
        await vm.ToggleAsync();
        return (vm, trigger);
    }

    private static async Task AddSegmentAsync(IDbContextFactory<AppDbContext> factory, int from, int to, int seconds)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.RouteSegments.Add(new RouteSegment
        {
            FromPoiId = from, ToPoiId = to, TravelMode = TravelMode.AnyAir,
            DurationSeconds = seconds, DistanceMeters = 8000,
            Fidelity = Fidelity.Manual, Source = "Manual", ComputedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<(DateTime? Start, int? Budget)> ReadSettingsAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.PoiCollections
            .Where(c => c.Id == CollectionId)
            .Select(c => new { c.TripStartTime, c.TimeBudgetMinutes })
            .FirstAsync();
        return (row.TripStartTime, row.TimeBudgetMinutes);
    }

    [Fact]
    public async Task SetTripStartTime_Persists_AndRoundTripsIntoTheVm()
    {
        var factory = Seed(placeable: 2);
        var (vm, _) = await EnabledVmAsync(factory, 2);
        await using var _v = vm;

        var start = new DateTime(2026, 6, 14, 9, 30, 0);
        await vm.SetTripStartTimeAsync(start);

        (await ReadSettingsAsync(factory)).Start.Should().Be(start, "the start time persists on the collection");
        vm.TripStartTime.Should().Be(start, "and round-trips into the VM after refresh");
    }

    [Fact]
    public async Task SetTripStartTime_Null_Clears()
    {
        var factory = Seed(placeable: 2);
        var (vm, _) = await EnabledVmAsync(factory, 2);
        await using var _v = vm;

        await vm.SetTripStartTimeAsync(new DateTime(2026, 6, 14, 9, 30, 0));
        await vm.SetTripStartTimeAsync(null);

        (await ReadSettingsAsync(factory)).Start.Should().BeNull("null clears the start time");
        vm.TripStartTime.Should().BeNull();
    }

    [Fact]
    public async Task SetTimeBudget_Persists_AndRoundTripsIntoTheVm()
    {
        var factory = Seed(placeable: 2);
        var (vm, _) = await EnabledVmAsync(factory, 2);
        await using var _v = vm;

        await vm.SetTimeBudgetMinutesAsync(480);

        (await ReadSettingsAsync(factory)).Budget.Should().Be(480);
        vm.TimeBudgetMinutes.Should().Be(480);
    }

    [Fact]
    public async Task SetTimeBudget_Null_Clears()
    {
        var factory = Seed(placeable: 2);
        var (vm, _) = await EnabledVmAsync(factory, 2);
        await using var _v = vm;

        await vm.SetTimeBudgetMinutesAsync(480);
        await vm.SetTimeBudgetMinutesAsync(null);

        (await ReadSettingsAsync(factory)).Budget.Should().BeNull();
        vm.TimeBudgetMinutes.Should().BeNull();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(TripViewModel.MaxBudgetMinutes + 1)]
    public async Task SetTimeBudget_OutOfRange_IsRejected_NoWrite(int minutes)
    {
        var factory = Seed(placeable: 2);
        var (vm, _) = await EnabledVmAsync(factory, 2);
        await using var _v = vm;

        await vm.SetTimeBudgetMinutesAsync(minutes);

        (await ReadSettingsAsync(factory)).Budget.Should().BeNull("out-of-range budget is rejected; no write");
    }

    [Fact]
    public async Task SetTimeBudget_AtMax_IsAccepted()
    {
        var factory = Seed(placeable: 2);
        var (vm, _) = await EnabledVmAsync(factory, 2);
        await using var _v = vm;

        await vm.SetTimeBudgetMinutesAsync(TripViewModel.MaxBudgetMinutes);

        (await ReadSettingsAsync(factory)).Budget.Should().Be(TripViewModel.MaxBudgetMinutes);
    }

    [Fact]
    public async Task SetTripStartTime_AndBudget_DoNotSignalTheTrigger()
    {
        // Both legs fully computed (Manual) so RefreshProjectionsAsync has no "computing"
        // leg to signal on — isolating that start/budget writes never signal the trigger.
        var factory = Seed(placeable: 2);
        await AddSegmentAsync(factory, 1, 2, 3600);
        await AddSegmentAsync(factory, 2, 1, 3600);
        var (vm, trigger) = await EnabledVmAsync(factory, 2);
        await using var _v = vm;
        // Drain any enable-time signal so the post-write assertion is clean.
        await trigger.WaitAsync(TimeSpan.Zero, CancellationToken.None);

        await vm.SetTripStartTimeAsync(new DateTime(2026, 6, 14, 9, 0, 0));
        (await trigger.WaitAsync(TimeSpan.Zero, CancellationToken.None))
            .Should().BeFalse("setting the start time must NOT signal the travel-time recompute");

        await vm.SetTimeBudgetMinutesAsync(120);
        (await trigger.WaitAsync(TimeSpan.Zero, CancellationToken.None))
            .Should().BeFalse("setting the budget must NOT signal the travel-time recompute");
    }

    [Fact]
    public async Task Timeline_ReflectsSeededStopsDwellLegs_AndBudgetOverrun()
    {
        // Roundtrip, both legs Manual 1h each (7200s = 120m). Budget 119 ⇒ overrun.
        var factory = Seed(placeable: 2);
        await AddSegmentAsync(factory, 1, 2, 3600);
        await AddSegmentAsync(factory, 2, 1, 3600);
        var (vm, _) = await EnabledVmAsync(factory, 2);
        await using var _v = vm;

        await vm.SetTimeBudgetMinutesAsync(119);
        await vm.SetTripStartTimeAsync(new DateTime(2026, 6, 14, 9, 0, 0));

        vm.Timeline.Stops.Should().HaveCount(2, "the VM Timeline reflects the seeded placeable stops");
        vm.Timeline.Stops[0].OffsetSeconds.Should().Be(0);
        vm.Timeline.FinishOrReturn!.PoiId.Should().Be(1, "roundtrip returns to Start");
        vm.Timeline.TotalSeconds.Should().Be(7200);
        vm.Timeline.IsOverBudget.Should().BeTrue("120m total exceeds the 119m budget");
        // Manual legs are confident ⇒ clean (no qualifier).
        vm.Timeline.TotalQualifyingFidelity.Should().BeNull();
        // Wall-clock present because a start time is set.
        vm.Timeline.Stops[0].ArrivalWallClock.Should().Be(new DateTime(2026, 6, 14, 9, 0, 0));
    }

    [Fact]
    public async Task Timeline_UnknownTotal_NeverFalseOverrun_InTheVm()
    {
        // No segments seeded ⇒ legs uncomputed ⇒ unknown total. Even a tiny budget set,
        // the VM Timeline must not assert an overrun.
        var factory = Seed(placeable: 2);
        var (vm, _) = await EnabledVmAsync(factory, 2);
        await using var _v = vm;

        await vm.SetTimeBudgetMinutesAsync(1);

        vm.Timeline.IsTotalUnknown.Should().BeTrue();
        vm.Timeline.IsOverBudget.Should().BeFalse("an uncertain total never trips a false overrun");
    }
}
