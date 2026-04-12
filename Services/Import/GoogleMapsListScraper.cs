using Microsoft.Playwright;

namespace LucidCartographer.Services.Import
{
    public class ScrapeResult
    {
        public string? ListName { get; set; }
        public List<ImportedPoi> Pois { get; set; } = new();
    }

    public class GoogleMapsListScraper : IGoogleMapsListScraper
    {
        private readonly ILogger<GoogleMapsListScraper> _logger;

        public GoogleMapsListScraper(ILogger<GoogleMapsListScraper> logger)
        {
            _logger = logger;
        }

        public async Task<ScrapeResult> ScrapeAsync(string listUrl, Action<int>? onProgress = null)
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
                    if (await btn.IsVisibleAsync())
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

            // Extract list name from the page header
            string? listName = null;
            try
            {
                // Google Maps list title appears in various header elements
                var titleSelectors = new[] { "h1.fontHeadlineLarge", "h1", "div.fontHeadlineLarge", "div.F63Kk span" };
                foreach (var sel in titleSelectors)
                {
                    var titleEl = page.Locator(sel).First;
                    if (await titleEl.IsVisibleAsync())
                    {
                        listName = (await titleEl.InnerTextAsync()).Trim();
                        if (!string.IsNullOrEmpty(listName) && listName.Length > 1)
                        {
                            _logger.LogInformation("Extracted list name: {Name}", listName);
                            break;
                        }
                        listName = null;
                    }
                }
            }
            catch { }

            ILocator? scrollContainer = null;
            foreach (var sel in scrollSelectors)
            {
                var loc = page.Locator(sel).First;
                if (await loc.IsVisibleAsync())
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

                    // === Extract data from list item (before clicking) ===
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

                    // Rating (e.g., "4.9")
                    double? rating = null;
                    try
                    {
                        var ratingEl = item.Locator("span.MW4etd").First;
                        if (await ratingEl.IsVisibleAsync())
                        {
                            var ratingText = await ratingEl.InnerTextAsync();
                            if (double.TryParse(ratingText.Trim().Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture, out var r))
                                rating = r;
                        }
                    }
                    catch { }

                    // Review count (e.g., "(9 323)")
                    int? reviewCount = null;
                    try
                    {
                        var reviewEl = item.Locator("span.UY7F9").First;
                        if (await reviewEl.IsVisibleAsync())
                        {
                            var reviewText = await reviewEl.InnerTextAsync();
                            reviewText = new string(reviewText.Where(c => char.IsDigit(c)).ToArray());
                            if (int.TryParse(reviewText, out var rc))
                                reviewCount = rc;
                        }
                    }
                    catch { }

                    // Category from list item (e.g., "Muzeum", "Zoo")
                    string? category = null;
                    string? description = null;
                    try
                    {
                        // Get all text lines from the item
                        var bodyEls = await item.Locator(".W4Efsd").AllAsync();
                        foreach (var bodyEl in bodyEls)
                        {
                            var text = (await bodyEl.InnerTextAsync()).Trim();
                            if (string.IsNullOrEmpty(text) || text == name) continue;
                            // First non-rating text is usually the category
                            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            foreach (var line in lines)
                            {
                                var clean = line.Trim(' ', '·', '\u00B7');
                                if (clean.Length < 2 || clean == name) continue;
                                if (clean.All(c => char.IsDigit(c) || c == '.' || c == ',' || c == '(' || c == ')' || c == ' ' || c == '★')) continue;
                                if (category == null)
                                    category = clean;
                                else if (description == null && clean != category)
                                    description = clean;
                            }
                        }
                    }
                    catch { }

                    // Image URL
                    string? imageUrl = null;
                    try
                    {
                        var imgEl = item.Locator("img").First;
                        if (await imgEl.IsVisibleAsync())
                            imageUrl = await imgEl.GetAttributeAsync("src");
                    }
                    catch { }

                    // === Click the item to get coordinates + address ===
                    await item.ClickAsync();

                    // Wait for URL to update with place data (!3d/!4d)
                    (double lat, double lon)? coords = null;
                    string currentUrl = "";
                    for (int wait = 0; wait < 5; wait++)
                    {
                        await page.WaitForTimeoutAsync(1000);
                        currentUrl = page.Url;
                        coords = ExtractCoordinates(currentUrl);
                        if (coords != null && currentUrl.Contains("!3d"))
                            break;
                    }

                    // Fallback: directions button
                    if (coords == null || !currentUrl.Contains("!3d"))
                    {
                        try
                        {
                            var dirBtn = page.Locator("a[data-value='Directions'], button[data-tooltip='Directions']").First;
                            if (await dirBtn.IsVisibleAsync())
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

                    // Extract address from the detail panel
                    string? address = null;
                    try
                    {
                        var addrEl = page.Locator("button[data-item-id='address'] .fontBodyMedium, div[data-item-id='address']").First;
                        if (await addrEl.IsVisibleAsync())
                            address = (await addrEl.InnerTextAsync()).Trim();
                    }
                    catch { }

                    // Extract website
                    string? website = null;
                    try
                    {
                        var webEl = page.Locator("a[data-item-id='authority'] .fontBodyMedium, a[data-item-id='authority']").First;
                        if (await webEl.IsVisibleAsync())
                            website = await webEl.GetAttributeAsync("href") ?? (await webEl.InnerTextAsync()).Trim();
                    }
                    catch { }

                    // Extract phone
                    string? phone = null;
                    try
                    {
                        var phoneEl = page.Locator("button[data-item-id*='phone'] .fontBodyMedium").First;
                        if (await phoneEl.IsVisibleAsync())
                            phone = (await phoneEl.InnerTextAsync()).Trim();
                    }
                    catch { }

                    if (coords != null)
                    {
                        results.Add(new ImportedPoi(
                            Name: name,
                            Latitude: coords.Value.lat,
                            Longitude: coords.Value.lon,
                            GoogleMapsUrl: currentUrl,
                            Address: address,
                            Category: category,
                            Description: description,
                            Rating: rating,
                            ReviewCount: reviewCount,
                            Website: website,
                            Phone: phone,
                            ImageUrl: imageUrl
                        ));
                        _logger.LogInformation("[{Idx}/{Total}] {Name} ({Cat}) ★{Rating} @ {Lat},{Lon}",
                            idx + 1, totalItems, name, category ?? "-", rating?.ToString("F1") ?? "-", coords.Value.lat, coords.Value.lon);
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

            _logger.LogInformation("Successfully scraped {Count} places from list '{ListName}'", results.Count, listName ?? "unknown");
            return new ScrapeResult { ListName = listName, Pois = results };
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
}
