using Microsoft.Playwright;

namespace LucidCartographer.Services.Browser;

/// <summary>Sign-in state of the shared Google browser session.</summary>
/// <param name="SignedIn">True when the shared session is signed into Google.</param>
/// <param name="Busy">
/// True when the status could not be read because another Google browser
/// operation (export / fetch / scrape) currently holds the session.
/// </param>
/// <param name="Detail">Human-readable note (URL, error, or hint).</param>
public sealed record GoogleSessionStatus(bool SignedIn, bool Busy, string? Detail = null);

/// <summary>
/// The single, long-lived, in-process headful Chromium session backed by the
/// persistent Chrome profile. All Google-account-dependent automation borrows
/// pages from here so the browser the user logs into (via the noVNC view) is the
/// very same one the exporter/scraper drive. Operations are serialised by
/// <see cref="GoogleBrowserLock"/> at the call sites.
/// </summary>
public interface IBrowserSession
{
    /// <summary>The resolved persistent profile directory.</summary>
    string ProfilePath { get; }

    /// <summary>True when the persistent profile directory exists with content.</summary>
    bool HasProfile { get; }

    /// <summary>
    /// Ensure Chromium is installed and the shared context is launched, then open
    /// a fresh page on it. The caller owns the page and must close it (but never
    /// the context).
    /// </summary>
    Task<IPage> NewPageAsync(CancellationToken ct = default);

    /// <summary>
    /// Open a fresh page on the shared (signed-in) context with per-page mobile
    /// emulation applied via CDP (iPhone UA + metrics + touch). The mobile web
    /// Maps UI is far simpler/more stable to scrape than desktop. The context's
    /// default (desktop) is untouched, so export keeps using <see cref="NewPageAsync"/>.
    /// The caller owns the page and must close it.
    /// </summary>
    Task<IPage> NewMobilePageAsync(CancellationToken ct = default);

    /// <summary>
    /// Read the current Google sign-in state without disturbing a running job
    /// (returns <see cref="GoogleSessionStatus.Busy"/> if one holds the session).
    /// </summary>
    Task<GoogleSessionStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Drive the shared session's visible tab to the Google sign-in page so the
    /// user can complete login in the noVNC view. Returns the busy state instead
    /// of throwing if a job is running.
    /// </summary>
    Task<GoogleSessionStatus> NavigateToSignInAsync(CancellationToken ct = default);

    /// <summary>
    /// Close the shared context (if open) and delete the persistent profile so the
    /// user can sign in with a different account. Throws if a job holds the session.
    /// </summary>
    Task ResetProfileAsync(CancellationToken ct = default);
}
