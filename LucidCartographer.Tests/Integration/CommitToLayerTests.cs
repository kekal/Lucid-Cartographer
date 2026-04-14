using Microsoft.Playwright;

namespace LucidCartographer.Tests.Integration
{
    [Collection("Integration")]
    public class CommitToLayerTests : IntegrationTestBase
    {
        private async Task SeedBothCollectionsAndRunSubtractAsync()
        {
            await ImportTestFileAsync("sample.gpx", "Set A", "#005bbf");
            await ImportTestFileAsync("sample.kml", "Set B", "#006e2c");

            await NavigateAndWaitAsync("/");
            await Page.Locator("nav a:has-text('Operations')").ClickAsync();
            await Page.WaitForURLAsync("**/operations");
            await Page.WaitForSelectorAsync("h3:has-text('Source Selection')", new() { Timeout = 10000 });

            // Select both datasets
            var selectA = Page.Locator("select").First;
            var selectOptionsA = await selectA.Locator("option").AllInnerTextsAsync();
            var setALabel = selectOptionsA.First(o => o.Contains("Set A"));
            await selectA.SelectOptionAsync(new SelectOptionValue { Label = setALabel });

            var selectB = Page.Locator("select").Nth(1);
            var selectOptionsB = await selectB.Locator("option").AllInnerTextsAsync();
            var setBLabel = selectOptionsB.First(o => o.Contains("Set B"));
            await selectB.SelectOptionAsync(new SelectOptionValue { Label = setBLabel });

            await Page.WaitForSelectorAsync("text=data points loaded", new() { Timeout = 5000 });

            // Run subtract operation
            await Page.Locator("button:has-text('A - B')").ClickAsync();
            await Page.WaitForSelectorAsync("text=Result Preview", new() { Timeout = 10000 });
        }

        [Fact]
        public async Task CommitDialog_ShowsNameInputPrefilledWithOperationLabel()
        {
            await SeedBothCollectionsAndRunSubtractAsync();

            // Click "Commit to Layer"
            await Page.Locator("button:has-text('Commit to Layer')").ClickAsync();
            await Page.WaitForSelectorAsync("text=Save as new collection", new() { Timeout = 5000 });

            // Verify dialog is visible
            var dialogTitle = await Page.Locator("text=Save as new collection").IsVisibleAsync();
            Assert.True(dialogTitle, "Commit dialog should be visible");

            // Verify name input is pre-filled with operation label
            var nameInput = Page.Locator("input[placeholder*='Filtered']");
            var inputValue = await nameInput.InputValueAsync();
            Assert.NotNull(inputValue);
            Assert.NotEmpty(inputValue);
            // Should contain operation label like "A − B" and dataset name
            Assert.True(inputValue.Contains("A") || inputValue.Contains("Set"),
                $"Name input should be pre-filled with operation label, got: {inputValue}");
        }

        [Fact]
        public async Task AfterSavingCommit_SuccessMessageAppears()
        {
            await SeedBothCollectionsAndRunSubtractAsync();

            // Click "Commit to Layer"
            await Page.Locator("button:has-text('Commit to Layer')").ClickAsync();
            await Page.WaitForSelectorAsync("text=Save as new collection", new() { Timeout = 5000 });

            // Fill in the name and save
            var nameInput = Page.Locator("input[placeholder*='Filtered']");
            await nameInput.ClearAsync();
            await nameInput.FillAsync("Test Committed Collection");
            await nameInput.PressAsync("Tab");
            await Page.Locator("button:has-text('Save')").ClickAsync();

            // Wait for success message to appear
            await Page.WaitForSelectorAsync("text=Saved", new() { Timeout = 10000 });

            // Verify success message contains the collection name
            var successText = await Page.Locator("p:has-text('Saved')").InnerTextAsync();
            Assert.Contains("Test Committed Collection", successText);
        }

        [Fact]
        public async Task CommittedCollection_AppearsInOperationsDropdowns()
        {
            await SeedBothCollectionsAndRunSubtractAsync();

            // Click "Commit to Layer"
            await Page.Locator("button:has-text('Commit to Layer')").ClickAsync();
            await Page.WaitForSelectorAsync("text=Save as new collection", new() { Timeout = 5000 });

            // Fill in the name and save
            var nameInput = Page.Locator("input[placeholder*='Filtered']");
            await nameInput.ClearAsync();
            await nameInput.FillAsync("My New Collection");
            await Page.Locator("button:has-text('Save')").ClickAsync();
            await Page.WaitForSelectorAsync("text=Saved", new() { Timeout = 10000 });

            // Re-navigate to Operations to refresh dropdowns
            await Page.Locator("nav a:has-text('Operations')").ClickAsync();
            await Page.WaitForURLAsync("**/operations");
            await Page.WaitForSelectorAsync("h3:has-text('Source Selection')", new() { Timeout = 10000 });

            // Check if the new collection appears in dropdown A
            var selectA = Page.Locator("select").First;
            var optionsA = await selectA.Locator("option").AllInnerTextsAsync();
            Assert.Contains(optionsA, o => o.Contains("My New Collection"));

            // Check if the new collection appears in dropdown B
            var selectB = Page.Locator("select").Nth(1);
            var optionsB = await selectB.Locator("option").AllInnerTextsAsync();
            Assert.Contains(optionsB, o => o.Contains("My New Collection"));
        }

        [Fact]
        public async Task CancelButton_ClosesDialogWithoutSaving()
        {
            await SeedBothCollectionsAndRunSubtractAsync();

            // Click "Commit to Layer"
            await Page.Locator("button:has-text('Commit to Layer')").ClickAsync();
            await Page.WaitForSelectorAsync("text=Save as new collection", new() { Timeout = 5000 });

            // Verify dialog is visible
            var dialog = await Page.Locator("text=Save as new collection").IsVisibleAsync();
            Assert.True(dialog);

            // Click Cancel button
            await Page.Locator("button:has-text('Cancel')").ClickAsync();
            await Page.WaitForSelectorAsync("text=Save as new collection", new() { State = WaitForSelectorState.Hidden, Timeout = 5000 });

            // Verify dialog is closed
            var dialogAfterCancel = await Page.Locator("text=Save as new collection").IsVisibleAsync();
            Assert.False(dialogAfterCancel, "Dialog should be closed after clicking Cancel");

            // Verify result table is still visible
            var resultTable = await Page.Locator("table tbody").IsVisibleAsync();
            Assert.True(resultTable, "Result table should still be visible after cancel");

            // Verify no success message appears (check for "Saved" text with quotes)
            var successMessage = await Page.Locator("p:has-text('Saved')").IsVisibleAsync();
            Assert.False(successMessage, "No success message should appear after cancel");
        }

        [Fact]
        public async Task CommitToLayerButtonDisabledWhenNoResults()
        {
            await ImportTestFileAsync("sample.gpx", "Set A", "#005bbf");
            await ImportTestFileAsync("sample.kml", "Set B", "#006e2c");

            await NavigateAndWaitAsync("/");
            await Page.Locator("nav a:has-text('Operations')").ClickAsync();
            await Page.WaitForURLAsync("**/operations");
            await Page.WaitForSelectorAsync("h3:has-text('Source Selection')", new() { Timeout = 10000 });

            // Do NOT run any operation

            // When no operation has been run, the result section (including Commit to Layer button)
            // is not rendered at all. Verify the button does not exist.
            var commitBtn = Page.Locator("button:has-text('Commit to Layer')");
            var count = await commitBtn.CountAsync();
            Assert.Equal(0, count);

            // Verify the instruction message is shown instead
            Assert.True(await Page.Locator("text=Select datasets and run an operation").IsVisibleAsync(),
                "Instruction message should be shown when no operation has been run");
        }

        [Fact]
        public async Task CommitExcludesDiscardedPois()
        {
            await SeedBothCollectionsAndRunSubtractAsync();

            // Discard the first POI
            await Page.Locator("button:has-text('Discard')").First.ClickAsync();
            await Page.WaitForSelectorAsync("tr.opacity-30", new() { Timeout = 5000 });

            // Count visible POIs before commit
            var visiblePoisBefore = await Page.Locator("tbody tr:not(.opacity-30)").CountAsync();

            // Click "Commit to Layer"
            await Page.Locator("button:has-text('Commit to Layer')").ClickAsync();
            await Page.WaitForSelectorAsync("text=Save as new collection", new() { Timeout = 5000 });

            // Fill in the name and save
            var nameInput = Page.Locator("input[placeholder*='Filtered']");
            await nameInput.ClearAsync();
            await nameInput.FillAsync("Filtered Result");
            await Page.Locator("button:has-text('Save')").ClickAsync();
            await Page.WaitForSelectorAsync("text=Saved", new() { Timeout = 10000 });

            // Verify success message includes correct POI count
            var successText = await Page.Locator("p:has-text('Saved')").InnerTextAsync();
            Assert.Contains("Filtered Result", successText);
            // The success message should reflect the count of non-discarded POIs
            Assert.Contains(visiblePoisBefore.ToString(), successText);
        }
    }
}
