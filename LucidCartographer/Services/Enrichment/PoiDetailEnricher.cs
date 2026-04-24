using Microsoft.Playwright;
using LucidCartographer.Services;
using Microsoft.Extensions.Logging;

namespace LucidCartographer.Services.Enrichment
{
    public record EnrichedDetails(
        string? Address,
        string? Website,
        string? Phone,
        double? Latitude,
        double? Longitude,
        string? GoogleMapsUrl,
        string? ImageUrl);

    /// <summary>
    /// Opens a Google Maps place on a provided Playwright page and reads the
    /// detail panel's address / website / phone, plus coordinates from the URL.
    /// Stateless helper — the caller owns the page lifecycle so we can pool /
    /// parallelize tabs in the background enrichment service.
    ///
    /// Two entry points:
    ///   - <see cref="EnrichAsync(IPage, string, CancellationToken)"/> — when
    ///     we already know the place URL.
    ///   - <see cref="EnrichByNameAsync(IPage, string, string?, CancellationToken)"/>
    ///     — when the scraper only captured the card name (shared/personal
    ///     list cards that have no place anchors). Uses Google Maps' public
    ///     search API URL to land on the place, then extracts coords from
    ///     the resulting URL.
    /// </summary>
    public static class PoiDetailEnricher
    {
        public static Task<EnrichedDetails> EnrichAsync(IPage page, string placeUrl, CancellationToken ct, ILogger? logger = null)
            => EnrichCoreAsync(page, placeUrl, ct, logger);

        public static Task<EnrichedDetails> EnrichByNameAsync(IPage page, string name, string? hint, CancellationToken ct, ILogger? logger = null)
        {
            // Appending the hint (category or description first line) helps
            // disambiguate common names. Example: "Zebra" + "Zabrze, Poland"
            // lands on the specific place rather than the generic feature.
            var query = string.IsNullOrWhiteSpace(hint) ? name : $"{name} {hint}";
            var url = "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(query);
            return EnrichCoreAsync(page, url, ct, logger);
        }

        private static async Task<EnrichedDetails> EnrichCoreAsync(IPage page, string navUrl, CancellationToken ct, ILogger? logger)
        {
            ct.ThrowIfCancellationRequested();

            // DOMContentLoaded, not NetworkIdle: Google Maps keeps background
            // XHRs going forever and NetworkIdle effectively never fires.
            await page.GotoAsync(navUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 20000
            });

            // Fresh BrowserContexts land on consent.google.com before the
            // real /maps URL. Click Accept and wait for the redirect to
            // complete. Shares cookies across enrichment calls, so after
            // the first successful click the rest skip this branch.
            if (page.Url.Contains("consent.google.com"))
            {
                string[] consentSelectors = {
                    "button[aria-label*='Accept']",
                    "button[aria-label*='accept']",
                    "button:has-text('Accept all')",
                    "button:has-text('Agree')",
                    "form[action*='consent'] button"
                };
                foreach (var sel in consentSelectors)
                {
                    try
                    {
                        var btn = page.Locator(sel).First;
                        if (await btn.IsVisibleAsync())
                        {
                            var clickTask = btn.ClickAsync();
                            try
                            {
                                await page.WaitForURLAsync(
                                    u => !u.Contains("consent.google.com"),
                                    new() { Timeout = 15000 });
                            }
                            catch (TimeoutException) { }
                            catch (PlaywrightException ex) when (IsNavigationAbortDuringConsent(ex))
                            {
                                logger?.LogDebug(ex, "Consent redirect wait aborted; continuing with current page URL '{Url}'", page.Url);
                            }
                            try
                            {
                                await clickTask;
                            }
                            catch (PlaywrightException ex) when (IsNavigationAbortDuringConsent(ex))
                            {
                                logger?.LogDebug(ex, "Consent click navigation aborted; continuing with current page URL '{Url}'", page.Url);
                            }
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Consent selector miss for '{Selector}'", sel);
                        EnrichmentMetrics.RecordSelectorMiss();
                    }
                }
            }

            // Wait for the URL to transition to the canonical /maps/place/
            // form — Google redirects the search URL to the matched place.
            // We also accept an @lat,lon segment as a sign that the map
            // has focused on the place.
            for (int i = 0; i < 30; i++)
            {
                ct.ThrowIfCancellationRequested();
                var u = page.Url;
                if (u.Contains("/maps/place/") && u.Contains("/@")) break;
                await Task.Delay(300, ct);
            }

            // Wait for the detail panel to hydrate enough to expose data-item-id
            // attributes. 10s upper bound — if nothing shows, fields stay null.
            try
            {
                await page.WaitForSelectorAsync("[data-item-id]", new() { Timeout = 10000 });
            }
            catch (TimeoutException) { /* best effort */ }

            var finalUrl = page.Url;
            var coords = PoiUrlHelper.ExtractCoordinatesFromUrl(finalUrl);

            var address = await TryInnerTextAsync(page, "button[data-item-id='address'] .fontBodyMedium, div[data-item-id='address']", "address", logger);
            var phone = await TryInnerTextAsync(page, "button[data-item-id*='phone'] .fontBodyMedium", "phone", logger);
            if (!string.IsNullOrWhiteSpace(address)) EnrichmentMetrics.RecordAddressFound();
            if (!string.IsNullOrWhiteSpace(phone)) EnrichmentMetrics.RecordPhoneFound();

            string? website = null;
            try
            {
                var webEl = page.Locator("a[data-item-id='authority']").First;
                if (await webEl.IsVisibleAsync())
                {
                    website = await webEl.GetAttributeAsync("href");
                    if (string.IsNullOrWhiteSpace(website))
                        website = (await webEl.InnerTextAsync()).Trim();
                    if (!string.IsNullOrWhiteSpace(website)) EnrichmentMetrics.RecordWebsiteFound();
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Website selector miss for Google Maps details panel");
                EnrichmentMetrics.RecordSelectorMiss();
            }

            var imageUrl = await TryExtractImageUrlAsync(page, logger);

            return new EnrichedDetails(
                Address: address,
                Website: website,
                Phone: phone,
                Latitude: coords?.lat,
                Longitude: coords?.lon,
                GoogleMapsUrl: finalUrl.Contains("/maps/place/") ? finalUrl : null,
                ImageUrl: imageUrl);
        }

        private static async Task<string?> TryInnerTextAsync(IPage page, string selector, string field, ILogger? logger)
        {
            try
            {
                var el = page.Locator(selector).First;
                if (await el.IsVisibleAsync())
                    return (await el.InnerTextAsync()).Trim();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Selector miss while extracting enrichment field '{Field}'", field);
                EnrichmentMetrics.RecordSelectorMiss();
            }
            return null;
        }

        private static bool IsNavigationAbortDuringConsent(PlaywrightException ex)
            => ex.Message.Contains("ERR_ABORTED", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("frame was detached", StringComparison.OrdinalIgnoreCase);

        private static async Task<string?> TryExtractImageUrlAsync(IPage page, ILogger? logger)
        {
            string[] selectors =
            {
                "button[jsaction*='pane.heroHeaderImage.click'] img[src]",
                "img[src*='googleusercontent.com']"
            };

            foreach (var selector in selectors)
            {
                try
                {
                    var img = page.Locator(selector).First;
                    if (await img.IsVisibleAsync())
                    {
                        var src = await img.GetAttributeAsync("src");
                        if (IsLikelyPlacePhotoUrl(src))
                            return src;
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Image selector miss for Google Maps details panel: {Selector}", selector);
                }
            }

            return null;
        }

        private static bool IsLikelyPlacePhotoUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return false;

            if (url.Contains("/maps/vt", StringComparison.OrdinalIgnoreCase)
                || url.Contains("/vt/", StringComparison.OrdinalIgnoreCase)
                || url.Contains("tile.openstreetmap.org", StringComparison.OrdinalIgnoreCase)
                || (url.Contains("x=", StringComparison.OrdinalIgnoreCase)
                    && url.Contains("y=", StringComparison.OrdinalIgnoreCase)
                    && url.Contains("z=", StringComparison.OrdinalIgnoreCase)))
                return false;

            return url.Contains("googleusercontent.com", StringComparison.OrdinalIgnoreCase)
                || url.Contains("streetviewpixels-pa.googleapis.com", StringComparison.OrdinalIgnoreCase);
        }
    }
}
