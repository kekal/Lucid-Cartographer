using Microsoft.Playwright;

namespace LucidCartographer.Tests.Integration;

[Collection("Integration")]
public class MobileOperationsTests : MobileTestBase
{
    private async Task SeedBothCollectionsAsync()
    {
        await ImportTestFileAsync("sample.gpx", "GPX Places", "#005bbf");
        await ImportTestFileAsync("sample.kml", "KML Places", "#006e2c");
    }

    [Fact]
    public async Task Mobile_SourcePickers_PopulateWithCollections()
    {
        await SeedBothCollectionsAsync();
        await MobileNavigateAndWaitAsync("/operations");

        // Wait for the operations screen to load collections — the options
        // won't populate until Vm.InitializeAsync completes.
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('.m-app select option:not([value=\"0\"])')",
            null, new PageWaitForFunctionOptions { Timeout = 15000 });

        var selects = Page.Locator(".m-app select");
        var count = await selects.CountAsync();
        Assert.True(count >= 2, "Should have at least 2 select elements");

        var optionsA = await selects.Nth(0).Locator("option").AllInnerTextsAsync();
        var optionsB = await selects.Nth(1).Locator("option").AllInnerTextsAsync();

        Assert.Contains(optionsA, o => o.Contains("GPX Places"));
        Assert.Contains(optionsA, o => o.Contains("KML Places"));
        Assert.Contains(optionsB, o => o.Contains("GPX Places"));
        Assert.Contains(optionsB, o => o.Contains("KML Places"));
    }

    private async Task WaitForCollectionOptionsAsync()
    {
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('.m-app select option:not([value=\"0\"])')",
            null, new PageWaitForFunctionOptions { Timeout = 15000 });
    }

    [Fact]
    public async Task Mobile_Intersect_PopulatesResults()
    {
        // Seed the same file twice so there ARE common POIs for intersection
        await ImportTestFileAsync("sample.gpx", "GPX Set A", "#005bbf");
        await ImportTestFileAsync("sample.gpx", "GPX Set B", "#006e2c");
        await MobileNavigateAndWaitAsync("/operations");
        await WaitForCollectionOptionsAsync();

        var selects = Page.Locator(".m-app select");

        // Select GPX Set A for A
        await selects.Nth(0).SelectOptionAsync(new SelectOptionValue
        {
            Label = (await selects.Nth(0).Locator("option").AllInnerTextsAsync())
                .First(o => o.Contains("GPX Set A"))
        });

        // Select GPX Set B for B
        await selects.Nth(1).SelectOptionAsync(new SelectOptionValue
        {
            Label = (await selects.Nth(1).Locator("option").AllInnerTextsAsync())
                .First(o => o.Contains("GPX Set B"))
        });

        // Click the Intersect card (A ∩ B)
        var intersectCard = Page.Locator(".m-app .m-op-card").Filter(
            new LocatorFilterOptions { HasText = "A ∩ B" });
        await intersectCard.ClickAsync();

        // Wait for result rows — the intersection of identical files should be 3 rows
        // The result list is inside .m-app .list (but not the source picker rows)
        await Page.WaitForFunctionAsync(
            "() => document.querySelectorAll('.m-app .list .row').length > 0",
            null, new PageWaitForFunctionOptions { Timeout = 15000 });
        var resultRows = await Page.Locator(".m-app .list .row").CountAsync();
        Assert.True(resultRows > 0, "Intersection of two identical collections should yield result rows");
    }

    [Fact]
    public async Task Mobile_DiscardRestore_TogglesRowStyle()
    {
        await ImportTestFileAsync("sample.gpx", "GPX Set A", "#005bbf");
        await ImportTestFileAsync("sample.gpx", "GPX Set B", "#006e2c");
        await MobileNavigateAndWaitAsync("/operations");
        await WaitForCollectionOptionsAsync();

        var selects = Page.Locator(".m-app select");
        await selects.Nth(0).SelectOptionAsync(new SelectOptionValue
        {
            Label = (await selects.Nth(0).Locator("option").AllInnerTextsAsync())
                .First(o => o.Contains("GPX Set A"))
        });
        await selects.Nth(1).SelectOptionAsync(new SelectOptionValue
        {
            Label = (await selects.Nth(1).Locator("option").AllInnerTextsAsync())
                .First(o => o.Contains("GPX Set B"))
        });

        var intersectCard = Page.Locator(".m-app .m-op-card").Filter(
            new LocatorFilterOptions { HasText = "A ∩ B" });
        await intersectCard.ClickAsync();

        // Wait for result rows
        await Page.WaitForSelectorAsync(".m-app .list .row", new() { Timeout = 15000 });

        var firstRow = Page.Locator(".m-app .list .row").First;
        // Before discard: no opacity/line-through
        var styleBefore = await firstRow.GetAttributeAsync("style");
        Assert.True(
            string.IsNullOrEmpty(styleBefore) || !styleBefore.Contains("opacity:0.4"),
            "Row should not have discard styling before tap");

        // Tap to discard — use DispatchEvent to bypass the tab bar overlay
        await firstRow.DispatchEventAsync("click");
        // M07: wait for the discard style to actually land instead of sleeping
        // a fixed window. The row's inline style flips both opacity and the
        // text-decoration in one Blazor render.
        await Page.WaitForFunctionAsync(@"() => {
            const r = document.querySelector('.m-app .list .row');
            const s = r ? r.getAttribute('style') || '' : '';
            return s.includes('opacity:0.4') && s.includes('line-through');
        }", null, new PageWaitForFunctionOptions { Timeout = 5000 });

        // M08: assert BOTH style halves so a buggy restore that clears one but
        // not the other is caught.
        var styleAfter = await firstRow.GetAttributeAsync("style");
        Assert.NotNull(styleAfter);
        Assert.Contains("opacity:0.4", styleAfter);
        Assert.Contains("line-through", styleAfter);

        // Tap again to restore
        await firstRow.DispatchEventAsync("click");
        // M07: wait for the discard style to be fully cleared on restore.
        await Page.WaitForFunctionAsync(@"() => {
            const r = document.querySelector('.m-app .list .row');
            const s = r ? r.getAttribute('style') || '' : '';
            return !s.includes('opacity:0.4') && !s.includes('line-through');
        }", null, new PageWaitForFunctionOptions { Timeout = 5000 });

        // M08: assert BOTH style halves are cleared.
        var styleRestored = await firstRow.GetAttributeAsync("style");
        Assert.True(
            string.IsNullOrEmpty(styleRestored) || !styleRestored.Contains("opacity:0.4"),
            "Row should not have opacity discard styling after restore");
        Assert.True(
            string.IsNullOrEmpty(styleRestored) || !styleRestored.Contains("line-through"),
            "Row should not have line-through discard styling after restore");
    }

    [Fact]
    public async Task Mobile_Commit_SavesNewCollection()
    {
        await ImportTestFileAsync("sample.gpx", "GPX Set A", "#005bbf");
        await ImportTestFileAsync("sample.gpx", "GPX Set B", "#006e2c");
        await MobileNavigateAndWaitAsync("/operations");
        await WaitForCollectionOptionsAsync();

        var selects = Page.Locator(".m-app select");
        await selects.Nth(0).SelectOptionAsync(new SelectOptionValue
        {
            Label = (await selects.Nth(0).Locator("option").AllInnerTextsAsync())
                .First(o => o.Contains("GPX Set A"))
        });
        await selects.Nth(1).SelectOptionAsync(new SelectOptionValue
        {
            Label = (await selects.Nth(1).Locator("option").AllInnerTextsAsync())
                .First(o => o.Contains("GPX Set B"))
        });

        var intersectCard = Page.Locator(".m-app .m-op-card").Filter(
            new LocatorFilterOptions { HasText = "A ∩ B" });
        await intersectCard.ClickAsync();

        // Wait for result rows
        await Page.WaitForSelectorAsync(".m-app .list .row", new() { Timeout = 15000 });

        // Click "Commit to Layer"
        var commitBtn = Page.Locator(".m-app .btn").Filter(
            new LocatorFilterOptions { HasText = "Commit to Layer" });
        await commitBtn.ClickAsync();

        // The commit modal should appear
        var commitModal = Page.Locator(".modal-screen");
        await commitModal.WaitForAsync(new() { Timeout = 5000, State = WaitForSelectorState.Visible });

        // Fill in the collection name
        var nameInput = Page.Locator(".modal-screen .field").First;
        await nameInput.FillAsync("Mobile Intersect Result");

        // Click Save
        var saveBtn = Page.Locator(".modal-screen .btn-primary").Filter(
            new LocatorFilterOptions { HasText = "Save" });
        await saveBtn.ClickAsync();

        // H10: previously the wait keyed on "points" which is statically
        // rendered the instant the commit dialog opens. Anchor on the actual
        // success-state string the VM sets after DoCommitAsync completes:
        // `Saved "{name}" with {N} POIs` (see OperationsPageViewModel.cs).
        await Page.WaitForSelectorAsync(".modal-screen", new() { Timeout = 10000 });
        await Page.WaitForFunctionAsync(
            "() => { const el = document.querySelector('.modal-screen'); return el && el.innerText.includes('Saved \"'); }",
            null, new PageWaitForFunctionOptions { Timeout = 10000 });
        var modalText = await Page.Locator(".modal-screen").InnerTextAsync();
        Assert.Contains("Saved \"", modalText);

        // Close the commit modal by clicking the back button in the modal head
        var modalBackBtn = Page.Locator(".modal-screen .icon-btn").First;
        await modalBackBtn.DispatchEventAsync("click");
        await Page.Locator(".modal-screen").WaitForAsync(new()
        {
            Timeout = 5000,
            State = WaitForSelectorState.Hidden
        });

        // Navigate to datasources via direct URL (to avoid overlay interception)
        await Page.GotoAsync($"{BaseUrl}/datasources", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 15000
        });
        // Wait for mobile layout on datasources (circuit is fresh; ViewportService needs re-init)
        // Since we navigated away, use the SPA workaround: root first, then tab
        await Page.GotoAsync($"{BaseUrl}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 15000
        });
        await Page.WaitForSelectorAsync(".m-tabbar", new() { Timeout = 15000 });
        await Page.Locator(".m-tabbar a").Nth(1).ClickAsync();
        await Page.WaitForURLAsync("**/datasources", new() { Timeout = 10000 });
        await Page.WaitForSelectorAsync(".m-app", new() { Timeout = 15000 });

        // Wait specifically for collection rows (not POI rows) on datasources
        // Collection rows have the "section-title" ancestor near them
        await Page.WaitForSelectorAsync(".m-app .section-title", new() { Timeout = 10000 });
        // The collection list appears after the "Managed sources" section title
        await Page.WaitForFunctionAsync(
            "() => { const rows = document.querySelectorAll('.m-app .list .row'); return rows.length > 0 && rows[0].innerText.includes('pts'); }",
            null, new PageWaitForFunctionOptions { Timeout = 10000 });

        var rows = Page.Locator(".m-app .list .row");
        var allTexts = await rows.AllInnerTextsAsync();
        Assert.Contains(allTexts, t => t.Contains("Mobile Intersect Result"));
    }
}
