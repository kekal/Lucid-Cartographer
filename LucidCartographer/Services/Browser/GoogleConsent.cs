using Microsoft.Playwright;

namespace LucidCartographer.Services.Browser;

/// <summary>
/// Single source of truth for dismissing Google's cookie/consent interstitial.
/// </summary>
public static class GoogleConsent
{
    private static readonly string[] ConsentSelectors =
    [
        "button[aria-label*='Accept']",
        "button[aria-label*='accept']",
        "button:has-text('Accept all')",
        "button:has-text('Agree')",
        "button:has-text('Принять')",
        "form[action*='consent'] button"
    ];

    /// <summary>
    /// If a consent dialog is present, click the accept control and wait for the
    /// redirect off <c>consent.google.com</c> to settle. Best-effort: swallows
    /// timeouts and the navigation-abort races that Google's redirect produces,
    /// continuing with whatever URL the page lands on. No-op when absent.
    /// </summary>
    public static async Task DismissAsync(IPage page, ILogger? logger = null)
    {
        try
        {
            foreach (var sel in ConsentSelectors)
            {
                ILocator btn;
                try
                {
                    btn = page.Locator(sel).First;
                    if (!await btn.IsVisibleAsync())
                    {
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Consent selector miss for '{Selector}'", sel);
                    continue;
                }

                logger?.LogInformation("Clicking consent button: {Selector}", sel);
                var clickTask = btn.ClickAsync();
                try
                {
                    await page.WaitForURLAsync(
                        u => !u.Contains("consent.google.com"),
                        new() { Timeout = 15000 });
                }
                catch (TimeoutException) { }
                catch (PlaywrightException ex) when (IsNavigationAbort(ex))
                {
                    logger?.LogDebug(ex, "Consent redirect wait aborted; continuing with URL '{Url}'", page.Url);
                }

                try
                {
                    await clickTask;
                }
                catch (PlaywrightException ex) when (IsNavigationAbort(ex))
                {
                    logger?.LogDebug(ex, "Consent click navigation aborted; continuing with URL '{Url}'", page.Url);
                }

                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 });
                }
                catch (TimeoutException) { /* NetworkIdle rarely reached on maps */ }
                break;
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "No consent dialog found or failed to dismiss it");
        }
    }

    /// <summary>
    /// Google's consent redirect frequently aborts the in-flight navigation
    /// (ERR_ABORTED / "frame was detached"); those are expected, not failures.
    /// </summary>
    private static bool IsNavigationAbort(PlaywrightException ex)
        => ex.Message.Contains("ERR_ABORTED", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("frame was detached", StringComparison.OrdinalIgnoreCase);
}
