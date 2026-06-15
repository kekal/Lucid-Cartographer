using Bunit;
using BunitTestContext = Bunit.TestContext;
using FluentAssertions;
using LucidCartographer.Components.Shared;
using LucidCartographer.Components.Shared.Trip;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using LucidCartographer.Tests;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests.Components;

/// <summary>
/// Story 1.1 (desktop list-region takeover): toggling Trip View ON must render the
/// <see cref="TripStopList"/> in the wide filtered-results region INSTEAD of the
/// <see cref="PoiTable"/>, and OFF must render the <see cref="PoiTable"/> with no
/// separate Trip side column.
///
/// MapPage.razor itself is impractical to render in bUnit: it is an
/// <c>@rendermode InteractiveServer</c> page whose OnAfterRender wire path awaits
/// <c>LeafletMap.WaitForInitAsync()</c> (a TCS that only completes from JS interop
/// that never runs under bUnit), so a full render would hang. The existing suite
/// deliberately covers MapPage's JS-bound paths via the Playwright integration
/// tests (see <c>Integration/TripViewIntegrationTests</c>, which assert the same
/// desktop takeover end-to-end after this change).
///
/// This bUnit test therefore exercises the *exact* takeover conditional from the
/// MapPage wide region — <c>@if (TripVm.IsTripViewEnabled) { TripStopList } else
/// { PoiTable }</c> — built as a render fragment over the real components and the
/// real <see cref="TripViewModel"/>, asserting the mutually-exclusive selection
/// (one surface present, the other absent) in both states.
/// </summary>
public class TripDesktopTakeoverTests : BunitTestContext
{
    private const int CollectionId = 1;

    public TripDesktopTakeoverTests()
    {
        // PoiTable injects IJSRuntime (clipboard / scroll helpers); loose mode
        // no-ops those interop calls so the OFF-state render does not throw.
        JSInterop.Mode = Bunit.JSRuntimeMode.Loose;
    }

    private static IDbContextFactory<AppDbContext> SeedFactory(int placeable)
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection { Id = CollectionId, Name = "Trip", Color = "#005bbf" });
        for (var i = 1; i <= placeable; i++)
        {
            db.Pois.Add(new Poi { Id = i, Name = $"P{i}", Latitude = 50 + i, Longitude = 20 + i, AddedDate = new DateTime(2025, 1, i) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = i, PoiCollectionId = CollectionId });
        }
        db.SaveChanges();
        return factory;
    }

    private static TripViewModel CreateVm(IDbContextFactory<AppDbContext> factory)
    {
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        return new TripViewModel(ordering, factory, writeLock, new TravelTimeTrigger(), new TravelTimeProgressService(), TestDbHelper.CreateInvalidationService(factory), NullLogger<TripViewModel>.Instance);
    }

    /// <summary>
    /// The wide filtered-results region from MapPage.razor (Story 1.1): Trip View ON
    /// renders the TripStopList, OFF renders the PoiTable — never both. Mirrors the
    /// production markup exactly so the branch selection is what is under test.
    /// </summary>
    private static RenderFragment WideRegion(TripViewModel tripVm, IReadOnlyList<Poi> pois) => builder =>
    {
        if (tripVm.IsTripViewEnabled)
        {
            builder.OpenComponent<TripStopList>(0);
            builder.AddAttribute(1, nameof(TripStopList.Vm), tripVm);
            builder.CloseComponent();
        }
        else
        {
            builder.OpenComponent<PoiTable>(0);
            builder.AddAttribute(1, nameof(PoiTable.Pois), pois);
            builder.CloseComponent();
        }
    };

    [Fact]
    public async Task TripViewOn_RendersTripStopList_NotPoiTable_InWideRegion()
    {
        var factory = SeedFactory(placeable: 3);
        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 3);
        await vm.ToggleAsync(); // Trip View ON
        vm.IsTripViewEnabled.Should().BeTrue();

        var pois = await PoisAsync(factory);
        var cut = Render(WideRegion(vm, pois));

        // The TripStopList takes over the wide region.
        cut.FindAll($"section[aria-label=\"{UiStrings.TripStopListAria}\"]").Should().ContainSingle();
        cut.FindAll("li[data-poi-id]").Should().HaveCount(3, "the stop rows render in the takeover region");

        // The plain PoiTable is NOT rendered at the same time.
        cut.Markup.Should().NotContain(UiStrings.FilteredResults, "the PoiTable header must be gone when Trip View is on");
        cut.FindAll("table").Should().BeEmpty();
    }

    [Fact]
    public async Task TripViewOff_RendersPoiTable_NoTripSideColumn()
    {
        var factory = SeedFactory(placeable: 3);
        await using var vm = CreateVm(factory);
        await vm.LoadAsync(CollectionId, 3); // loaded but NOT toggled ⇒ Trip View OFF
        vm.IsTripViewEnabled.Should().BeFalse();

        var pois = await PoisAsync(factory);
        var cut = Render(WideRegion(vm, pois));

        // The plain PoiTable owns the region.
        cut.Markup.Should().Contain(UiStrings.FilteredResults);
        cut.FindAll("table").Should().ContainSingle();

        // No Trip stop-list (the additive w-64 side column is gone — there is no
        // separate Trip surface when Trip View is off).
        cut.FindAll($"section[aria-label=\"{UiStrings.TripStopListAria}\"]").Should().BeEmpty();
        cut.FindAll("li[data-poi-id]").Should().BeEmpty();
    }

    private static async Task<IReadOnlyList<Poi>> PoisAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Pois.OrderBy(p => p.Id).ToListAsync();
    }
}
