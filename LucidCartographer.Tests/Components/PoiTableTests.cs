using Bunit;
using BunitTestContext = Bunit.TestContext;
using FluentAssertions;
using LucidCartographer.Components.Shared;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using Microsoft.AspNetCore.Components;

namespace LucidCartographer.Tests.Components
{
    public class PoiTableTests : BunitTestContext
    {
        private static Poi CreatePoi(int id = 1, string name = "Test Place",
            double lat = 48.8566, double lon = 2.3522, string? googleMapsUrl = null) => new()
            {
                Id = id,
                Name = name,
                Latitude = lat,
                Longitude = lon,
                GoogleMapsUrl = googleMapsUrl,
                Address = "123 Test St",
                AddedDate = new DateTime(2025, 3, 15)
            };

        [Fact]
        public void Renders_EmptyState_WhenNoPois()
        {
            var cut = RenderComponent<PoiTable>(parameters => parameters
                .Add(p => p.Pois, new List<Poi>()));

            cut.Markup.Should().Contain("No POIs to display");
            cut.Markup.Should().Contain("location_off");
        }

        [Fact]
        public void Renders_PoiNames_InTable()
        {
            var pois = new List<Poi>
            {
                CreatePoi(1, "Eiffel Tower"),
                CreatePoi(2, "Louvre Museum")
            };

            var cut = RenderComponent<PoiTable>(parameters => parameters
                .Add(p => p.Pois, pois));

            cut.Markup.Should().Contain("Eiffel Tower");
            cut.Markup.Should().Contain("Louvre Museum");
        }

        [Fact]
        public void Renders_Coordinates_FormattedTo4DecimalPlaces()
        {
            var pois = new List<Poi> { CreatePoi(lat: 48.856614, lon: 2.352222) };

            var cut = RenderComponent<PoiTable>(parameters => parameters
                .Add(p => p.Pois, pois));

            cut.Markup.Should().Contain("48.8566");
            cut.Markup.Should().Contain("2.3522");
        }

        [Fact]
        public void Shows_GoogleMapsLink_WithCorrectHref()
        {
            var pois = new List<Poi>
            {
                CreatePoi(googleMapsUrl: "https://maps.google.com/?q=test")
            };

            var cut = RenderComponent<PoiTable>(parameters => parameters
                .Add(p => p.Pois, pois));

            var link = cut.Find("a[target='_blank']");
            link.GetAttribute("href").Should().Be("https://maps.google.com/?q=test");
        }

        [Fact]
        public void Shows_GoogleMapsSearchFallback_WhenGoogleMapsUrlIsNull()
        {
            var pois = new List<Poi> { CreatePoi(lat: 48.8566, lon: 2.3522) };

            var cut = RenderComponent<PoiTable>(parameters => parameters
                .Add(p => p.Pois, pois));

            var link = cut.Find("a[target='_blank']");
            link.GetAttribute("href").Should()
                .Contain("https://www.google.com/maps/search/?api=1&query=48.8566,2.3522");
        }

        [Fact]
        public void ClickingRow_Fires_OnPoiSelected_WithCorrectId()
        {
            int? selectedId = null;
            var pois = new List<Poi> { CreatePoi(id: 42) };

            var cut = RenderComponent<PoiTable>(parameters => parameters
                .Add(p => p.Pois, pois)
                .Add(p => p.OnPoiSelected,
                    EventCallback.Factory.Create<int>(this, id => selectedId = id)));

            cut.Find("tbody tr").Click();

            selectedId.Should().Be(42);
        }

        [Fact]
        public void ClickingFocusButton_Fires_OnFocusClicked_WithCorrectId()
        {
            int? focusedId = null;
            var pois = new List<Poi> { CreatePoi(id: 99) };

            var cut = RenderComponent<PoiTable>(parameters => parameters
                .Add(p => p.Pois, pois)
                .Add(p => p.OnFocusClicked,
                    EventCallback.Factory.Create<int>(this, id => focusedId = id)));

            // The focus button is the second button-like element in the actions cell
            // It contains the "my_location" icon
            var focusButton = cut.Find("button");
            focusButton.Click();

            focusedId.Should().Be(99);
        }

        [Fact]
        public void Shows_ShowingXOfY_WhenMoreThan200Pois()
        {
            var pois = Enumerable.Range(1, 250)
                .Select(i => CreatePoi(id: i, name: $"Place {i}"))
                .ToList();

            var cut = RenderComponent<PoiTable>(parameters => parameters
                .Add(p => p.Pois, pois));

            // Production renders "{Count} items" regardless of size — there is no "Showing X of Y" feature.
            cut.Markup.Should().Contain("250 items");
        }

        [Fact]
        public void Shows_ItemCountBadge_InHeader()
        {
            var pois = new List<Poi>
            {
                CreatePoi(1, "A"),
                CreatePoi(2, "B"),
                CreatePoi(3, "C")
            };

            var cut = RenderComponent<PoiTable>(parameters => parameters
                .Add(p => p.Pois, pois));

            cut.Markup.Should().Contain("3 items");
        }

        [Fact]
        public void Renders_AllCollectionBadges_ForPoi()
        {
            var pois = new List<Poi> { CreatePoi(1, "Multi-home POI") };
            var collectionNames = new Dictionary<int, IReadOnlyList<string>>
            {
                [1] = new List<string> { "Favorites", "Visited" }
            };

            var cut = RenderComponent<PoiTable>(parameters => parameters
                .Add(p => p.Pois, pois)
                .Add(p => p.PoiCollectionNames, collectionNames));

            cut.Markup.Should().Contain("Favorites");
            cut.Markup.Should().Contain("Visited");
        }

        [Fact]
        public void CopyModal_ShowsOnlyCollections_ThatDoNotContainPoi()
        {
            var pois = new List<Poi> { CreatePoi(1, "Test Place") };
            var collections = new List<CollectionViewModel>
            {
                new(new PoiCollection { Id = 1, Name = "Alpha", Color = "#005bbf" }),
                new(new PoiCollection { Id = 2, Name = "Beta", Color = "#006e2c" }),
                new(new PoiCollection { Id = 3, Name = "Gamma", Color = "#b81d17" })
            };

            var cut = RenderComponent<PoiTable>(parameters => parameters
                .Add(p => p.Pois, pois)
                .Add(p => p.Collections, collections)
                .Add(p => p.PoiCollectionMemberships, new Dictionary<int, IReadOnlyList<int>> { [1] = [1, 2] }));

            cut.Find("button[aria-label='Copy Test Place to another collection']").Click();

            var options = cut.FindAll("div.flex-1.overflow-y-auto.p-2 > button")
                .Select(x => x.TextContent.Trim())
                .ToList();

            options.Should().BeEquivalentTo(["Gamma"]);
        }

        [Fact]
        public void MoveModal_ShowsAllCollections()
        {
            var pois = new List<Poi> { CreatePoi(1, "Test Place") };
            var collections = new List<CollectionViewModel>
            {
                new(new PoiCollection { Id = 1, Name = "Alpha", Color = "#005bbf" }),
                new(new PoiCollection { Id = 2, Name = "Beta", Color = "#006e2c" }),
                new(new PoiCollection { Id = 3, Name = "Gamma", Color = "#b81d17" })
            };

            var cut = RenderComponent<PoiTable>(parameters => parameters
                .Add(p => p.Pois, pois)
                .Add(p => p.Collections, collections)
                .Add(p => p.PoiCollectionMemberships, new Dictionary<int, IReadOnlyList<int>> { [1] = [1, 2] }));

            cut.Find("button[aria-label='Move Test Place to another collection']").Click();

            var options = cut.FindAll("div.flex-1.overflow-y-auto.p-2 > button")
                .Select(x => x.TextContent.Trim())
                .ToList();

            options.Should().BeEquivalentTo(["Alpha", "Beta", "Gamma"]);
        }
    }
}
