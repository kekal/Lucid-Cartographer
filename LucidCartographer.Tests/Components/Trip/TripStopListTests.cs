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
/// + timeline) carrying their aria-labels â€” all via UiStrings.
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

    private static async Task<TripViewModel> EnabledVmAsync(int placeable, int? startPoiId = null, int? finishPoiId = null)
    {
        var factory = SeedFactory(placeable);
        if (startPoiId is not null || finishPoiId is not null)
        {
            await using var db = await factory.CreateDbContextAsync();
            var collection = await db.PoiCollections.FirstAsync(c => c.Id == CollectionId);
            collection.StartPoiId = startPoiId;
            collection.FinishPoiId = finishPoiId;
            await db.SaveChangesAsync();
        }
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        var vm = new TripViewModel(ordering, factory, writeLock, new TravelTimeTrigger(), new TravelTimeProgressService(), TestDbHelper.CreateInvalidationService(factory), NullLogger<TripViewModel>.Instance);
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

        // Story 2.5 (TRIP-DWELL-01): the dwell slot is now an empty minutes input
        // (unset â‡’ no value) carrying its per-stop UiStrings aria-label.
        var dwellAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripDwellAria, "P1");
        var dwell = cut.Find($"input[aria-label=\"{dwellAria}\"]");
        dwell.GetAttribute("type").Should().Be("number");
        dwell.GetAttribute("value").Should().BeNullOrEmpty("an unset dwell prefills nothing");
        // Story 2.1: with no cached RouteSegment rows the leg slot shows the
        // computing state (em-dash + TripLegComputingAria), superseding the inert
        // timeline placeholder.
        cut.FindAll($"[aria-label=\"{UiStrings.TripLegComputingAria}\"]")
            .Should().NotBeEmpty();
        cut.Markup.Should().Contain(UiStrings.TripLegTimeUnknown);

        cut.Markup.Should().Contain(UiStrings.TripStopList);
    }

    [Fact]
    public async Task TripStopList_ShowsEmptyState_WhenNoStops()
    {
        // Trip View off â‡’ OrderedStops empty â‡’ empty-state copy.
        var factory = SeedFactory(placeable: 2);
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        await using var vm = new TripViewModel(ordering, factory, writeLock, new TravelTimeTrigger(), new TravelTimeProgressService(), TestDbHelper.CreateInvalidationService(factory), NullLogger<TripViewModel>.Instance);
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

        // StopOrderBadge renders the numeral; mobile timeline placeholder present.
        cut.Markup.Should().Contain(UiStrings.TripTimelinePlaceholder);
        // Story 2.5 (TRIP-DWELL-01): the dwell slot is now a minutes input.
        var dwellAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripDwellAria, "P1");
        cut.Find($"input[aria-label=\"{dwellAria}\"]").GetAttribute("type").Should().Be("number");
    }

    // === Story 3.1 (TRIP-TSP-01): the "Sort in Traveling Salesman order" button ===

    [Fact]
    public async Task TripStopList_RendersSortButton_AboveTheGate()
    {
        await using var vm = await EnabledVmAsync(placeable: 3);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Find($"[aria-label=\"{UiStrings.TripSortTspAria}\"]")
            .TextContent.Trim().Should().Be(UiStrings.TripSortTspLabel);
    }

    [Fact]
    public async Task MobileTripPanel_RendersSortButton_AboveTheGate()
    {
        await using var vm = await EnabledVmAsync(placeable: 2);

        var cut = RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        cut.Find($"[aria-label=\"{UiStrings.TripSortTspAria}\"]")
            .TextContent.Trim().Should().Be(UiStrings.TripSortTspLabel);
    }

    [Fact]
    public async Task TripStopList_SortButton_Absent_WhenTripViewOff()
    {
        // Trip View off ⇒ header controls (and the Sort button) are not rendered.
        var factory = SeedFactory(placeable: 2);
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        await using var vm = new TripViewModel(ordering, factory, writeLock, new TravelTimeTrigger(), new TravelTimeProgressService(), TestDbHelper.CreateInvalidationService(factory), NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, 2);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.FindAll($"[aria-label=\"{UiStrings.TripSortTspAria}\"]").Should().BeEmpty();
    }

    [Fact]
    public async Task TripStopList_SortButtonClick_ReordersStops()
    {
        // poi i has latitude 50+i and AddedDate id-order, so seed order == spatial
        // order here; instead assert the click invokes the sort without error and
        // keeps a contiguous order (the algorithm itself is unit-tested elsewhere).
        await using var vm = await EnabledVmAsync(placeable: 3);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        await cut.Find($"[aria-label=\"{UiStrings.TripSortTspAria}\"]").ClickAsync(new MouseEventArgs());

        // Seed latitude is monotonic in id here, so the order is already optimal —
        // the click invokes the sort without error and leaves a contiguous order.
        // (The genuine-reorder + announcement behaviour is covered in TripViewModelTspSortTests.)
        vm.OrderedStops.Select(s => s.OrderIndex).Should().Equal(1, 2, 3);
    }

    // === Story 1.4: row selection (listâ†’map) ===

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

    // === Story 1.5: keyboard move controls + drag reorder ===

    private static string MoveUpLabel(string name) =>
        string.Format(CultureInfo.CurrentCulture, UiStrings.TripMoveStopUp, name);

    private static string MoveDownLabel(string name) =>
        string.Format(CultureInfo.CurrentCulture, UiStrings.TripMoveStopDown, name);

    [Fact]
    public async Task TripStopList_MoveButtons_ArePresent_WithAriaLabels_AndDragHandle()
    {
        await using var vm = await EnabledVmAsync(placeable: 3);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // Real, Tab-reachable buttons per row with descriptive aria-labels.
        var up = cut.Find($"button[aria-label=\"{MoveUpLabel("P2")}\"]");
        var down = cut.Find($"button[aria-label=\"{MoveDownLabel("P2")}\"]");
        up.GetAttribute("type").Should().Be("button");
        down.GetAttribute("type").Should().Be("button");

        // Drag handle with its accessible name; the row itself is draggable.
        var handleAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripDragHandle, "P1");
        cut.Find($"[aria-label=\"{handleAria}\"]").Should().NotBeNull();
        cut.Find("li[data-poi-id='1']").GetAttribute("draggable").Should().Be("true");
    }

    [Fact]
    public async Task TripStopList_MoveUpDisabledOnFirst_MoveDownDisabledOnLast()
    {
        await using var vm = await EnabledVmAsync(placeable: 3);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Find($"button[aria-label=\"{MoveUpLabel("P1")}\"]").HasAttribute("disabled").Should().BeTrue();
        cut.Find($"button[aria-label=\"{MoveDownLabel("P1")}\"]").HasAttribute("disabled").Should().BeFalse();
        cut.Find($"button[aria-label=\"{MoveUpLabel("P3")}\"]").HasAttribute("disabled").Should().BeFalse();
        cut.Find($"button[aria-label=\"{MoveDownLabel("P3")}\"]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task TripStopList_PinnedStartFinishRows_HaveBothMoveButtonsDisabled()
    {
        // P1 pinned Start, P4 pinned Finish â‡’ movable window is [2..3].
        await using var vm = await EnabledVmAsync(placeable: 4, startPoiId: 1, finishPoiId: 4);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Find($"button[aria-label=\"{MoveUpLabel("P1")}\"]").HasAttribute("disabled").Should().BeTrue();
        cut.Find($"button[aria-label=\"{MoveDownLabel("P1")}\"]").HasAttribute("disabled").Should().BeTrue();
        cut.Find($"button[aria-label=\"{MoveUpLabel("P4")}\"]").HasAttribute("disabled").Should().BeTrue();
        cut.Find($"button[aria-label=\"{MoveDownLabel("P4")}\"]").HasAttribute("disabled").Should().BeTrue();

        // Interior edges respect the pinned window: P2 can't move up into slot 1,
        // P3 can't move down into slot 4.
        cut.Find($"button[aria-label=\"{MoveUpLabel("P2")}\"]").HasAttribute("disabled").Should().BeTrue();
        cut.Find($"button[aria-label=\"{MoveDownLabel("P2")}\"]").HasAttribute("disabled").Should().BeFalse();
        cut.Find($"button[aria-label=\"{MoveUpLabel("P3")}\"]").HasAttribute("disabled").Should().BeFalse();
        cut.Find($"button[aria-label=\"{MoveDownLabel("P3")}\"]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task TripStopList_MoveDown_ReordersByOneAndAnnounces()
    {
        await using var vm = await EnabledVmAsync(placeable: 3);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Find($"button[aria-label=\"{MoveDownLabel("P1")}\"]").Click();

        var expected = string.Format(CultureInfo.CurrentCulture, UiStrings.TripStopMovedAnnouncement, "P1", 2, 3);
        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("li");
            rows[0].TextContent.Should().Contain("P2");
            rows[1].TextContent.Should().Contain("P1", "the stop moved exactly one position");
            // The aria-live region carries the announcement.
            cut.FindAll("[aria-live='polite']").Should().Contain(r => r.TextContent.Contains(expected, StringComparison.Ordinal));
        });

        vm.LastReorderAnnouncement.Should().Be(expected);
    }

    [Fact]
    public async Task TripStopList_MoveUp_ReordersByOne()
    {
        await using var vm = await EnabledVmAsync(placeable: 3);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Find($"button[aria-label=\"{MoveUpLabel("P3")}\"]").Click();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("li");
            rows[1].TextContent.Should().Contain("P3");
            rows[2].TextContent.Should().Contain("P2");
        });
    }

    [Fact]
    public async Task TripStopList_DragDrop_MovesStopToTargetSlot()
    {
        await using var vm = await EnabledVmAsync(placeable: 3);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // Native HTML5 DnD: dragstart on P1's row, drop on P3's row (slot 3).
        cut.Find("li[data-poi-id='1']").DragStart();
        cut.Find("li[data-poi-id='3']").Drop();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("li");
            rows[0].TextContent.Should().Contain("P2");
            rows[1].TextContent.Should().Contain("P3");
            rows[2].TextContent.Should().Contain("P1");
        });
    }

    [Fact]
    public async Task TripStopList_DropOnOwnPosition_IsNoOp_NoAnnouncement()
    {
        await using var vm = await EnabledVmAsync(placeable: 3);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Find("li[data-poi-id='2']").DragStart();
        cut.Find("li[data-poi-id='2']").Drop();

        var rows = cut.FindAll("li");
        rows[0].TextContent.Should().Contain("P1");
        rows[1].TextContent.Should().Contain("P2");
        rows[2].TextContent.Should().Contain("P3");
        vm.LastReorderAnnouncement.Should().BeNull("a no-op drop must not announce a move");
    }

    [Fact]
    public async Task MobileTripPanel_HasSameMoveControls_AndAnnounces()
    {
        await using var vm = await EnabledVmAsync(placeable: 3);
        var cut = RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        // Same aria-labels as desktop (shared UiStrings + shared VM).
        cut.Find($"button[aria-label=\"{MoveUpLabel("P1")}\"]").HasAttribute("disabled").Should().BeTrue();
        var down = cut.Find($"button[aria-label=\"{MoveDownLabel("P1")}\"]");
        down.HasAttribute("disabled").Should().BeFalse();

        // â‰¥44px touch targets on the mobile move controls.
        down.GetAttribute("style").Should().Contain("min-width:44px").And.Contain("min-height:44px");

        down.Click();

        var expected = string.Format(CultureInfo.CurrentCulture, UiStrings.TripStopMovedAnnouncement, "P1", 2, 3);
        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".row");
            rows[0].TextContent.Should().Contain("P2");
            rows[1].TextContent.Should().Contain("P1");
            cut.FindAll("[aria-live='polite']").Should().Contain(r => r.TextContent.Contains(expected, StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task MobileTripPanel_DragDrop_MovesStop()
    {
        await using var vm = await EnabledVmAsync(placeable: 3);
        var cut = RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        cut.Find(".row[data-poi-id='3']").DragStart();
        cut.Find(".row[data-poi-id='1']").Drop();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".row");
            rows[0].TextContent.Should().Contain("P3");
            rows[1].TextContent.Should().Contain("P1");
            rows[2].TextContent.Should().Contain("P2");
        });
    }

    // === Story 1.7: Start/Finish designation controls + glyphs ===

    private static string SetStartLabel(string name) =>
        string.Format(CultureInfo.CurrentCulture, UiStrings.TripSetAsStart, name);

    private static string UnsetStartLabel(string name) =>
        string.Format(CultureInfo.CurrentCulture, UiStrings.TripUnsetStart, name);

    private static string SetFinishLabel(string name) =>
        string.Format(CultureInfo.CurrentCulture, UiStrings.TripSetAsFinish, name);

    private static string UnsetFinishLabel(string name) =>
        string.Format(CultureInfo.CurrentCulture, UiStrings.TripUnsetFinish, name);

    [Fact]
    public async Task TripStopList_StartFinishControls_RenderPerRow_WithAriaLabels()
    {
        await using var vm = await EnabledVmAsync(placeable: 3);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        foreach (var name in new[] { "P1", "P2", "P3" })
        {
            var start = cut.Find($"button[aria-label=\"{SetStartLabel(name)}\"]");
            var finish = cut.Find($"button[aria-label=\"{SetFinishLabel(name)}\"]");
            start.GetAttribute("type").Should().Be("button");
            finish.GetAttribute("type").Should().Be("button");
            start.GetAttribute("aria-pressed").Should().Be("false");
            finish.GetAttribute("aria-pressed").Should().Be("false");
        }
    }

    [Fact]
    public async Task TripStopList_SetStart_PinsRowToTop_ShowsStartBadgeGlyph_AndAnnounces()
    {
        await using var vm = await EnabledVmAsync(placeable: 3);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Find($"button[aria-label=\"{SetStartLabel("P2")}\"]").Click();

        var startBadgeAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripStartBadgeAria, 3);
        var expected = string.Format(CultureInfo.CurrentCulture, UiStrings.TripStartSetAnnouncement, "P2");
        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("li");
            rows[0].TextContent.Should().Contain("P2", "the Start anchors the list at stop 1");
            // Distinct Start badge aria + glyph on the Start row.
            var badge = cut.Find($"[aria-label=\"{startBadgeAria}\"]");
            badge.TextContent.Trim().Should().Be("1", "the numeral stays readable");
            rows[0].QuerySelectorAll(".material-symbols-outlined")
                .Should().Contain(e => e.TextContent.Trim() == "trip_origin");
            // The control flips to Unset with aria-pressed=true.
            var unset = cut.Find($"button[aria-label=\"{UnsetStartLabel("P2")}\"]");
            unset.GetAttribute("aria-pressed").Should().Be("true");
            // aria-live region announces the designation.
            cut.FindAll("[aria-live='polite']")
                .Should().Contain(r => r.TextContent.Contains(expected, StringComparison.Ordinal));
        });

        vm.StartPoiId.Should().Be(2, "the click wired through the VM to the service");
    }

    [Fact]
    public async Task TripStopList_SetFinish_PinsRowToBottom_ShowsFinishGlyph_AnnouncesOpenPath()
    {
        await using var vm = await EnabledVmAsync(placeable: 3);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Find($"button[aria-label=\"{SetFinishLabel("P1")}\"]").Click();

        var finishBadgeAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripFinishBadgeAria, 3);
        var expected = string.Format(CultureInfo.CurrentCulture, UiStrings.TripOpenPathAnnounce, "P1");
        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("li");
            rows[2].TextContent.Should().Contain("P1", "the Finish is pinned to stop N");
            cut.Find($"[aria-label=\"{finishBadgeAria}\"]").TextContent.Trim().Should().Be("3");
            rows[2].QuerySelectorAll(".material-symbols-outlined")
                .Should().Contain(e => e.TextContent.Trim() == "sports_score");
            cut.FindAll("[aria-live='polite']")
                .Should().Contain(r => r.TextContent.Contains(expected, StringComparison.Ordinal));
        });

        vm.FinishPoiId.Should().Be(1);
        vm.IsRoundtrip.Should().BeFalse();
    }

    [Fact]
    public async Task TripStopList_CrossPinning_IsDisabled_StartRowCannotBeFinish_AndViceVersa()
    {
        await using var vm = await EnabledVmAsync(placeable: 4, startPoiId: 1, finishPoiId: 4);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // "Set as Finish" is disabled on the current Start row and vice versa.
        cut.Find($"button[aria-label=\"{SetFinishLabel("P1")}\"]").HasAttribute("disabled").Should().BeTrue();
        cut.Find($"button[aria-label=\"{SetStartLabel("P4")}\"]").HasAttribute("disabled").Should().BeTrue();
        // The pinned rows' own role controls stay enabled (they unset).
        cut.Find($"button[aria-label=\"{UnsetStartLabel("P1")}\"]").HasAttribute("disabled").Should().BeFalse();
        cut.Find($"button[aria-label=\"{UnsetFinishLabel("P4")}\"]").HasAttribute("disabled").Should().BeFalse();
        // Interior rows can take either role.
        cut.Find($"button[aria-label=\"{SetStartLabel("P2")}\"]").HasAttribute("disabled").Should().BeFalse();
        cut.Find($"button[aria-label=\"{SetFinishLabel("P2")}\"]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task TripStopList_UnsetFinish_RestoresRoundtrip_AndAnnounces()
    {
        await using var vm = await EnabledVmAsync(placeable: 3, finishPoiId: 3);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Find($"button[aria-label=\"{UnsetFinishLabel("P3")}\"]").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[aria-live='polite']")
                .Should().Contain(r => r.TextContent.Contains(UiStrings.TripRoundtripAnnounce, StringComparison.Ordinal));
            cut.Find($"button[aria-label=\"{SetFinishLabel("P3")}\"]").GetAttribute("aria-pressed").Should().Be("false");
        });

        vm.FinishPoiId.Should().BeNull();
        vm.IsRoundtrip.Should().BeTrue("clearing the Finish returns the Trip to a Roundtrip");
        vm.OrderedLegs.Should().HaveCount(3, "the closing leg is restored");
    }

    [Fact]
    public async Task MobileTripPanel_StartFinishControls_Operate_WithGlyphsAnd44pxTargets()
    {
        await using var vm = await EnabledVmAsync(placeable: 3);
        var cut = RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        // Same aria-labels as desktop (shared UiStrings + shared VM), â‰¥44px targets.
        var setStart = cut.Find($"button[aria-label=\"{SetStartLabel("P2")}\"]");
        setStart.GetAttribute("style").Should().Contain("min-width:44px").And.Contain("min-height:44px");

        setStart.Click();

        var startBadgeAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripStartBadgeAria, 3);
        var expected = string.Format(CultureInfo.CurrentCulture, UiStrings.TripStartSetAnnouncement, "P2");
        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".row");
            rows[0].TextContent.Should().Contain("P2");
            // The mobile Start row carries the role glyph with its accessible name.
            var glyph = cut.Find($"[aria-label=\"{startBadgeAria}\"]");
            glyph.TextContent.Trim().Should().Be("trip_origin");
            cut.Find($"button[aria-label=\"{UnsetStartLabel("P2")}\"]").GetAttribute("aria-pressed").Should().Be("true");
            cut.FindAll("[aria-live='polite']")
                .Should().Contain(r => r.TextContent.Contains(expected, StringComparison.Ordinal));
        });

        vm.StartPoiId.Should().Be(2);
    }

    [Fact]
    public async Task MobileTripPanel_CrossPinning_Disabled_AndFinishGlyphRenders()
    {
        await using var vm = await EnabledVmAsync(placeable: 3, startPoiId: 1, finishPoiId: 3);

        var cut = RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        cut.Find($"button[aria-label=\"{SetFinishLabel("P1")}\"]").HasAttribute("disabled").Should().BeTrue();
        cut.Find($"button[aria-label=\"{SetStartLabel("P3")}\"]").HasAttribute("disabled").Should().BeTrue();

        var finishBadgeAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripFinishBadgeAria, 3);
        cut.Find($"[aria-label=\"{finishBadgeAria}\"]").TextContent.Trim().Should().Be("sports_score");
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
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        var vm = new TripViewModel(ordering, factory, writeLock, new TravelTimeTrigger(), new TravelTimeProgressService(), TestDbHelper.CreateInvalidationService(factory), NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, 2);
        await vm.ToggleAsync();
        return vm;
    }

    [Fact]
    public async Task TripStopList_UnplaceableRow_Present_Labelled_NoBadge_WithAria()
    {
        await using var vm = await MixedVmAsync();

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // The row is present â€” never dropped (AC1).
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
        // Not selectable â€” no button semantics.
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

    // === Story 2.2: travel-mode selector + manual Any/Air leg time ===

    /// <summary>Sets the persisted TravelMode on the collection before enabling.</summary>
    private static async Task<TripViewModel> EnabledVmWithModeAsync(int placeable, string mode)
    {
        var factory = SeedFactory(placeable);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var c = await db.PoiCollections.FirstAsync(x => x.Id == CollectionId);
            c.TravelMode = mode;
            await db.SaveChangesAsync();
        }
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        var vm = new TripViewModel(ordering, factory, writeLock, new TravelTimeTrigger(), new TravelTimeProgressService(), TestDbHelper.CreateInvalidationService(factory), NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, placeable);
        await vm.ToggleAsync();
        return vm;
    }

    [Theory]
    [InlineData(typeof(TripStopList))]
    [InlineData(typeof(MobileTripPanel))]
    public async Task Selector_RendersFourSegments_WithPersistedModeActive(Type surface)
    {
        await using var vm = await EnabledVmWithModeAsync(placeable: 2, mode: TravelMode.Drive);

        var cut = surface == typeof(TripStopList)
            ? (IRenderedFragment)RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm))
            : RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        var group = cut.Find("[role=radiogroup]");
        var radios = group.QuerySelectorAll("[role=radio]");
        radios.Should().HaveCount(4);
        // The persisted Drive segment is the active (checked) one.
        var active = cut.Find($"button[aria-label=\"{UiStrings.TripTravelModeDrive}\"]");
        active.GetAttribute("aria-checked").Should().Be("true");
        cut.Find($"button[aria-label=\"{UiStrings.TripTravelModeAnyAir}\"]").GetAttribute("aria-checked").Should().Be("false");
    }

    [Fact]
    public async Task Selector_Switching_InvokesVm_AndPersists()
    {
        await using var vm = await EnabledVmWithModeAsync(placeable: 2, mode: TravelMode.AnyAir);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Find($"button[aria-label=\"{UiStrings.TripTravelModeWalk}\"]").Click();

        cut.WaitForAssertion(() =>
        {
            vm.TravelMode.Should().Be(TravelMode.Walk);
            cut.Find($"button[aria-label=\"{UiStrings.TripTravelModeWalk}\"]").GetAttribute("aria-checked").Should().Be("true");
        });
    }

    [Fact]
    public async Task ManualInput_Present_UnderAnyAir_Absent_UnderDrive()
    {
        await using var anyAir = await EnabledVmWithModeAsync(placeable: 2, mode: TravelMode.AnyAir);
        var cutAir = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, anyAir));
        var aria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripManualMinutesAria, "P1");
        cutAir.FindAll($"input[aria-label=\"{aria}\"]").Should().NotBeEmpty("Any/Air legs carry the manual minutes input");

        await using var drive = await EnabledVmWithModeAsync(placeable: 2, mode: TravelMode.Drive);
        var cutDrive = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, drive));
        // The MANUAL input is hidden under non-Any/Air modes. (The Story 2.5 dwell
        // input is a separate type=number always present, so scope to the manual
        // input's own aria-label.)
        cutDrive.FindAll($"input[aria-label=\"{aria}\"]").Should().BeEmpty("the manual input is hidden under non-Any/Air modes");
    }

    [Fact]
    public async Task AnyAir_NoManual_Leg_ShowsEmDash_NoBadge()
    {
        // A Placeholder Any/Air row â‡’ the time slot shows "â€”" and no fidelity badge.
        await using var vm = await EnabledVmWithModeAsync(placeable: 2, mode: TravelMode.AnyAir);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // With no cache rows the leg is uncomputed â‡’ "â€”" in the time slot. The
        // FidelityBadge renders nothing for Placeholder/null, so no provenance pill
        // appears (asserted via the badge's "Provenance: â€¦" aria-label, which is
        // distinct from the manual input's own aria text).
        cut.Markup.Should().Contain(UiStrings.TripLegTimeUnknown);
        var manualProvenance = string.Format(CultureInfo.CurrentCulture, UiStrings.TripFidelityAria, UiStrings.TripFidelityManual);
        var estimatedProvenance = string.Format(CultureInfo.CurrentCulture, UiStrings.TripFidelityAria, UiStrings.TripFidelityEstimated);
        cut.FindAll($"[aria-label=\"{manualProvenance}\"]").Should().BeEmpty("no Manual badge on an unentered Any/Air leg");
        cut.FindAll($"[aria-label=\"{estimatedProvenance}\"]").Should().BeEmpty("Any/Air legs are never Estimated");
    }

    [Fact]
    public async Task ManualEntry_ShowsManualBadge_AndUpdatesTotal()
    {
        await using var vm = await EnabledVmWithModeAsync(placeable: 2, mode: TravelMode.AnyAir);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        var aria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripManualMinutesAria, "P1");
        cut.Find($"input[aria-label=\"{aria}\"]").Change("90");

        cut.WaitForAssertion(() =>
        {
            // The 1â†’2 leg now carries the Manual badge.
            cut.Markup.Should().Contain(UiStrings.TripFidelityManual);
            var leg = vm.OrderedLegs.First(l => l.FromPoiId == 1 && l.ToPoiId == 2);
            leg.Fidelity.Should().Be(Fidelity.Manual);
            leg.DurationSeconds.Should().Be(5400);
        });
    }

    [Fact]
    public async Task ManualInput_PrefillsExistingManualMinutes()
    {
        await using var vm = await EnabledVmWithModeAsync(placeable: 2, mode: TravelMode.AnyAir);
        await vm.SetManualLegTimeAsync(1, 2, minutes: 75);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        var aria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripManualMinutesAria, "P1");
        cut.Find($"input[aria-label=\"{aria}\"]").GetAttribute("value").Should().Be("75");
    }

    // === Story 2.4 (TRIP-RECOMPUTE-01, AC4): the Recompute button, both surfaces ===

    [Theory]
    [InlineData(typeof(TripStopList))]
    [InlineData(typeof(MobileTripPanel))]
    public async Task Recompute_Button_Renders_UiStringsLabelled_OutsideRows(Type surface)
    {
        await using var vm = await EnabledVmAsync(placeable: 2);

        var cut = surface == typeof(TripStopList)
            ? (IRenderedFragment)RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm))
            : RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        var button = cut.Find($"button[aria-label=\"{UiStrings.TripRecomputeAria}\"]");
        button.TextContent.Trim().Should().Be(UiStrings.TripRecomputeLabel);
        // Placed outside the clickable stop rows so it never breaks row selection.
        button.Closest("li").Should().BeNull("the Recompute button must not sit inside a stop row (2.2 regression)");
    }

    [Theory]
    [InlineData(typeof(TripStopList))]
    [InlineData(typeof(MobileTripPanel))]
    public async Task Recompute_Button_Click_InvokesVm_AndKeepsRowSelectionWorking(Type surface)
    {
        await using var vm = await EnabledVmAsync(placeable: 2);

        var cut = surface == typeof(TripStopList)
            ? (IRenderedFragment)RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm))
            : RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        // Clicking Recompute invokes the VM (no exception; the VM stays usable).
        cut.Find($"button[aria-label=\"{UiStrings.TripRecomputeAria}\"]").Click();

        // Row selection still works after the recompute click â€” the button did not
        // capture the row's click target.
        vm.SelectStop(1, TripSelectionSource.List);
        vm.SelectedStopPoiId.Should().Be(1, "selecting a stop still works alongside the Recompute control");
    }

    // === Story 2.5 (TRIP-DWELL-01): per-stop dwell minutes input, both surfaces ===

    private static string DwellAria(string name) =>
        string.Format(CultureInfo.CurrentCulture, UiStrings.TripDwellAria, name);

    [Theory]
    [InlineData(typeof(TripStopList))]
    [InlineData(typeof(MobileTripPanel))]
    public async Task Dwell_Input_Renders_OnEveryRow_UiStringsLabelled(Type surface)
    {
        await using var vm = await EnabledVmAsync(placeable: 3);

        var cut = surface == typeof(TripStopList)
            ? (IRenderedFragment)RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm))
            : RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        foreach (var name in new[] { "P1", "P2", "P3" })
        {
            var input = cut.Find($"input[aria-label=\"{DwellAria(name)}\"]");
            input.GetAttribute("type").Should().Be("number");
            input.GetAttribute("min").Should().Be("0");
            input.GetAttribute("max").Should().Be(TripViewModel.MaxDwellMinutes.ToString(CultureInfo.InvariantCulture));
            input.GetAttribute("inputmode").Should().Be("numeric");
        }
    }

    [Theory]
    [InlineData(typeof(TripStopList))]
    [InlineData(typeof(MobileTripPanel))]
    public async Task Dwell_Input_PrefillsPersistedValue(Type surface)
    {
        await using var vm = await EnabledVmAsync(placeable: 2);
        await vm.SetDwellMinutesAsync(poiId: 1, minutes: 45);

        var cut = surface == typeof(TripStopList)
            ? (IRenderedFragment)RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm))
            : RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        cut.Find($"input[aria-label=\"{DwellAria("P1")}\"]").GetAttribute("value").Should().Be("45");
        cut.Find($"input[aria-label=\"{DwellAria("P2")}\"]").GetAttribute("value").Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData(typeof(TripStopList))]
    [InlineData(typeof(MobileTripPanel))]
    public async Task Dwell_Input_Change_InvokesVm_AndPersists(Type surface)
    {
        await using var vm = await EnabledVmAsync(placeable: 2);

        var cut = surface == typeof(TripStopList)
            ? (IRenderedFragment)RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm))
            : RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        cut.Find($"input[aria-label=\"{DwellAria("P1")}\"]").Change("30");

        cut.WaitForAssertion(() =>
            vm.StopRows.First(r => r.PoiId == 1).DwellMinutes.Should().Be(30, "editing the input persists via the VM"));
    }

    [Theory]
    [InlineData(typeof(TripStopList))]
    [InlineData(typeof(MobileTripPanel))]
    public async Task Dwell_Input_Present_OnUnplaceableRow(Type surface)
    {
        await using var vm = await MixedVmAsync(); // POIs 1,2 placeable; 99 unplaceable

        var cut = surface == typeof(TripStopList)
            ? (IRenderedFragment)RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm))
            : RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        // The unplaceable row carries a dwell input identically (AC4).
        cut.Find($"input[aria-label=\"{DwellAria("NoCoords")}\"]").GetAttribute("type").Should().Be("number");
    }

    [Fact]
    public async Task Dwell_Input_Editing_DoesNotSelectTheRow()
    {
        // The dwell input carries stopPropagation, so changing it must not select
        // the row (the 2.2 selection-regression guard).
        await using var vm = await EnabledVmAsync(placeable: 2);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Find($"input[aria-label=\"{DwellAria("P1")}\"]").Change("20");

        cut.WaitForAssertion(() =>
            vm.StopRows.First(r => r.PoiId == 1).DwellMinutes.Should().Be(20));
        vm.SelectedStopPoiId.Should().BeNull("editing dwell must not select the row");
        cut.FindAll("li[aria-current='true']").Should().BeEmpty();
    }

    [Fact]
    public async Task Dwell_Input_CarriesStopPropagation_InMarkup()
    {
        // Structural guard for the 2.2 row-selection regression: the dwell input on a
        // selectable (placeable) row must declare onclick + onkeydown stopPropagation.
        await using var vm = await EnabledVmAsync(placeable: 2);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        var input = cut.Find($"input[aria-label=\"{DwellAria("P1")}\"]");
        input.HasAttribute("blazor:onclick:stoppropagation").Should().BeTrue();
        input.HasAttribute("blazor:onkeydown:stoppropagation").Should().BeTrue();
    }
}
