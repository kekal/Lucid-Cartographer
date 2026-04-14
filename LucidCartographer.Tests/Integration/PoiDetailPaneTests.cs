using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace LucidCartographer.Tests.Integration
{
    [Collection("Integration")]
    public class PoiDetailPaneTests : IntegrationTestBase
    {
        [Fact]
        public async Task DetailPane_ShowsPoisName()
        {
            // Pre-seed with sample.gpx
            await ImportTestFileAsync("sample.gpx", "Test Places", "#b81d17");
            await NavigateAndWaitAsync("/");

            // Click on collection in sidebar to show table
            await Page.Locator(".w-60 .cursor-pointer:has-text('Test Places')").ClickAsync();
            await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });

            // Click on Wawel Castle row to open detail pane
            await Page.Locator("tr:has-text('Wawel Castle')").ClickAsync();
            await Page.WaitForSelectorAsync("h4:has-text('Wawel Castle')", new() { Timeout = 10000 });

            // Detail pane should show POI name
            var poiName = Page.Locator("h4:has-text('Wawel Castle')");
            Assert.True(await poiName.IsVisibleAsync(), "Detail pane should show POI name");
        }

        [Fact]
        public async Task DetailPane_ShowsAddress()
        {
            // Seed a POI with an explicit address so we can assert it's displayed
            await SeedDataAsync(async db =>
            {
                var col = new PoiCollection
                {
                    Name = "Address Places",
                    Color = "#b81d17",
                    IsVisible = true,
                    PoiCount = 1
                };
                db.PoiCollections.Add(col);
                await db.SaveChangesAsync();

                var poi = new Poi
                {
                    Name = "Addressed Spot",
                    Latitude = 50.05,
                    Longitude = 19.95,
                    Address = "123 Main Street, Krakow",
                    AddedDate = DateTime.UtcNow
                };
                db.Pois.Add(poi);
                await db.SaveChangesAsync();

                db.PoiCollectionItems.Add(new PoiCollectionItem
                {
                    PoiId = poi.Id,
                    PoiCollectionId = col.Id
                });
                await db.SaveChangesAsync();
            });

            await NavigateAndWaitAsync("/");
            await Page.WaitForSelectorAsync(".w-60 .cursor-pointer:has-text('Address Places')", new() { Timeout = 10000 });

            // Click on collection to show table
            await Page.Locator(".w-60 .cursor-pointer:has-text('Address Places')").ClickAsync();
            await Page.WaitForSelectorAsync("td:has-text('Addressed Spot')", new() { Timeout = 10000 });

            // Click on the POI row to open detail pane
            await Page.Locator("tr:has-text('Addressed Spot')").ClickAsync();
            await Page.WaitForSelectorAsync("h4:has-text('Addressed Spot')", new() { Timeout = 10000 });

            // Detail pane should show the address
            var addressElement = Page.Locator(".flex.items-start.gap-1\\.5:has(span:has-text('location_on'))");
            Assert.True(await addressElement.IsVisibleAsync(), "Address section should be visible in detail pane");

            // Verify the actual address text is displayed
            var addressText = await addressElement.InnerTextAsync();
            Assert.Contains("123 Main Street, Krakow", addressText);
        }

        [Fact]
        public async Task DetailPane_ShowsCoordinates()
        {
            // Pre-seed with sample.gpx
            await ImportTestFileAsync("sample.gpx", "Test Places", "#b81d17");
            await NavigateAndWaitAsync("/");

            // Click on collection to show table
            await Page.Locator(".w-60 .cursor-pointer:has-text('Test Places')").ClickAsync();
            await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });

            // Click on a POI row
            await Page.Locator("tr:has-text('Wawel Castle')").ClickAsync();
            await Page.WaitForSelectorAsync("h4:has-text('Wawel Castle')", new() { Timeout = 10000 });

            // The detail pane should be visible with the POI name
            var detailPane = Page.Locator("h4:has-text('Wawel Castle')");
            Assert.True(await detailPane.IsVisibleAsync(), "Detail pane should be open");

            // Get the POI data from the table row to verify coordinates exist
            var coordCell = Page.Locator("tr:has-text('Wawel Castle') td.text-xs.text-on-surface-variant.font-mono");
            Assert.True(await coordCell.IsVisibleAsync(), "Coordinates should be shown in table");
        }

        [Fact]
        public async Task DetailPane_ShowsOpenInGoogleMapsButton()
        {
            // Pre-seed with sample.gpx
            await ImportTestFileAsync("sample.gpx", "Test Places", "#b81d17");
            await NavigateAndWaitAsync("/");

            // Click on collection to show table
            await Page.Locator(".w-60 .cursor-pointer:has-text('Test Places')").ClickAsync();
            await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });

            // Click on a POI row
            await Page.Locator("tr:has-text('Wawel Castle')").ClickAsync();
            await Page.WaitForSelectorAsync("h4:has-text('Wawel Castle')", new() { Timeout = 10000 });

            // Detail pane should show "Open in Google Maps" button with href
            var googleMapsLink = Page.Locator("a:has-text('Open in Google Maps')");
            Assert.True(await googleMapsLink.IsVisibleAsync(), "Detail pane should show 'Open in Google Maps' button");

            // Verify href contains google.com/maps
            var href = await googleMapsLink.GetAttributeAsync("href");
            Assert.NotNull(href);
            Assert.Contains("google.com/maps", href);
        }

        [Fact]
        public async Task DetailPane_CloseButton_HidesPane()
        {
            // Pre-seed with sample.gpx
            await ImportTestFileAsync("sample.gpx", "Test Places", "#b81d17");
            await NavigateAndWaitAsync("/");

            // Click on collection to show table
            await Page.Locator(".w-60 .cursor-pointer:has-text('Test Places')").ClickAsync();
            await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });

            // Click on a POI row to open detail pane
            await Page.Locator("tr:has-text('Wawel Castle')").ClickAsync();
            await Page.WaitForSelectorAsync("h4:has-text('Wawel Castle')", new() { Timeout = 10000 });

            // Verify detail pane is visible
            var poiName = Page.Locator("h4:has-text('Wawel Castle')");
            Assert.True(await poiName.IsVisibleAsync(), "Detail pane should be open");

            // Click the close button (in the detail pane header, not the main layout)
            // The detail pane's close button is in the w-80 right pane
            var closeButton = Page.Locator(".w-80 button:has(span:has-text('close'))").First;
            Assert.True(await closeButton.IsVisibleAsync(), "Close button should be visible");
            await closeButton.ClickAsync();
            await Page.WaitForSelectorAsync("h4:has-text('Wawel Castle')", new() { State = WaitForSelectorState.Hidden, Timeout = 10000 });

            // Detail pane should no longer be visible
            Assert.False(await poiName.IsVisibleAsync(), "Detail pane should be hidden after close");

            // POI table should still be visible
            var tableHeader = Page.Locator("span.font-bold:has-text('Filtered Results')");
            Assert.True(await tableHeader.IsVisibleAsync(), "POI table should still be visible");
        }

        [Fact]
        public async Task DetailPane_ShowsGoogleRating_WhenAvailable()
        {
            // Seed data with a POI that has GoogleRating set
            await SeedDataAsync(async db =>
            {
                var col = new PoiCollection
                {
                    Name = "Rated Places",
                    Color = "#b81d17",
                    IsVisible = true,
                    PoiCount = 1
                };
                db.PoiCollections.Add(col);
                await db.SaveChangesAsync();

                var poi = new Poi
                {
                    Name = "Highly Rated Spot",
                    Latitude = 50.05,
                    Longitude = 19.95,
                    GoogleRating = 4.5,
                    ReviewCount = 128,
                    AddedDate = DateTime.UtcNow
                };
                db.Pois.Add(poi);
                await db.SaveChangesAsync();

                db.PoiCollectionItems.Add(new PoiCollectionItem
                {
                    PoiId = poi.Id,
                    PoiCollectionId = col.Id
                });
                await db.SaveChangesAsync();
            });

            await NavigateAndWaitAsync("/");
            await Page.WaitForSelectorAsync(".w-60 .cursor-pointer:has-text('Rated Places')", new() { Timeout = 10000 });

            // Click on collection to show table
            await Page.Locator(".w-60 .cursor-pointer:has-text('Rated Places')").ClickAsync();
            await Page.WaitForSelectorAsync("td:has-text('Highly Rated Spot')", new() { Timeout = 10000 });

            // Click on the POI row to open detail pane
            await Page.Locator("tr:has-text('Highly Rated Spot')").ClickAsync();
            await Page.WaitForSelectorAsync("h4:has-text('Highly Rated Spot')", new() { Timeout = 10000 });

            // Detail pane should show the rating
            var ratingText = Page.Locator("text='4.5'");
            Assert.True(await ratingText.IsVisibleAsync(), "Google rating should be displayed");

            // Should show review count
            var reviewText = Page.Locator("text=/\\(128 reviews\\)/");
            Assert.True(await reviewText.IsVisibleAsync(), "Review count should be displayed");

            // Should show stars
            var stars = Page.Locator("span.material-symbols-outlined:has-text('star')");
            var starCount = await stars.CountAsync();
            Assert.True(starCount > 0, "Star rating should be displayed");
        }
    }
}
