using LucidCartographer.Services;
using Microsoft.Playwright;

namespace LucidCartographer.Services.Import
{
    public class GoogleMapsListScraper : IGoogleMapsListScraper
    {
        private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        // HIGH-07: Limit to one concurrent scrape to prevent multiple Chromium instances
        // exhausting server memory. Additional requests will wait in the queue.
        private static readonly SemaphoreSlim _scrapeSemaphore = new(1, 1);

        // Lazy, process-wide bootstrap of the Playwright browser binaries so the app
        // works out-of-the-box on a clean machine (no manual `playwright install` step).
        // Playwright's install command is idempotent and fast when browsers are already
        // present, so calling it once per process is cheap.
        private static readonly SemaphoreSlim _installLock = new(1, 1);
        private static bool _browsersInstalled;

        private static readonly string[] AllowedUrlPrefixes =
        [
            "https://www.google.com/maps/",
            "https://maps.google.com/",
            "https://maps.app.goo.gl/",
            "https://goo.gl/maps/",
            "http://www.google.com/maps/",
            "http://maps.google.com/",
            "http://maps.app.goo.gl/",
            "http://goo.gl/maps/"
        ];

        private readonly ILogger<GoogleMapsListScraper> _logger;

        public GoogleMapsListScraper(ILogger<GoogleMapsListScraper> logger)
        {
            _logger = logger;
        }

        public async Task<ScrapeResult> ScrapeAsync(string listUrl, Action<int>? onProgress = null, CancellationToken cancellationToken = default)
        {
            // URL validation: prevent SSRF
            if (string.IsNullOrWhiteSpace(listUrl))
                throw new ArgumentException("List URL must not be empty.", nameof(listUrl));

            var trimmedUrl = listUrl.Trim();
            if (!AllowedUrlPrefixes.Any(prefix => trimmedUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("URL must be a Google Maps URL (https://www.google.com/maps/... or https://maps.app.goo.gl/...).", nameof(listUrl));

            // HIGH-07: Acquire semaphore to ensure only one scrape runs at a time
            // ARCH-HIGH-04: Add timeout to semaphore wait to prevent unbounded queuing
            if (!await _scrapeSemaphore.WaitAsync(TimeSpan.FromMinutes(10), cancellationToken))
            {
                throw new TimeoutException("Timed out waiting for scraper availability. Another scrape may be in progress.");
            }
            try
            {
                // ARCH-HIGH-08: Overall operation timeout of 10 minutes
                using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                operationCts.CancelAfter(TimeSpan.FromMinutes(10));
                return await ScrapeInternalAsync(trimmedUrl, onProgress, operationCts.Token);
            }
            finally
            {
                _scrapeSemaphore.Release();
            }
        }

        private async Task<ScrapeResult> ScrapeInternalAsync(string trimmedUrl, Action<int>? onProgress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting scrape of {Url}", trimmedUrl);

            await EnsureBrowsersInstalledAsync(cancellationToken);

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                Locale = "en-US",
                UserAgent = DefaultUserAgent
            });
            var page = await context.NewPageAsync();

            // Navigate to the list URL
            await page.GotoAsync(trimmedUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000 });

            // Wait for list content to load
            await page.WaitForTimeoutAsync(5000);

            _logger.LogInformation("Page URL after navigation: {Url}", page.Url);

            // Accept cookies/consent if dialog appears
            try
            {
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
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No cookie consent dialog found or failed to click it");
            }

            _logger.LogDebug("Post-consent URL: {Url}", page.Url);

            // Scroll the list panel to load all places
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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract list name");
            }

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
                scrollContainer = page.Locator("body").First;
            }

            if (await scrollContainer.IsVisibleAsync())
            {
                var previousCount = 0;
                var stableRounds = 0;

                for (int i = 0; i < 100; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await scrollContainer.EvaluateAsync("el => el.scrollTop = el.scrollHeight");
                    await page.WaitForTimeoutAsync(1500);

                    var currentCount = await page.Locator("a[href*='/maps/place/']").CountAsync();
                    _logger.LogInformation("Scroll {Round}: {Count} places found", i + 1, currentCount);
                    onProgress?.Invoke(currentCount);

                    if (currentCount == previousCount)
                    {
                        stableRounds++;
                        if (stableRounds >= 3) break;
                    }
                    else
                    {
                        stableRounds = 0;
                    }
                    previousCount = currentCount;
                }
            }

            var itemSelectors = new[]
            {
                "div.Nv2PK",
                "div.BsJqK",
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

            _logger.LogInformation("Using click-through strategy for {Count} items", listItems.Count);

            var totalItems = listItems.Count;
            for (int idx = 0; idx < totalItems; idx++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
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
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to extract name via specific selector for item {Idx}, falling back to full text", idx);
                        name = (await item.InnerTextAsync()).Split('\n').FirstOrDefault()?.Trim() ?? "Unknown";
                    }
                    name = name.Split('\n').FirstOrDefault()?.Trim() ?? "Unknown";

                    // Rating
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
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to extract rating for item {Idx}", idx);
                    }

                    // Review count
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
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to extract review count for item {Idx}", idx);
                    }

                    // Category from list item
                    string? category = null;
                    string? description = null;
                    try
                    {
                        var bodyEls = await item.Locator(".W4Efsd").AllAsync();
                        foreach (var bodyEl in bodyEls)
                        {
                            var text = (await bodyEl.InnerTextAsync()).Trim();
                            if (string.IsNullOrEmpty(text) || text == name) continue;
                            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            foreach (var line in lines)
                            {
                                var clean = line.Trim(' ', '\u00B7', '\u00B7');
                                if (clean.Length < 2 || clean == name) continue;
                                if (clean.All(c => char.IsDigit(c) || c == '.' || c == ',' || c == '(' || c == ')' || c == ' ' || c == '\u2605')) continue;
                                if (category == null)
                                    category = clean;
                                else if (description == null && clean != category)
                                    description = clean;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to extract category/description for item {Idx} '{Name}'", idx, name);
                    }

                    // Image URL
                    string? imageUrl = null;
                    try
                    {
                        var imgEl = item.Locator("img").First;
                        if (await imgEl.IsVisibleAsync())
                            imageUrl = await imgEl.GetAttributeAsync("src");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to extract image URL for item {Idx}", idx);
                    }

                    // === Click the item to get coordinates + address ===
                    await item.ClickAsync();

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
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to extract coordinates from directions button for item {Idx}", idx);
                        }
                    }

                    // Extract address
                    string? address = null;
                    try
                    {
                        var addrEl = page.Locator("button[data-item-id='address'] .fontBodyMedium, div[data-item-id='address']").First;
                        if (await addrEl.IsVisibleAsync())
                            address = (await addrEl.InnerTextAsync()).Trim();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to extract address for item {Idx} '{Name}'", idx, name);
                    }

                    // Extract website
                    string? website = null;
                    try
                    {
                        var webEl = page.Locator("a[data-item-id='authority'] .fontBodyMedium, a[data-item-id='authority']").First;
                        if (await webEl.IsVisibleAsync())
                            website = await webEl.GetAttributeAsync("href") ?? (await webEl.InnerTextAsync()).Trim();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to extract website for item {Idx} '{Name}'", idx, name);
                    }

                    // Extract phone
                    string? phone = null;
                    try
                    {
                        var phoneEl = page.Locator("button[data-item-id*='phone'] .fontBodyMedium").First;
                        if (await phoneEl.IsVisibleAsync())
                            phone = (await phoneEl.InnerTextAsync()).Trim();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to extract phone for item {Idx} '{Name}'", idx, name);
                    }

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
                        _logger.LogInformation("[{Idx}/{Total}] {Name} ({Cat}) @ {Lat},{Lon}",
                            idx + 1, totalItems, name, category ?? "-", coords.Value.lat, coords.Value.lon);
                    }
                    else
                    {
                        _logger.LogWarning("[{Idx}/{Total}] Skipped {Name} -- no coordinates found", idx + 1, totalItems, name);
                    }

                    onProgress?.Invoke(results.Count);

                    // Go back to the list
                    await page.GoBackAsync(new PageGoBackOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 10000 });
                    await page.WaitForTimeoutAsync(1500);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process item {Idx}", idx);
                    try
                    {
                        await page.GoBackAsync();
                        await page.WaitForTimeoutAsync(2000);
                    }
                    catch (Exception backEx)
                    {
                        _logger.LogWarning(backEx, "Failed to go back after error on item {Idx}", idx);
                    }
                }
            }

            _logger.LogInformation("Successfully scraped {Count} places from list '{ListName}'", results.Count, listName ?? "unknown");
            return new ScrapeResult { ListName = listName, Pois = results };
        }

        /// <summary>
        /// Ensures Playwright's Chromium browser is installed on the host. Runs the
        /// install command once per process (idempotent, fast when already present) so
        /// a fresh clone / clean machine works without the user having to invoke
        /// `playwright install` manually. Marshalled onto a background thread because
        /// Microsoft.Playwright.Program.Main is synchronous.
        /// </summary>
        private async Task EnsureBrowsersInstalledAsync(CancellationToken cancellationToken)
        {
            if (_browsersInstalled) return;

            await _installLock.WaitAsync(cancellationToken);
            try
            {
                if (_browsersInstalled) return;

                _logger.LogInformation("Ensuring Playwright Chromium browser is installed (one-time bootstrap)…");
                var exitCode = await Task.Run(
                    () => Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }),
                    cancellationToken);

                if (exitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Playwright browser install failed with exit code {exitCode}. " +
                        "Run `playwright install chromium` manually to diagnose.");
                }

                _browsersInstalled = true;
                _logger.LogInformation("Playwright Chromium browser is ready.");
            }
            finally
            {
                _installLock.Release();
            }
        }

        /// <summary>
        /// IE-14: Delegates to shared PoiUrlHelper.ExtractCoordinatesFromUrl to eliminate
        /// duplicated @/ coordinate parsing logic.
        /// </summary>
        private static (double lat, double lon)? ExtractCoordinates(string url)
            => PoiUrlHelper.ExtractCoordinatesFromUrl(url);
    }
}
