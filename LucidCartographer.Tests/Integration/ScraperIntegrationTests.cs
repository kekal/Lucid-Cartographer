using LucidCartographer.Services.Import;
using Microsoft.Playwright;

namespace LucidCartographer.Tests.Integration;

/// <summary>
/// Integration tests for the Google Maps URL scraper import flow.
/// Tests the Shared Google List card functionality with a fake scraper.
/// </summary>
[Collection("Integration")]
public class ScraperIntegrationTests : ScraperTestBase
{

    [Fact]
    public async Task EnterGoogleMapsListUrl_TypesIntoInput()
    {
        await NavigateToDataSourcesAsync();

        // Click "Shared Google List" card
        await Page.Locator("h3:has-text('Shared Google List')").ClickAsync();
        await Page.WaitForSelectorAsync("input[placeholder*='maps.app.goo.gl']", new() { Timeout = 5000 });

        // Fill URL input
        var urlInput = Page.Locator("input[placeholder*='maps.app.goo.gl']");
        await urlInput.FillAsync("https://maps.app.goo.gl/abc123");
        await urlInput.PressAsync("Tab");

        // Verify input value
        var inputValue = await urlInput.InputValueAsync();
        Assert.Equal("https://maps.app.goo.gl/abc123", inputValue);

        // Verify Import button is enabled
        var importButton = Page.Locator("button:has-text('Import')");
        var isEnabled = !await importButton.IsDisabledAsync();
        Assert.True(isEnabled, "Import button should be enabled when URL is present");
    }

    [Fact]
    public async Task ScrapeValidGoogleMapsList_ImportsPoIs()
    {
        // Configure fake scraper to return 3 POIs
        FakeScraper.ResultToReturn = new ScrapeResult
        {
            ListName = "My Saved Places",
            Pois = (List<ImportedPoi>)
            [
                new("Scraped Place 1", 50.06, 19.94, "https://maps.google.com/place/1", "Kraków, Poland", "Museum", Rating: 4.5, ReviewCount: 1200),
                new("Scraped Place 2", 52.23, 21.01, "https://maps.google.com/place/2", "Warsaw, Poland", "Park", Rating: 4.2, ReviewCount: 800),
                new("Scraped Place 3", 51.11, 17.04, "https://maps.google.com/place/3", "Wrocław, Poland", "Restaurant", Rating: 4.8, ReviewCount: 350)
            ]
        };
        FakeScraper.ExceptionToThrow = null;

        await NavigateToDataSourcesAsync();

        // Click "Shared Google List" card
        await Page.Locator("h3:has-text('Shared Google List')").ClickAsync();
        await Page.WaitForSelectorAsync("input[placeholder*='maps.app.goo.gl']", new() { Timeout = 5000 });

        // Fill URL
        var urlInput = Page.Locator("input[placeholder*='maps.app.goo.gl']");
        await urlInput.FillAsync("https://maps.app.goo.gl/test123");

        // Fill collection name (or leave empty for auto-fill)
        var collectionNameInput = Page.Locator("input[placeholder*='Poland']");
        await collectionNameInput.FillAsync("Scraped Locations");
        await collectionNameInput.PressAsync("Tab");

        // Click Import button
        await Page.Locator("button:has-text('Import')").ClickAsync();

        // Wait for import to complete
        await Page.WaitForSelectorAsync("span.font-medium:has-text('Import complete')", new() { Timeout = 15000 });

        // Verify "3" appears in result
        var resultText = await Page.Locator("span:has-text('Import complete')").Locator("..").Locator("..").InnerTextAsync();
        Assert.Contains("3", resultText);

        // Verify collection appears in managed sources table
        await Page.WaitForSelectorAsync("td span.font-medium:has-text('Scraped Locations')", new() { Timeout = 5000 });
        Assert.True(await Page.Locator("td span.font-medium:has-text('Scraped Locations')").IsVisibleAsync(),
            "Scraped collection should appear in managed sources");
    }

    /// <summary>
    /// Helper: fill URL, trigger @bind change, and wait for Import button to become enabled.
    /// </summary>
    private async Task FillUrlAndWaitForButtonAsync(string url)
    {
        var urlInput = Page.Locator("input[placeholder*='maps.app.goo.gl']");
        await urlInput.FillAsync(url);
        await urlInput.PressAsync("Tab");
        // Wait for SignalR round-trip to enable the button
        var importBtn = Page.Locator("button:has-text('Import')");
        await Page.WaitForFunctionAsync(
            "btn => !btn.disabled",
            await importBtn.ElementHandleAsync(),
            new() { Timeout = 5000 });
    }

    [Fact]
    public async Task ScrapeProgress_ShowsIndicator()
    {
        // Configure fake scraper with delay to show progress
        FakeScraper.DelayMs = 2000;
        FakeScraper.ResultToReturn = new ScrapeResult
        {
            ListName = "Test List",
            Pois = (List<ImportedPoi>)
            [
                new("Place 1", 50.0, 20.0, "https://maps.google.com/place/1", "Poland", "Museum", Rating: 4.5, ReviewCount: 100),
                new("Place 2", 51.0, 21.0, "https://maps.google.com/place/2", "Poland", "Park", Rating: 4.2, ReviewCount: 200),
                new("Place 3", 52.0, 22.0, "https://maps.google.com/place/3", "Poland", "Restaurant", Rating: 4.8, ReviewCount: 300)
            ]
        };
        FakeScraper.ExceptionToThrow = null;

        await NavigateToDataSourcesAsync();

        // Click "Shared Google List" card
        await Page.Locator("h3:has-text('Shared Google List')").ClickAsync();
        await Page.WaitForSelectorAsync("input[placeholder*='maps.app.goo.gl']", new() { Timeout = 5000 });

        // Fill URL and wait for Import button to become enabled
        await FillUrlAndWaitForButtonAsync("https://maps.app.goo.gl/test");
        await Page.Locator("button:has-text('Import')").ClickAsync();

        // During scraping, the button should show "Scraping..." text
        // Allow a moment for the scraping state to activate
        await Page.WaitForSelectorAsync("text=Scraping", new() { Timeout = 5000 });
        var scrapingVisible = await Page.Locator("text=Scraping").IsVisibleAsync();
        // Progress indicator may or may not be visible depending on rendering timing

        // Wait for import complete
        await Page.WaitForSelectorAsync("span.font-medium:has-text('Import complete')", new() { Timeout = 15000 });

        // Verify result appears
        Assert.True(await Page.Locator("span:has-text('Import complete')").IsVisibleAsync(),
            "Import complete message should be visible");
    }

    [Fact]
    public async Task ScrapeReturnsZeroPlaces_ShowsEmptyMessage()
    {
        // Configure fake scraper to return zero POIs
        FakeScraper.ResultToReturn = new ScrapeResult
        {
            ListName = "Empty List",
            Pois = (List<ImportedPoi>)[]
        };
        FakeScraper.ExceptionToThrow = null;

        await NavigateToDataSourcesAsync();

        // Click "Shared Google List" card
        await Page.Locator("h3:has-text('Shared Google List')").ClickAsync();
        await Page.WaitForSelectorAsync("input[placeholder*='maps.app.goo.gl']", new() { Timeout = 5000 });

        // Fill URL and wait for Import button to become enabled
        await FillUrlAndWaitForButtonAsync("https://maps.app.goo.gl/empty");
        await Page.Locator("button:has-text('Import')").ClickAsync();

        // When 0 POIs are returned, the invocable publishes Failed with "No places found"
        // Coravel's default ConsummationDelay is 30s, so we wait longer than the default 15s timeout
        await Page.WaitForSelectorAsync("text=No places found", new() { Timeout = 35000 });

        // Verify error message mentions "No places found"
        var errorText = await Page.Locator("text=No places found").IsVisibleAsync();
        Assert.True(errorText, "Error message should indicate no places were found");
    }

    [Fact]
    public async Task ScrapeUsesGoogleListNameAsFallback()
    {
        // Configure fake scraper with a specific list name
        FakeScraper.ResultToReturn = new ScrapeResult
        {
            ListName = "Warsaw Restaurants",
            Pois = (List<ImportedPoi>)
            [
                new("Restaurant 1", 52.2, 21.0, "https://maps.google.com/place/1", "Warsaw", "Restaurant", Rating: 4.5, ReviewCount: 100),
                new("Restaurant 2", 52.22, 21.02, "https://maps.google.com/place/2", "Warsaw", "Restaurant", Rating: 4.2, ReviewCount: 200)
            ]
        };
        FakeScraper.ExceptionToThrow = null;

        await NavigateToDataSourcesAsync();

        // Click "Shared Google List" card
        await Page.Locator("h3:has-text('Shared Google List')").ClickAsync();
        await Page.WaitForSelectorAsync("input[placeholder*='maps.app.goo.gl']", new() { Timeout = 5000 });

        // Leave collection name EMPTY (for auto-fill from scrape result)
        var collectionNameInput = Page.Locator("input[placeholder*='Poland']");
        await collectionNameInput.ClearAsync();
        await collectionNameInput.PressAsync("Tab");

        // Fill URL and click Import
        var urlInput = Page.Locator("input[placeholder*='maps.app.goo.gl']");
        await urlInput.FillAsync("https://maps.app.goo.gl/warsaw");
        await urlInput.PressAsync("Tab");
        // Playwright auto-waits for Tab key processing
        await Page.Locator("button:has-text('Import')").ClickAsync();

        // Wait for import complete
        await Page.WaitForSelectorAsync("span.font-medium:has-text('Import complete')", new() { Timeout = 15000 });

        // Verify collection name is auto-filled from scrape result
        await Page.WaitForSelectorAsync("td span.font-medium:has-text('Warsaw Restaurants')", new() { Timeout = 5000 });
        Assert.True(await Page.Locator("td span.font-medium:has-text('Warsaw Restaurants')").IsVisibleAsync(),
            "Collection should use the scraped list name 'Warsaw Restaurants' when user field is empty");
    }

    [Fact]
    public async Task ScrapeUsesGenericFallbackWhenBothEmpty()
    {
        // Configure fake scraper with null ListName
        FakeScraper.ResultToReturn = new ScrapeResult
        {
            ListName = null,
            Pois = (List<ImportedPoi>)
            [
                new("Place 1", 50.0, 20.0, "https://maps.google.com/place/1", "Poland", "Museum", Rating: 4.5, ReviewCount: 100),
                new("Place 2", 51.0, 21.0, "https://maps.google.com/place/2", "Poland", "Park", Rating: 4.2, ReviewCount: 200)
            ]
        };
        FakeScraper.ExceptionToThrow = null;

        await NavigateToDataSourcesAsync();

        // Click "Shared Google List" card
        await Page.Locator("h3:has-text('Shared Google List')").ClickAsync();
        await Page.WaitForSelectorAsync("input[placeholder*='maps.app.goo.gl']", new() { Timeout = 5000 });

        // Leave collection name EMPTY
        var collectionNameInput = Page.Locator("input[placeholder*='Poland']");
        await collectionNameInput.ClearAsync();
        await collectionNameInput.PressAsync("Tab");

        // Fill URL and click Import
        var urlInput = Page.Locator("input[placeholder*='maps.app.goo.gl']");
        await urlInput.FillAsync("https://maps.app.goo.gl/generic");
        await urlInput.PressAsync("Tab");
        // Playwright auto-waits for Tab key processing
        await Page.Locator("button:has-text('Import')").ClickAsync();

        // Wait for import complete — with null ListName and empty collection name,
        // fallback is "Shared List (2 places)"
        await Page.WaitForSelectorAsync("span.font-medium:has-text('Import complete')", new() { Timeout = 15000 });

        // Verify collection has a generic fallback name (not empty, not a technical error)
        var collectionElement = Page.Locator("table tbody tr td span.font-medium").First;
        var collectionName = await collectionElement.InnerTextAsync();
        Assert.False(string.IsNullOrWhiteSpace(collectionName), "Collection should have a fallback name when both sources are empty");
        // Expected name: "Shared List (2 places)"
        Assert.Contains("Shared List", collectionName);
    }

    [Fact]
    public async Task ScrapeFailsWithException_ShowsErrorMessage()
    {
        // Configure fake scraper to throw an exception
        FakeScraper.ExceptionToThrow = new InvalidOperationException("Scrape failed");
        FakeScraper.ResultToReturn = null;

        await NavigateToDataSourcesAsync();

        // Click "Shared Google List" card
        await Page.Locator("h3:has-text('Shared Google List')").ClickAsync();
        await Page.WaitForSelectorAsync("input[placeholder*='maps.app.goo.gl']", new() { Timeout = 5000 });

        // Fill URL and wait for Import button to become enabled
        await FillUrlAndWaitForButtonAsync("https://maps.app.goo.gl/error");
        await Page.Locator("button:has-text('Import')").ClickAsync();

        // Wait for error message (should appear instead of success)
        // The invocable publishes "Import failed: Scrape failed" as the error message
        await Page.WaitForSelectorAsync("text=Import failed", new() { Timeout = 35000 });

        // Verify error heading is visible
        Assert.True(await Page.Locator("text=Import failed").IsVisibleAsync(),
            "Error message heading should be displayed when scraper throws");

        // Verify error detail mentions the exception message
        Assert.True(await Page.Locator("text=Scrape failed").IsVisibleAsync(),
            "Error detail should mention the exception message 'Scrape failed'");

        // Verify no success message appears
        Assert.False(await Page.Locator("span.font-medium:has-text('Import complete')").IsVisibleAsync(),
            "Success message should not appear when error occurs");
    }

    [Fact]
    public async Task ImportButtonDisabledDuringScrape()
    {
        // Configure fake scraper with long delay
        FakeScraper.DelayMs = 3000;
        FakeScraper.ResultToReturn = new ScrapeResult
        {
            ListName = "Test",
            Pois = (List<ImportedPoi>)
            [
                new("Place", 50.0, 20.0, "https://maps.google.com/place/1", "Poland", "Museum", Rating: 4.5, ReviewCount: 100)
            ]
        };
        FakeScraper.ExceptionToThrow = null;

        await NavigateToDataSourcesAsync();

        // Click "Shared Google List" card
        await Page.Locator("h3:has-text('Shared Google List')").ClickAsync();
        await Page.WaitForSelectorAsync("input[placeholder*='maps.app.goo.gl']", new() { Timeout = 5000 });

        // Fill URL and wait for button to become enabled
        await FillUrlAndWaitForButtonAsync("https://maps.app.goo.gl/long");

        // Click Import
        var importButton = Page.Locator("button:has-text('Import')");
        await importButton.ClickAsync();

        // Wait for the "Scraping..." text to appear, indicating the button is in disabled state
        await Page.WaitForSelectorAsync("text=Scraping", new() { Timeout = 5000 });

        // The button should be disabled while _isScraping is true
        var isDisabledDuring = await importButton.IsDisabledAsync();
        Assert.True(isDisabledDuring, "Import button should be disabled while scraping");

        // Wait for import to complete
        await Page.WaitForSelectorAsync("span.font-medium:has-text('Import complete')", new() { Timeout = 15000 });

        // After scraping completes, the "Scraping..." text should no longer be visible
        await Page.WaitForSelectorAsync("text=Scraping...", new() { State = WaitForSelectorState.Hidden, Timeout = 5000 });
        var scrapingTextAfter = await Page.Locator("text=Scraping...").IsVisibleAsync();
        Assert.False(scrapingTextAfter, "Scraping indicator should be gone after completion");

        // The button text should show "Import" again (not "Scraping...")
        var buttonText = await importButton.InnerTextAsync();
        Assert.Contains("Import", buttonText);

        // Note: button may still be disabled because _sharedListUrl is cleared after successful import.
        // That is correct behavior - user would need to enter a new URL to import again.
    }
}