namespace LucidCartographer.Tests.Integration
{
    [Collection("Integration")]
    public class MapIntegrationTests : IntegrationTestBase
    {
        private async Task SeedGpxDataAsync()
        {
            await ImportTestFileAsync("sample.gpx", "Test Places", "#005bbf");
        }

        [Fact]
        public async Task MapPage_ShowsCollectionInSidebar()
        {
            await SeedGpxDataAsync();
            await NavigateAndWaitAsync("/");

            Assert.True(await Page.Locator(".w-60 .truncate:has-text('Test Places')").IsVisibleAsync(),
                "Collection name should appear in the sidebar");
        }

        [Fact]
        public async Task Sidebar_ShowsPointCount()
        {
            await SeedGpxDataAsync();
            await NavigateAndWaitAsync("/");

            // The sidebar shows the point count next to the collection name
            var sidebarText = await Page.Locator(".w-60 .cursor-pointer:has-text('Test Places')").First.InnerTextAsync();
            Assert.Contains("3", sidebarText);
        }

        [Fact]
        public async Task TogglingVisibility_ChangesEyeIcon()
        {
            await SeedGpxDataAsync();
            await NavigateAndWaitAsync("/");

            // Find the visibility toggle button in the sidebar row for "Test Places"
            var visibilityButton = Page.Locator(".w-60 .cursor-pointer:has-text('Test Places') button:has(span:has-text('visibility'))").First;
            Assert.True(await visibilityButton.IsVisibleAsync(), "Visibility toggle should be visible");

            // Click to toggle off — the icon FILL changes from 1 to 0
            await visibilityButton.ClickAsync();
            // Wait for re-render by checking the icon style attribute changed
            await Page.WaitForTimeoutAsync(500);

            var iconStyle = await Page.Locator(".w-60 .cursor-pointer:has-text('Test Places') span:has-text('visibility')").First.GetAttributeAsync("style");
            Assert.NotNull(iconStyle);
            Assert.Contains("0", iconStyle);
        }

        [Fact]
        public async Task ClickingCollection_ShowsPoisInBottomTable()
        {
            await SeedGpxDataAsync();
            await NavigateAndWaitAsync("/");

            // Click on the collection name in the sidebar
            await Page.Locator(".w-60 .cursor-pointer:has-text('Test Places')").ClickAsync();
            await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });

            // The bottom table should show POI names
            Assert.True(await Page.Locator("td:has-text('Wawel Castle')").IsVisibleAsync(),
                "Wawel Castle should appear in the bottom table");
            Assert.True(await Page.Locator("td:has-text('Palace of Culture and Science')").IsVisibleAsync(),
                "Palace of Culture and Science should appear in the bottom table");
            Assert.True(await Page.Locator("td:has-text('Wrocław Market Square')").IsVisibleAsync(),
                "Wrocław Market Square should appear in the bottom table");
        }

        [Fact]
        public async Task BottomTable_ShowsOpenInGoogleMapsLinks()
        {
            await SeedGpxDataAsync();
            await NavigateAndWaitAsync("/");

            await Page.Locator(".w-60 .cursor-pointer:has-text('Test Places')").ClickAsync();
            await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });

            // The PoiTable has links with open_in_new icon pointing to Google Maps
            var googleMapsLinks = Page.Locator("a[href*='google.com/maps']");
            var linkCount = await googleMapsLinks.CountAsync();
            Assert.True(linkCount >= 3, $"Expected at least 3 Google Maps links, found {linkCount}");
        }

        [Fact]
        public async Task ClickingPoiRow_OpensDetailPane()
        {
            await SeedGpxDataAsync();
            await NavigateAndWaitAsync("/");

            await Page.Locator(".w-60 .cursor-pointer:has-text('Test Places')").ClickAsync();
            await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });

            // Click on Wawel Castle row in the table
            await Page.Locator("tr:has-text('Wawel Castle')").ClickAsync();
            await Page.WaitForSelectorAsync("h4:has-text('Wawel Castle')", new() { Timeout = 10000 });

            // Detail pane should appear with the POI name and Google Maps button
            Assert.True(await Page.Locator("h4:has-text('Wawel Castle')").IsVisibleAsync(),
                "Detail pane should show the POI name");
            Assert.True(await Page.Locator("a:has-text('Open in Google Maps')").IsVisibleAsync(),
                "Detail pane should show 'Open in Google Maps' button");
        }

        [Fact]
        public async Task FitAllButton_IsVisibleWhenCollectionsAreVisible()
        {
            await SeedGpxDataAsync();
            await NavigateAndWaitAsync("/");
            await Page.WaitForSelectorAsync("button:has-text('Fit All')", new() { Timeout = 10000 });

            // "Fit All" button should be visible when at least one collection is visible
            Assert.True(await Page.Locator("button:has-text('Fit All')").IsVisibleAsync(),
                "'Fit All' button should be visible when collections are shown");
        }
    }
}
