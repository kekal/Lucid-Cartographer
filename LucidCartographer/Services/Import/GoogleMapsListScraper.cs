using LucidCartographer.Services;
using LucidCartographer.Services.Browser;
using Microsoft.Playwright;
using Polly;
using Polly.Registry;

namespace LucidCartographer.Services.Import;

public class GoogleMapsListScraper(
    ILogger<GoogleMapsListScraper> logger,
    ResiliencePipelineProvider<string> pipelineProvider,
    GoogleBrowserLock browserLock,
    IBrowserSession session)
    : IGoogleMapsListScraper
{
    // Separate semaphore for the interactive FetchSavedListsAsync flow —
    // this must NOT go through the "scraper" Polly pipeline which has a
    // 10-min timeout and concurrency=1 meant for headless scrapes.
    private readonly SemaphoreSlim _fetchListsSemaphore = new(1, 1);

    // Concurrency, retry, and timeout enforced by the "scraper" Polly resilience pipeline.
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline("scraper");

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

    public async Task<ScrapeResult> ScrapeAsync(string listUrl, Action<int>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(listUrl))
        {
            throw new ArgumentException("List URL must not be empty.", nameof(listUrl));
        }

        var trimmedUrl = listUrl.Trim();
        if (!AllowedUrlPrefixes.Any(prefix => trimmedUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("URL must be a Google Maps URL (https://www.google.com/maps/... or https://maps.app.goo.gl/...).", nameof(listUrl));
        }

        // Polly pipeline enforces single-flight concurrency with retry and timeout.
        // Callers may catch RateLimiterRejectedException to surface "scraper busy" errors.
        try
        {
            return await _pipeline.ExecuteAsync(
                async ct => await ScrapeInternalAsync(trimmedUrl, onProgress, ct),
                cancellationToken);
        }
        catch (Polly.RateLimiting.RateLimiterRejectedException ex)
        {
            logger.LogWarning(ex, "Scraper rate limiter rejected the request — another scrape is already running");
            throw;
        }
    }

    public bool HasBrowserProfile => session.HasProfile;

    public Task ResetBrowserProfileAsync(CancellationToken cancellationToken = default)
        => session.ResetProfileAsync(cancellationToken);

    public async Task<IReadOnlyList<SavedListInfo>> FetchSavedListsAsync(CancellationToken cancellationToken = default)
    {
        if (!await _fetchListsSemaphore.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("Another Fetch My Lists operation is already running.");
        }

        try
        {
            return await FetchSavedListsInternalAsync(cancellationToken);
        }
        finally
        {
            _fetchListsSemaphore.Release();
        }
    }

    private async Task<IReadOnlyList<SavedListInfo>> FetchSavedListsInternalAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Fetching saved Google Maps lists (profile: {Path})", session.ProfilePath);

        // Serialise against the exporter / authenticated scrape — all drive the
        // single shared browser. Refuse immediately (rather than freeze) if a
        // Google browser operation is already running.
        using var lease = await browserLock.TryAcquireAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "A Google browser operation is already running. Please wait for it to finish, then try again.");

        // Mobile web Maps: simpler, stable DOM (clean list rows) — far better to
        // scrape than the obfuscated/rotating desktop panel.
        var page = await session.NewMobilePageAsync(cancellationToken);
        try
        {
            return await FetchSavedListsOnPageAsync(page, cancellationToken);
        }
        finally
        {
            try { await page.CloseAsync(); } catch (Exception ex) { logger.LogDebug(ex, "Error closing fetch-lists page"); }
        }
    }

    /// <summary>
    /// Click the first <c>button</c>/<c>a</c> whose accessible name (aria-label or
    /// trimmed text) equals one of <paramref name="labels"/> (case-insensitive),
    /// via a JS click to avoid mobile pointer-interception. Returns false if none.
    /// When <paramref name="preferSelector"/> is supplied, elements matching that
    /// CSS selector are scanned first before falling back to the generic scan.
    /// </summary>
    private static async Task<bool> ClickByLabelAsync(IPage page, string[] labels, string? preferSelector = null)
    {
        // Pass the array as the single evaluate arg (Playwright serialises it to a
        // JS array). Passing a JSON *string* makes `labels.map` throw in-page.
        return await page.EvaluateAsync<bool>(@"
            ([labels, preferSelector]) => {
                const want = labels.map(s => s.toLowerCase());
                const check = (el) => {
                    const t = ((el.getAttribute('aria-label') || el.textContent || '').trim()).toLowerCase();
                    if (t && want.includes(t)) { el.click(); return true; }
                    return false;
                };
                if (preferSelector) {
                    for (const el of document.querySelectorAll(preferSelector)) {
                        if (check(el)) return true;
                    }
                }
                for (const el of document.querySelectorAll('button, a, [role=tab], [role=button]')) {
                    if (check(el)) return true;
                }
                return false;
            }", new object[] { labels, preferSelector! });
    }

    /// <summary>Log page URL and top buttons/headings for debugging unexpected pages.</summary>
    private async Task DumpPageAsync(IPage page, string reason)
    {
        try
        {
            var dump = await page.EvaluateAsync<string>(@"
                () => {
                    const txt = els => Array.from(els).map(e => (e.getAttribute('aria-label') || e.textContent || '').replace(/\s+/g,' ').trim()).filter(Boolean).slice(0, 25).join(' | ');
                    return 'BUTTONS: ' + txt(document.querySelectorAll('button, a, [role=tab]')) +
                           ' || HEADINGS: ' + txt(document.querySelectorAll('h1, h2, h3'));
                }");
            logger.LogDebug("PAGE DUMP ({Reason}) url={Url} :: {Dump}", reason, page.Url, dump);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Page dump failed ({Reason})", reason);
        }
    }

    /// <summary>Dismiss mobile "open in app" interstitials across EN/RU variants.</summary>
    private async Task DismissAppInterstitialAsync(IPage page)
    {
        var dismissed = await ClickByLabelAsync(page,
            ["Stay in browser", "Continue in browser", "Use Google Maps in your browser",
            "Not now", "No thanks", "Dismiss", "Close",
            "Остаться в браузере", "Продолжить в браузере", "Не сейчас", "Нет, спасибо", "Закрыть"]);
        if (dismissed)
        {
            logger.LogInformation("Dismissed an 'open in app' interstitial/banner");
            await page.WaitForTimeoutAsync(600);
        }
    }

    /// <summary>Deterministically open the mobile Saved tab via full menu nav (GoBack/URL-restore unreliable).</summary>
    private async Task OpenSavedTabAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await page.GotoAsync("https://www.google.com/maps?hl=en",
            new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = 30000 });
        await page.WaitForTimeoutAsync(3500);
        try { await GoogleConsent.DismissAsync(page, logger); } catch (Exception ex) { logger.LogDebug(ex, "consent"); }
        try { await DismissAppInterstitialAsync(page); } catch (Exception ex) { logger.LogDebug(ex, "interstitial"); }

        if (await ClickByLabelAsync(page, ["Menu", "Меню"]))
        {
            await page.WaitForTimeoutAsync(700);
        }
        if (!await ClickByLabelAsync(page, ["Your places", "Saved places", "Мои места"]))
        {
            logger.LogWarning("'Your places' control not found on Saved-tab nav (url={Url})", page.Url);
        }
        await page.WaitForTimeoutAsync(1200);
        if (!await ClickByLabelAsync(page, ["Saved", "Сохраненные", "Сохранённые"], preferSelector: "[role=tab]"))
        {
            logger.LogWarning("'Saved' tab control not found on Saved-tab nav (url={Url})", page.Url);
        }
        await page.WaitForTimeoutAsync(2000);
    }

    private async Task<IReadOnlyList<SavedListInfo>> FetchSavedListsOnPageAsync(IPage page, CancellationToken cancellationToken)
    {
        await OpenSavedTabAsync(page, cancellationToken);
        await DumpPageAsync(page, "after Saved-tab nav");

        if (!await GoogleSignIn.IsSignedInAsync(page, logger))
        {
            throw new InvalidOperationException(
                "Not signed in to Google. Open the Google session page (Data Sources → Google session), " +
                "sign in, then try Fetch My Lists again.");
        }

        var listsJson = await page.EvaluateAsync<System.Text.Json.JsonElement>(
            GoogleMapsScraperScripts.DiscoverSavedListsMobile);

        // Re-resolve by name on each pass — DOM indices are only valid for current state.
        var discovered = ParseDiscoveredLists(listsJson);

        logger.LogInformation("Discovered {Count} saved list cards, clicking each to capture URLs", discovered.Count);

        var results = new List<SavedListInfo>();
        var savedPanelUrl = page.Url;
        var currentTags = discovered;

        foreach (var (_, cardName, cardCount) in discovered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Re-resolve by name — positional indices change across re-opens.
                var match = currentTags.FirstOrDefault(t => string.Equals(t.Name, cardName, StringComparison.Ordinal));
                if (match.Name is null)
                {
                    logger.LogWarning("List '{Name}' not present in the current Saved tab, skipping", cardName);
                    continue;
                }

                // Use JS click to avoid pointer-interception issues
                var clicked = await page.EvaluateAsync<bool>($@"
                        (() => {{
                            const el = document.querySelector('[data-savedlist-idx=""{match.Idx}""]');
                            if (!el) return false;
                            el.click();
                            return true;
                        }})()");

                if (!clicked)
                {
                    logger.LogWarning("Card {Idx} '{Name}' not found in DOM, skipping", match.Idx, cardName);
                    continue;
                }

                await page.WaitForTimeoutAsync(3000);

                var listUrl = page.Url;
                logger.LogInformation("Card '{Name}' → URL: {Url}", cardName, listUrl);

                if (!string.IsNullOrEmpty(listUrl) && listUrl != savedPanelUrl)
                {
                    results.Add(new SavedListInfo(cardName, listUrl, cardCount));
                }
                else
                {
                    logger.LogWarning("Card '{Name}' click did not navigate, skipping", cardName);
                }

                cancellationToken.ThrowIfCancellationRequested();
                await OpenSavedTabAsync(page, cancellationToken);
                var retagJson = await page.EvaluateAsync<System.Text.Json.JsonElement>(GoogleMapsScraperScripts.DiscoverSavedListsMobile);
                currentTags = ParseDiscoveredLists(retagJson);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to capture URL for card '{Name}'", cardName);
            }
        }

        logger.LogInformation("Discovered {Count} saved lists with URLs", results.Count);
        return results;
    }

    /// <summary>Parse saved-list rows; indices are only valid for current DOM state.</summary>
    private static List<(int Idx, string Name, int? Count)> ParseDiscoveredLists(System.Text.Json.JsonElement arr)
    {
        var parsed = new List<(int Idx, string Name, int? Count)>();
        foreach (var item in arr.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var nEl) && nEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? nEl.GetString() : null;
            var idx = item.TryGetProperty("idx", out var iEl) && iEl.ValueKind == System.Text.Json.JsonValueKind.Number
                ? iEl.GetInt32() : -1;
            int? count = item.TryGetProperty("count", out var cEl) && cEl.ValueKind == System.Text.Json.JsonValueKind.Number
                ? cEl.GetInt32() : null;

            if (!string.IsNullOrWhiteSpace(name) && idx >= 0)
            {
                parsed.Add((idx, name!, count));
            }
        }
        return parsed;
    }

    private async Task<ScrapeResult> ScrapeInternalAsync(string trimmedUrl, Action<int>? onProgress, CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting scrape of {Url}", trimmedUrl);

        // Serialise against other Google browser operations (shared session).
        using var lease = await browserLock.AcquireAsync(cancellationToken);

        var page = await session.NewPageAsync(cancellationToken);

        try
        {
            await page.GotoAsync(trimmedUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
            await page.WaitForTimeoutAsync(5000);
            logger.LogInformation("Page URL after navigation: {Url}", page.Url);

            await GoogleConsent.DismissAsync(page, logger);
            logger.LogInformation("Post-consent URL: {Url}", page.Url);

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
                            logger.LogInformation("Extracted list name: {Name}", listName);
                            break;
                        }
                        listName = null;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to extract list name");
            }

            // Discover list panel via DOM topology (class names are obfuscated/rotating).
            // Tags stable data attributes for all downstream operations.
            (int total, bool scrollFound, int divsExamined, string? diag) discovery = (0, false, 0, null);
            var reloadAttempted = false;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                discovery = await DiscoverAsync();
                if (discovery.total > 0)
                {
                    break;
                }

                if (attempt == 10 && !reloadAttempted)
                {
                    logger.LogInformation(
                        "List panel still empty after 10s (examined {Divs} divs); reloading once",
                        discovery.divsExamined);
                    reloadAttempted = true;
                    try
                    {
                        await page.ReloadAsync(new PageReloadOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = 15000
                        });
                    }
                    catch (TimeoutException) { }
                }
                await page.WaitForTimeoutAsync(1000);
            }

            if (discovery.total == 0)
            {
                logger.LogWarning(
                    "Could not locate list cards via DOM topology. " +
                    "Divs examined: {Divs}. Top containers by text length: {Diag}. " +
                    "Page may not be a list URL, or Google restructured the list panel.",
                    discovery.divsExamined, discovery.diag ?? "(none)");
            }
            else
            {
                logger.LogInformation(
                    "Discovered {Count} initial list items (examined {Divs} divs)",
                    discovery.total, discovery.divsExamined);

                try
                {
                    var dump = await page.EvaluateAsync<string>(@"
                        (() => {
                            const card = document.querySelector('[data-scraper-idx=""0""]');
                            if (!card) return '(none)';
                            const btn = card.querySelector('button');
                            const attrs = (el) => el ? Array.from(el.attributes).map(a => a.name + '=' + a.value.slice(0, 120)).join(' | ') : '(no el)';
                            return JSON.stringify({
                                cardAttrs: attrs(card),
                                btnAttrs: attrs(btn),
                                html: card.outerHTML.slice(0, 1500)
                            });
                        })()");
                    logger.LogInformation("DIAG first-card: {Dump}", dump);
                }
                catch (Exception ex) { logger.LogWarning(ex, "DIAG dump failed"); }
            }

            // Scroll to load lazy-rendered cards; terminate after 3 stable rounds.
            if (discovery.total > 0)
            {
                var scrollContainer = page.Locator("[data-scraper-scroll='1']").First;
                var previousCount = discovery.total;
                var stableRounds = 0;

                for (var i = 0; i < 100; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await scrollContainer.EvaluateAsync("el => el.scrollTop = el.scrollHeight");
                    await page.WaitForTimeoutAsync(1500);

                    var (currentCount, _, _, _) = await DiscoverAsync();
                    logger.LogInformation("Scroll {Round}: {Count} places tagged", i + 1, currentCount);
                    onProgress?.Invoke(currentCount);

                    if (currentCount == previousCount)
                    {
                        stableRounds++;
                        if (stableRounds >= 3)
                        {
                            break;
                        }
                    }
                    else
                    {
                        stableRounds = 0;
                    }
                    previousCount = currentCount;
                }
            }

            // Harvest all card metadata in one JS round-trip; fallback to click-through for coordinates if needed.
            var harvestJson = await page.EvaluateAsync<System.Text.Json.JsonElement>(GoogleMapsScraperScripts.HarvestAll);
            var cards = harvestJson.EnumerateArray().ToList();
            var totalItems = cards.Count;
            var results = new List<ImportedPoi>();

            if (totalItems == 0)
            {
                logger.LogWarning("Harvest returned zero cards — list panel may have been empty.");
            }
            else
            {
                logger.LogInformation("Harvested metadata for {Count} list cards", totalItems);
            }

            for (var i = 0; i < cards.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var card = cards[i];

                var idx = card.TryGetProperty("idx", out var idxEl) && idxEl.ValueKind == System.Text.Json.JsonValueKind.Number ? idxEl.GetInt32() : i;
                var name = card.TryGetProperty("name", out var nEl) && nEl.ValueKind == System.Text.Json.JsonValueKind.String ? nEl.GetString() : null;
                var href = card.TryGetProperty("href", out var hEl) && hEl.ValueKind == System.Text.Json.JsonValueKind.String ? hEl.GetString() : null;

                if (string.IsNullOrWhiteSpace(name))
                {
                    logger.LogWarning("[{Idx}/{Total}] Skipping card — missing name", i + 1, totalItems);
                    continue;
                }

                (double lat, double lon)? coords = null;
                if (!string.IsNullOrEmpty(href))
                {
                    coords = ExtractCoordinates(href);
                }

                var hrefIsPlaceUrl = !string.IsNullOrEmpty(href) && href.Contains("/maps/place/");
                if (!hrefIsPlaceUrl)
                {
                    logger.LogInformation("[{Idx}/{Total}] '{Name}' — href '{Href}' is not a place URL, trying click-through", i + 1, totalItems, name, href ?? "(none)");
                    try
                    {
                        var cardSelector = $"[data-scraper-idx=\"{idx}\"]";
                        var cardEl = page.Locator(cardSelector).First;
                        if (await cardEl.IsVisibleAsync())
                        {
                            await cardEl.ClickAsync();
                            for (var w = 0; w < 40; w++)
                            {
                                var u = page.Url;
                                if (u.Contains("/maps/place/") && u.Contains("/@"))
                                {
                                    href = u;
                                    coords = ExtractCoordinates(href);
                                    logger.LogInformation("[{Idx}/{Total}] '{Name}' — click-through resolved: {Url}", i + 1, totalItems, name, href);
                                    break;
                                }
                                await page.WaitForTimeoutAsync(300);
                            }
                            await page.GoBackAsync(new PageGoBackOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 15000 });
                            await page.WaitForTimeoutAsync(1000);
                        }
                    }
                    catch (Exception clickEx)
                    {
                        logger.LogWarning(clickEx, "[{Idx}/{Total}] '{Name}' — click-through failed, deferred to enrichment", i + 1, totalItems, name);
                    }
                }

                if (coords == null && string.IsNullOrEmpty(href))
                {
                    logger.LogInformation("[{Idx}/{Total}] '{Name}' — no coords after click-through, deferred to enrichment", i + 1, totalItems, name);
                }

                double? rating = null;
                if (card.TryGetProperty("rating", out var rEl) && rEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    if (double.TryParse(rEl.GetString()!.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r))
                    {
                        rating = r;
                    }
                }

                int? reviewCount = null;
                if (card.TryGetProperty("reviewCount", out var rcEl) && rcEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var digits = new string(rcEl.GetString()!.Where(char.IsDigit).ToArray());
                    if (int.TryParse(digits, out var rc))
                    {
                        reviewCount = rc;
                    }
                }

                var category = card.TryGetProperty("category", out var cEl) && cEl.ValueKind == System.Text.Json.JsonValueKind.String ? cEl.GetString() : null;
                var description = card.TryGetProperty("description", out var dEl) && dEl.ValueKind == System.Text.Json.JsonValueKind.String ? dEl.GetString() : null;

                // Fetch image bytes now — signed token expires in minutes; download before returning from scraper.
                string? imageUrl = null;
                byte[]? imageData = null;
                string? imageContentType = null;
                if (card.TryGetProperty("imageSrc", out var imgEl) && imgEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var rawSrc = imgEl.GetString();
                    if (!string.IsNullOrEmpty(rawSrc) && rawSrc.Contains("googleusercontent.com"))
                    {
                        var equalsIdx = rawSrc.LastIndexOf('=');
                        var baseUrl = equalsIdx > 0 ? rawSrc[..equalsIdx] : rawSrc;
                        imageUrl = baseUrl + "=w1024";
                        try
                        {
                            var resp = await page.Context.APIRequest.GetAsync(imageUrl);
                            if (resp.Status == 200)
                            {
                                imageData = await resp.BodyAsync();
                                imageContentType = resp.Headers.TryGetValue("content-type", out var ct) ? ct : "image/jpeg";
                            }
                            else
                            {
                                logger.LogWarning("Image fetch for '{Name}' returned HTTP {Status}", name, resp.Status);
                            }
                        }
                        catch (Exception fetchEx)
                        {
                            logger.LogWarning(fetchEx, "Failed to download image bytes for '{Name}' from {Url}", name, imageUrl);
                        }
                    }
                }

                var lat = coords?.lat;
                var lon = coords?.lon;
                results.Add(new ImportedPoi(
                    Name: name,
                    Latitude: lat,
                    Longitude: lon,
                    GoogleMapsUrl: href,
                    // Address/Website/Phone filled later by PoiEnrichmentBackgroundService.
                    Address: null,
                    Category: category,
                    Description: description,
                    Rating: rating,
                    ReviewCount: reviewCount,
                    Website: null,
                    Phone: null,
                    ImageUrl: imageUrl,
                    ImageData: imageData,
                    ImageContentType: imageContentType
                ));

                logger.LogInformation("[{Idx}/{Total}] {Name} ({Cat}) @ {Lat},{Lon}",
                    i + 1, totalItems, name, category ?? "-", lat, lon);
                onProgress?.Invoke(results.Count);
            }

            logger.LogInformation("Successfully scraped {Count} places from list '{ListName}'", results.Count, listName ?? "unknown");
            return new ScrapeResult { ListName = listName, Pois = results };

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
        }
        finally
        {
            try { await page.CloseAsync(); } catch (Exception ex) { logger.LogDebug(ex, "Error closing scrape page"); }
        }
    }

    /// <summary>Extract @lat,lon coordinates from a Google Maps URL.</summary>
    private static (double lat, double lon)? ExtractCoordinates(string url)
        => PoiUrlHelper.ExtractCoordinatesFromUrl(url);
}
