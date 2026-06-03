using Microsoft.Playwright;

namespace LucidCartographer.Services.Browser;

/// <summary>
/// Detects whether the current page is signed into a Google account. Shared by
/// the exporter, the "Fetch My Lists" scraper, and the Google session status
/// check so the heuristic lives in one place.
/// </summary>
public static class GoogleSignIn
{
    /// <summary>
    /// True when <paramref name="page"/> (expected to be on google.com/maps)
    /// appears signed in. Two negative signals: the URL is an accounts/sign-in
    /// page, or the DOM still exposes a ServiceLogin / sign-in link. We do NOT
    /// match on a bare <c>accounts.google.com</c> href because the sign-OUT link
    /// also lives on that domain.
    /// </summary>
    public static async Task<bool> IsSignedInAsync(IPage page, ILogger? logger = null)
    {
        var url = page.Url;
        if (url.Contains("accounts.google.com") || url.Contains("signin"))
        {
            logger?.LogInformation("IsSignedIn: false (URL contains accounts/signin): {Url}", url);
            return false;
        }

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

        var signedIn = string.IsNullOrEmpty(signInHref);
        logger?.LogInformation("IsSignedIn: {Result}", signedIn);
        return signedIn;
    }
}
