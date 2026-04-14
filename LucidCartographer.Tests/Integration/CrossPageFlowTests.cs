using Microsoft.Playwright;

namespace LucidCartographer.Tests.Integration
{
    /// <summary>
    /// End-to-end lifecycle tests covering navigation across pages and complete workflows.
    /// </summary>
    [Collection("Integration")]
    public class CrossPageFlowTests : IntegrationTestBase
    {
        private async Task NavigateToMapAsync()
        {
            await NavigateAndWaitAsync("/");
            // LOW-06: Map element ID is now dynamically generated (leaflet-map-{guid})
            await Page.WaitForSelectorAsync("div[id^='leaflet-map-']", new() { State = WaitForSelectorState.Attached, Timeout = 10000 });
        }

        /// <summary>
        /// Test: Import-to-Map flow
        /// Navigate to Data Sources → upload sample.gpx → navigate to Map → verify collection in sidebar + POIs in table
        /// </summary>
        [Fact]
        public async Task ImportToMapFlow_ImportsGpxAndShowsOnMap()
        {
            await NavigateToDataSourcesAsync();

            // Open file upload card
            await Page.Locator("h3:has-text('KML/GPX Upload')").ClickAsync();
            await Page.WaitForSelectorAsync("h3:has-text('Import File')", new() { Timeout = 5000 });

            // Fill collection name and upload
            await Page.Locator("input[placeholder*='Poland']").FillAsync("Test GPX Collection");
            await Page.Locator("input[placeholder*='Poland']").PressAsync("Tab");

            var filePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "sample.gpx");
            await Page.Locator("input[type='file']").SetInputFilesAsync(filePath);
            await Page.WaitForSelectorAsync("span.font-medium:has-text('Import complete')", new() { Timeout = 15000 });

            // Navigate to Map
            await NavigateToMapAsync();

            // Verify collection appears in sidebar
            var sidebarCollection = Page.Locator(".w-60 .cursor-pointer:has-text('Test GPX Collection')");
            Assert.True(await sidebarCollection.IsVisibleAsync(),
                "Collection should appear in the sidebar");

            // Click collection in sidebar to load POIs in table
            await sidebarCollection.ClickAsync();
            await Page.WaitForSelectorAsync("td:has-text('Wawel Castle')", new() { Timeout = 10000 });

            // Verify POIs appear in the table
            var poiRows = Page.Locator("tbody tr");
            var count = await poiRows.CountAsync();
            Assert.True(count >= 3, $"At least 3 POIs should be visible in the table, got {count}");

            // Verify specific POI names are in the table (from sample.gpx)
            Assert.True(await Page.Locator("td:has-text('Wawel Castle')").IsVisibleAsync(),
                "POI 'Wawel Castle' should be visible in the table");
        }

        /// <summary>
        /// Test: Import-to-Operations flow
        /// Upload two files on Data Sources → navigate to Operations → both collections in dropdowns → run subtract → verify results
        /// </summary>
        [Fact]
        public async Task ImportToOperationsFlow_ImportsAndRunsOperation()
        {
            await NavigateToDataSourcesAsync();

            // Import first file (GPX)
            await Page.Locator("h3:has-text('KML/GPX Upload')").ClickAsync();
            await Page.WaitForSelectorAsync("h3:has-text('Import File')", new() { Timeout = 5000 });
            await Page.Locator("input[placeholder*='Poland']").FillAsync("GPX Collection");
            await Page.Locator("input[placeholder*='Poland']").PressAsync("Tab");
            var gpxPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "sample.gpx");
            await Page.Locator("input[type='file']").SetInputFilesAsync(gpxPath);
            await Page.WaitForSelectorAsync("span.font-medium:has-text('Import complete')", new() { Timeout = 15000 });

            // Import second file (KML) — reopen the card
            await Page.Locator("h3:has-text('KML/GPX Upload')").ClickAsync();
            await Page.WaitForSelectorAsync("h3:has-text('Import File')", new() { Timeout = 5000 });
            await Page.Locator("input[placeholder*='Poland']").FillAsync("KML Collection");
            await Page.Locator("input[placeholder*='Poland']").PressAsync("Tab");
            var kmlPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "sample.kml");
            await Page.Locator("input[type='file']").SetInputFilesAsync(kmlPath);
            await Page.WaitForSelectorAsync("span.font-medium:has-text('Import complete')", new() { Timeout = 15000 });

            // Navigate to Operations
            await NavigateToOperationsAsync();

            // Verify both collections appear in dropdowns
            var dropdownA = Page.Locator("select").First;
            var dropdownAOptions = await dropdownA.Locator("option").AllInnerTextsAsync();
            var dropdownAText = string.Join(",", dropdownAOptions);
            Assert.Contains("GPX Collection", dropdownAText);
            Assert.Contains("KML Collection", dropdownAText);

            // Select GPX as A and KML as B using label matching (options have "GPX Collection (3 pts)" format)
            var gpxLabel = dropdownAOptions.First(o => o.Contains("GPX Collection"));
            await dropdownA.SelectOptionAsync(new SelectOptionValue { Label = gpxLabel });
            await Page.WaitForSelectorAsync("text=data points loaded", new() { Timeout = 5000 });

            var dropdownB = Page.Locator("select").Nth(1);
            var dropdownBOptions = await dropdownB.Locator("option").AllInnerTextsAsync();
            var kmlLabel = dropdownBOptions.First(o => o.Contains("KML Collection"));
            await dropdownB.SelectOptionAsync(new SelectOptionValue { Label = kmlLabel });
            await Page.WaitForSelectorAsync("text=data points loaded", new() { Timeout = 5000 });

            // Click A - B (Subtract) operation button
            await Page.Locator("button:has-text('A - B')").ClickAsync();
            await Page.WaitForSelectorAsync("text=Result Preview", new() { Timeout = 10000 });

            // Verify results appear
            Assert.True(await Page.Locator("text=Result Preview").IsVisibleAsync(),
                "Result Preview should be visible");
        }

        /// <summary>
        /// Test: Search lifecycle
        /// Import data → search for a POI name → verify result → navigate to Map tab → verify normal view restored (no search filter)
        /// </summary>
        [Fact]
        public async Task SearchLifecycle_SearchRestoresAfterNavigation()
        {
            // Seed data
            await ImportTestFileAsync("sample.gpx", "Search Test Collection", "#005bbf");

            // Navigate to Map
            await NavigateToMapAsync();

            // Perform a search by filling the search input in the header
            var searchInput = Page.Locator("input[aria-label='Search POIs...']");
            await searchInput.FillAsync("Wawel");
            await searchInput.PressAsync("Enter");

            // The form submits with data-enhance="false", causing full navigation to /?search=Wawel
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Page.WaitForSelectorAsync("text=Wawel Castle", new() { Timeout = 10000 });

            // Verify search results are displayed (use .First to avoid strict mode on multiple matches)
            Assert.True(await Page.Locator("text=Wawel Castle").First.IsVisibleAsync(),
                "Searched POI should be visible");

            // Navigate away to another tab (e.g., Data Sources) and back to Map
            await Page.Locator("nav a:has-text('Data Sources')").ClickAsync();
            await Page.WaitForURLAsync("**/datasources");
            await Page.WaitForSelectorAsync("h2:has-text('Data & Imports')", new() { Timeout = 10000 });

            // Navigate back to Map (clicking the nav link navigates to "/" without search param)
            await Page.Locator("nav a:has-text('Map')").ClickAsync();
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Page.WaitForSelectorAsync("nav a", new() { Timeout = 10000 });

            // Verify the collection is visible in sidebar after navigation
            var sidebarCol = Page.Locator(".w-60 .text-sm.font-medium.truncate:has-text('Search Test Collection')");
            Assert.True(await sidebarCol.IsVisibleAsync(),
                "Collection should be visible in sidebar after navigation");

            // Search input should be empty after navigating back without search param
            var freshSearchInput = Page.Locator("input[aria-label='Search POIs...']");
            var searchValue = await freshSearchInput.InputValueAsync();
            Assert.True(string.IsNullOrEmpty(searchValue),
                $"Search input should be empty after navigation, got: '{searchValue}'");
        }
    }
}
