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

            await PlaywrightBootstrap.EnsureBrowsersInstalledAsync(_logger, cancellationToken);

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
                        // The consent button redirects back to the real maps URL;
                        // WaitForNavigationAsync only works for real navigations, but
                        // consent.google.com does a real one, so wait for the URL to
                        // move off consent.google.com rather than sleeping blind.
                        var clickTask = btn.ClickAsync();
                        try
                        {
                            await page.WaitForURLAsync(
                                u => !u.Contains("consent.google.com"),
                                new() { Timeout = 15000 });
                        }
                        catch (TimeoutException)
                        {
                            _logger.LogWarning("Consent redirect did not complete within 15s; continuing anyway");
                        }
                        await clickTask;
                        // Then wait for the maps UI to finish laying out.
                        try
                        {
                            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 });
                        }
                        catch (TimeoutException) { /* NetworkIdle rarely reached on maps */ }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No cookie consent dialog found or failed to click it");
            }

            _logger.LogInformation("Post-consent URL: {Url}", page.Url);

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

            // === Structure discovery =================================================
            //
            // Obfuscated class names (div.BsJqK, div.Nv2PK …) rotate every few months
            // whenever Google reships the maps UI, and link patterns vary across list
            // views (some use `/maps/place/` anchors, others use click-handled buttons
            // with no href at all). CSS overflow isn't reliable either: short lists
            // fit without overflow, and virtualized panels use `overflow: hidden` with
            // JS-driven scroll. So we lean on pure DOM topology:
            //
            //   A list panel is the div with the highest count of *repeating row*
            //   children — direct children (or one-layer-nested if the container
            //   wraps them in a single spacer) that have real height and real text.
            //
            // We walk every div, score it by how many card-like children it has,
            // and pick the winner. Three-or-more card-like children rules out page
            // chrome (header, footer, single buttons), and we skip any candidate
            // that's an ancestor of a better one so we pick the *tightest* container.
            //
            // The chosen container gets `data-scraper-scroll='1'`; each card gets
            // `data-scraper-idx='N'`. Everything after this runs in C# against those
            // stable data attributes.
            //
            // Called repeatedly: at startup until the list hydrates, after each
            // scroll pass (tags newly lazy-rendered cards, leaves existing indices
            // intact), and after each GoBack from the detail view.
            // JS source lives in GoogleMapsScraperScripts so it can be unit-tested
            // against fixture HTML independently of the full scraper pipeline.

            async Task<(int total, bool scrollFound, int divsExamined, string? diag)> DiscoverAsync()
            {
                var result = await page.EvaluateAsync<System.Text.Json.JsonElement>(GoogleMapsScraperScripts.Discover);
                var total = result.GetProperty("total").GetInt32();
                var scrollFound = result.GetProperty("scrollFound").GetBoolean();
                var divsExamined = result.TryGetProperty("divsExamined", out var d) ? d.GetInt32() : 0;
                string? diag = null;
                if (result.TryGetProperty("diag", out var diagEl))
                {
                    diag = diagEl.GetRawText();
                }
                return (total, scrollFound, divsExamined, diag);
            }

            // Initial discovery — wait up to ~15s for the list panel to hydrate.
            // Consent redirect + lazy rendering can delay the list by several seconds.
            (int total, bool scrollFound, int divsExamined, string? diag) discovery = (0, false, 0, null);
            for (int attempt = 0; attempt < 15; attempt++)
            {
                discovery = await DiscoverAsync();
                if (discovery.total > 0) break;
                await page.WaitForTimeoutAsync(1000);
            }

            if (discovery.total == 0)
            {
                _logger.LogWarning(
                    "Could not locate list cards via DOM topology. " +
                    "Divs examined: {Divs}. Top containers by text length: {Diag}. " +
                    "Page may not be a list URL, or Google restructured the list panel.",
                    discovery.divsExamined, discovery.diag ?? "(none)");
            }
            else
            {
                _logger.LogInformation(
                    "Discovered {Count} initial list items (examined {Divs} divs)",
                    discovery.total, discovery.divsExamined);
            }

            // Scroll loop — drives lazy-loaded cards into the DOM, re-tags each
            // pass, terminates when the tagged count stops growing for 3 rounds.
            if (discovery.total > 0)
            {
                var scrollContainer = page.Locator("[data-scraper-scroll='1']").First;
                var previousCount = discovery.total;
                var stableRounds = 0;

                for (int i = 0; i < 100; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await scrollContainer.EvaluateAsync("el => el.scrollTop = el.scrollHeight");
                    await page.WaitForTimeoutAsync(1500);

                    var (currentCount, _, _, _) = await DiscoverAsync();
                    _logger.LogInformation("Scroll {Round}: {Count} places tagged", i + 1, currentCount);
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

            var listItems = await page.Locator("[data-scraper-idx]").AllAsync();

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
                    // Address the card by its stable data-scraper-idx tag instead of
                    // re-enumerating — iteration order is preserved, and going back
                    // via `GoBack` keeps the tags intact.
                    var item = page.Locator($"[data-scraper-idx='{idx}']").First;

                    if (!await item.IsVisibleAsync())
                    {
                        _logger.LogWarning("Item {Idx} no longer present in DOM — skipping", idx);
                        continue;
                    }

                    // === Extract data from list item (before clicking) ===
                    // Prefer the place anchor's aria-label (accessibility-required, set
                    // by Google for screen readers) — it's the single most stable name
                    // source on the card. Fall back to legacy class-based selectors,
                    // then to the card's first text line.
                    string name = "Unknown";
                    try
                    {
                        var placeAnchor = item.Locator("a[href*='/maps/place/']").First;
                        var ariaLabel = await placeAnchor.GetAttributeAsync("aria-label");
                        if (!string.IsNullOrWhiteSpace(ariaLabel))
                            name = ariaLabel.Trim();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "aria-label name extraction failed for item {Idx}", idx);
                    }
                    if (name == "Unknown")
                    {
                        try
                        {
                            var nameEl = item.Locator(".fontHeadlineSmall, .qBF1Pd, .NrDZNb").First;
                            if (await nameEl.IsVisibleAsync())
                                name = (await nameEl.InnerTextAsync()).Trim();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Class-based name extraction failed for item {Idx}", idx);
                        }
                    }
                    if (name == "Unknown")
                    {
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

                    // Image: extract URL from the list card, upsize it (`…=w92-h92-k-no`
                    // → `…=w1024`), and download the bytes *now* while the signed
                    // `gps-cs-s` token is still fresh. Google blocks cross-origin
                    // hotlinking of these URLs and the token expires in ~minutes, so
                    // persisting only the URL is useless. We fetch via Playwright's
                    // APIRequest so the request carries the browser session's cookies
                    // and bypasses Google's anti-hotlink checks, then store the bytes
                    // on the Poi entity and serve them from /api/poi-image/{id}.
                    string? imageUrl = null;
                    byte[]? imageData = null;
                    string? imageContentType = null;
                    try
                    {
                        var imgEl = item.Locator("img").First;
                        if (await imgEl.IsVisibleAsync())
                        {
                            var rawSrc = await imgEl.GetAttributeAsync("src");
                            if (!string.IsNullOrEmpty(rawSrc) && rawSrc.Contains("googleusercontent.com"))
                            {
                                var equalsIdx = rawSrc.LastIndexOf('=');
                                var baseUrl = equalsIdx > 0 ? rawSrc[..equalsIdx] : rawSrc;
                                imageUrl = baseUrl + "=w1024";

                                try
                                {
                                    var resp = await context.APIRequest.GetAsync(imageUrl);
                                    if (resp.Status == 200)
                                    {
                                        imageData = await resp.BodyAsync();
                                        var headers = resp.Headers;
                                        imageContentType = headers.TryGetValue("content-type", out var ct) ? ct : "image/jpeg";
                                    }
                                    else
                                    {
                                        _logger.LogWarning("Image fetch for item {Idx} '{Name}' returned HTTP {Status}", idx, name, resp.Status);
                                    }
                                }
                                catch (Exception fetchEx)
                                {
                                    _logger.LogWarning(fetchEx, "Failed to download image bytes for item {Idx} '{Name}' from {Url}", idx, name, imageUrl);
                                }
                            }
                        }
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
                            ImageUrl: imageUrl,
                            ImageData: imageData,
                            ImageContentType: imageContentType
                        ));
                        _logger.LogInformation("[{Idx}/{Total}] {Name} ({Cat}) @ {Lat},{Lon}",
                            idx + 1, totalItems, name, category ?? "-", coords.Value.lat, coords.Value.lon);
                    }
                    else
                    {
                        _logger.LogWarning("[{Idx}/{Total}] Skipped {Name} -- no coordinates found", idx + 1, totalItems, name);
                    }

                    onProgress?.Invoke(results.Count);

                    // Go back to the list. The detail view is a full navigation on
                    // Google Maps, so the DOM snapshot we come back to is a fresh one
                    // — our data-scraper-* attributes are gone. Re-run discovery so
                    // the next iteration can address its card by index again.
                    await page.GoBackAsync(new PageGoBackOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 10000 });
                    await page.WaitForTimeoutAsync(1500);
                    await DiscoverAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process item {Idx}", idx);
                    try
                    {
                        await page.GoBackAsync();
                        await page.WaitForTimeoutAsync(2000);
                        await DiscoverAsync();
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
        /// IE-14: Delegates to shared PoiUrlHelper.ExtractCoordinatesFromUrl to eliminate
        /// duplicated @/ coordinate parsing logic.
        /// </summary>
        private static (double lat, double lon)? ExtractCoordinates(string url)
            => PoiUrlHelper.ExtractCoordinatesFromUrl(url);
    }
}
