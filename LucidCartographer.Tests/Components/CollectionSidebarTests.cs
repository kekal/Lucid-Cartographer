using Bunit;
using BunitTestContext = Bunit.TestContext;
using FluentAssertions;
using LucidCartographer.Components.Shared;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using Microsoft.AspNetCore.Components;

namespace LucidCartographer.Tests.Components;

public class CollectionSidebarTests : BunitTestContext
{
    private static CollectionDisplayState MakeVm(int id, string name, string color, int poiCount = 0, bool isVisible = true)
    {
        var col = new PoiCollection { Id = id, Name = name, Color = color, PoiCount = poiCount, IsVisible = isVisible };
        return new CollectionDisplayState(col);
    }

    [Fact]
    public void Renders_EmptyStateMessage_WhenNoCollections()
    {
        var cut = RenderComponent<CollectionSidebar>(parameters => parameters
            .Add(p => p.Collections, (List<CollectionDisplayState>)[]));

        cut.Markup.Should().Contain("No collections yet.");
        cut.Markup.Should().Contain("Import data via Data Sources tab.");
    }

    [Fact]
    public void Renders_CollectionNames_WithCorrectCount()
    {
        List<CollectionDisplayState> vms =
        [
            MakeVm(1, "Restaurants", "#ff0000", 12),
            MakeVm(2, "Hotels", "#00ff00", 5)
        ];

        var cut = RenderComponent<CollectionSidebar>(parameters => parameters
            .Add(p => p.Collections, vms));

        cut.Markup.Should().Contain("Restaurants");
        cut.Markup.Should().Contain("Hotels");
        cut.Markup.Should().Contain("12");
        cut.Markup.Should().Contain("5");
    }

    [Fact]
    public void Renders_ColorDot_ForEachCollection_WithCorrectStyle()
    {
        List<CollectionDisplayState> vms = [MakeVm(1, "Parks", "#22cc44", 3)];

        var cut = RenderComponent<CollectionSidebar>(parameters => parameters
            .Add(p => p.Collections, vms));

        var colorDot = cut.Find("div.w-3.h-3.rounded-full");
        colorDot.GetAttribute("style").Should().Contain("background-color: #22cc44");
    }

    [Fact]
    public void ClickingRow_Fires_OnVisibilityToggled_WithCorrectId()
    {
        // The whole row is a visibility toggle now — clicking it (not just the
        // eye icon) shows/hides that collection. There is no separate
        // "select collection" action.
        int? toggledId = null;
        List<CollectionDisplayState> vms = [MakeVm(7, "Cafes", "#aabbcc", 2)];

        var cut = RenderComponent<CollectionSidebar>(parameters => parameters
            .Add(p => p.Collections, vms)
            .Add(p => p.OnVisibilityToggled,
                EventCallback.Factory.Create<int>(this, id => toggledId = id)));

        cut.Find("div.cursor-pointer").Click();

        toggledId.Should().Be(7);
    }

    [Fact]
    public void Highlights_VisibleCollections_WithBgClass()
    {
        // Visible rows get the "active" background; hidden rows don't.
        List<CollectionDisplayState> vms =
        [
            MakeVm(1, "Shown", "#000000", isVisible: true),
            MakeVm(2, "Hidden", "#ffffff", isVisible: false)
        ];

        var cut = RenderComponent<CollectionSidebar>(parameters => parameters
            .Add(p => p.Collections, vms));

        var rows = cut.FindAll("div.cursor-pointer");
        rows[0].ClassList.Should().Contain("bg-surface-container-high");
        rows[1].ClassList.Should().NotContain("bg-surface-container-high");
    }

    [Fact]
    public void Row_HasToggleSemantics_AriaPressedReflectsVisibility()
    {
        List<CollectionDisplayState> vms =
        [
            MakeVm(1, "Shown", "#000000", isVisible: true),
            MakeVm(2, "Hidden", "#ffffff", isVisible: false)
        ];

        var cut = RenderComponent<CollectionSidebar>(parameters => parameters
            .Add(p => p.Collections, vms));

        var rows = cut.FindAll("div.cursor-pointer");
        rows[0].GetAttribute("role").Should().Be("button");
        rows[0].GetAttribute("aria-pressed").Should().Be("true");
        rows[1].GetAttribute("aria-pressed").Should().Be("false");
    }

    [Fact]
    public void VisibilityIcon_ShowsFilledVsOutline_BasedOnIsVisible()
    {
        List<CollectionDisplayState> vms =
        [
            MakeVm(1, "Visible", "#000000", isVisible: true),
            MakeVm(2, "Hidden", "#ffffff", isVisible: false)
        ];

        var cut = RenderComponent<CollectionSidebar>(parameters => parameters
            .Add(p => p.Collections, vms));

        var visibilityIcons = cut.FindAll("span.material-symbols-outlined.text-base");
        visibilityIcons[0].GetAttribute("style").Should().Contain("'FILL' 1");
        visibilityIcons[1].GetAttribute("style").Should().Contain("'FILL' 0");
    }
}