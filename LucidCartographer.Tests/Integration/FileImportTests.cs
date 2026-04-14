namespace LucidCartographer.Tests.Integration
{
    /// <summary>
    /// Integration tests for file import functionality (GeoJSON, CSV, KML, GPX).
    /// Tests the KML/GPX Upload card with various file formats.
    /// </summary>
    [Collection("Integration")]
    public class FileImportTests : IntegrationTestBase
    {
        private async Task UploadFileAsync(string fileName, string collectionName)
        {
            // Click KML/GPX Upload card
            await Page.Locator("h3:has-text('KML/GPX Upload')").ClickAsync();
            await Page.WaitForSelectorAsync("h3:has-text('Import File')", new() { Timeout = 5000 });

            // Fill collection name
            var collectionInput = Page.Locator("input[placeholder*='Poland']");
            await collectionInput.FillAsync(collectionName);
            await collectionInput.PressAsync("Tab");

            // Upload file
            var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", fileName);
            await Page.Locator("input[type='file']").SetInputFilesAsync(filePath);

            // Wait for import to complete
            await Page.WaitForSelectorAsync("span.font-medium:has-text('Import complete')", new() { Timeout = 15000 });
        }

        [Fact]
        public async Task UploadGeoJsonFile_ImportsCorrectly()
        {
            await NavigateAndWaitAsync("/");
            await ClickDataSourcesTabAsync();
            await UploadFileAsync("sample.geojson", "GeoJSON Places");

            // Verify "2" appears in result (sample.geojson has 2 POIs)
            var resultText = await Page.Locator("span:has-text('Import complete')").Locator("..").Locator("..").InnerTextAsync();
            Assert.Contains("2", resultText);

            // Verify collection appears in managed sources table
            await Page.WaitForSelectorAsync("td span.font-medium:has-text('GeoJSON Places')", new() { Timeout = 5000 });
            Assert.True(await Page.Locator("td span.font-medium:has-text('GeoJSON Places')").IsVisibleAsync(),
                "GeoJSON collection should appear in managed sources");
            Assert.True(await Page.Locator("td:has-text('2')").First.IsVisibleAsync(),
                "Point count of 2 should appear in the table");
        }

        [Fact]
        public async Task UploadCsvFile_ImportsCorrectly()
        {
            await NavigateAndWaitAsync("/");
            await ClickDataSourcesTabAsync();
            await UploadFileAsync("sample.csv", "CSV Places");

            // Verify "3" appears in result (sample.csv has 3 POIs: Zakopane, Gdansk, Malbork)
            var resultText = await Page.Locator("span:has-text('Import complete')").Locator("..").Locator("..").InnerTextAsync();
            Assert.Contains("3", resultText);

            // Verify collection appears in managed sources table
            await Page.WaitForSelectorAsync("td span.font-medium:has-text('CSV Places')", new() { Timeout = 5000 });
            Assert.True(await Page.Locator("td span.font-medium:has-text('CSV Places')").IsVisibleAsync(),
                "CSV collection should appear in managed sources");
        }

        [Fact]
        public async Task UploadKmlFile_ImportsCorrectly()
        {
            await NavigateAndWaitAsync("/");
            await ClickDataSourcesTabAsync();
            await UploadFileAsync("sample.kml", "KML Places");

            // Verify "2" appears in result (sample.kml has 2 POIs)
            var resultText = await Page.Locator("span:has-text('Import complete')").Locator("..").Locator("..").InnerTextAsync();
            Assert.Contains("2", resultText);

            // Verify collection appears in managed sources table
            await Page.WaitForSelectorAsync("td span.font-medium:has-text('KML Places')", new() { Timeout = 5000 });
            Assert.True(await Page.Locator("td span.font-medium:has-text('KML Places')").IsVisibleAsync(),
                "KML collection should appear in managed sources");
        }

        [Fact]
        public async Task UploadEmptyGpx_ShowsZeroPoisImported()
        {
            await NavigateAndWaitAsync("/");
            await ClickDataSourcesTabAsync();
            await UploadFileAsync("empty.gpx", "Empty Collection");

            // Verify "0" appears in result
            var resultText = await Page.Locator("span:has-text('Import complete')").Locator("..").Locator("..").InnerTextAsync();
            Assert.Contains("0", resultText);

            // Collection may or may not appear in table (either is acceptable, but import should not crash)
            // The key test is that it completes without error
            Assert.True(await Page.Locator("span:has-text('Import complete')").IsVisibleAsync(),
                "Empty file should complete import without crashing");
        }

        [Fact]
        public async Task UploadCorruptFile_ShowsErrorMessage()
        {
            await NavigateAndWaitAsync("/");
            await ClickDataSourcesTabAsync();

            // Click KML/GPX Upload card
            await Page.Locator("h3:has-text('KML/GPX Upload')").ClickAsync();
            await Page.WaitForSelectorAsync("h3:has-text('Import File')", new() { Timeout = 5000 });

            // Fill collection name
            var collectionInput = Page.Locator("input[placeholder*='Poland']");
            await collectionInput.FillAsync("Bad File");
            await collectionInput.PressAsync("Tab");

            // Upload corrupt file
            var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "corrupt.gpx");
            await Page.Locator("input[type='file']").SetInputFilesAsync(filePath);

            // Wait for error message to appear — a corrupt file should show "Import failed"
            await Page.WaitForSelectorAsync("span.font-medium:has-text('Import failed')", new() { Timeout = 10000 });

            Assert.True(await Page.Locator("span.font-medium:has-text('Import failed')").IsVisibleAsync(),
                "Error message should be displayed for corrupt file");
            Assert.False(await Page.Locator("span.font-medium:has-text('Import complete')").IsVisibleAsync(),
                "Success message should not appear for corrupt file");
        }
    }
}
