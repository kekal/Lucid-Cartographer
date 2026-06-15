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
/// Story 2.4 (FR-8/10, RD11): TripStopList renders the quiet OSRM-recommendation note
/// (with the docs/osrm.md link) when <see cref="TripViewModel.RecommendsOsrm"/>, omits it
/// otherwise, keeps it DISTINCT from the engine-unreachable fallback note, and the
/// recompute control carries no fidelity-upgrade implication (FR-9).
/// </summary>
public class TripMockEstimateNoteRenderTests : BunitTestContext
{
    private const int CollectionId = 1;

    private static IDbContextFactory<AppDbContext> Seed()
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection
        {
            Id = CollectionId, Name = "Trip", Color = "#005bbf", TravelMode = TravelMode.Drive,
        });
        for (var i = 1; i <= 2; i++)
        {
            db.Pois.Add(new Poi { Id = i, Name = $"P{i}", Latitude = 50 + i, Longitude = 20 + i, AddedDate = new DateTime(2025, 1, i) });
            // Story 3.2 (TRIP-LEGMODE-01): per-leg Drive mode so the legs resolve the
            // Drive cache rows (the trip-wide selector no longer drives leg lookup).
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = i, PoiCollectionId = CollectionId, OutgoingTravelMode = TravelMode.Drive });
        }
        db.SaveChanges();
        return factory;
    }

    private static async Task AddSegmentAsync(
        IDbContextFactory<AppDbContext> factory, int from, int to, string fidelity, string source)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.RouteSegments.Add(new RouteSegment
        {
            FromPoiId = from, ToPoiId = to, TravelMode = TravelMode.Drive,
            DurationSeconds = 4800, DistanceMeters = 12000,
            Fidelity = fidelity, Source = source, ComputedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<TripViewModel> EnabledVmAsync(IDbContextFactory<AppDbContext> factory)
    {
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        var vm = new TripViewModel(
            ordering, factory, writeLock,
            new TravelTimeTrigger(), new TravelTimeProgressService(),
            TestDbHelper.CreateInvalidationService(factory),
            NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, 2);
        await vm.ToggleAsync();
        return vm;
    }

    [Fact]
    public async Task TripStopList_MockEstimated_ShowsOsrmRecommendationNote_WithDocsLink()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, Fidelity.Estimated, TravelTimeSource.Mock);
        await AddSegmentAsync(factory, 2, 1, Fidelity.Estimated, TravelTimeSource.Mock);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // The note renders the plain-language Mock-estimate explanation. It is a
        // PERSISTENT contextual hint, so it is read in normal document flow — NOT a
        // role=status/aria-live region (which would re-announce on re-render); the
        // transient engine-unreachable fallback note keeps its live region.
        cut.Markup.Should().Contain(UiStrings.TripMockEstimateNote);
        cut.FindAll("[role='status'][aria-live='polite']")
            .Should().NotContain(r => r.TextContent.Contains(UiStrings.TripMockEstimateNote, StringComparison.Ordinal));
        // ...with the docs/osrm.md link (new tab, noopener).
        var link = cut.Find($"a[href='{UiStrings.TripMockEstimateOsrmHref}']");
        link.GetAttribute("target").Should().Be("_blank");
        link.GetAttribute("rel").Should().Contain("noopener");
        link.TextContent.Trim().Should().Be(UiStrings.TripMockEstimateOsrmLink);
        // It is the recommendation, NOT the engine-unreachable fallback note.
        cut.Markup.Should().NotContain(UiStrings.TripApproximateEstimatesNote);
    }

    [Fact]
    public async Task TripStopList_HealthyMockTrip_RecomputeCopyCarriesNoUpgradeImplication()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, Fidelity.Estimated, TravelTimeSource.Mock);
        await AddSegmentAsync(factory, 2, 1, Fidelity.Estimated, TravelTimeSource.Mock);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // FR-9: the recompute control is the neutral "Recompute travel times" — it never
        // promises a fidelity upgrade (no "measured"/"upgrade"/"accurate" copy on it).
        var recompute = cut.Find($"[aria-label='{UiStrings.TripRecomputeAria}']");
        recompute.TextContent.Trim().Should().Be(UiStrings.TripRecomputeLabel);
        recompute.TextContent.Should().NotContainEquivalentOf("measured");
        recompute.TextContent.Should().NotContainEquivalentOf("upgrade");
    }

    [Fact]
    public async Task TripStopList_FallbackOnly_ShowsFallbackNote_NotOsrmRecommendation()
    {
        var factory = Seed();
        // The only estimates are engine-unreachable fallbacks — the fallback note owns
        // this state; the OSRM-recommendation note must stay distinct (absent).
        await AddSegmentAsync(factory, 1, 2, Fidelity.Estimated, TravelTimeSource.EstimatedFallback);
        await AddSegmentAsync(factory, 2, 1, Fidelity.Estimated, TravelTimeSource.EstimatedFallback);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Markup.Should().Contain(UiStrings.TripApproximateEstimatesNote);
        cut.Markup.Should().NotContain(UiStrings.TripMockEstimateNote);
        cut.FindAll($"a[href='{UiStrings.TripMockEstimateOsrmHref}']").Should().BeEmpty();
    }

    [Fact]
    public async Task TripStopList_MeasuredTrip_ShowsNoOsrmRecommendation()
    {
        var factory = Seed();
        await AddSegmentAsync(factory, 1, 2, Fidelity.Measured, TravelTimeSource.Osrm);
        await AddSegmentAsync(factory, 2, 1, Fidelity.Measured, TravelTimeSource.Osrm);
        await using var vm = await EnabledVmAsync(factory);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Markup.Should().NotContain(UiStrings.TripMockEstimateNote);
    }
}
