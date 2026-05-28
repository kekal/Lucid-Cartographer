using Microsoft.Playwright;

namespace LucidCartographer.Tests.Integration;

[Collection("Integration")]
public class MobileSourcesTests : MobileTestBase
{
    [Fact]
    public async Task Mobile_FourImportCards_Render()
    {
        await MobileNavigateAndWaitAsync("/datasources");

        // Wait for the mobile sources screen to appear
        await Page.WaitForSelectorAsync(".m-app .m-import-card", new() { Timeout = 15000 });

        var cards = Page.Locator(".m-app .m-import-card");
        var count = await cards.CountAsync();
        Assert.Equal(4, count);

        var texts = await cards.AllInnerTextsAsync();
        Assert.Contains(texts, t => t.Contains("File upload"));
        Assert.Contains(texts, t => t.Contains("Google Takeout"));
        Assert.Contains(texts, t => t.Contains("Shared list"));
        Assert.Contains(texts, t => t.Contains("Single POI"));
    }

    [Fact]
    public async Task Mobile_TapFileCard_OpensImportModal()
    {
        await MobileNavigateAndWaitAsync("/datasources");

        // Wait for the import cards
        await Page.WaitForSelectorAsync(".m-app .m-import-card", new() { Timeout = 15000 });

        // Tap the "File upload" card
        var fileCard = Page.Locator(".m-app .m-import-card").Filter(
            new LocatorFilterOptions { HasText = "File upload" });
        await fileCard.ClickAsync();

        // A modal screen should appear with the "Upload file" title and dropzone
        var modal = Page.Locator(".modal-screen");
        await modal.WaitForAsync(new() { Timeout = 5000, State = WaitForSelectorState.Visible });
        Assert.True(await modal.IsVisibleAsync(), "Import modal should open");

        // The modal title should say "Upload file"
        var modalTitle = await Page.Locator(".modal-screen .modal-title").InnerTextAsync();
        Assert.Contains("Upload", modalTitle, StringComparison.OrdinalIgnoreCase);

        // The dropzone (.m-dropzone) should be visible
        var dropzone = Page.Locator(".modal-screen .m-dropzone");
        Assert.True(await dropzone.IsVisibleAsync(), ".m-dropzone should be visible in the import modal");
    }

    [Fact]
    public async Task Mobile_SeededCollection_AppearsInManagedList()
    {
        await ImportTestFileAsync("sample.gpx", "GPX Places", "#005bbf");
        await MobileNavigateAndWaitAsync("/datasources");

        // Wait for the managed list to render
        await Page.WaitForSelectorAsync(".m-app .list .row", new() { Timeout = 15000 });

        // Check that "GPX Places" appears in the list
        var row = Page.Locator(".m-app .list .row").Filter(
            new LocatorFilterOptions { HasText = "GPX Places" });
        Assert.True(await row.IsVisibleAsync(), "GPX Places collection should appear in managed list");

        // Check that "3 pts" appears (sample.gpx has 3 POIs)
        var rowText = await row.InnerTextAsync();
        Assert.Contains("3", rowText);
    }

    [Fact]
    public async Task Mobile_TapCollectionRow_OpensCollectionDetail()
    {
        await ImportTestFileAsync("sample.gpx", "GPX Places", "#005bbf");
        await MobileNavigateAndWaitAsync("/datasources");

        await Page.WaitForSelectorAsync(".m-app .list .row", new() { Timeout = 15000 });

        // Tap the collection row
        var row = Page.Locator(".m-app .list .row").Filter(
            new LocatorFilterOptions { HasText = "GPX Places" });
        await row.ClickAsync();

        // A modal screen should appear with the collection name
        var modal = Page.Locator(".modal-screen");
        await modal.WaitForAsync(new() { Timeout = 5000, State = WaitForSelectorState.Visible });
        Assert.True(await modal.IsVisibleAsync(), "Collection detail modal should open");

        // The modal should contain the collection name
        var modalText = await modal.InnerTextAsync();
        Assert.Contains("GPX Places", modalText);

        // The action buttons (Re-enrich, Export) should be present
        var reenrichBtn = Page.Locator(".modal-screen .btn").Filter(
            new LocatorFilterOptions { HasText = "Re-enrich" });
        Assert.True(await reenrichBtn.IsVisibleAsync(), "Re-enrich button should be visible");

        var exportBtn = Page.Locator(".modal-screen .btn").Filter(
            new LocatorFilterOptions { HasText = "Export" });
        Assert.True(await exportBtn.IsVisibleAsync(), "Export button should be visible");
    }

    [Fact]
    public async Task Mobile_RenameCollection_UpdatesRow()
    {
        await ImportTestFileAsync("sample.gpx", "GPX Places", "#005bbf");
        await MobileNavigateAndWaitAsync("/datasources");

        await Page.WaitForSelectorAsync(".m-app .list .row", new() { Timeout = 15000 });

        // Open collection detail
        var row = Page.Locator(".m-app .list .row").Filter(
            new LocatorFilterOptions { HasText = "GPX Places" });
        await row.ClickAsync();

        var modal = Page.Locator(".modal-screen");
        await modal.WaitForAsync(new() { Timeout = 5000, State = WaitForSelectorState.Visible });

        // Fill in the rename input (inside the modal, the rename section has a .field input)
        var renameInput = Page.Locator(".modal-screen .field").First;
        await renameInput.FillAsync("Renamed Places");

        // Click Save
        var saveBtn = Page.Locator(".modal-screen .btn-primary").Filter(
            new LocatorFilterOptions { HasText = "Save" });
        await saveBtn.ClickAsync();

        // After renaming, the modal should close (rename closes the collection modal)
        await modal.WaitForAsync(new() { Timeout = 8000, State = WaitForSelectorState.Hidden });

        // The renamed collection should appear in the list
        var renamedRow = Page.Locator(".m-app .list .row").Filter(
            new LocatorFilterOptions { HasText = "Renamed Places" });
        Assert.True(await renamedRow.IsVisibleAsync(), "Renamed collection should appear in managed list");
    }
}
