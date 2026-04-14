using Bunit;
using BunitTestContext = Bunit.TestContext;
using FluentAssertions;
using LucidCartographer.Components.Shared;
using LucidCartographer.Data.Entities;
using Microsoft.AspNetCore.Components;

namespace LucidCartographer.Tests.Components
{
    public class PoiDetailPaneTests : BunitTestContext
    {
        private static Poi CreateFullPoi() => new()
        {
            Id = 1,
            Name = "Test Cafe",
            Address = "42 Bean Street",
            Latitude = 51.5074,
            Longitude = -0.1278,
            GoogleRating = 4.3,
            ReviewCount = 1250,
            Website = "https://www.testcafe.com/menu",
            Phone = "+44 20 1234 5678",
            Category = "cafe",
            Status = "visited",
            ImageUrl = "https://example.com/image.jpg",
            GoogleMapsUrl = "https://maps.google.com/?q=testcafe",
            AddedDate = new DateTime(2025, 1, 10)
        };

        [Fact]
        public void Renders_Nothing_WhenPoiIsNull()
        {
            var cut = RenderComponent<PoiDetailPane>(parameters => parameters
                .Add(p => p.Poi, (Poi?)null));

            cut.Markup.Trim().Should().BeEmpty();
        }

        [Fact]
        public void Renders_PoiName_AndAddress()
        {
            var poi = CreateFullPoi();

            var cut = RenderComponent<PoiDetailPane>(parameters => parameters
                .Add(p => p.Poi, poi));

            cut.Markup.Should().Contain("Test Cafe");
            cut.Markup.Should().Contain("42 Bean Street");
        }

        [Fact]
        public void Renders_GoogleRating_WithStarsAndReviewCount()
        {
            var poi = CreateFullPoi();

            var cut = RenderComponent<PoiDetailPane>(parameters => parameters
                .Add(p => p.Poi, poi));

            // Rating value displayed
            cut.Markup.Should().Contain("4.3");
            // Review count with formatted number
            cut.Markup.Should().Contain("1,250");
            cut.Markup.Should().Contain("reviews");
            // Star icons should be rendered (5 stars total)
            var stars = cut.FindAll("span.material-symbols-outlined")
                .Where(s => s.TextContent.Contains("star"))
                .ToList();
            stars.Should().HaveCountGreaterThanOrEqualTo(5);
        }

        [Fact]
        public void Renders_Website_AsClickableLink()
        {
            var poi = CreateFullPoi();

            var cut = RenderComponent<PoiDetailPane>(parameters => parameters
                .Add(p => p.Poi, poi));

            var websiteLink = cut.Find("a[href='https://www.testcafe.com/menu']");
            websiteLink.Should().NotBeNull();
            websiteLink.GetAttribute("target").Should().Be("_blank");
        }

        [Fact]
        public void Renders_PhoneNumber()
        {
            var poi = CreateFullPoi();

            var cut = RenderComponent<PoiDetailPane>(parameters => parameters
                .Add(p => p.Poi, poi));

            cut.Markup.Should().Contain("+44 20 1234 5678");
        }

        [Fact]
        public void Renders_Category_AndStatus_Chips()
        {
            var poi = CreateFullPoi();

            var cut = RenderComponent<PoiDetailPane>(parameters => parameters
                .Add(p => p.Poi, poi));

            cut.Markup.Should().Contain("cafe");
            // Status "visited" is rendered with underscore replaced by space
            cut.Markup.Should().Contain("visited");
        }

        [Fact]
        public void Renders_Image_WhenImageUrlIsProvided()
        {
            var poi = CreateFullPoi();

            var cut = RenderComponent<PoiDetailPane>(parameters => parameters
                .Add(p => p.Poi, poi));

            var img = cut.Find("img");
            img.GetAttribute("src").Should().Be("https://example.com/image.jpg");
            img.GetAttribute("alt").Should().Be("Test Cafe");
        }

        [Fact]
        public void DoesNotRender_ImageSection_WhenImageUrlIsNull()
        {
            var poi = CreateFullPoi();
            poi.ImageUrl = null;

            var cut = RenderComponent<PoiDetailPane>(parameters => parameters
                .Add(p => p.Poi, poi));

            cut.FindAll("img").Should().BeEmpty();
        }

        [Fact]
        public void Renders_Coordinates()
        {
            var poi = CreateFullPoi();

            var cut = RenderComponent<PoiDetailPane>(parameters => parameters
                .Add(p => p.Poi, poi));

            cut.Markup.Should().Contain("51.507400");
            cut.Markup.Should().Contain("-0.127800");
        }

        [Fact]
        public void Renders_OpenInGoogleMaps_Button_WithCorrectHref()
        {
            var poi = CreateFullPoi();

            var cut = RenderComponent<PoiDetailPane>(parameters => parameters
                .Add(p => p.Poi, poi));

            var mapsLink = cut.FindAll("a[target='_blank']")
                .First(a => a.TextContent.Contains("Open in Google Maps"));
            mapsLink.GetAttribute("href").Should().Be("https://maps.google.com/?q=testcafe");
        }

        [Fact]
        public void CloseButton_Fires_OnClose_Callback()
        {
            bool closed = false;
            var poi = CreateFullPoi();

            var cut = RenderComponent<PoiDetailPane>(parameters => parameters
                .Add(p => p.Poi, poi)
                .Add(p => p.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

            // The close button contains the "close" material icon
            var closeButton = cut.FindAll("button")
                .First(b => b.InnerHtml.Contains("close"));
            closeButton.Click();

            closed.Should().BeTrue();
        }
    }
}
