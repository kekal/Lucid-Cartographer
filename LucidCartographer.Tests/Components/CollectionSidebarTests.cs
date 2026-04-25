using Bunit;
using BunitTestContext = Bunit.TestContext;
using FluentAssertions;
using LucidCartographer.Components.Shared;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using Microsoft.AspNetCore.Components;

namespace LucidCartographer.Tests.Components
{
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
                .Add(p => p.Collections, new List<CollectionDisplayState>()));

            cut.Markup.Should().Contain("No collections yet.");
            cut.Markup.Should().Contain("Import data via Data Sources tab.");
        }

        [Fact]
        public void Renders_CollectionNames_WithCorrectCount()
        {
            var vms = new List<CollectionDisplayState>
            {
                MakeVm(1, "Restaurants", "#ff0000", 12),
                MakeVm(2, "Hotels", "#00ff00", 5)
            };

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
            var vms = new List<CollectionDisplayState> { MakeVm(1, "Parks", "#22cc44", 3) };

            var cut = RenderComponent<CollectionSidebar>(parameters => parameters
                .Add(p => p.Collections, vms));

            var colorDot = cut.Find("div.w-3.h-3.rounded-full");
            colorDot.GetAttribute("style").Should().Contain("background-color: #22cc44");
        }

        [Fact]
        public void ClickingCollection_Fires_OnCollectionSelected_WithCorrectId()
        {
            int? selectedId = null;
            var vms = new List<CollectionDisplayState> { MakeVm(7, "Cafes", "#aabbcc", 2) };

            var cut = RenderComponent<CollectionSidebar>(parameters => parameters
                .Add(p => p.Collections, vms)
                .Add(p => p.OnCollectionSelected,
                    EventCallback.Factory.Create<int>(this, id => selectedId = id)));

            cut.Find("div.cursor-pointer").Click();

            selectedId.Should().Be(7);
        }

        [Fact]
        public void ClickingVisibilityIcon_Fires_OnVisibilityToggled_WithCorrectId()
        {
            int? toggledId = null;
            var vms = new List<CollectionDisplayState> { MakeVm(3, "Museums", "#112233", 1, true) };

            var cut = RenderComponent<CollectionSidebar>(parameters => parameters
                .Add(p => p.Collections, vms)
                .Add(p => p.OnVisibilityToggled,
                    EventCallback.Factory.Create<int>(this, id => toggledId = id)));

            cut.Find("button").Click();

            toggledId.Should().Be(3);
        }

        [Fact]
        public void Highlights_SelectedCollection_WithBgClass()
        {
            var vms = new List<CollectionDisplayState>
            {
                MakeVm(1, "Selected", "#000000"),
                MakeVm(2, "Other", "#ffffff")
            };

            var cut = RenderComponent<CollectionSidebar>(parameters => parameters
                .Add(p => p.Collections, vms)
                .Add(p => p.SelectedCollectionId, 1));

            var rows = cut.FindAll("div.cursor-pointer");
            rows[0].ClassList.Should().Contain("bg-surface-container-high");
            rows[1].ClassList.Should().NotContain("bg-surface-container-high");
        }

        [Fact]
        public void VisibilityIcon_ShowsFilledVsOutline_BasedOnIsVisible()
        {
            var vms = new List<CollectionDisplayState>
            {
                MakeVm(1, "Visible", "#000000", isVisible: true),
                MakeVm(2, "Hidden", "#ffffff", isVisible: false)
            };

            var cut = RenderComponent<CollectionSidebar>(parameters => parameters
                .Add(p => p.Collections, vms));

            var visibilityIcons = cut.FindAll("span.material-symbols-outlined.text-base");
            visibilityIcons[0].GetAttribute("style").Should().Contain("'FILL' 1");
            visibilityIcons[1].GetAttribute("style").Should().Contain("'FILL' 0");
        }
    }
}
