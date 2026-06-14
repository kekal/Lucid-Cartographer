using System.Globalization;
using Bunit;
using BunitTestContext = Bunit.TestContext;
using FluentAssertions;
using LucidCartographer.Components.Shared.Trip;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using LucidCartographer.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests.Components;

/// <summary>
/// Story 2.1 (AC 4, 5, 8): bUnit coverage that TripStopList (desktop) and
/// MobileTripPanel (mobile) render per-leg travel time + distance + the
/// Estimated Fidelity badge (on-surface-variant tone), the "—" unknown marker
/// when a leg is uncomputed, and never render a Placeholder badge.
/// </summary>
public class TripTravelTimeRenderTests : BunitTestContext
{
    private const int CollectionId = 1;

    private static IDbContextFactory<AppDbContext> Seed()
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection
        {
            Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = TravelMode.AnyAir,
        });
        for (var i = 1; i <= 2; i++)
        {
            db.Pois.Add(new Poi { Id = i, Name = $"P{i}", Latitude = 50 + i, Longitude = 20 + i, AddedDate = new DateTime(2025, 1, i) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = i, PoiCollectionId = CollectionId });
        }
        db.SaveChanges();
        return factory;
    }

    private static async Task AddSegmentAsync(
        IDbContextFactory<AppDbContext> factory, int from, int to, int seconds, double meters, string fidelity,
        string source = "Mock")
    {
        await using var db = await factory.CreateDbContextAsync();
        db.RouteSegments.Add(new RouteSegment
        {
            FromPoiId = from, ToPoiId = to, TravelMode = TravelMode.AnyAir,
            DurationSeconds = seconds, DistanceMeters = meters,
            Fidelity = fidelity, Source = source, ComputedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<TripViewModel> EnabledVmAsync(IDbContextFactory<AppDbContext> factory)
    {
        var writeLock = new SqliteWriteLock();
        var ordering = new TripOrderingService(factory, writeLock, NullLogger<TripOrderingService>.Instance);
        var vm = new TripViewModel(
            ordering, factory, writeLock,
            new TravelTimeTrigger(), new TravelTimeProgressService(),
            TestDbHelper.CreateInvalidationService(factory),
            NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, 2);
        await vm.ToggleAsync();
        return vm;
    }

    [Theory]
    [InlineData(Fidelity.Estimated, "Estimated")]
    [InlineData(Fidelity.Measured, "Measured")]
    [InlineData(Fidelity.Manual, "Manual")]
    public void FidelityBadge_Renders_ForVisibleFidelities(string fidelity, string expected)
    {
        var cut = RenderComponent<FidelityBadge>(p => p.Add(x => x.Fidelity, fidelity));
        cut.Markup.Should().Contain(expected);
    }

    [Theory]
    [InlineData(Fidelity.Placeholder)]
    [InlineData(null)]
    public void FidelityBadge_RendersNothing_ForPlaceholderOrNull(string? fidelity)
    {
        var cut = RenderComponent<FidelityBadge>(p => p.Add(x => x.Fidelity, fidelity));
        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public async Task TripStopList_RendersEstimatedBadge_TimeDistance_AndTotal()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 4800, 12000, Fidelity.Estimated); // 1h 20m, 12 km
        await AddSegmentAsync(factory, 2, 1, 4800, 12000, Fidelity.Estimated);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // Estimated badge text + muted (on-surface-variant) tone.
        var badges = cut.FindAll("span").Where(s => s.TextContent.Trim() == UiStrings.TripFidelityEstimated).ToList();
        badges.Should().NotBeEmpty();
        cut.Markup.Should().Contain("text-on-surface-variant");
        // Formatted time + distance present.
        cut.Markup.Should().Contain("1h 20m");
        cut.Markup.Should().Contain("12 km");
        // Trip total label + value rendered once.
        cut.Markup.Should().Contain(UiStrings.TripTotalTravelTimeLabel);
        var totalAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripTotalTravelTimeAria, "2h 40m");
        cut.Find($"[aria-label=\"{totalAria}\"]").TextContent.Trim().Should().Be("2h 40m");
    }

    [Fact]
    public async Task TripStopList_UncomputedLeg_ShowsEmDash_AndComputingAnnouncement()
    {
        var factory = Seed();
        // No segments seeded ⇒ both legs uncomputed.
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Markup.Should().Contain(UiStrings.TripLegTimeUnknown);
        // Total is em-dash (no false precision).
        var totalAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripTotalTravelTimeAria, UiStrings.TripLegTimeUnknown);
        cut.Find($"[aria-label=\"{totalAria}\"]").TextContent.Trim().Should().Be(UiStrings.TripLegTimeUnknown);
        // Computing announcement in an aria-live region.
        cut.FindAll("[aria-live='polite']")
            .Should().Contain(r => r.TextContent.Contains(UiStrings.TripLegComputingAnnouncement, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TripStopList_ComputedPlaceholderLeg_ShowsEmDash_NoBadge_NoRealTime()
    {
        var factory = Seed();
        // TRIP-TRAVELMODE-01 (AC4): a COMPUTED Placeholder row carries a real
        // straight-line air estimate (600s = "10m" each). The UI must show "—" for
        // the time and "—" for the total — never the air estimate — and never a badge.
        await AddSegmentAsync(factory, 1, 2, 600, 8000, Fidelity.Placeholder);
        await AddSegmentAsync(factory, 2, 1, 600, 8000, Fidelity.Placeholder);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // No badge for Placeholder.
        cut.Markup.Should().NotContain(Fidelity.Placeholder, "Placeholder is internal-only — never a badge");
        var provenanceAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripFidelityAria, Fidelity.Placeholder);
        cut.FindAll($"[aria-label=\"{provenanceAria}\"]").Should().BeEmpty();
        // The air estimate must NOT be rendered as a real time.
        cut.Markup.Should().NotContain("10m", "a Placeholder air estimate is never shown as a real leg time");
        cut.Markup.Should().NotContain("20m", "the trip total must not sum Placeholder air estimates");
        // The trip total slot is the honest em-dash.
        var totalAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripTotalTravelTimeAria, UiStrings.TripLegTimeUnknown);
        cut.Find($"[aria-label=\"{totalAria}\"]").TextContent.Trim().Should().Be(UiStrings.TripLegTimeUnknown);
        // ...and a computed Placeholder leg is NOT announced as "computing".
        cut.FindAll("[aria-live='polite']")
            .Should().NotContain(r => r.TextContent.Contains(UiStrings.TripLegComputingAnnouncement, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MobileTripPanel_RendersEstimatedBadge_TimeDistance_AndNeverPlaceholder()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 4800, 12000, Fidelity.Estimated);
        await AddSegmentAsync(factory, 2, 1, 4800, 12000, Fidelity.Estimated);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        cut.Markup.Should().Contain(UiStrings.TripFidelityEstimated);
        cut.Markup.Should().Contain("1h 20m");
        cut.Markup.Should().Contain("12 km");
        cut.Markup.Should().Contain(UiStrings.TripTotalTravelTimeLabel);
        cut.Markup.Should().NotContain(Fidelity.Placeholder);
    }

    [Fact]
    public async Task MobileTripPanel_UncomputedLeg_ShowsEmDash_AndComputingAnnouncement()
    {
        var factory = Seed();
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        cut.Markup.Should().Contain(UiStrings.TripLegTimeUnknown);
        cut.FindAll("[aria-live='polite']")
            .Should().Contain(r => r.TextContent.Contains(UiStrings.TripLegComputingAnnouncement, StringComparison.Ordinal));
    }

    // --- Story 2.3 (TRIP-DEGRADE-01): the honest approximate note, both surfaces ---

    [Fact]
    public async Task TripStopList_DegradedTrip_ShowsApproximateNote_InAriaLiveRegion()
    {
        var factory = Seed();
        // Both legs degraded (fallback). The note must render in a role=status
        // aria-live region; the fallback leg keeps its real Estimated time + badge.
        await AddSegmentAsync(factory, 1, 2, 4800, 12000, Fidelity.Estimated, TravelTimeSource.EstimatedFallback);
        await AddSegmentAsync(factory, 2, 1, 4800, 12000, Fidelity.Estimated, TravelTimeSource.EstimatedFallback);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // The note text appears inside an aria-live status region.
        cut.FindAll("[role='status'][aria-live='polite']")
            .Should().Contain(r => r.TextContent.Contains(UiStrings.TripApproximateEstimatesNote, StringComparison.Ordinal));
        // A fallback Estimated leg still shows the Estimated badge and a real time.
        cut.Markup.Should().Contain(UiStrings.TripFidelityEstimated);
        cut.Markup.Should().Contain("1h 20m");
    }

    [Fact]
    public async Task TripStopList_HealthyTrip_ShowsNoApproximateNote()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 4800, 12000, Fidelity.Estimated, TravelTimeSource.Mock);
        await AddSegmentAsync(factory, 2, 1, 4800, 12000, Fidelity.Estimated, TravelTimeSource.Mock);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Markup.Should().NotContain(UiStrings.TripApproximateEstimatesNote,
            "a normal Mock-Estimated trip is not a degradation");
    }

    [Fact]
    public async Task MobileTripPanel_DegradedTrip_ShowsApproximateNote_InAriaLiveRegion()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 4800, 12000, Fidelity.Estimated, TravelTimeSource.EstimatedFallback);
        await AddSegmentAsync(factory, 2, 1, 4800, 12000, Fidelity.Estimated, TravelTimeSource.EstimatedFallback);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        cut.FindAll("[role='status'][aria-live='polite']")
            .Should().Contain(r => r.TextContent.Contains(UiStrings.TripApproximateEstimatesNote, StringComparison.Ordinal));
        cut.Markup.Should().Contain(UiStrings.TripFidelityEstimated);
        cut.Markup.Should().Contain("1h 20m");
    }

    [Fact]
    public async Task MobileTripPanel_HealthyTrip_ShowsNoApproximateNote()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, 4800, 12000, Fidelity.Estimated, TravelTimeSource.Mock);
        await AddSegmentAsync(factory, 2, 1, 4800, 12000, Fidelity.Estimated, TravelTimeSource.Mock);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        cut.Markup.Should().NotContain(UiStrings.TripApproximateEstimatesNote);
    }
}
