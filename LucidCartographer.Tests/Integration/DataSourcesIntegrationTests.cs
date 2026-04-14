using Microsoft.Playwright;

namespace LucidCartographer.Tests.Integration
{
    [Collection("Integration")]
    public class DataSourcesIntegrationTests : IntegrationTestBase
    {
        [Fact]
        public async Task DataSourcesPage_ShowsThreeImportCards()
        {
            await NavigateToDataSourcesAsync();

            Assert.True(await Page.Locator("h3:has-text('KML/GPX Upload')").IsVisibleAsync(),
                "KML/GPX Upload card should be visible");
            Assert.True(await Page.Locator("h3:has-text('Google Takeout')").IsVisibleAsync(),
                "Google Takeout card should be visible");
            Assert.True(await Page.Locator("h3:has-text('Shared Google List')").IsVisibleAsync(),
                "Shared Google List card should be visible");
        }

        [Fact]
        public async Task ClickingKmlGpxCard_OpensUploadPanel()
        {
            await NavigateToDataSourcesAsync();

            await Page.Locator("h3:has-text('KML/GPX Upload')").ClickAsync();
            await Page.WaitForSelectorAsync("h3:has-text('Import File')", new() { Timeout = 5000 });

            Assert.True(await Page.Locator("h3:has-text('Import File')").IsVisibleAsync(),
                "Upload panel with 'Import File' title should appear");
            Assert.True(await Page.Locator("input[type='file']").IsVisibleAsync(),
                "File input should be visible in the upload panel");
        }

        [Fact]
        public async Task FileUploadWithGpx_ImportsThreePois()
        {
            await NavigateToDataSourcesAsync();

            // Open the file upload card
            await Page.Locator("h3:has-text('KML/GPX Upload')").ClickAsync();
            await Page.WaitForSelectorAsync("h3:has-text('Import File')", new() { Timeout = 5000 });

            // Fill collection name (Tab to trigger @bind change event)
            await Page.Locator("input[placeholder*='Poland']").FillAsync("Test GPX Collection");
            await Page.Locator("input[placeholder*='Poland']").PressAsync("Tab");

            // Upload the GPX file
            var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "sample.gpx");
            await Page.Locator("input[type='file']").SetInputFilesAsync(filePath);

            // Wait for import to complete
            await Page.WaitForSelectorAsync("span.font-medium:has-text('Import complete')", new() { Timeout = 15000 });

            // Verify "3" new POIs added
            var resultText = await Page.Locator("span:has-text('Import complete')").Locator("..").Locator("..").InnerTextAsync();
            Assert.Contains("3", resultText);
        }

        [Fact]
        public async Task AfterImport_ManagedSourcesTableShowsCollection()
        {
            await NavigateToDataSourcesAsync();

            // Open the file upload card and import
            await Page.Locator("h3:has-text('KML/GPX Upload')").ClickAsync();
            await Page.WaitForSelectorAsync("h3:has-text('Import File')", new() { Timeout = 5000 });
            await Page.Locator("input[placeholder*='Poland']").FillAsync("My GPX Places");
            await Page.Locator("input[placeholder*='Poland']").PressAsync("Tab");

            var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "sample.gpx");
            await Page.Locator("input[type='file']").SetInputFilesAsync(filePath);
            await Page.WaitForSelectorAsync("span.font-medium:has-text('Import complete')", new() { Timeout = 15000 });

            // Wait for the managed sources table to show the collection
            await Page.WaitForSelectorAsync("td span.font-medium:has-text('My GPX Places')", new() { Timeout = 5000 });

            // Check the managed sources table
            Assert.True(await Page.Locator("td span.font-medium:has-text('My GPX Places')").IsVisibleAsync(),
                "Collection name should appear in the managed sources table");
            Assert.True(await Page.Locator("td:has-text('3')").First.IsVisibleAsync(),
                "Point count of 3 should appear in the table");
        }

        [Fact]
        public async Task ImportTwoFiles_TableShowsTwoCollections()
        {
            await NavigateToDataSourcesAsync();

            // Import first file (GPX)
            await Page.Locator("h3:has-text('KML/GPX Upload')").ClickAsync();
            await Page.WaitForSelectorAsync("h3:has-text('Import File')", new() { Timeout = 5000 });
            await Page.Locator("input[placeholder*='Poland']").FillAsync("GPX Places");
            await Page.Locator("input[placeholder*='Poland']").PressAsync("Tab");
            var gpxPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "sample.gpx");
            await Page.Locator("input[type='file']").SetInputFilesAsync(gpxPath);
            await Page.WaitForSelectorAsync("span.font-medium:has-text('Import complete')", new() { Timeout = 15000 });

            // Import second file (KML) — reopen the card
            await Page.Locator("h3:has-text('KML/GPX Upload')").ClickAsync();
            await Page.WaitForSelectorAsync("h3:has-text('Import File')", new() { Timeout = 5000 });
            await Page.Locator("input[placeholder*='Poland']").FillAsync("KML Places");
            await Page.Locator("input[placeholder*='Poland']").PressAsync("Tab");
            var kmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "sample.kml");
            await Page.Locator("input[type='file']").SetInputFilesAsync(kmlPath);
            await Page.WaitForSelectorAsync("span.font-medium:has-text('Import complete')", new() { Timeout = 15000 });

            // Wait for the managed sources table to update
            await Page.WaitForSelectorAsync("td span.font-medium:has-text('KML Places')", new() { Timeout = 5000 });

            // Both collections should show in the table
            Assert.True(await Page.Locator("td span.font-medium:has-text('GPX Places')").IsVisibleAsync(),
                "First collection should be in the table");
            Assert.True(await Page.Locator("td span.font-medium:has-text('KML Places')").IsVisibleAsync(),
                "Second collection should be in the table");

            var datasetCountText = await Page.Locator("span:has-text('dataset(s)')").InnerTextAsync();
            Assert.Contains("2", datasetCountText);
        }

        [Fact]
        public async Task DeleteCollection_RemovesItFromTable()
        {
            // Pre-seed a collection
            await ImportTestFileAsync("sample.gpx", "To Delete", "#005bbf");
            await NavigateToDataSourcesAsync();

            // Verify it exists
            Assert.True(await Page.Locator("text=To Delete").IsVisibleAsync(),
                "Collection should exist before deletion");

            // HIGH-03: Click delete button shows confirmation, then click "Yes" to confirm
            await Page.Locator("tr:has-text('To Delete') button:has(span:has-text('delete'))").ClickAsync();
            await Page.WaitForSelectorAsync("tr:has-text('To Delete') button:has-text('Yes')", new() { Timeout = 5000 });
            await Page.Locator("tr:has-text('To Delete') button:has-text('Yes')").ClickAsync();
            await Page.WaitForSelectorAsync("text=To Delete", new() { State = WaitForSelectorState.Hidden, Timeout = 10000 });

            // Verify it is gone
            Assert.False(await Page.Locator("text=To Delete").IsVisibleAsync(),
                "Collection should be removed after deletion");
        }

        [Fact]
        public async Task GoogleTakeoutCard_ShowsInstructionsWithTakeoutUrl()
        {
            await NavigateToDataSourcesAsync();

            await Page.Locator("h3:has-text('Google Takeout')").ClickAsync();
            await Page.WaitForSelectorAsync("text=takeout.google.com", new() { Timeout = 5000 });

            Assert.True(await Page.Locator("text=takeout.google.com").IsVisibleAsync(),
                "Instructions should mention takeout.google.com");
        }

        [Fact]
        public async Task SharedGoogleListCard_ShowsUrlInputField()
        {
            await NavigateToDataSourcesAsync();

            await Page.Locator("h3:has-text('Shared Google List')").ClickAsync();
            await Page.WaitForSelectorAsync("input[placeholder*='maps.app.goo.gl']", new() { Timeout = 5000 });

            Assert.True(await Page.Locator("input[placeholder*='maps.app.goo.gl']").IsVisibleAsync(),
                "URL input field should be visible for shared Google list");
        }

        [Fact]
        public async Task CollectionName_AutoFillsFromFilename()
        {
            await NavigateToDataSourcesAsync();

            await Page.Locator("h3:has-text('KML/GPX Upload')").ClickAsync();
            await Page.WaitForSelectorAsync("h3:has-text('Import File')", new() { Timeout = 5000 });

            // Clear the collection name and upload — Blazor auto-fills from filename
            await Page.Locator("input[placeholder*='Poland']").FillAsync("");
            await Page.Locator("input[placeholder*='Poland']").PressAsync("Tab");

            var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "sample.gpx");
            await Page.Locator("input[type='file']").SetInputFilesAsync(filePath);
            await Page.WaitForSelectorAsync("span.font-medium:has-text('Import complete')", new() { Timeout = 15000 });

            // The collection should be named "sample" (from filename without extension)
            Assert.True(await Page.Locator("text=sample").First.IsVisibleAsync(),
                "Collection name should auto-fill from the uploaded filename");
        }

        [Fact]
        public async Task ColorPicker_HasEightCircles()
        {
            await NavigateToDataSourcesAsync();

            await Page.Locator("h3:has-text('KML/GPX Upload')").ClickAsync();
            await Page.WaitForSelectorAsync("h3:has-text('Import File')", new() { Timeout = 5000 });

            // Color picker circles are round buttons under the "Color" label
            var colorButtons = Page.Locator("button.rounded-full[style*='background-color']");
            var count = await colorButtons.CountAsync();
            Assert.Equal(8, count);
        }
    }
}
