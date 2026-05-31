using Microsoft.Playwright;

namespace LucidCartographer.Services.Enrichment;

public record EnrichedDetails(
    string? Address,
    string? Website,
    string? Phone,
    double? Latitude,
    double? Longitude,
    string? GoogleMapsUrl,
    string? ImageUrl)
{
    /// <summary>
    /// True only when THIS enrichment pass landed on a canonical Google
    /// <c>/maps/place/</c> URL — the single trustworthy signal that we actually
    /// resolved the place. <see cref="GoogleMapsUrl"/> is populated (in
    /// <see cref="PoiDetailEnricher"/>) exactly when the final URL contains
    /// <c>/maps/place/</c>, so it is the authoritative gate.
    ///
    /// Address / website / phone come from selectors that only exist on the
    /// place panel, so they cannot be present without a place URL anyway — and
    /// a photo MUST NOT count on its own: a search-results (SERP) page exposes
    /// stray <c>googleusercontent.com</c> thumbnails that belong to other
    /// places (the POI #604 / "PUB 320" bug). Counting a photo alone marked
    /// such rows "Enriched" with a wrong image and no canonical URL.
    /// </summary>
    public bool ResolvedPlace => !string.IsNullOrWhiteSpace(GoogleMapsUrl);
}

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
        => EnrichCoreAsync(page, placeUrl, searchName: null, ct, logger);

    public static Task<EnrichedDetails> EnrichByNameAsync(IPage page, string name, string? hint, CancellationToken ct, ILogger? logger = null)
        => EnrichByNameAsync(page, name, hint, latitude: null, longitude: null, ct, logger);

    public static Task<EnrichedDetails> EnrichByNameAsync(IPage page, string name, string? hint, double? latitude, double? longitude, CancellationToken ct, ILogger? logger = null)
    {
        // Appending the hint (category or description first line) helps
        // disambiguate common names. Example: "Zebra" + "Zabrze, Poland"
        // lands on the specific place rather than the generic feature.
        var query = string.IsNullOrWhiteSpace(hint) ? name : $"{name} {hint}";

        // When the POI carries coordinates (KML import, manual pin), use the
        // path-based search URL with a /@lat,lon,17z viewport suffix instead
        // of the ?api=1&query= form. Maps then biases the search to that
        // viewport AND, for a near-unique hit, opens the place panel directly
        // — which is what the address/website/phone selectors expect. Without
        // the viewport anchor, ?api=1 lands on a SERP that has no place panel
        // and all three fields come back empty.
        string url;
        if (latitude.HasValue && longitude.HasValue)
        {
            var lat = latitude.Value.ToString("F7", System.Globalization.CultureInfo.InvariantCulture);
            var lon = longitude.Value.ToString("F7", System.Globalization.CultureInfo.InvariantCulture);
            url = $"https://www.google.com/maps/search/{Uri.EscapeDataString(query)}/@{lat},{lon},17z";
        }
        else
        {
            url = "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(query);
        }
        // Pass the bare POI name (not the hint-augmented query) so the
        // results-list auto-picker matches against the actual place name.
        return EnrichCoreAsync(page, url, searchName: name, ct, logger);
    }

    private static async Task<EnrichedDetails> EnrichCoreAsync(IPage page, string navUrl, string? searchName, CancellationToken ct, ILogger? logger)
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
            string[] consentSelectors =
            [
                "button[aria-label*='Accept']",
                "button[aria-label*='accept']",
                "button:has-text('Accept all')",
                "button:has-text('Agree')",
                "form[action*='consent'] button"
            ];
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
        await WaitForPlaceUrlAsync(page, ct);

        // Still on a results list (the search was ambiguous and Google didn't
        // auto-open a single place). If exactly one result card unambiguously
        // matches the POI name, navigate to it; otherwise leave the page as-is
        // so the caller flags a manual-URL fallback.
        if (searchName is not null && !IsOnPlacePage(page.Url))
        {
            var picked = await TryPickResultUrlAsync(page, searchName, logger);
            if (picked is not null)
            {
                try
                {
                    await page.GotoAsync(picked, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 20000
                    });
                    await WaitForPlaceUrlAsync(page, ct);
                    logger?.LogInformation(
                        "Enrichment auto-picked the single matching search result for '{Name}'", searchName);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex,
                        "Failed to open auto-picked search result for '{Name}'; falling back", searchName);
                }
            }
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
        if (!string.IsNullOrWhiteSpace(address))
        {
            EnrichmentMetrics.RecordAddressFound();
        }

        if (!string.IsNullOrWhiteSpace(phone))
        {
            EnrichmentMetrics.RecordPhoneFound();
        }

        string? website = null;
        try
        {
            var webEl = page.Locator("a[data-item-id='authority']").First;
            if (await webEl.IsVisibleAsync())
            {
                website = await webEl.GetAttributeAsync("href");
                if (string.IsNullOrWhiteSpace(website))
                {
                    website = (await webEl.InnerTextAsync()).Trim();
                }

                if (!string.IsNullOrWhiteSpace(website))
                {
                    EnrichmentMetrics.RecordWebsiteFound();
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Website selector miss for Google Maps details panel");
            EnrichmentMetrics.RecordSelectorMiss();
        }

        // Only trust a photo when we actually landed on a place page. On a
        // results (SERP) page the img[src*='googleusercontent.com'] selector
        // matches a stray thumbnail belonging to some other listing — storing
        // it gave POI #604 the wrong "PUB 320" menu image.
        var imageUrl = finalUrl.Contains("/maps/place/")
            ? await TryExtractImageUrlAsync(page, logger)
            : null;

        return new EnrichedDetails(
            Address: address,
            Website: website,
            Phone: phone,
            Latitude: coords?.lat,
            Longitude: coords?.lon,
            GoogleMapsUrl: finalUrl.Contains("/maps/place/") ? finalUrl : null,
            ImageUrl: imageUrl);
    }

    private static bool IsOnPlacePage(string url)
        => url.Contains("/maps/place/") && url.Contains("/@");

    /// <summary>
    /// Polls (up to ~9s) for the URL to settle on the canonical /maps/place/
    /// form. Google redirects a name search to the matched place; the @lat,lon
    /// segment confirms the map focused on it.
    /// </summary>
    private static async Task WaitForPlaceUrlAsync(IPage page, CancellationToken ct)
    {
        for (var i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (IsOnPlacePage(page.Url))
            {
                break;
            }

            await Task.Delay(300, ct);
        }
    }

    /// <summary>
    /// Reads the search-results feed and returns the href of the single card
    /// whose name unambiguously matches <paramref name="searchName"/>, or null
    /// when there is no result list, no match, or more than one match. Selector
    /// misses degrade gracefully to null (→ manual fallback).
    /// </summary>
    private static async Task<string?> TryPickResultUrlAsync(IPage page, string searchName, ILogger? logger)
    {
        try
        {
            // Result cards in the Maps feed are <a class="hfpxzc"> with the
            // place name in aria-label and the canonical place URL in href.
            try
            {
                await page.WaitForSelectorAsync("a.hfpxzc", new() { Timeout = 4000 });
            }
            catch (TimeoutException)
            {
                return null;
            }

            var anchors = await page.Locator("a.hfpxzc").AllAsync();
            if (anchors.Count == 0)
            {
                return null;
            }

            var names = new List<string>(anchors.Count);
            var hrefs = new List<string?>(anchors.Count);
            foreach (var anchor in anchors)
            {
                names.Add((await anchor.GetAttributeAsync("aria-label"))?.Trim() ?? string.Empty);
                hrefs.Add(await anchor.GetAttributeAsync("href"));
            }

            var index = EnrichmentResultPicker.PickUnambiguousMatch(searchName, names);
            if (index is null)
            {
                logger?.LogDebug(
                    "Search for '{Name}' returned {Count} result(s); no single unambiguous match",
                    searchName, anchors.Count);
                return null;
            }

            var href = hrefs[index.Value];
            return href is not null && href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? href
                : null;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Search-result auto-pick failed for '{Name}'", searchName);
            return null;
        }
    }

    private static async Task<string?> TryInnerTextAsync(IPage page, string selector, string field, ILogger? logger)
    {
        try
        {
            var el = page.Locator(selector).First;
            if (await el.IsVisibleAsync())
            {
                return (await el.InnerTextAsync()).Trim();
            }
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
        [
            "button[jsaction*='pane.heroHeaderImage.click'] img[src]",
            "img[src*='googleusercontent.com']"
        ];

        foreach (var selector in selectors)
        {
            try
            {
                var img = page.Locator(selector).First;
                if (await img.IsVisibleAsync())
                {
                    var src = await img.GetAttributeAsync("src");
                    if (IsLikelyPlacePhotoUrl(src))
                    {
                        return src;
                    }
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
        {
            return false;
        }

        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (url.Contains("/maps/vt", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/vt/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("tile.openstreetmap.org", StringComparison.OrdinalIgnoreCase)
            || (url.Contains("x=", StringComparison.OrdinalIgnoreCase)
                && url.Contains("y=", StringComparison.OrdinalIgnoreCase)
                && url.Contains("z=", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return url.Contains("googleusercontent.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("streetviewpixels-pa.googleapis.com", StringComparison.OrdinalIgnoreCase);
    }
}
