using Microsoft.Playwright;
using Polly;
using Polly.Registry;

namespace LucidCartographer.Services.Import
{
    public class GoogleMapsListScraper : IGoogleMapsListScraper
    {
        private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        // HIGH-07: Concurrency, retry, and timeout are now enforced by the
        // "scraper" Polly resilience pipeline registered in Program.cs
        // (ConcurrencyLimiter(permits=1) + Retry + Timeout). This replaces
        // the previous static SemaphoreSlim + manual timeout plumbing.
        private readonly ResiliencePipeline _pipeline;

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

        public GoogleMapsListScraper(
            ILogger<GoogleMapsListScraper> logger,
            ResiliencePipelineProvider<string> pipelineProvider)
        {
            _logger = logger;
            _pipeline = pipelineProvider.GetPipeline("scraper");
        }

        public async Task<ScrapeResult> ScrapeAsync(string listUrl, Action<int>? onProgress = null, CancellationToken cancellationToken = default)
        {
            // URL validation: prevent SSRF
            if (string.IsNullOrWhiteSpace(listUrl))
                throw new ArgumentException("List URL must not be empty.", nameof(listUrl));

            var trimmedUrl = listUrl.Trim();
            if (!AllowedUrlPrefixes.Any(prefix => trimmedUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("URL must be a Google Maps URL (https://www.google.com/maps/... or https://maps.app.goo.gl/...).", nameof(listUrl));

            // Polly "scraper" pipeline enforces: single-flight (permit=1),
            // retry (2 attempts with jittered backoff), and a 10-minute
            // per-attempt timeout. Upstream callers should catch
            // Polly.RateLimiting.RateLimiterRejectedException if they want
            // to surface a "scraper busy" message; previously this was
            // TimeoutException("Timed out waiting for scraper availability…").
            try
            {
                return await _pipeline.ExecuteAsync(
                    async ct => await ScrapeInternalAsync(trimmedUrl, onProgress, ct),
                    cancellationToken);
            }
            catch (Polly.RateLimiting.RateLimiterRejectedException ex)
            {
                _logger.LogWarning(ex, "Scraper rate limiter rejected the request — another scrape is already running");
                throw;
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

            // Initial discovery — wait up to ~30s for the list panel to hydrate.
            // Consent redirect + lazy rendering can delay the list panel by
            // several seconds on a cold cache. If we still see nothing after
            // ~10s, trigger a single Reload() — empirically, the first nav
            // post-consent sometimes lands on a half-rendered `/@/` shell
            // where the list data payload never gets parsed into cards, but
            // a cheap reload picks up the correct state because the cookie
            // jar and URL are already canonical by then.
            (int total, bool scrollFound, int divsExamined, string? diag) discovery = (0, false, 0, null);
            var reloadAttempted = false;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                discovery = await DiscoverAsync();
                if (discovery.total > 0) break;
                if (attempt == 10 && !reloadAttempted)
                {
                    _logger.LogInformation(
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
                    catch (TimeoutException) { /* best effort */ }
                }
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

                // DIAG: dump first card's structure so we can spot place-id attrs
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
                    _logger.LogInformation("DIAG first-card: {Dump}", dump);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "DIAG dump failed"); }
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

            // Fast-path harvest: read every tagged card's visible data in one
            // JS round-trip. All card-level metadata (name, rating, category,
            // description, image URL) comes from this single pass — we do NOT
            // re-read the DOM per item. For each card we then try the fast
            // path: if the card embeds a place anchor whose href contains
            // `@lat,lon,…`, we parse coords directly. Otherwise the card is
            // an anchor-less `<button jsaction>` (common on personal / shared
            // lists) and we fall back to a click-through just to navigate the
            // URL bar, read the coords, and go back. Address / website / phone
            // are filled later by PoiEnrichmentBackgroundService.
            var harvestJson = await page.EvaluateAsync<System.Text.Json.JsonElement>(GoogleMapsScraperScripts.HarvestAll);
            var cards = harvestJson.EnumerateArray().ToList();
            var totalItems = cards.Count;
            var results = new List<ImportedPoi>();

            if (totalItems == 0)
            {
                _logger.LogWarning("Harvest returned zero cards — list panel may have been empty.");
            }
            else
            {
                _logger.LogInformation("Harvested metadata for {Count} list cards", totalItems);
            }

            for (int i = 0; i < cards.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var card = cards[i];

                var idx = card.TryGetProperty("idx", out var idxEl) && idxEl.ValueKind == System.Text.Json.JsonValueKind.Number ? idxEl.GetInt32() : i;
                string? name = card.TryGetProperty("name", out var nEl) && nEl.ValueKind == System.Text.Json.JsonValueKind.String ? nEl.GetString() : null;
                string? href = card.TryGetProperty("href", out var hEl) && hEl.ValueKind == System.Text.Json.JsonValueKind.String ? hEl.GetString() : null;

                if (string.IsNullOrWhiteSpace(name))
                {
                    _logger.LogWarning("[{Idx}/{Total}] Skipping card — missing name", i + 1, totalItems);
                    continue;
                }

                // Fast path: href may already contain coordinates.
                (double lat, double lon)? coords = null;
                if (!string.IsNullOrEmpty(href))
                {
                    coords = ExtractCoordinates(href);
                }

                // No click-through here. When href has no coords (common on
                // personal / shared lists whose cards are anchor-less
                // `<button jsaction>` elements), we leave coords null and
                // persist the POI with a (0,0) placeholder. The background
                // enrichment service then runs a name-based Google Maps
                // search per POI to fill real coords + address/website/phone.
                if (coords == null)
                {
                    _logger.LogInformation("[{Idx}/{Total}] '{Name}' — no href coords, deferred to enrichment", i + 1, totalItems, name);
                }

                // Rating (text → double)
                double? rating = null;
                if (card.TryGetProperty("rating", out var rEl) && rEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    if (double.TryParse(rEl.GetString()!.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r))
                        rating = r;
                }

                // Review count (raw text → digits → int)
                int? reviewCount = null;
                if (card.TryGetProperty("reviewCount", out var rcEl) && rcEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var digits = new string(rcEl.GetString()!.Where(char.IsDigit).ToArray());
                    if (int.TryParse(digits, out var rc)) reviewCount = rc;
                }

                string? category = card.TryGetProperty("category", out var cEl) && cEl.ValueKind == System.Text.Json.JsonValueKind.String ? cEl.GetString() : null;
                string? description = card.TryGetProperty("description", out var dEl) && dEl.ValueKind == System.Text.Json.JsonValueKind.String ? dEl.GetString() : null;

                // Image: upsize the thumbnail URL (`…=w92-h92-k-no` → `…=w1024`)
                // and download the bytes now, while the signed `gps-cs-s` token
                // is still fresh. Google blocks cross-origin hotlinking and the
                // token expires in minutes, so persisting only the URL is useless.
                // APIRequest carries session cookies and bypasses the anti-hotlink
                // check; bytes land on the Poi entity and are served from
                // /api/poi-image/{id}.
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
                            var resp = await context.APIRequest.GetAsync(imageUrl);
                            if (resp.Status == 200)
                            {
                                imageData = await resp.BodyAsync();
                                imageContentType = resp.Headers.TryGetValue("content-type", out var ct) ? ct : "image/jpeg";
                            }
                            else
                            {
                                _logger.LogWarning("Image fetch for '{Name}' returned HTTP {Status}", name, resp.Status);
                            }
                        }
                        catch (Exception fetchEx)
                        {
                            _logger.LogWarning(fetchEx, "Failed to download image bytes for '{Name}' from {Url}", name, imageUrl);
                        }
                    }
                }

                // Placeholder (0,0) coords when href had none — enrichment
                // service will fill real lat/lon via a name-based search.
                var lat = coords?.lat ?? 0.0;
                var lon = coords?.lon ?? 0.0;
                results.Add(new ImportedPoi(
                    Name: name!,
                    Latitude: lat,
                    Longitude: lon,
                    GoogleMapsUrl: href,
                    // Address / Website / Phone deliberately null — filled later
                    // by PoiEnrichmentBackgroundService via the place URL.
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

                _logger.LogInformation("[{Idx}/{Total}] {Name} ({Cat}) @ {Lat},{Lon}",
                    i + 1, totalItems, name, category ?? "-", lat, lon);
                onProgress?.Invoke(results.Count);
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
