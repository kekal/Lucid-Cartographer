using Microsoft.Playwright;
using Polly;
using Polly.Registry;

namespace LucidCartographer.Services.Import
{
    public class GoogleMapsListScraper : IGoogleMapsListScraper
    {
        private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        private static readonly string BrowserProfilePath =
            Path.Combine(AppContext.BaseDirectory, "data", "chrome-profile");

        // Separate semaphore for the interactive FetchSavedListsAsync flow —
        // this must NOT go through the "scraper" Polly pipeline which has a
        // 10-min timeout and concurrency=1 meant for headless scrapes.
        private readonly SemaphoreSlim _fetchListsSemaphore = new(1, 1);

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

        public bool HasBrowserProfile
        {
            get
            {
                try
                {
                    return Directory.Exists(BrowserProfilePath) &&
                           Directory.EnumerateFileSystemEntries(BrowserProfilePath).Any();
                }
                catch
                {
                    return false;
                }
            }
        }

        public void ResetBrowserProfile()
        {
            try
            {
                if (Directory.Exists(BrowserProfilePath))
                {
                    Directory.Delete(BrowserProfilePath, recursive: true);
                    _logger.LogInformation("Browser profile reset: {Path}", BrowserProfilePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete browser profile at {Path}", BrowserProfilePath);
                throw;
            }
        }

        public async Task<IReadOnlyList<SavedListInfo>> FetchSavedListsAsync(CancellationToken cancellationToken = default)
        {
            if (!await _fetchListsSemaphore.WaitAsync(0, cancellationToken))
                throw new InvalidOperationException("Another Fetch My Lists operation is already running.");

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
            _logger.LogInformation("Fetching saved Google Maps lists (profile: {Path})", BrowserProfilePath);

            await PlaywrightBootstrap.EnsureBrowsersInstalledAsync(_logger, cancellationToken);

            Directory.CreateDirectory(BrowserProfilePath);

            using var playwright = await Playwright.CreateAsync();
            await using var context = await playwright.Chromium.LaunchPersistentContextAsync(
                BrowserProfilePath,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = false,
                    Locale = "en-US",
                    UserAgent = DefaultUserAgent,
                    Args = new[] { "--disable-blink-features=AutomationControlled" }
                });

            var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();

            // Navigate to Google Maps, then open the saved lists via the
            // hamburger menu → "Your places" → "Lists" tab. There is no
            // standalone /maps/lists URL; saved lists live inside the SPA.
            await page.GotoAsync("https://www.google.com/maps",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
            await page.WaitForTimeoutAsync(5000); // Let the SPA render

            _logger.LogInformation("Post-navigation URL: {Url}", page.Url);

            // Handle consent dialog (same pattern as ScrapeInternalAsync)
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
                        var clickTask = btn.ClickAsync();
                        try
                        {
                            await page.WaitForURLAsync(
                                u => !u.Contains("consent.google.com"),
                                new() { Timeout = 15000 });
                        }
                        catch (TimeoutException) { }
                        await clickTask;
                        try
                        {
                            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 });
                        }
                        catch (TimeoutException) { }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No consent dialog found or failed to click");
            }

            // Check if user is logged in. Two scenarios:
            // 1. URL redirected to accounts.google.com (explicit login page)
            // 2. On Maps page but not logged in (has a "Sign in" link)
            // In both cases, the browser is visible — wait for user to log in.
            async Task<bool> IsLoggedInAsync()
            {
                var url = page.Url;
                if (url.Contains("accounts.google.com") || url.Contains("signin"))
                {
                    _logger.LogInformation("IsLoggedIn: false (URL contains accounts/signin): {Url}", url);
                    return false;
                }
                // Check if the page has a sign-in link (ServiceLogin).
                // Important: do NOT match on generic 'accounts.google.com'
                // because the sign-OUT link also uses that domain.
                var signInHref = await page.EvaluateAsync<string>(@"
                    (() => {
                        const links = document.querySelectorAll('a');
                        for (const el of links) {
                            const href = el.href || '';
                            if (href.includes('ServiceLogin') || href.includes('/signin/identifier'))
                                return href;
                        }
                        return '';
                    })()");
                var isLoggedIn = string.IsNullOrEmpty(signInHref);
                _logger.LogInformation("IsLoggedIn: {Result} (signInHref='{Href}')", isLoggedIn, signInHref.Length > 100 ? signInHref[..100] : signInHref);
                return isLoggedIn;
            }

            var loginDeadline = DateTime.UtcNow.AddMinutes(5);
            while (!await IsLoggedInAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > loginDeadline)
                    throw new TimeoutException("Sign-in was not completed within 5 minutes. Please sign in to Google in the browser window.");

                _logger.LogInformation("Waiting for user to sign in... (URL: {Url})", page.Url);

                // If on the Maps page with a sign-in link, click it to redirect to login
                try
                {
                    var signInLink = page.Locator("a[href*='ServiceLogin']").First;
                    if (await signInLink.IsVisibleAsync())
                    {
                        await signInLink.ClickAsync();
                        _logger.LogInformation("Clicked sign-in link to redirect to Google login");
                        await page.WaitForTimeoutAsync(3000);
                    }
                }
                catch { /* best effort */ }

                await page.WaitForTimeoutAsync(2000);
            }

            _logger.LogInformation("User is logged in. Current URL: {Url}", page.Url);

            // After login, ensure we're on Google Maps
            if (!page.Url.Contains("/maps"))
            {
                await page.GotoAsync("https://www.google.com/maps",
                    new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000 });
            }

            await page.WaitForTimeoutAsync(2000);

            // Click "Saved" button — it's the 2nd item in the sidebar ul
            // XPath: /html/body/div[1]/div[2]/div[9]/div[8]/div/div/div/div[1]/ul/li[2]/button
            try
            {
                var savedBtn = page.Locator("ul > li:nth-child(2) > button").First;
                await savedBtn.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
                _logger.LogInformation("Clicked 'Saved' button in sidebar");
                await page.WaitForTimeoutAsync(3000);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to click 'Saved' button");
            }

            _logger.LogInformation("After Saved click, URL: {Url}", page.Url);

            // Clicking "Saved" may redirect to sign-in if session expired.
            // If so, wait for the user to log in, then navigate back to Maps.
            if (page.Url.Contains("accounts.google.com") || page.Url.Contains("signin"))
            {
                _logger.LogInformation("Redirected to sign-in after clicking Saved — waiting for login...");
                var loginDeadline2 = DateTime.UtcNow.AddMinutes(5);
                while (page.Url.Contains("accounts.google.com") || page.Url.Contains("signin"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow > loginDeadline2)
                        throw new TimeoutException("Sign-in was not completed within 5 minutes.");
                    await page.WaitForTimeoutAsync(2000);
                }
                _logger.LogInformation("Login completed, URL: {Url}", page.Url);

                // After login we land back on Maps — re-click "Saved"
                await page.WaitForTimeoutAsync(3000);
                try
                {
                    var savedBtn2 = page.Locator("ul > li:nth-child(2) > button").First;
                    await savedBtn2.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
                    _logger.LogInformation("Re-clicked 'Saved' button after login");
                    await page.WaitForTimeoutAsync(3000);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to re-click 'Saved' button after login");
                }
            }

            // Extract saved lists using JS (button.CsEnBe cards)
            var listsJson = await page.EvaluateAsync<System.Text.Json.JsonElement>(
                GoogleMapsScraperScripts.DiscoverSavedLists);

            // Parse discovered cards (name + count, no URLs yet)
            var discovered = new List<(int Idx, string Name, int? Count)>();
            foreach (var item in listsJson.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var nEl) && nEl.ValueKind == System.Text.Json.JsonValueKind.String
                    ? nEl.GetString() : null;
                var idx = item.TryGetProperty("idx", out var iEl) && iEl.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? iEl.GetInt32() : -1;
                int? count = item.TryGetProperty("count", out var cEl) && cEl.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? cEl.GetInt32() : null;

                if (!string.IsNullOrWhiteSpace(name) && idx >= 0)
                    discovered.Add((idx, name!, count));
            }

            _logger.LogInformation("Discovered {Count} saved list cards, clicking each to capture URLs", discovered.Count);

            // Click-through: click each card, capture the navigated URL, go back
            var results = new List<SavedListInfo>();
            var savedPanelUrl = page.Url;

            foreach (var (cardIdx, cardName, cardCount) in discovered)
            {
                try
                {
                    // Use JS click to avoid pointer-interception issues
                    var clicked = await page.EvaluateAsync<bool>($@"
                        (() => {{
                            const el = document.querySelector('[data-savedlist-idx=""{cardIdx}""]');
                            if (!el) return false;
                            el.click();
                            return true;
                        }})()");

                    if (!clicked)
                    {
                        _logger.LogWarning("Card {Idx} '{Name}' not found in DOM, skipping", cardIdx, cardName);
                        continue;
                    }

                    await page.WaitForTimeoutAsync(3000);

                    var listUrl = page.Url;
                    _logger.LogInformation("Card '{Name}' → URL: {Url}", cardName, listUrl);

                    if (!string.IsNullOrEmpty(listUrl) && listUrl != savedPanelUrl)
                    {
                        results.Add(new SavedListInfo(cardName, listUrl, cardCount));
                    }
                    else
                    {
                        _logger.LogWarning("Card '{Name}' click did not navigate, skipping", cardName);
                    }

                    // Go back to the saved lists panel
                    await page.GoBackAsync(new PageGoBackOptions { Timeout = 10000 });
                    await page.WaitForTimeoutAsync(2500);

                    // Re-tag cards (DOM may have been rebuilt after navigation)
                    await page.EvaluateAsync(GoogleMapsScraperScripts.DiscoverSavedLists);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to capture URL for card '{Name}'", cardName);
                }
            }

            _logger.LogInformation("Discovered {Count} saved lists with URLs", results.Count);

            // Close the browser (profile persists on disk)
            await context.CloseAsync();

            return results;
        }

        private async Task<ScrapeResult> ScrapeInternalAsync(string trimmedUrl, Action<int>? onProgress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting scrape of {Url}", trimmedUrl);

            await PlaywrightBootstrap.EnsureBrowsersInstalledAsync(_logger, cancellationToken);

            using var playwright = await Playwright.CreateAsync();

            // Use the persistent profile if available (needed for private/saved lists).
            // Otherwise fall back to an anonymous headless browser.
            IBrowser? browser = null;
            IBrowserContext context;
            if (HasBrowserProfile)
            {
                _logger.LogInformation("Using persistent browser profile for authenticated scrape");
                context = await playwright.Chromium.LaunchPersistentContextAsync(
                    BrowserProfilePath,
                    new BrowserTypeLaunchPersistentContextOptions
                    {
                        Headless = false,
                        Locale = "en-US",
                        UserAgent = DefaultUserAgent,
                        Args = new[] { "--disable-blink-features=AutomationControlled" }
                    });
            }
            else
            {
                browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true
                });
                context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    Locale = "en-US",
                    UserAgent = DefaultUserAgent
                });
            }

            try
            {
            var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();

            // Navigate to the list URL
            await page.GotoAsync(trimmedUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });

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

                // Click-through fallback: when the card has no place anchor
                // (common on personal / shared lists whose cards are anchor-less
                // `<button jsaction>` elements), click the card to navigate to
                // the place page, read the URL (which contains coords + place ID),
                // then go back to the list.
                if (coords == null)
                {
                    _logger.LogInformation("[{Idx}/{Total}] '{Name}' — no href, trying click-through", i + 1, totalItems, name);
                    try
                    {
                        var cardSelector = $"[data-scraper-idx=\"{idx}\"]";
                        var cardEl = page.Locator(cardSelector).First;
                        if (await cardEl.IsVisibleAsync())
                        {
                            await cardEl.ClickAsync();
                            // Wait for URL to transition to /maps/place/
                            for (int w = 0; w < 40; w++)
                            {
                                var u = page.Url;
                                if (u.Contains("/maps/place/") && u.Contains("/@"))
                                {
                                    href = u;
                                    coords = ExtractCoordinates(href);
                                    _logger.LogInformation("[{Idx}/{Total}] '{Name}' — click-through resolved: {Url}", i + 1, totalItems, name, href);
                                    break;
                                }
                                await page.WaitForTimeoutAsync(300);
                            }
                            // Navigate back to the list
                            await page.GoBackAsync(new PageGoBackOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 15000 });
                            // Wait for the list to re-render
                            await page.WaitForTimeoutAsync(1000);
                        }
                    }
                    catch (Exception clickEx)
                    {
                        _logger.LogWarning(clickEx, "[{Idx}/{Total}] '{Name}' — click-through failed, deferred to enrichment", i + 1, totalItems, name);
                    }
                }

                if (coords == null && string.IsNullOrEmpty(href))
                {
                    _logger.LogInformation("[{Idx}/{Total}] '{Name}' — no coords after click-through, deferred to enrichment", i + 1, totalItems, name);
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

            } // end try
            finally
            {
                await context.CloseAsync();
                if (browser != null) await browser.CloseAsync();
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
