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

        var rows = cut.FindAll("li[data-poi-id]");
        rows.Should().HaveCount(3);
        rows[0].TextContent.Should().Contain("P1");
        rows[2].TextContent.Should().Contain("P3");

        // Order badge with the "Stop X of Y" aria-label.
        var badgeAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripStopBadgeAria, 1, 3);
        cut.Find($"[aria-label=\"{badgeAria}\"]").TextContent.Trim().Should().Be("1");

        // Story 2.5 (TRIP-DWELL-01) / Story 4.4 (FR-30): the dwell slot is now an empty
        // HH:MM picker (unset ⇒ no value) carrying its per-stop UiStrings aria-label.
        var dwellAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripDwellAria, "P1");
        var dwell = cut.Find($"input[aria-label=\"{dwellAria}\"]");
        dwell.GetAttribute("type").Should().Be("time");
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
            var rows = cut.FindAll("li[data-poi-id]");
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
            var rows = cut.FindAll("li[data-poi-id]");
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
            var rows = cut.FindAll("li[data-poi-id]");
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

        var rows = cut.FindAll("li[data-poi-id]");
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
            var rows = cut.FindAll("li[data-poi-id]");
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
            var rows = cut.FindAll("li[data-poi-id]");
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
        var rows = cut.FindAll("li[data-poi-id]");
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

    // Story 3.4 (FR-23): the trip-wide TravelModeSelector is removed from the DESKTOP
    // surface, so only MobileTripPanel still renders the radiogroup. (Desktop "user can
    // set mode" intent is re-expressed by the per-leg LegModePill tests in
    // LegModePillTests + Desktop_NoTravelModeSelector_PerLegPillPresent below.)
    [Fact]
    public async Task MobileSelector_RendersFourSegments_WithPersistedModeActive()
    {
        await using var vm = await EnabledVmWithModeAsync(placeable: 2, mode: TravelMode.Drive);

        var cut = RenderComponent<MobileTripPanel>(p => p.Add(x => x.Vm, vm));

        var group = cut.Find("[role=radiogroup]");
        var radios = group.QuerySelectorAll("[role=radio]");
        radios.Should().HaveCount(4);
        // The persisted Drive segment is the active (checked) one.
        var active = cut.Find($"button[aria-label=\"{UiStrings.TripTravelModeDrive}\"]");
        active.GetAttribute("aria-checked").Should().Be("true");
        cut.Find($"button[aria-label=\"{UiStrings.TripTravelModeAnyAir}\"]").GetAttribute("aria-checked").Should().Be("false");
    }

    // Story 3.4 (FR-23): the DESKTOP TripStopList no longer renders the trip-wide
    // selector (no role=radiogroup / no "Travel mode" radiogroup aria); the per-leg
    // LegModePill takes its place on each connector.
    [Fact]
    public async Task Desktop_NoTravelModeSelector_PerLegPillPresent()
    {
        await using var vm = await EnabledVmWithModeAsync(placeable: 2, mode: TravelMode.AnyAir);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.FindAll("[role=radiogroup]").Should().BeEmpty("the trip-wide selector is removed from desktop (FR-23)");
        // The per-leg pill replaces it — a leg connector carries the mode pill.
        var pillAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripLegModePillAria, "P1");
        cut.FindAll($"button[aria-label=\"{pillAria}\"]").Should().NotBeEmpty("the per-leg mode pill replaces the trip-wide selector");
    }

    [Fact]
    public async Task ManualTime_IsClickToEdit_OnAnyLeg_GroundAndAnyAir()
    {
        // Story 3.5 (UX-DR6): the manual minutes edit is no longer gated on the leg's
        // mode — the travel time is CLICK-TO-EDIT on ANY leg (ground or Any/Air). The
        // input is hidden at rest and appears when the leg's time button is clicked.
        await using var vm = await EnabledVmWithModeAsync(placeable: 2, mode: TravelMode.AnyAir);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));
        var aria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripManualMinutesAria, "P1");
        var editTitle = string.Format(CultureInfo.CurrentCulture, UiStrings.TripLegEditTimeAria, "P1");

        // At rest: no input; a click-to-edit time button is present even on the Any/Air leg.
        cut.FindAll($"input[aria-label=\"{aria}\"]").Should().BeEmpty("the input is hidden until the time is clicked");
        cut.Find($"button[title=\"{editTitle}\"]").Click();
        cut.WaitForAssertion(() =>
            cut.FindAll($"input[aria-label=\"{aria}\"]").Should().NotBeEmpty("clicking the Any/Air leg's time opens the inline editor"));

        // Set the 1â†’2 leg's mode to Drive (per-leg) and re-render fresh: the click-to-edit
        // time button is STILL present on the ground leg (no mode gate, Story 3.5).
        await vm.SetLegModeAsync(1, TravelMode.Drive);
        var cutDrive = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));
        cutDrive.FindAll($"input[aria-label=\"{aria}\"]").Should().BeEmpty("the ground leg's input is also hidden at rest");
        cutDrive.Find($"button[title=\"{editTitle}\"]").Click();
        cutDrive.WaitForAssertion(() =>
            cutDrive.FindAll($"input[aria-label=\"{aria}\"]").Should().NotBeEmpty("a ground (Drive) leg's time is click-to-edit too"));
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
        var manualProvenance = UiStrings.TripFidelityManualTooltip;
        var estimatedProvenance = UiStrings.TripFidelityEstimatedTooltip;
        cut.FindAll($"[aria-label=\"{manualProvenance}\"]").Should().BeEmpty("no Manual badge on an unentered Any/Air leg");
        cut.FindAll($"[aria-label=\"{estimatedProvenance}\"]").Should().BeEmpty("Any/Air legs are never Estimated");
    }

    [Fact]
    public async Task ManualEntry_ShowsManualBadge_AndUpdatesTotal()
    {
        await using var vm = await EnabledVmWithModeAsync(placeable: 2, mode: TravelMode.AnyAir);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        var aria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripManualMinutesAria, "P1");
        // Story 3.5 (UX-DR6): open the inline editor by clicking the leg's time button.
        var editTitle = string.Format(CultureInfo.CurrentCulture, UiStrings.TripLegEditTimeAria, "P1");
        cut.Find($"button[title=\"{editTitle}\"]").Click();
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
        // Story 3.5 (UX-DR6): open the inline editor; it pre-fills the existing Manual minutes.
        var editTitle = string.Format(CultureInfo.CurrentCulture, UiStrings.TripLegEditTimeAria, "P1");
        cut.Find($"button[title=\"{editTitle}\"]").Click();
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

        var isDesktop = surface == typeof(TripStopList);
        foreach (var name in new[] { "P1", "P2", "P3" })
        {
            var input = cut.Find($"input[aria-label=\"{DwellAria(name)}\"]");
            if (isDesktop)
            {
                // Story 4.4 (FR-30): the desktop dwell control is a native HH:MM picker.
                input.GetAttribute("type").Should().Be("time");
            }
            else
            {
                // Mobile keeps the minutes input (deferred mirror).
                input.GetAttribute("type").Should().Be("number");
                input.GetAttribute("min").Should().Be("0");
                input.GetAttribute("max").Should().Be(TripViewModel.MaxDwellMinutes.ToString(CultureInfo.InvariantCulture));
                input.GetAttribute("inputmode").Should().Be("numeric");
            }
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

        // Story 4.4 (FR-30): desktop renders the canonical 45 minutes as the HH:mm wire
        // value "00:45"; mobile keeps the raw-minutes value "45".
        var expected = surface == typeof(TripStopList) ? "00:45" : "45";
        cut.Find($"input[aria-label=\"{DwellAria("P1")}\"]").GetAttribute("value").Should().Be(expected);
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

        // Story 4.4 (FR-30): desktop edits in HH:mm ("00:30"); mobile edits in minutes.
        var entered = surface == typeof(TripStopList) ? "00:30" : "30";
        cut.Find($"input[aria-label=\"{DwellAria("P1")}\"]").Change(entered);

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

        // The unplaceable row carries a dwell input identically (AC4). Story 4.4
        // (FR-30): desktop is the HH:MM picker; mobile keeps the minutes input.
        var expectedType = surface == typeof(TripStopList) ? "time" : "number";
        cut.Find($"input[aria-label=\"{DwellAria("NoCoords")}\"]").GetAttribute("type").Should().Be(expectedType);
    }

    // === Story 4.4 (FR-30): desktop dwell HH:MM picker ===

    [Theory]
    [InlineData(45, "00:45")]
    [InlineData(90, "01:30")]
    [InlineData(0, "00:00")]
    [InlineData(125, "02:05")]
    public async Task Dwell_Input_Desktop_RoundTripsMinutesToHhmm(int minutes, string expected)
    {
        // The desktop dwell control is a native HH:MM picker; a set value renders as the
        // invariant "HH:mm" wire value. Canonical DwellMinutes stays minutes.
        await using var vm = await EnabledVmAsync(placeable: 2);
        await vm.SetDwellMinutesAsync(poiId: 1, minutes: minutes);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Find($"input[aria-label=\"{DwellAria("P1")}\"]").GetAttribute("value").Should().Be(expected);
    }

    [Fact]
    public async Task Dwell_Input_Desktop_EntersHhmm_PersistsCanonicalMinutes()
    {
        // Entering "01:30" persists the canonical 90 minutes via the VM.
        await using var vm = await EnabledVmAsync(placeable: 2);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Find($"input[aria-label=\"{DwellAria("P1")}\"]").Change("01:30");

        cut.WaitForAssertion(() =>
            vm.StopRows.First(r => r.PoiId == 1).DwellMinutes.Should().Be(90));
    }

    [Fact]
    public async Task Dwell_Input_Desktop_Clearing_PersistsNull()
    {
        // Clearing the HH:MM control persists null (dwell removed).
        await using var vm = await EnabledVmAsync(placeable: 2);
        await vm.SetDwellMinutesAsync(poiId: 1, minutes: 60);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Find($"input[aria-label=\"{DwellAria("P1")}\"]").Change(string.Empty);

        cut.WaitForAssertion(() =>
            vm.StopRows.First(r => r.PoiId == 1).DwellMinutes.Should().BeNull());
    }

    [Fact]
    public async Task Dwell_Input_Desktop_UnplaceableRow_EntersHhmm_PersistsMinutes()
    {
        // An unplaceable row's dwell HH:MM picker persists canonical minutes too (AC4).
        await using var vm = await MixedVmAsync(); // POI 99 is unplaceable
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Find($"input[aria-label=\"{DwellAria("NoCoords")}\"]").Change("00:45");

        cut.WaitForAssertion(() =>
            vm.StopRows.First(r => r.PoiId == 99).DwellMinutes.Should().Be(45));
    }

    [Fact]
    public async Task Dwell_Input_Editing_DoesNotSelectTheRow()
    {
        // The dwell input carries stopPropagation, so changing it must not select
        // the row (the 2.2 selection-regression guard).
        await using var vm = await EnabledVmAsync(placeable: 2);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        cut.Find($"input[aria-label=\"{DwellAria("P1")}\"]").Change("00:20");

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

    // === Story 1.2: wide trip-scoped table with trip-only columns ===

    private static string FocusAria(string name) =>
        string.Format(CultureInfo.CurrentCulture, UiStrings.TripFocusOnMapAria, name);

    private static string OpenMapsAria(string name) =>
        string.Format(CultureInfo.CurrentCulture, UiStrings.TripOpenInGoogleMapsAria, name);

    /// <summary>
    /// Seed with rich POI fields (full name, address, enrichment flags, GoogleMapsUrl)
    /// so the Story 1.2 Name column + Actions have real data to project. P1 enriched
    /// with an explicit maps URL + address; P2 needs-manual-url; P3 waiting (default).
    /// </summary>
    private static async Task<TripViewModel> RichVmAsync()
    {
        var factory = TestDbHelper.CreateFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.PoiCollections.Add(new PoiCollection { Id = CollectionId, Name = "Trip", Color = "#005bbf" });
            db.Pois.Add(new Poi
            {
                Id = 1,
                Name = "A Very Long Point Of Interest Name That Should Not Be Truncated Away",
                Latitude = 51, Longitude = 21, AddedDate = new DateTime(2025, 1, 1),
                Address = "123 Example Street, Krakow",
                IsEnriched = true,
                GoogleMapsUrl = "https://www.google.com/maps/place/Wawel",
            });
            db.Pois.Add(new Poi
            {
                Id = 2, Name = "P2", Latitude = 52, Longitude = 22, AddedDate = new DateTime(2025, 1, 2),
                EnrichmentNeedsManualUrl = true,
            });
            db.Pois.Add(new Poi
            {
                Id = 3, Name = "P3", Latitude = 53, Longitude = 23, AddedDate = new DateTime(2025, 1, 3),
            });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 1, PoiCollectionId = CollectionId });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 2, PoiCollectionId = CollectionId });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 3, PoiCollectionId = CollectionId });
            await db.SaveChangesAsync();
        }

        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        var vm = new TripViewModel(ordering, factory, writeLock, new TravelTimeTrigger(), new TravelTimeProgressService(), TestDbHelper.CreateInvalidationService(factory), NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, 3);
        await vm.ToggleAsync();
        return vm;
    }

    [Fact]
    public async Task WideTable_RendersAllSevenColumns_PerRow()
    {
        // AC1/FR-2: each row shows reorder gutter (drag handle + ▲▼), stop-# badge,
        // name, dwell, arrival, start/finish, and actions (Focus + Open-in-Maps).
        await using var vm = await RichVmAsync();
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        var row = cut.Find("li[data-poi-id='1']");
        // Reorder gutter: drag handle + ▲▼.
        row.QuerySelector($"[aria-label=\"{string.Format(CultureInfo.CurrentCulture, UiStrings.TripDragHandle, "A Very Long Point Of Interest Name That Should Not Be Truncated Away")}\"]").Should().NotBeNull();
        row.QuerySelector($"button[aria-label=\"{MoveUpLabel("A Very Long Point Of Interest Name That Should Not Be Truncated Away")}\"]").Should().NotBeNull();
        row.QuerySelector($"button[aria-label=\"{MoveDownLabel("A Very Long Point Of Interest Name That Should Not Be Truncated Away")}\"]").Should().NotBeNull();
        // Stop-# badge.
        var badgeAria = string.Format(CultureInfo.CurrentCulture, UiStrings.TripStopBadgeAria, 1, 3);
        row.QuerySelector($"[aria-label=\"{badgeAria}\"]").Should().NotBeNull();
        // Dwell input.
        row.QuerySelector($"input[aria-label=\"{DwellAria("A Very Long Point Of Interest Name That Should Not Be Truncated Away")}\"]").Should().NotBeNull();
        // Start/Finish controls.
        row.QuerySelector($"button[aria-label=\"{SetStartLabel("A Very Long Point Of Interest Name That Should Not Be Truncated Away")}\"]").Should().NotBeNull();
        row.QuerySelector($"button[aria-label=\"{SetFinishLabel("A Very Long Point Of Interest Name That Should Not Be Truncated Away")}\"]").Should().NotBeNull();
    }

    [Fact]
    public async Task WideTable_ShowsFullName_NotTruncatedAway()
    {
        // AC1/UX-DR1: the full POI name is present (not clipped to a tiny width).
        await using var vm = await RichVmAsync();
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        const string fullName = "A Very Long Point Of Interest Name That Should Not Be Truncated Away";
        cut.Find("li[data-poi-id='1']").TextContent.Should().Contain(fullName);
        // The name span must not carry the `truncate` utility (the old cramped behavior).
        var nameSpan = cut.Find("li[data-poi-id='1'] span[title=\"" + fullName + "\"]");
        nameSpan.GetAttribute("class").Should().NotContain("truncate");
    }

    [Fact]
    public async Task WideTable_ShowsAddressSubLine_AndEnrichmentIcon()
    {
        // AC1: address sub-line (when present) + enrichment-state icon mirroring PoiTable.
        await using var vm = await RichVmAsync();
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // Address sub-line on the enriched POI with an address.
        cut.Find("li[data-poi-id='1']").TextContent.Should().Contain("123 Example Street, Krakow");
        // Enriched ⇒ location_on/muted with the "Enriched" title.
        var enrichedIcon = cut.Find($"li[data-poi-id='1'] [title=\"{UiStrings.TripEnrichmentEnriched}\"]");
        enrichedIcon.TextContent.Trim().Should().Be("location_on");
        // Needs-manual-url ⇒ error/red.
        var failedIcon = cut.Find($"li[data-poi-id='2'] [title=\"{UiStrings.TripEnrichmentFailed}\"]");
        failedIcon.TextContent.Trim().Should().Be("error");
        // Waiting (default) ⇒ hourglass_empty/amber.
        var waitingIcon = cut.Find($"li[data-poi-id='3'] [title=\"{UiStrings.TripEnrichmentWaiting}\"]");
        waitingIcon.TextContent.Trim().Should().Be("hourglass_empty");
    }

    [Fact]
    public async Task WideTable_Actions_AreExactly_Focus_AndOpenInGoogleMaps()
    {
        // AC2: Actions are Focus on map + Open in Google Maps ONLY — no select checkbox,
        // coords, collection chips, added-date, move-to/copy/delete, no batch toolbar.
        await using var vm = await RichVmAsync();
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        const string fullName = "A Very Long Point Of Interest Name That Should Not Be Truncated Away";
        var row = cut.Find("li[data-poi-id='1']");

        // Focus on map present.
        row.QuerySelector($"button[aria-label=\"{FocusAria(fullName)}\"]").Should().NotBeNull();
        // Open in Google Maps anchor present with the projected URL.
        var openLink = row.QuerySelector($"a[aria-label=\"{OpenMapsAria(fullName)}\"]");
        openLink.Should().NotBeNull();
        openLink!.GetAttribute("href").Should().Be("https://www.google.com/maps/place/Wawel");
        openLink.GetAttribute("target").Should().Be("_blank");

        // Absent: the PoiTable management actions/columns.
        cut.FindAll("input[type='checkbox']").Should().BeEmpty("no Select checkbox");
        cut.Markup.Should().NotContain("drive_file_move", "no Move-to-collection action");
        cut.Markup.Should().NotContain("content_copy", "no Copy action");
        cut.Markup.Should().NotContain("delete", "no Delete action");
        cut.Markup.Should().NotContain(UiStrings.FilteredResults, "no PoiTable batch toolbar/header");
    }

    [Fact]
    public async Task WideTable_PerLegTime_IsNotInsideAStopRow()
    {
        // AC2/FR-3: per-leg travel time/distance/fidelity lives in the inter-row
        // connector, NOT inside any stop <li>.
        var factory = TestDbHelper.CreateFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.PoiCollections.Add(new PoiCollection { Id = CollectionId, Name = "Trip", Color = "#005bbf" });
            for (var i = 1; i <= 2; i++)
            {
                db.Pois.Add(new Poi { Id = i, Name = $"P{i}", Latitude = 50 + i, Longitude = 20 + i, AddedDate = new DateTime(2025, 1, i) });
                db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = i, PoiCollectionId = CollectionId });
            }
            db.RouteSegments.Add(new RouteSegment { FromPoiId = 1, ToPoiId = 2, TravelMode = TravelMode.AnyAir, DurationSeconds = 4800, DistanceMeters = 12000, Fidelity = Fidelity.Estimated, Source = "Mock", ComputedAt = DateTime.UtcNow });
            db.RouteSegments.Add(new RouteSegment { FromPoiId = 2, ToPoiId = 1, TravelMode = TravelMode.AnyAir, DurationSeconds = 4800, DistanceMeters = 12000, Fidelity = Fidelity.Estimated, Source = "Mock", ComputedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        await using var vm = new TripViewModel(ordering, factory, writeLock, new TravelTimeTrigger(), new TravelTimeProgressService(), TestDbHelper.CreateInvalidationService(factory), NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, 2);
        await vm.ToggleAsync();

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // Story 1.3: the leg time + distance render in the LegConnector, on the
        // shared edge between rows — inside an aria-hidden <li> WITHOUT a
        // data-poi-id (valid list nesting), never inside a STOP row.
        var connector = cut.Find("div.trip-leg-connector");
        connector.TextContent.Should().Contain("1h 20 min").And.Contain("12 km");
        var connectorLi = connector.Closest("li");
        connectorLi.Should().NotBeNull("the connector is wrapped in an <li> for valid <ul> nesting");
        connectorLi!.HasAttribute("data-poi-id").Should().BeFalse("the connector <li> is not a stop row");
        // NOT aria-hidden: the connector carries meaningful info + focusable controls
        // that must stay in the accessibility tree (NFR7).
        connectorLi.HasAttribute("aria-hidden").Should().BeFalse("the connector content is exposed to AT");

        // No stop row contains the per-leg distance or the fidelity badge. (The leg
        // TIME is not asserted here because the Arrival column — which legitimately
        // STAYS a row column — can share the same duration string as the offset; the
        // connector check above already proves the leg time lives between rows.)
        foreach (var li in cut.FindAll("li[data-poi-id]"))
        {
            li.TextContent.Should().NotContain("12 km", "per-leg distance is not a stop-row column");
            li.TextContent.Should().NotContain(UiStrings.TripFidelityEstimated, "the fidelity badge is not a stop-row column");
        }
    }

    [Fact]
    public async Task WideTable_RowClickSelects_ButActionClicksDoNot()
    {
        // AC3: row click selects; dwell/action clicks stopPropagation (no selection).
        await using var vm = await RichVmAsync();
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        const string fullName = "A Very Long Point Of Interest Name That Should Not Be Truncated Away";

        // Both actions stopPropagation so a click on them never bubbles to select the
        // row (structural guard — the Open-in-Maps anchor navigates, the Focus button
        // has its own handler; both declare onclick stopPropagation in markup).
        var openLink = cut.Find($"li[data-poi-id='1'] a[aria-label=\"{OpenMapsAria(fullName)}\"]");
        openLink.HasAttribute("blazor:onclick:stoppropagation").Should().BeTrue();
        var focusBtn = cut.Find($"li[data-poi-id='1'] button[aria-label=\"{FocusAria(fullName)}\"]");
        focusBtn.HasAttribute("blazor:onclick:stoppropagation").Should().BeTrue();

        // Editing the dwell input must not select the row.
        cut.Find($"li[data-poi-id='1'] input[aria-label=\"{DwellAria(fullName)}\"]").Change("00:15");
        cut.WaitForAssertion(() => vm.StopRows.First(r => r.PoiId == 1).DwellMinutes.Should().Be(15));
        vm.SelectedStopPoiId.Should().BeNull("editing dwell must not select the row");

        // A genuine row-body click DOES select.
        cut.Find("li[data-poi-id='1']").Click();
        cut.WaitForAssertion(() => vm.SelectedStopPoiId.Should().Be(1));
    }

    [Fact]
    public async Task WideTable_FocusButton_InvokesOnFocusClicked_WhenWired()
    {
        // AC1/FR-2: the Focus-on-map button wires to the host callback (parity with
        // PoiTable's OnFocusClicked → MapPageViewModel.HandleFocusPoiAsync).
        await using var vm = await RichVmAsync();
        int? focused = null;
        var cut = RenderComponent<TripStopList>(p => p
            .Add(x => x.Vm, vm)
            .Add(x => x.OnFocusClicked, (int id) => focused = id));

        const string fullName = "A Very Long Point Of Interest Name That Should Not Be Truncated Away";
        await cut.Find($"li[data-poi-id='1'] button[aria-label=\"{FocusAria(fullName)}\"]").ClickAsync(new MouseEventArgs());

        focused.Should().Be(1, "Focus on map forwards the POI id to the host map-focus handler");
        vm.SelectedStopPoiId.Should().BeNull("with the host wired, Focus does not fall back to selecting the row");
    }

    [Fact]
    public async Task WideTable_AllRowsShareTheSameGridTemplate_AcrossVariedStates()
    {
        // AC4/FR-11/12: columns stay aligned across placeable, unplaceable, pinned, and
        // long/short-name states — every row uses the SAME grid-template-columns.
        var factory = SeedFactory(placeable: 4);
        await using (var db = await factory.CreateDbContextAsync())
        {
            // P1 pinned Start, P4 pinned Finish; plus an unplaceable POI.
            var c = await db.PoiCollections.FirstAsync(x => x.Id == CollectionId);
            c.StartPoiId = 1;
            c.FinishPoiId = 4;
            db.Pois.Add(new Poi { Id = 99, Name = "NoCoords", Latitude = null, Longitude = null, AddedDate = new DateTime(2025, 1, 9) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = 99, PoiCollectionId = CollectionId });
            await db.SaveChangesAsync();
        }
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        await using var vm = new TripViewModel(ordering, factory, writeLock, new TravelTimeTrigger(), new TravelTimeProgressService(), TestDbHelper.CreateInvalidationService(factory), NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, 4);
        await vm.ToggleAsync();

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        var rows = cut.FindAll("li[data-poi-id]");
        rows.Should().HaveCount(5, "4 placeable (incl. pinned) + 1 unplaceable");

        // Every row — placeable, pinned, long-name, and the unplaceable one — declares
        // the identical grid-template-columns so the columns line up (FR-11/12).
        var templates = rows
            .Select(r => r.GetAttribute("style"))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();
        templates.Should().HaveCount(5);
        templates.Should().OnlyContain(s => s.Contains("display:grid"));
        var first = templates[0];
        templates.Should().AllBe(first, "all rows share one column template ⇒ aligned columns");
    }

    // === Story 1.3: the inter-row LegConnector placement + valid list nesting ===

    /// <summary>
    /// Seeds <paramref name="placeable"/> stops plus the full directional set of
    /// AnyAir route segments between consecutive stops AND the roundtrip closing
    /// pair, so every leg (including the closing leg) carries a real Estimated
    /// duration/distance the connector can render.
    /// </summary>
    private static async Task<TripViewModel> RoutedRoundtripVmAsync(int placeable)
    {
        var factory = SeedFactory(placeable);
        await using (var db = await factory.CreateDbContextAsync())
        {
            void Seg(int from, int to) => db.RouteSegments.Add(new RouteSegment
            {
                FromPoiId = from, ToPoiId = to, TravelMode = TravelMode.AnyAir,
                DurationSeconds = 4800, DistanceMeters = 12000,
                Fidelity = Fidelity.Estimated, Source = "Mock", ComputedAt = DateTime.UtcNow,
            });
            for (var i = 1; i < placeable; i++) { Seg(i, i + 1); Seg(i + 1, i); }
            // Roundtrip closing leg: last → first (and the reverse, for safety).
            Seg(placeable, 1); Seg(1, placeable);
            await db.SaveChangesAsync();
        }
        var writeLock = new SqliteWriteLock();
        var ordering = TestDbHelper.CreateOrderingService(factory, writeLock);
        var vm = new TripViewModel(ordering, factory, writeLock, new TravelTimeTrigger(), new TravelTimeProgressService(), TestDbHelper.CreateInvalidationService(factory), NullLogger<TripViewModel>.Instance);
        await vm.LoadAsync(CollectionId, placeable);
        await vm.ToggleAsync();
        return vm;
    }

    [Fact]
    public async Task TripStopList_Connector_RendersBetweenConsecutiveRows_AsLiWithoutPoiId()
    {
        await using var vm = await RoutedRoundtripVmAsync(placeable: 3);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        // A 3-stop roundtrip has 3 legs ⇒ 3 connectors, each in its own <li>
        // WITHOUT a data-poi-id (so li[data-poi-id] still counts exactly the 3 stop
        // rows — valid <ul> nesting, no bare <div> child). The connector <li> is NOT
        // aria-hidden — its content/controls stay in the accessibility tree (NFR7).
        cut.FindAll("li[data-poi-id]").Should().HaveCount(3, "only stop rows carry data-poi-id");
        var connectorLis = cut.FindAll(".trip-stop-list > li:not([data-poi-id])");
        connectorLis.Should().HaveCount(3, "one connector per leg, each wrapped in its own <li>");
        cut.FindAll("div.trip-leg-connector").Should().HaveCount(3);

        // Each connector sits AFTER its origin row in document order: the first
        // connector follows stop row 1 (between rows 1 and 2).
        var allLis = cut.FindAll(".trip-stop-list > li");
        allLis[0].HasAttribute("data-poi-id").Should().BeTrue("row 1 first");
        allLis[1].HasAttribute("data-poi-id").Should().BeFalse("then the 1→2 connector");
    }

    [Fact]
    public async Task TripStopList_ClosingConnector_RendersAfterLastRow_AndBeforeFinishFooter()
    {
        // A roundtrip closing leg (FromPoiId == last stop) renders after the last
        // stop row; the finish/return footer sits OUTSIDE the <ul>, so the closing
        // connector is before it (AC2).
        await using var vm = await RoutedRoundtripVmAsync(placeable: 3);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        var listChildren = cut.FindAll(".trip-stop-list > li");
        // Last child of the list is the closing connector (after the last stop row).
        var last = listChildren[^1];
        last.HasAttribute("data-poi-id").Should().BeFalse("the closing connector is the last list child");
        last.QuerySelector("div.trip-leg-connector").Should().NotBeNull("the last list child is the closing connector");
        // The stop row immediately before it is the last placeable stop (P3).
        listChildren[^2].GetAttribute("data-poi-id").Should().Be("3");

        // The finish/return footer is rendered OUTSIDE the <ul> (after it), so the
        // closing connector (the last <ul> child) precedes it.
        cut.Markup.Should().Contain(UiStrings.TripTimelineFinishLabel, "the return-to-start footer renders for a roundtrip");
        cut.FindAll("ul.trip-stop-list .trip-leg-connector")
            .Should().HaveCount(3, "all 3 connectors (incl. the closing one) live inside the <ul>, before the footer");
    }

    [Fact]
    public async Task TripStopList_OpenPath_HasNoClosingConnector_AfterFinishRow()
    {
        // A distinct Finish opens the path (N−1 legs, no closing leg) ⇒ N−1
        // connectors; the last stop row (the Finish) has NO departing connector.
        await using var vm = await EnabledVmAsync(placeable: 3, finishPoiId: 3);

        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        vm.OrderedLegs.Should().HaveCount(2, "an open path has N−1 legs");
        cut.FindAll(".trip-stop-list > li:not([data-poi-id])").Should().HaveCount(2, "no closing connector on an open path");
        // The last list child is the Finish stop row, not a connector.
        var listChildren = cut.FindAll(".trip-stop-list > li");
        listChildren[^1].GetAttribute("data-poi-id").Should().Be("3", "the Finish row is last — no trailing connector");
    }

    // Story 2.5 (FR-17/18, UX-DR10): the icon-only trip controls named in the AC
    // (move up/down, Set/Unset Start ○, Set/Unset Finish ⚑, TSP-Sort, Recompute)
    // expose a native `title` at parity with their `aria-label` — sighted hover
    // parity with AT. (The Focus/Open actions intentionally use a short title vs a
    // descriptive aria-label, mirroring PoiTable, so they are not in this set.)
    [Fact]
    public async Task IconControls_HaveTitle_AtParityWithAriaLabel()
    {
        await using var vm = await EnabledVmAsync(placeable: 3);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        var name = vm.OrderedStops.First(s => s.PoiId == 2).Name; // an interior row (move both ways)
        var ic = System.Globalization.CultureInfo.CurrentCulture;
        var expected = new[]
        {
            UiStrings.TripSortTspAria,
            UiStrings.TripRecomputeAria,
            string.Format(ic, UiStrings.TripMoveStopUp, name),
            string.Format(ic, UiStrings.TripMoveStopDown, name),
            string.Format(ic, UiStrings.TripSetAsStart, name),
            string.Format(ic, UiStrings.TripSetAsFinish, name),
        };

        foreach (var aria in expected)
        {
            var btn = cut.Find($"button[aria-label=\"{aria}\"]");
            btn.GetAttribute("title").Should().Be(aria,
                "the control's tooltip is at parity with its aria-label (FR-18)");
        }
    }

    // Story 2.5 (FR-18): the Start tooltip reflects the control's state — "Set as
    // start" when unpinned, "Unset … as start" when this row is the Start.
    [Fact]
    public async Task StartControl_Title_ReflectsState()
    {
        await using var vm = await EnabledVmAsync(placeable: 3, startPoiId: 1);
        var cut = RenderComponent<TripStopList>(p => p.Add(x => x.Vm, vm));

        var poi1Name = vm.OrderedStops.First(s => s.PoiId == 1).Name;
        var poi2Name = vm.OrderedStops.First(s => s.PoiId == 2).Name;

        // Row 1 is the Start ⇒ its Start control reads "Unset …"; a non-start row reads "Set as …".
        var setStart = string.Format(System.Globalization.CultureInfo.CurrentCulture, UiStrings.TripSetAsStart, poi2Name);
        var unsetStart = string.Format(System.Globalization.CultureInfo.CurrentCulture, UiStrings.TripUnsetStart, poi1Name);
        cut.FindAll("button[title]").Select(b => b.GetAttribute("title"))
            .Should().Contain(unsetStart).And.Contain(setStart);
    }
}
