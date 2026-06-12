using System.Globalization;
using Bunit;
using BunitTestContext = Bunit.TestContext;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
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
/// bUnit coverage for the Story 1.3 stop-list panels (desktop TripStopList +
/// mobile MobileTripPanel). Verifies rows render one-per-placeable-stop in order
/// with the order badge, POI name, and the two inert em-dash placeholders (dwell
/// + timeline) carrying their aria-labels — all via UiStrings.
/// </summary>
public class TripStopListTests : BunitTestContext
{
    private const int CollectionId = 1;

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

    private static async Task<TripViewModel> EnabledVmAsync(int placeable)
    {
        var factory = SeedFactory(placeable);
        var writeLock = new SqliteWriteLock();
        var ordering = new TripOrderingService(factory, writeLock, NullLogger<TripOrderingService>.Instance);
        var vm = new TripViewModel(ordering, factory, writeLock, NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, placeable);
        await vm.ToggleAsync(); // seed + enable so OrderedStops is populated
        return vm;
    }

    [Fact]
    public async Task TripStopList_RendersOneRowPerStop_InOrder_WithBadgeNameAndPlaceholders()
    {
        await using var vm = await EnabledVmAsync(placeable: 3);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        var rows = cut.FindAll("li");
        rows.Should().HaveCount(3);
        rows[0].TextContent.Should().Contain("P1");
        rows[2].TextContent.Should().Contain("P3");

        // Order badge with the "Stop X of Y" aria-label.
        var badgeAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripStopBadgeAria, 1, 3);
        cut.Find($"[aria-label=\"{badgeAria}\"]").TextContent.Trim().Should().Be("1");

        // Both inert placeholders render an em-dash with their aria-labels.
        var dwell = cut.Find($"[aria-label=\"{UiStrings.TripDwellAria}\"]");
        dwell.TextContent.Trim().Should().Be(UiStrings.TripDwellPlaceholder);
        var timeline = cut.Find($"[aria-label=\"{UiStrings.TripTimelineAria}\"]");
        timeline.TextContent.Trim().Should().Be(UiStrings.TripTimelinePlaceholder);

        cut.Markup.Should().Contain(UiStrings.TripStopList);
    }

    [Fact]
    public async Task TripStopList_ShowsEmptyState_WhenNoStops()
    {
        // Trip View off ⇒ OrderedStops empty ⇒ empty-state copy.
        var factory = SeedFactory(placeable: 2);
        var writeLock = new SqliteWriteLock();
        var ordering = new TripOrderingService(factory, writeLock, NullLogger<TripOrderingService>.Instance);
        await using var vm = new TripViewModel(ordering, factory, writeLock, NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, 2);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.FindAll("li").Should().BeEmpty();
        cut.Markup.Should().Contain(UiStrings.TripStopListEmpty);
    }

    [Fact]
    public async Task MobileTripPanel_RendersRows_WithDataPoiId_BadgeAndPlaceholders()
    {
        await using var vm = await EnabledVmAsync(placeable: 2);

        var cut = RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        var rows = cut.FindAll(".row");
        rows.Should().HaveCount(2);
        rows[0].GetAttribute("data-poi-id").Should().Be("1");
        rows[0].TextContent.Should().Contain("P1");

        // StopOrderBadge renders the numeral; mobile timeline + dwell placeholders present.
        cut.Markup.Should().Contain(UiStrings.TripTimelinePlaceholder);
        cut.Find($"[aria-label=\"{UiStrings.TripDwellAria}\"]").TextContent.Trim()
            .Should().Be(UiStrings.TripDwellPlaceholder);
    }

    // === Story 1.4: row selection (list→map) ===

    [Fact]
    public async Task TripStopList_Rows_AreSelectableButtons_WithDataPoiId()
    {
        await using var vm = await EnabledVmAsync(placeable: 2);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        var row = cut.Find("li[data-poi-id='1']");
        row.GetAttribute("role").Should().Be("button");
        row.GetAttribute("tabindex").Should().Be("0");
    }

    [Fact]
    public async Task TripStopList_RowClick_SetsAriaCurrent_SingleSelection()
    {
        await using var vm = await EnabledVmAsync(placeable: 3);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Find("li[data-poi-id='1']").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("li[data-poi-id='1']").GetAttribute("aria-current").Should().Be("true");
            cut.FindAll("li[aria-current='true']").Should().HaveCount(1);
        });

        // Selecting another replaces the prior selection (only one emphasised).
        cut.Find("li[data-poi-id='3']").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("li[data-poi-id='3']").GetAttribute("aria-current").Should().Be("true");
            cut.Find("li[data-poi-id='1']").HasAttribute("aria-current").Should().BeFalse();
            cut.FindAll("li[aria-current='true']").Should().HaveCount(1);
        });
    }

    [Fact]
    public async Task TripStopList_Row_KeyboardEnter_Selects()
    {
        await using var vm = await EnabledVmAsync(placeable: 2);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Find("li[data-poi-id='2']").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        cut.WaitForAssertion(() =>
            cut.Find("li[data-poi-id='2']").GetAttribute("aria-current").Should().Be("true"));
    }

    [Fact]
    public async Task MobileTripPanel_RowClick_SetsAriaCurrent()
    {
        await using var vm = await EnabledVmAsync(placeable: 2);
        var cut = RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        cut.Find(".row[data-poi-id='1']").Click();

        cut.WaitForAssertion(() =>
            cut.Find(".row[data-poi-id='1']").GetAttribute("aria-current").Should().Be("true"));
    }

    // === Story 1.6: "Not placeable" row treatment ([TRIP-PLACE-04/05]) ===

    /// <summary>
    /// Mixed-membership VM: 2 placeable POIs (1, 2) plus an unplaceable POI (99,
    /// no coordinates) that must stay visible with the "Not placeable" treatment.
    /// </summary>
    private static async Task<TripViewModel> MixedVmAsync()
    {
        var factory = SeedFactory(placeable: 2);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Pois.Add(new Poi { Id = 99, Name = "NoCoords", Latitude = null, Longitude = null, AddedDate = new DateTime(2025, 1, 9) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 99, PoiCollectionId = CollectionId });
            await db.SaveChangesAsync();
        }

        var writeLock = new SqliteWriteLock();
        var ordering = new TripOrderingService(factory, writeLock, NullLogger<TripOrderingService>.Instance);
        var vm = new TripViewModel(ordering, factory, writeLock, NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, 2);
        await vm.ToggleAsync();
        return vm;
    }

    [Fact]
    public async Task TripStopList_UnplaceableRow_Present_Labelled_NoBadge_WithAria()
    {
        await using var vm = await MixedVmAsync();

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // The row is present — never dropped (AC1).
        var rows = cut.FindAll("li");
        rows.Should().HaveCount(3, "the unplaceable POI stays in the list");

        var row = cut.Find("li[data-poi-id='99']");
        row.TextContent.Should().Contain("NoCoords");
        // "Not placeable" copy via UiStrings + the honest detail sentence (AC5).
        row.TextContent.Should().Contain(UiStrings.TripStopNotPlaceable);
        row.GetAttribute("title").Should().Be(UiStrings.TripStopNotPlaceableDetail);
        // Screen-reader label describes the not-placeable state.
        row.GetAttribute("aria-label").Should().Be(UiStrings.TripStopNotPlaceableAria);
        // No routed order badge on the unplaceable row (AC4): the badge aria-label
        // pattern ("Stop X of Y") must not appear inside it.
        row.QuerySelectorAll("[aria-label^='Stop']").Should().BeEmpty();
        // Not selectable — no button semantics.
        row.HasAttribute("role").Should().BeFalse();
        row.HasAttribute("tabindex").Should().BeFalse();
    }

    [Fact]
    public async Task TripStopList_PlaceableBadges_ReadContiguous_WithUnplaceablePresent()
    {
        await using var vm = await MixedVmAsync();

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // Placeable badges read 1..2 with no visible gap (AC4).
        var badge1 = string.Format(CultureInfo.CurrentCulture, UiStrings.TripStopBadgeAria, 1, 2);
        var badge2 = string.Format(CultureInfo.CurrentCulture, UiStrings.TripStopBadgeAria, 2, 2);
        cut.Find($"[aria-label=\"{badge1}\"]").TextContent.Trim().Should().Be("1");
        cut.Find($"[aria-label=\"{badge2}\"]").TextContent.Trim().Should().Be("2");
    }

    [Fact]
    public async Task TripStopList_UnplaceableRow_Click_DoesNotSelect()
    {
        await using var vm = await MixedVmAsync();
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // The unplaceable row carries no click handler at all (not selectable),
        // so bUnit reports a missing handler rather than dispatching.
        var click = () => cut.Find("li[data-poi-id='99']").Click();
        click.Should().Throw<Bunit.MissingEventHandlerException>();

        // And the VM-level guard ignores it even if invoked directly.
        vm.SelectStop(99);
        vm.SelectedStopPoiId.Should().BeNull("an unplaceable row is not selectable");
        cut.FindAll("li[aria-current='true']").Should().BeEmpty();
    }

    [Fact]
    public async Task MobileTripPanel_UnplaceableRow_Present_Labelled_NoBadge_WithAria()
    {
        await using var vm = await MixedVmAsync();

        var cut = RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        // Identical treatment on the mobile surface (AC6).
        var rows = cut.FindAll(".row");
        rows.Should().HaveCount(3, "the unplaceable POI stays in the mobile list");

        var row = cut.Find(".row[data-poi-id='99']");
        row.TextContent.Should().Contain("NoCoords");
        row.TextContent.Should().Contain(UiStrings.TripStopNotPlaceable);
        row.GetAttribute("title").Should().Be(UiStrings.TripStopNotPlaceableDetail);
        row.GetAttribute("aria-label").Should().Be(UiStrings.TripStopNotPlaceableAria);
        // No StopOrderBadge inside the unplaceable row, and no button semantics.
        row.QuerySelectorAll("[aria-label^='Stop']").Should().BeEmpty();
        row.HasAttribute("role").Should().BeFalse();

        // Placeable rows still carry their contiguous badges 1..2.
        var badge1 = string.Format(CultureInfo.CurrentCulture, UiStrings.StopOrderBadgeAria, 1);
        cut.Find($"[aria-label=\"{badge1}\"]").Should().NotBeNull();
    }
}
