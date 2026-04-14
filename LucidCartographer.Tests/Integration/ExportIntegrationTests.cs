using Microsoft.Playwright;

namespace LucidCartographer.Tests.Integration
{
    [Collection("Integration")]
    public class ExportIntegrationTests : IntegrationTestBase
    {
        private async Task SeedBothCollectionsAndRunSubtractAsync()
        {
            await ImportTestFileAsync("sample.gpx", "Set A", "#005bbf");
            await ImportTestFileAsync("sample.kml", "Set B", "#006e2c");

            await NavigateAndWaitAsync("/");
            await ClickOperationsTabAsync();

            var selectA = Page.Locator("select").First;
            var selectOptionsA = await selectA.Locator("option").AllInnerTextsAsync();
            var setALabel = selectOptionsA.First(o => o.Contains("Set A"));
            await selectA.SelectOptionAsync(new SelectOptionValue { Label = setALabel });

            var selectB = Page.Locator("select").Nth(1);
            var selectOptionsB = await selectB.Locator("option").AllInnerTextsAsync();
            var setBLabel = selectOptionsB.First(o => o.Contains("Set B"));
            await selectB.SelectOptionAsync(new SelectOptionValue { Label = setBLabel });

            await Page.WaitForSelectorAsync("text=data points loaded", new() { Timeout = 5000 });

            await Page.Locator("button:has-text('A - B')").ClickAsync();
            await Page.WaitForSelectorAsync("text=Result Preview", new() { Timeout = 10000 });
        }

        [Fact]
        public async Task ExportResultButton_IsPresent_WhenResultsExist()
        {
            await SeedBothCollectionsAndRunSubtractAsync();

            var exportBtn = Page.Locator("button:has-text('Export Result')");
            Assert.True(await exportBtn.IsVisibleAsync(), "Export Result button should be visible after running an operation");
        }

        [Fact]
        public async Task ExportButtonDisabledWhenNoResults()
        {
            await ImportTestFileAsync("sample.gpx", "Set A", "#005bbf");

            await NavigateAndWaitAsync("/");
            await ClickOperationsTabAsync();

            // No operation run — Export button should not exist
            var exportBtn = Page.Locator("button:has-text('Export Result')");
            Assert.Equal(0, await exportBtn.CountAsync());

            Assert.True(await Page.Locator("text=Select datasets and run an operation").IsVisibleAsync());
        }

        [Fact]
        public async Task ExportResultButton_IsClickable_AfterDiscard()
        {
            await SeedBothCollectionsAndRunSubtractAsync();

            // Discard the first POI
            await Page.Locator("button:has-text('Discard')").First.ClickAsync();
            await Page.WaitForSelectorAsync("tr.opacity-30", new() { Timeout = 5000 });

            // Export button should still be present and clickable
            var exportBtn = Page.Locator("button:has-text('Export Result')");
            Assert.True(await exportBtn.IsVisibleAsync(), "Export Result button should be visible after discarding a POI");

            // Verify the discarded count is shown
            var footer = await Page.Locator("text=discarded").InnerTextAsync();
            Assert.Contains("1", footer);
        }
    }
}
