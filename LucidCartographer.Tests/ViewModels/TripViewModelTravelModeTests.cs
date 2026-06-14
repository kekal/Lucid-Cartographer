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
/// Story 2.2 (AC 1, 2, 5): the ViewModel persists the per-trip TravelMode + signals
/// the recompute trigger (no-op on the active mode), and upserts/clears a manual
/// Any/Air leg time (Fidelity.Manual, Source "Manual", minutes→seconds) updating the
/// total — clearing reverts the leg to a Placeholder/uncomputed state.
/// </summary>
public class TripViewModelTravelModeTests
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

    private static async Task<(TripViewModel Vm, TravelTimeTrigger Trigger, IDbContextFactory<AppDbContext> Factory)> EnabledVmAsync(int placeable)
    {
        var factory = Seed(placeable);
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
        return (vm, trigger, factory);
    }

    private static async Task<string?> ReadModeAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.PoiCollections.Where(c => c.Id == CollectionId).Select(c => c.TravelMode).FirstAsync();
    }

    [Fact]
    public async Task SetTravelMode_PersistsMode_SignalsTrigger_UpdatesState()
    {
        var (vm, trigger, factory) = await EnabledVmAsync(placeable: 2);
        await using var _ = vm;
        // Drain any pending signal raised during enable (legs uncomputed under
        // AnyAir) so the post-change assertion observes only the mode-change signal.
        // The channel is Bounded(1)+DropWrite, so one drain empties it.
        await trigger.WaitAsync(TimeSpan.Zero, CancellationToken.None);

        await vm.SetTravelModeAsync(TravelMode.Drive);

        vm.TravelMode.Should().Be(TravelMode.Drive);
        (await ReadModeAsync(factory)).Should().Be(TravelMode.Drive, "the choice persists to PoiCollection.TravelMode");
        // The trigger was signalled (WaitAsync completes immediately, true on signal).
        var signalled = await trigger.WaitAsync(TimeSpan.Zero, CancellationToken.None);
        signalled.Should().BeTrue("a mode change signals the background recompute");
    }

    [Fact]
    public async Task SetTravelMode_SameMode_IsNoOp()
    {
        var (vm, _, factory) = await EnabledVmAsync(placeable: 2);
        await using var _v = vm;

        await vm.SetTravelModeAsync(TravelMode.AnyAir); // already active

        vm.TravelMode.Should().Be(TravelMode.AnyAir);
        (await ReadModeAsync(factory)).Should().Be(TravelMode.AnyAir);
    }

    [Fact]
    public async Task SetTravelMode_InvalidMode_IsNoOp()
    {
        var (vm, _, factory) = await EnabledVmAsync(placeable: 2);
        await using var _v = vm;

        await vm.SetTravelModeAsync("Teleport");

        vm.TravelMode.Should().Be(TravelMode.AnyAir);
        (await ReadModeAsync(factory)).Should().Be(TravelMode.AnyAir);
    }

    [Fact]
    public async Task SetTravelMode_SwitchesLegsToNewModeRows()
    {
        var (vm, _, factory) = await EnabledVmAsync(placeable: 2);
        await using var _v = vm;
        // Seed Drive rows for both roundtrip legs only.
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.RouteSegments.Add(new RouteSegment { FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.Drive, DurationSeconds = 300, DistanceMeters = 5000, Fidelity = Fidelity.Estimated, Source = "Mock", ComputedAt = DateTime.UtcNow });
            db.RouteSegments.Add(new RouteSegment { FromPoiId = 2, ToPoiId = 1, TravelMode = TravelMode.Drive, DurationSeconds = 300, DistanceMeters = 5000, Fidelity = Fidelity.Estimated, Source = "Mock", ComputedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await vm.SetTravelModeAsync(TravelMode.Drive);

        vm.OrderedLegs.Should().OnlyContain(l => l.Fidelity == Fidelity.Estimated);
        vm.TotalTravelTimeSeconds.Should().Be(600, "the Drive cache rows now back the legs");
    }

    [Fact]
    public async Task SetManualLegTime_WritesManualRow_UpdatesTotal()
    {
        var (vm, _, factory) = await EnabledVmAsync(placeable: 2);
        await using var _v = vm;
        // Seed the other roundtrip leg (2→1) so the total can resolve once 1→2 is manual.
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.RouteSegments.Add(new RouteSegment { FromPoiId = 2, ToPoiId = 1, TravelMode = TravelMode.AnyAir, DurationSeconds = 600, DistanceMeters = 8000, Fidelity = Fidelity.Manual, Source = "Manual", ComputedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await vm.SetManualLegTimeAsync(1, 2, minutes: 90);

        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = await db.RouteSegments.FirstAsync(r => r.FromPoiId == 1 && r.ToPoiId == 2 && r.TravelMode == TravelMode.AnyAir);
            row.DurationSeconds.Should().Be(90 * 60, "minutes are converted to canonical seconds at the UI edge");
            row.Fidelity.Should().Be(Fidelity.Manual);
            row.Source.Should().Be("Manual");
            row.GeometryPolyline.Should().BeNull();
            row.DistanceMeters.Should().BeGreaterThan(0, "the haversine distance backs the display");
        }

        var leg = vm.OrderedLegs.First(l => l.FromPoiId == 1 && l.ToPoiId == 2);
        leg.Fidelity.Should().Be(Fidelity.Manual);
        leg.DurationSeconds.Should().Be(5400);
        vm.TotalTravelTimeSeconds.Should().Be(5400 + 600, "the manual leg folds into the total");
    }

    [Fact]
    public async Task ClearManualLegTime_RemovesRow_RevertsToUncomputed()
    {
        var (vm, _, factory) = await EnabledVmAsync(placeable: 2);
        await using var _v = vm;
        await vm.SetManualLegTimeAsync(1, 2, minutes: 45);

        await vm.ClearManualLegTimeAsync(1, 2);

        await using (var db = await factory.CreateDbContextAsync())
        {
            (await db.RouteSegments.AnyAsync(r => r.FromPoiId == 1 && r.ToPoiId == 2 && r.TravelMode == TravelMode.AnyAir))
                .Should().BeFalse("clearing deletes the Manual row so the Mock recomputes a Placeholder");
        }

        var leg = vm.OrderedLegs.First(l => l.FromPoiId == 1 && l.ToPoiId == 2);
        leg.Fidelity.Should().BeNull("no row ⇒ uncomputed (em-dash, no badge)");
        leg.DurationSeconds.Should().BeNull();
    }
}
