using LucidCartographer.Services.Browser;
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
    /// True when the enrichment landed on a canonical <c>/maps/place/</c> URL,
    /// the sole trustworthy signal of a resolved place. Photos alone can mislead
    /// (SERP stray thumbnails); this gate prevents false enrichment.
    /// </summary>
    public bool ResolvedPlace => !string.IsNullOrWhiteSpace(GoogleMapsUrl);
}

/// <summary>
/// Opens a Google Maps place and extracts address, website, phone, and
/// coordinates. Stateless helper for background enrichment with pooled
/// tabs. Two entry points: EnrichAsync (place URL) and EnrichByNameAsync
/// (place name or name with hint/viewport).
/// </summary>
public static class PoiDetailEnricher
{
    public static Task<EnrichedDetails> EnrichAsync(IPage page, string placeUrl, CancellationToken ct, ILogger? logger = null)
        => EnrichCoreAsync(page, placeUrl, searchName: null, ct, logger);

    public static Task<EnrichedDetails> EnrichByNameAsync(IPage page, string name, string? hint, CancellationToken ct, ILogger? logger = null)
        => EnrichByNameAsync(page, name, hint, latitude: null, longitude: null, ct, logger);

    public static Task<EnrichedDetails> EnrichByNameAsync(IPage page, string name, string? hint, double? latitude, double? longitude, CancellationToken ct, ILogger? logger = null)
    {
        // Hint disambiguates common names (e.g., "Zebra" + "Zabrze, Poland").
        var query = string.IsNullOrWhiteSpace(hint) ? name : $"{name} {hint}";

        // With coords, use /@lat,lon,17z URL to open place panel directly;
        // without it, ?api=1 lands on SERP with no panel.
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
        // Use bare name (not hint-augmented query) for result auto-picker.
        return EnrichCoreAsync(page, url, searchName: name, ct, logger);
    }

    private static async Task<EnrichedDetails> EnrichCoreAsync(IPage page, string navUrl, string? searchName, CancellationToken ct, ILogger? logger)
    {
        ct.ThrowIfCancellationRequested();

        // DOMContentLoaded, not NetworkIdle (Maps has infinite background XHRs).
        await page.GotoAsync(navUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 20000
        });

        // Fresh contexts land on consent.google.com; cookies persist across calls.
        if (page.Url.Contains("consent.google.com"))
        {
            await GoogleConsent.DismissAsync(page, logger);
        }

        // Wait for URL to settle on /maps/place/ with @lat,lon.
        await WaitForPlaceUrlAsync(page, ct);

        // If still on results list, try to pick the unambiguous match.
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

        // Wait for detail panel to expose data-item-id attributes (10s timeout).
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

        // Only trust photos from place pages; SERP images belong to other listings.
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
    /// Polls for URL to settle on canonical /maps/place/ with @lat,lon.
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
    /// Returns the href of the single result card matching <paramref name="searchName"/>,
    /// or null if no list, no match, or ambiguous. Selector misses degrade gracefully.
    /// </summary>
    private static async Task<string?> TryPickResultUrlAsync(IPage page, string searchName, ILogger? logger)
    {
        try
        {
            // Maps result cards: <a class="hfpxzc"> with aria-label and href.
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
