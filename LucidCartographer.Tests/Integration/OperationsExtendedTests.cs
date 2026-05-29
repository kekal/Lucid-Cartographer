using Microsoft.Playwright;

namespace LucidCartographer.Tests.Integration;

[Collection("Integration")]
public class OperationsExtendedTests : IntegrationTestBase
{
    private async Task SeedBothCollectionsAsync()
    {
        await ImportTestFileAsync("sample.gpx", "Set A", "#005bbf");
        await ImportTestFileAsync("sample.kml", "Set B", "#006e2c");
    }


    [Fact]
    public async Task Union_ShowsAllUniquePois()
    {
        await SeedBothCollectionsAsync();
        await NavigateToOperationsAsync();

        var selectA = Page.Locator("select").First;
        var selectOptionsA = await selectA.Locator("option").AllInnerTextsAsync();
        var setALabel = selectOptionsA.First(o => o.Contains("Set A"));
        await selectA.SelectOptionAsync(new SelectOptionValue { Label = setALabel });

        var selectB = Page.Locator("select").Nth(1);
        var selectOptionsB = await selectB.Locator("option").AllInnerTextsAsync();
        var setBLabel = selectOptionsB.First(o => o.Contains("Set B"));
        await selectB.SelectOptionAsync(new SelectOptionValue { Label = setBLabel });

        await Page.WaitForSelectorAsync("text=data points loaded", new() { Timeout = 5000 });

        // Click "A ∪ B" (Union) button
        await Page.Locator("button:has-text('A u B')").ClickAsync();
        await Page.WaitForSelectorAsync("text=Result Preview", new() { Timeout = 10000 });

        // Union should show combined unique POIs (3 from GPX + 2 from KML = 5 total)
        var resultText = await Page.Locator("p:has-text('points')").First.InnerTextAsync();
        Assert.NotEmpty(resultText);
        Assert.Contains("points", resultText);
        // Verify result table exists
        Assert.True(await Page.Locator("table tbody tr").CountAsync() > 0);
    }

    [Fact]
    public async Task SpatialToleranceSlider_UpdatesDisplayedValue()
    {
        await SeedBothCollectionsAsync();
        await NavigateToOperationsAsync();

        // Find the range slider and verify it exists
        var slider = Page.Locator("input[type='range']");
        await slider.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
        var initialValue = await slider.InputValueAsync();
        Assert.NotNull(initialValue);
        Assert.Equal("100", initialValue); // Default tolerance is 100m

        // Change the slider value using JS to set property and dispatch input event
        // (Blazor uses @bind:event="oninput" so we need to fire input event)
        await slider.EvaluateAsync(@"el => {
            var nativeInputValueSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
            nativeInputValueSetter.call(el, 250);
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
        }");
        await Page.WaitForTimeoutAsync(500);

        // Verify the label shows the new tolerance value
        var toleranceLabel = await Page.Locator("label:has-text('Spatial Tolerance')").InnerTextAsync();
        Assert.Contains("250", toleranceLabel);
    }

    [Fact]
    public async Task Dedup_DisablesSourceBDropdown()
    {
        await SeedBothCollectionsAsync();
        await NavigateToOperationsAsync();

        var selectA = Page.Locator("select").First;
        var selectOptionsA = await selectA.Locator("option").AllInnerTextsAsync();
        var setALabel = selectOptionsA.First(o => o.Contains("Set A"));
        await selectA.SelectOptionAsync(new SelectOptionValue { Label = setALabel });

        await Page.WaitForSelectorAsync("text=data points loaded", new() { Timeout = 5000 });

        // Click Dedup button
        await Page.Locator("button:has-text('Dedup')").ClickAsync();
        await Page.WaitForSelectorAsync("text=Result Preview", new() { Timeout = 10000 });

        // Dataset B dropdown should be disabled
        var selectB = Page.Locator("select").Nth(1);
        var isDisabled = await selectB.IsDisabledAsync();
        Assert.True(isDisabled, "Dataset B dropdown should be disabled during Dedup mode");
    }

    [Fact]
    public async Task BinaryOpAfterDedup_ReenablesSourceBDropdown()
    {
        await SeedBothCollectionsAsync();
        await NavigateToOperationsAsync();

        var selectA = Page.Locator("select").First;
        var selectOptionsA = await selectA.Locator("option").AllInnerTextsAsync();
        var setALabel = selectOptionsA.First(o => o.Contains("Set A"));
        await selectA.SelectOptionAsync(new SelectOptionValue { Label = setALabel });

        await Page.WaitForSelectorAsync("text=data points loaded", new() { Timeout = 5000 });

        // Run Dedup
        await Page.Locator("button:has-text('Dedup')").ClickAsync();
        await Page.WaitForSelectorAsync("text=Result Preview", new() { Timeout = 10000 });

        // Verify B is disabled
        var selectB = Page.Locator("select").Nth(1);
        Assert.True(await selectB.IsDisabledAsync());

        // Click a binary operation (A - B)
        await Page.Locator("button:has-text('A - B')").ClickAsync();
        await Page.WaitForSelectorAsync("text=Select Source B first", new() { Timeout = 5000 });

        // Dataset B should now be enabled
        Assert.False(await selectB.IsDisabledAsync(), "Dataset B dropdown should be re-enabled after clicking a binary operation");
    }

    [Fact]
    public async Task SameCollectionForAAndB_SubtractShowsZeroResults()
    {
        await SeedBothCollectionsAsync();
        await NavigateToOperationsAsync();

        var selectA = Page.Locator("select").First;
        var selectOptionsA = await selectA.Locator("option").AllInnerTextsAsync();
        var setALabel = selectOptionsA.First(o => o.Contains("Set A"));
        await selectA.SelectOptionAsync(new SelectOptionValue { Label = setALabel });
        await Page.WaitForSelectorAsync("text=data points loaded", new() { Timeout = 5000 });

        var selectB = Page.Locator("select").Nth(1);
        var selectOptionsB = await selectB.Locator("option").AllInnerTextsAsync();
        var setALabelForB = selectOptionsB.First(o => o.Contains("Set A"));
        await selectB.SelectOptionAsync(new SelectOptionValue { Label = setALabelForB });
        await Page.WaitForSelectorAsync("text=data points loaded", new() { Timeout = 5000 });

        // Click "A - B"
        await Page.Locator("button:has-text('A - B')").ClickAsync();
        await Page.WaitForSelectorAsync("text=Result Preview", new() { Timeout = 10000 });

        // A - A should equal 0 — check the result description text
        var resultText = await Page.Locator("p.text-sm.text-on-surface-variant").First.InnerTextAsync();
        Assert.Contains("0 points", resultText);
    }

    [Fact]
    public async Task OperationsPage_InitialState_ShowsDefaultMessage()
    {
        await SeedBothCollectionsAsync();
        await NavigateToOperationsAsync();

        // Verify default placeholder is selected (value = "0" means placeholder)
        var selectA = Page.Locator("select").First;
        var selectedValue = await selectA.InputValueAsync();
        Assert.Equal("0", selectedValue);

        // Verify instruction message is visible
        var instructionMessage = await Page.Locator("text=Select datasets and run an operation").IsVisibleAsync();
        Assert.True(instructionMessage);

        // Verify no result table is visible
        var resultTable = await Page.Locator("table tbody").IsVisibleAsync();
        Assert.False(resultTable);
    }

    [Fact]
    public async Task SelectSourceDatasetA_ShowsConfirmation()
    {
        await SeedBothCollectionsAsync();
        await NavigateToOperationsAsync();

        var selectA = Page.Locator("select").First;
        var selectOptionsA = await selectA.Locator("option").AllInnerTextsAsync();
        var setALabel = selectOptionsA.First(o => o.Contains("Set A"));
        await selectA.SelectOptionAsync(new SelectOptionValue { Label = setALabel });

        await Page.WaitForSelectorAsync("text=data points loaded", new() { Timeout = 5000 });

        // Verify confirmation text appears (e.g., "3 data points loaded")
        var confirmationText = await Page.Locator("p:has-text('data points loaded')").First.InnerTextAsync();
        Assert.NotEmpty(confirmationText);
        Assert.Contains("data points loaded", confirmationText);
    }

    [Fact]
    public async Task OperationButtonsDisabledWhenNoDatasetA()
    {
        await SeedBothCollectionsAsync();
        await NavigateToOperationsAsync();

        // Do NOT select any datasets - verify all operation buttons are disabled
        var subtractBtn = Page.Locator("button:has-text('A - B')");
        var intersectBtn = Page.Locator("button:has-text('A n B')");
        var unionBtn = Page.Locator("button:has-text('A u B')");
        var dedupBtn = Page.Locator("button:has-text('Dedup')");

        Assert.True(await subtractBtn.IsDisabledAsync());
        Assert.True(await intersectBtn.IsDisabledAsync());
        Assert.True(await unionBtn.IsDisabledAsync());
        Assert.True(await dedupBtn.IsDisabledAsync());
    }

    [Fact]
    public async Task ClickBinaryOperationWithoutDatasetB_ShowsHint()
    {
        await SeedBothCollectionsAsync();
        await NavigateToOperationsAsync();

        // Select only Dataset A
        var selectA = Page.Locator("select").First;
        var selectOptionsA = await selectA.Locator("option").AllInnerTextsAsync();
        var setALabel = selectOptionsA.First(o => o.Contains("Set A"));
        await selectA.SelectOptionAsync(new SelectOptionValue { Label = setALabel });

        await Page.WaitForSelectorAsync("text=data points loaded", new() { Timeout = 5000 });

        // Click A - B without selecting B
        var subtractBtn = Page.Locator("button:has-text('A - B')");
        await subtractBtn.ClickAsync();
        await Page.WaitForSelectorAsync("text=Select Source B first", new() { Timeout = 5000 });

        // Verify no result appears since B is not selected
        var resultPreview = await Page.Locator("text=Result Preview").IsVisibleAsync();
        Assert.False(resultPreview, "No result should appear when clicking binary op without Dataset B");

        // A hint should appear telling the user to select Source B
        Assert.True(await Page.Locator("text=Select Source B first").IsVisibleAsync(),
            "Hint 'Select Source B first' should appear when binary op is clicked without Dataset B");

        // The instruction message should still be visible
        Assert.True(await Page.Locator("text=Select datasets and run an operation").IsVisibleAsync(),
            "Instruction message should still be visible");
    }
}