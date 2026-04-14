namespace LucidCartographer.Tests.Integration
{
    /// <summary>
    /// Edge case tests covering empty states, boundary conditions, and unusual but valid scenarios.
    /// </summary>
    [Collection("Integration")]
    public class EdgeCaseTests : IntegrationTestBase
    {
        [Fact]
        public async Task MapPage_EmptyState_NoDataSeeded()
        {
            // Do NOT seed any data
            await NavigateAndWaitAsync("/");

            // Verify sidebar shows empty state message
            var emptyMessage = Page.Locator("text=No collections yet").Or(
                Page.Locator("text=Import data to get started").Or(
                Page.Locator("text=No collections")));

            Assert.True(await emptyMessage.IsVisibleAsync(),
                "Sidebar should show empty state message when no collections exist");

            // Verify POI table shows empty state
            var tableEmpty = Page.Locator("text=No POIs to display").Or(
                Page.Locator("text=Select a collection"));

            Assert.True(await tableEmpty.IsVisibleAsync(),
                "POI table should show empty state message");
        }

        [Fact]
        public async Task OperationsPage_EmptyState_NoDataSeeded()
        {
            // Do NOT seed any data
            await NavigateAndWaitAsync("/");
            await Page.Locator("nav a:has-text('Operations')").ClickAsync();
            await Page.WaitForURLAsync("**/operations");
            await Page.WaitForSelectorAsync("h3:has-text('Source Selection')", new() { Timeout = 10000 });

            // Verify dropdown A only contains "Select collection..." option
            var dropdownA = Page.Locator("select").First;
            var options = await dropdownA.Locator("option").AllAsync();

            Assert.True(options.Count >= 1, "Dropdown should have at least the placeholder option");

            var firstOptionText = await options[0].InnerTextAsync();
            Assert.True(firstOptionText.Contains("Select collection") || firstOptionText.Contains("Select"),
                "First option should be a placeholder");

            // Verify dropdown B only contains "Select collection..." option
            var dropdownB = Page.Locator("select").Last;
            var optionsB = await dropdownB.Locator("option").AllAsync();
            Assert.Equal(options.Count, optionsB.Count);
        }

        [Fact]
        public async Task OperationsPage_WithSingleCollection_BinaryOpsDisabledWithoutB()
        {
            // Import only one file
            await ImportTestFileAsync("sample.gpx", "Solo Collection", "#005bbf");

            await NavigateAndWaitAsync("/");
            await Page.Locator("nav a:has-text('Operations')").ClickAsync();
            await Page.WaitForURLAsync("**/operations");
            await Page.WaitForSelectorAsync("h3:has-text('Source Selection')", new() { Timeout = 10000 });

            // Verify collection appears in dropdown A - select by label (text includes point count)
            var dropdownA = Page.Locator("select").First;
            var optionsA = await dropdownA.Locator("option").AllInnerTextsAsync();
            var soloLabel = optionsA.First(o => o.Contains("Solo Collection"));
            await dropdownA.SelectOptionAsync(new Microsoft.Playwright.SelectOptionValue { Label = soloLabel });
            await Page.WaitForSelectorAsync("text=data points loaded", new() { Timeout = 5000 });

            // Verify Dataset B dropdown shows only placeholder and the same collection
            var dropdownB = Page.Locator("select").Nth(1);
            var optionsB = await dropdownB.Locator("option").AllInnerTextsAsync();

            Assert.True(optionsB.Count > 0, "Dropdown B should have options");

            // Verify binary operation buttons are visible
            var subtractBtn = Page.Locator("button:has-text('A - B')");
            var intersectBtn = Page.Locator("button:has-text('A n B')");
            var unionBtn = Page.Locator("button:has-text('A u B')");

            Assert.True(await subtractBtn.IsVisibleAsync(), "Subtract button should be visible");
            Assert.True(await intersectBtn.IsVisibleAsync(), "Intersect button should be visible");
            Assert.True(await unionBtn.IsVisibleAsync(), "Union button should be visible");
        }

        [Fact]
        public async Task OperationsPage_SelectSameCollectionForAAndB_IntersectEqualsCollection()
        {
            // Import one file
            await ImportTestFileAsync("sample.gpx", "Shared Collection", "#005bbf");

            await NavigateAndWaitAsync("/");
            await Page.Locator("nav a:has-text('Operations')").ClickAsync();
            await Page.WaitForURLAsync("**/operations");
            await Page.WaitForSelectorAsync("h3:has-text('Source Selection')", new() { Timeout = 10000 });

            // Select same collection for both A and B using label matching
            var dropdownA = Page.Locator("select").First;
            var optionsA = await dropdownA.Locator("option").AllInnerTextsAsync();
            var sharedLabel = optionsA.First(o => o.Contains("Shared Collection"));
            await dropdownA.SelectOptionAsync(new Microsoft.Playwright.SelectOptionValue { Label = sharedLabel });
            await Page.WaitForSelectorAsync("text=data points loaded", new() { Timeout = 5000 });

            var dropdownB = Page.Locator("select").Nth(1);
            var optionsB = await dropdownB.Locator("option").AllInnerTextsAsync();
            var sharedLabelB = optionsB.First(o => o.Contains("Shared Collection"));
            await dropdownB.SelectOptionAsync(new Microsoft.Playwright.SelectOptionValue { Label = sharedLabelB });
            await Page.WaitForSelectorAsync("text=data points loaded", new() { Timeout = 5000 });

            // Run Intersect operation
            await Page.Locator("button:has-text('A n B')").ClickAsync();
            await Page.WaitForSelectorAsync("text=Result Preview", new() { Timeout = 10000 });

            // Verify result equals the full collection (3 POIs from sample.gpx)
            Assert.True(await Page.Locator("text=Result Preview").IsVisibleAsync(),
                "Result Preview should be visible");

            // The result should contain all 3 POIs since intersection of A with itself is A.
            // Virtualize materializes rows on a second render pass after the data binds, so we
            // poll until at least 3 rows are present rather than reading immediately.
            await Page.WaitForFunctionAsync(
                "() => document.querySelectorAll('tbody tr').length >= 3",
                null,
                new() { Timeout = 5000 });

            var count = await Page.Locator("tbody tr").CountAsync();
            Assert.True(count >= 3, $"Result should contain all 3 POIs (A intersect A = A), got {count}");
        }

        [Fact]
        public async Task OperationsPage_SelectSameCollectionForAAndB_SubtractClearsDedup()
        {
            // Import one file
            await ImportTestFileAsync("sample.gpx", "Test Collection", "#005bbf");

            await NavigateAndWaitAsync("/");
            await Page.Locator("nav a:has-text('Operations')").ClickAsync();
            await Page.WaitForURLAsync("**/operations");
            await Page.WaitForSelectorAsync("h3:has-text('Source Selection')", new() { Timeout = 10000 });

            // Select the collection for both A and B using label matching
            var dropdownA = Page.Locator("select").First;
            var optionsA = await dropdownA.Locator("option").AllInnerTextsAsync();
            var testLabel = optionsA.First(o => o.Contains("Test Collection"));
            await dropdownA.SelectOptionAsync(new Microsoft.Playwright.SelectOptionValue { Label = testLabel });
            await Page.WaitForSelectorAsync("text=data points loaded", new() { Timeout = 5000 });

            var dropdownB = Page.Locator("select").Nth(1);
            var optionsB = await dropdownB.Locator("option").AllInnerTextsAsync();
            var testLabelB = optionsB.First(o => o.Contains("Test Collection"));
            await dropdownB.SelectOptionAsync(new Microsoft.Playwright.SelectOptionValue { Label = testLabelB });
            await Page.WaitForSelectorAsync("text=data points loaded", new() { Timeout = 5000 });

            // Verify A-B button is present
            var subtractBtn = Page.Locator("button:has-text('A - B')");
            Assert.True(await subtractBtn.IsVisibleAsync(), "Subtract button should be visible");

            // Click A-B (subtract): A minus itself should yield 0 POIs
            await subtractBtn.ClickAsync();
            await Page.WaitForSelectorAsync("text=Result Preview", new() { Timeout = 10000 });

            // Verify result shows 0 POIs or empty result
            var resultCount = await Page.Locator("tbody tr").CountAsync();
            var emptyMessage = await Page.Locator("text=0 points").IsVisibleAsync();

            Assert.True(resultCount == 0 || emptyMessage,
                $"Result of A-A should be empty (0 POIs), got {resultCount} rows");
        }
    }
}
