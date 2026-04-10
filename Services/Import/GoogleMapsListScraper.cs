using Microsoft.Playwright;

namespace LucidCartographer.Services.Import;

public class GoogleMapsListScraper
{
    private readonly ILogger<GoogleMapsListScraper> _logger;

    public GoogleMapsListScraper(ILogger<GoogleMapsListScraper> logger)
    {
        _logger = logger;
    }

    public async Task<List<ImportedPoi>> ScrapeAsync(string listUrl, Action<int>? onProgress = null)
    {
        _logger.LogInformation("Starting scrape of {Url}", listUrl);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            Locale = "en-US",
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
        });
        var page = await context.NewPageAsync();

        // Navigate to the list URL
        await page.GotoAsync(listUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000 });

        // Wait for list content to load
        await page.WaitForTimeoutAsync(5000);

        // Debug: save screenshot and page URL after redirect
        _logger.LogInformation("Page URL after navigation: {Url}", page.Url);
        try
        {
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = "data/debug_scrape.png" });
            var html = await page.ContentAsync();
            File.WriteAllText("data/debug_scrape.html", html);
            _logger.LogInformation("Debug screenshot and HTML saved to data/");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save debug files");
        }

        // Accept cookies/consent if dialog appears
        try
        {
            // Try various consent button selectors
            var consentSelectors = new[]
            {
                "button[aria-label*='Accept']",
                "button[aria-label*='accept']",
                "button:has-text('Accept all')",
                "button:has-text('Agree')",
                "button:has-text('Принять')",
                "form[action*='consent'] button",
            };
            foreach (var sel in consentSelectors)
            {
                var btn = page.Locator(sel).First;
                if (await btn.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 1000 }))
                {
                    _logger.LogInformation("Clicking consent button: {Selector}", sel);
                    await btn.ClickAsync();
                    await page.WaitForTimeoutAsync(3000);
                    break;
                }
            }
        }
        catch { /* no cookie dialog */ }

        // Debug: save another screenshot after consent
        try
        {
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = "data/debug_scrape2.png" });
            _logger.LogInformation("Post-consent URL: {Url}", page.Url);
        }
        catch { }

        // Scroll the list panel to load all places
        // Google Maps lists lazy-load as you scroll — try multiple selectors
        var scrollSelectors = new[]
        {
            "div[role='feed']",
            "div.m6QErb.DxyBCb.kA9KIf.dS8AEf",
            "div.m6QErb",
            "div[aria-label] div.e07Vkf",
        };

        ILocator? scrollContainer = null;
        foreach (var sel in scrollSelectors)
        {
            var loc = page.Locator(sel).First;
            if (await loc.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 2000 }))
            {
                scrollContainer = loc;
                _logger.LogInformation("Found scroll container: {Selector}", sel);
                break;
            }
        }

        if (scrollContainer == null)
        {
            _logger.LogWarning("No scroll container found. Trying page-level scroll.");
            // Fallback: scroll the whole page
            scrollContainer = page.Locator("body").First;
        }

        if (await scrollContainer.IsVisibleAsync())
        {
            var previousCount = 0;
            var stableRounds = 0;

            for (int i = 0; i < 100; i++) // max 100 scroll attempts
            {
                await scrollContainer.EvaluateAsync("el => el.scrollTop = el.scrollHeight");
                await page.WaitForTimeoutAsync(1500);

                var currentCount = await page.Locator("a[href*='/maps/place/']").CountAsync();
                _logger.LogInformation("Scroll {Round}: {Count} places found", i + 1, currentCount);
                onProgress?.Invoke(currentCount);

                if (currentCount == previousCount)
                {
                    stableRounds++;
                    if (stableRounds >= 3) break; // no new items after 3 scrolls
                }
                else
                {
                    stableRounds = 0;
                }
                previousCount = currentCount;
            }
        }

        // Debug: save final HTML and screenshot
        try
        {
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = "data/debug_scrape3.png" });
            var finalHtml = await page.ContentAsync();
            File.WriteAllText("data/debug_scrape_final.html", finalHtml);
        }
        catch { }

        // Google Maps list items are NOT <a> links — they are div elements
        // that get clicked via JS. We need to click each item to get its URL,
        // or extract data directly from the list item DOM.

        // Strategy: extract place data by clicking each list item and reading
        // the URL that appears, then going back to the list.
        // But simpler: use the data already visible in the DOM.

        // Google Maps list items use obfuscated class names.
        // Try multiple known selectors in order of specificity.
        var itemSelectors = new[]
        {
            "div.Nv2PK",           // Standard search results
            "div.BsJqK",           // List view items (observed in debug HTML)
            "div.m6QErb div[role='article']",
            "div.lI9IFe",
        };

        IReadOnlyList<ILocator> listItems = Array.Empty<ILocator>();
        string usedSelector = "";
        foreach (var sel in itemSelectors)
        {
            var items = await page.Locator(sel).AllAsync();
            _logger.LogInformation("Selector '{Sel}': {Count} items", sel, items.Count);
            if (items.Count > 0)
            {
                listItems = items;
                usedSelector = sel;
                break;
            }
        }

        if (!listItems.Any())
        {
            _logger.LogWarning("No list items found with any selector.");
        }

        var results = new List<ImportedPoi>();

        // Strategy: click each list item to navigate to its detail view,
        // extract name + coordinates from the URL, then go back to the list.
        _logger.LogInformation("Using click-through strategy for {Count} items", listItems.Count);

        var totalItems = listItems.Count;
        for (int idx = 0; idx < totalItems; idx++)
        {
            try
            {
                // Re-query items each time since DOM changes after navigation
                var items = await page.Locator(usedSelector).AllAsync();

                if (idx >= items.Count)
                {
                    _logger.LogWarning("Item index {Idx} out of range ({Count} items)", idx, items.Count);
                    break;
                }

                var item = items[idx];

                // Extract name before clicking
                string name;
                try
                {
                    var nameEl = item.Locator(".fontHeadlineSmall, .qBF1Pd, .NrDZNb").First;
                    name = await nameEl.InnerTextAsync();
                }
                catch
                {
                    name = (await item.InnerTextAsync()).Split('\n').FirstOrDefault()?.Trim() ?? "Unknown";
                }
                name = name.Split('\n').FirstOrDefault()?.Trim() ?? "Unknown";

                // Extract category/type if visible
                string? category = null;
                try
                {
                    var catEl = item.Locator(".W4Efsd .W4Efsd span:not(.MW4etd):not(.UY7F9)").First;
                    if (await catEl.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                        category = (await catEl.InnerTextAsync()).Trim().TrimEnd('·').Trim();
                }
                catch { }

                // Click the item to navigate to its place page
                await item.ClickAsync();

                // Wait for URL to update with place data (!3d/!4d)
                (double lat, double lon)? coords = null;
                string currentUrl = "";
                for (int wait = 0; wait < 5; wait++)
                {
                    await page.WaitForTimeoutAsync(1000);
                    currentUrl = page.Url;
                    coords = ExtractCoordinates(currentUrl);
                    // If we got !3d/!4d coords (not just viewport), we're good
                    if (coords != null && currentUrl.Contains("!3d"))
                        break;
                }

                // Fallback: try to extract coords from the place action buttons
                // Google Maps place pages have a share/directions button with coords
                if (coords == null || !currentUrl.Contains("!3d"))
                {
                    try
                    {
                        // The directions button href contains the destination coords
                        var dirBtn = page.Locator("a[data-value='Directions'], button[data-tooltip='Directions']").First;
                        if (await dirBtn.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 1000 }))
                        {
                            var dirHref = await dirBtn.GetAttributeAsync("href");
                            if (dirHref != null)
                            {
                                var dirCoords = ExtractCoordinates(dirHref);
                                if (dirCoords != null) coords = dirCoords;
                            }
                        }
                    }
                    catch { }
                }

                if (coords != null)
                {
                    results.Add(new ImportedPoi(
                        Name: name,
                        Latitude: coords.Value.lat,
                        Longitude: coords.Value.lon,
                        GoogleMapsUrl: currentUrl,
                        Category: category
                    ));
                    _logger.LogInformation("[{Idx}/{Total}] {Name} @ {Lat},{Lon}", idx + 1, totalItems, name, coords.Value.lat, coords.Value.lon);
                }
                else
                {
                    _logger.LogWarning("[{Idx}/{Total}] Skipped {Name} — no coordinates found", idx + 1, totalItems, name);
                }

                onProgress?.Invoke(results.Count);

                // Go back to the list
                await page.GoBackAsync(new PageGoBackOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 10000 });
                await page.WaitForTimeoutAsync(1500);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process item {Idx}", idx);
                // Try to recover by going back
                try { await page.GoBackAsync(); await page.WaitForTimeoutAsync(2000); } catch { }
            }
        }

        _logger.LogInformation("Successfully scraped {Count} places", results.Count);
        return results;
    }

    private static (double lat, double lon)? ExtractCoordinates(string url)
    {
        // Priority 1: !3d<lat>!4d<lon> — actual place coordinates
        var lat3d = ExtractBang(url, "!3d");
        var lon4d = ExtractBang(url, "!4d");
        if (lat3d.HasValue && lon4d.HasValue)
            return (lat3d.Value, lon4d.Value);

        // Priority 2: /place/Name/lat,lon pattern
        // e.g., /place/Museum/@51.1,17.0,15z/data=...
        var placeIdx = url.IndexOf("/place/");
        if (placeIdx >= 0)
        {
            var atIdx = url.IndexOf("/@", placeIdx);
            if (atIdx >= 0)
            {
                var afterAt = url[(atIdx + 2)..];
                var parts = afterAt.Split(',');
                if (parts.Length >= 2
                    && double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var lat)
                    && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var lon))
                {
                    return (lat, lon);
                }
            }
        }

        // Priority 3: /@lat,lon — viewport coordinates (least accurate, last resort)
        var atIdx2 = url.IndexOf("/@");
        if (atIdx2 >= 0)
        {
            var afterAt = url[(atIdx2 + 2)..];
            var parts = afterAt.Split(',');
            if (parts.Length >= 2
                && double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var lat)
                && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var lon))
            {
                return (lat, lon);
            }
        }

        return null;
    }

    private static double? ExtractBang(string url, string prefix)
    {
        var idx = url.IndexOf(prefix);
        if (idx < 0) return null;
        var start = idx + prefix.Length;
        var end = start;
        while (end < url.Length && (char.IsDigit(url[end]) || url[end] == '.' || url[end] == '-'))
            end++;
        if (end > start && double.TryParse(url[start..end], System.Globalization.CultureInfo.InvariantCulture, out var val))
            return val;
        return null;
    }
}
