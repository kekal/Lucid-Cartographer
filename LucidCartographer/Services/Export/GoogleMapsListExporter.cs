using LucidCartographer.Services;
using LucidCartographer.Services.Browser;
using Microsoft.Playwright;

namespace LucidCartographer.Services.Export;

/// <summary>
/// Drives a headful Chromium on the shared persistent profile to push a set of
/// places into a Google Maps <em>Saved List</em> via the UI: navigate to each
/// place → open the place-panel <b>Save</b> control → create the list (when it
/// doesn't yet exist) or tick it → detect the already-saved state for
/// idempotency. There is no Google API for personal Saved Lists, so UI
/// automation is the only path.
///
/// Selectors were captured by the spike (see docs/google-saved-list-export-spike.md):
/// the Save button name is locale- AND state-dependent (Save/Saved/Сохранить/
/// Сохранено); lists render as <c>menuitemradio</c> whose name is the (truncated)
/// list name + a "{visibility} · N place(s)" suffix, matched by name prefix;
/// membership is read from <c>aria-checked</c>; navigation is retried because the
/// Maps SPA intermittently stalls. The launch/login/consent sequence mirrors
/// <see cref="Import.GoogleMapsListScraper"/>. A shared <see cref="GoogleBrowserLock"/>
/// serialises this against the scraper (both use the same profile dir).
/// </summary>
public class GoogleMapsListExporter(
    ILogger<GoogleMapsListExporter> logger,
    GoogleBrowserLock browserLock,
    IBrowserSession session)
    : IGoogleMapsListExporter
{
    // "Saved"/"Сохранено" (past tense) is what the button reads once the place is
    // already in ANY list; "Save"/"Сохранить" before. Match all four.
    // List rows in the Save picker render as either role; match both.
    private const string ListItemsSelector = "[role=menuitemradio], [role=menuitemcheckbox]";

    private static readonly string[] SaveLabels = ["Save", "Saved", "Сохранить", "Сохранено"];
    private static readonly string[] NewListLabels = ["New list", "Create list", "New", "Новый список", "Создать список"];
    private static readonly string[] ConfirmLabels = ["Create", "Save", "Done", "Создать", "Сохранить", "Готово"];

    /// <summary>
    /// Push <paramref name="placeUrls"/> into the Saved List named
    /// <paramref name="listName"/>, creating the list if absent. Reports coarse
    /// per-place progress via <paramref name="onProgress"/>. Acquires the shared
    /// browser lock (waits its turn behind any running scraper/export).
    /// </summary>
    public async Task<ExportRunReport> ExportAsync(
        string listName,
        IReadOnlyList<string> placeUrls,
        Action<ExportProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(listName))
        {
            throw new ArgumentException("List name must not be empty.", nameof(listName));
        }
        if (placeUrls is null || placeUrls.Count == 0)
        {
            throw new ArgumentException("At least one place URL is required.", nameof(placeUrls));
        }

        using var lease = await browserLock.AcquireAsync(ct);
        return await ExportInternalAsync(listName.Trim(), placeUrls, onProgress, ct);
    }

    private async Task<ExportRunReport> ExportInternalAsync(
        string listName, IReadOnlyList<string> placeUrls, Action<ExportProgress>? onProgress, CancellationToken ct)
    {
        logger.LogInformation(
            "Export starting: list='{List}', {Count} place(s), profile={Path}",
            listName, placeUrls.Count, session.ProfilePath);

        // Borrow a page from the single shared session (the same browser the user
        // signs into via the Google session page). Close the page, never the context.
        var page = await session.NewPageAsync(ct);
        try
        {
            await EnsureSignedInAsync(page, ct);

            return await RunExportAsync(page, listName, placeUrls, onProgress, ct);
        }
        finally
        {
            try { await page.CloseAsync(); } catch (Exception ex) { logger.LogDebug(ex, "Error closing export page"); }
        }
    }

    private async Task<ExportRunReport> RunExportAsync(
        IPage page, string listName, IReadOnlyList<string> placeUrls, Action<ExportProgress>? onProgress, CancellationToken ct)
    {
        var results = new List<ExportPlaceResult>(placeUrls.Count);
        for (var i = 0; i < placeUrls.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var url = placeUrls[i];
            ExportPlaceResult result;
            try
            {
                result = await ExportOnePlaceAsync(page, listName, url, ct);
            }
            catch (OperationCanceledException)
            {
                throw; // honor cancellation (shutdown / user cancel) — don't swallow as a per-place failure
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Export failed for {Url}", url);
                result = new ExportPlaceResult(url, null, ExportOutcome.Failed, ex.Message);
            }

            logger.LogInformation("  {Outcome}: {Url} ({Note})", result.Outcome, url, result.Note ?? "");
            results.Add(result);
            // Progress counts derive from the partial results — no parallel tally to drift.
            onProgress?.Invoke(new ExportProgress(
                results.Count, placeUrls.Count, result.Name,
                results.Count(r => r.Outcome == ExportOutcome.Added),
                results.Count(r => r.Outcome == ExportOutcome.Created),
                results.Count(r => r.Outcome == ExportOutcome.AlreadySaved),
                results.Count(r => r.Outcome == ExportOutcome.Failed)));

            // Human-paced jitter BETWEEN places (skip after the last — no work follows).
            if (i < placeUrls.Count - 1)
            {
                await page.WaitForTimeoutAsync(Random.Shared.Next(1500, 4000));
            }
        }

        var report = new ExportRunReport(listName, results);
        logger.LogInformation(
            "Export finished: created={Created}, added={Added}, alreadySaved={Already}, failed={Failed}, total={Total}",
            report.Created, report.Added, report.AlreadySaved, report.Failed, report.Total);
        return report;
    }

    /// <summary>
    /// Navigate to Maps, clear consent, and fail fast if the shared session isn't
    /// signed into Google. Login is no longer performed inline (there's no window
    /// to watch during a background job) — the user signs in once on the Google
    /// session page, which drives this same shared browser.
    /// </summary>
    private async Task EnsureSignedInAsync(IPage page, CancellationToken ct)
    {
        await page.GotoAsync("https://www.google.com/maps",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
        await page.WaitForTimeoutAsync(5000); // Let the SPA render
        ct.ThrowIfCancellationRequested();

        logger.LogInformation("Post-navigation URL: {Url}", page.Url);
        await GoogleConsent.DismissAsync(page, logger);

        if (!await GoogleSignIn.IsSignedInAsync(page, logger))
        {
            throw new InvalidOperationException(
                "Not signed in to Google. Open the Google session page (Data Sources → Google session), " +
                "sign in, then retry the export.");
        }

        logger.LogInformation("Shared session is signed in. URL: {Url}", page.Url);
    }

    /// <summary>
    /// Longest prefix of <paramref name="listName"/> (down to <paramref name="threshold"/>)
    /// that occurs as a substring of <paramref name="candidate"/>; -1 if none reaches
    /// the threshold. Matching a prefix as a substring — rather than anchored at index 0 —
    /// tolerates BOTH a truncated list name AND leading icon/ligature text that Google
    /// prepends to a Save-menu row's accessible name (which otherwise made every place
    /// create a fresh duplicate list).
    /// </summary>
    private static int MatchScore(string candidate, string listName, int threshold)
    {
        for (var len = listName.Length; len >= threshold; len--)
        {
            if (candidate.Contains(listName[..len], StringComparison.Ordinal))
            {
                return len;
            }
        }
        return -1;
    }

    /// <summary>Force the Maps UI into English via the hl query param.</summary>
    private static string WithEnglishUi(string url)
    {
        if (url.Contains("hl=en", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }
        return url + (url.Contains('?') ? "&hl=en" : "?hl=en");
    }

    /// <summary>
    /// Clicks the first visible/clickable locator produced by <paramref name="byName"/>
    /// across the candidate names. Returns false if none matched.
    /// </summary>
    private static async Task<bool> TryClickNamedAsync(
        Func<string, ILocator> byName, IReadOnlyList<string> names, int timeoutMs)
    {
        foreach (var name in names)
        {
            var loc = byName(name).First;
            try
            {
                if (await loc.IsVisibleAsync())
                {
                    await loc.ClickAsync(new LocatorClickOptions { Timeout = timeoutMs });
                    return true;
                }
            }
            catch
            {
                // Try the next candidate label.
            }
        }
        return false;
    }

    private async Task<ExportPlaceResult> ExportOnePlaceAsync(
        IPage page, string listName, string placeUrl, CancellationToken ct)
    {
        // hl=en forces the Maps UI into English when the account language allows;
        // localized label fallbacks cover when it doesn't. Navigate with retry:
        // the SPA intermittently stalls even on Commit; the heading gate confirms
        // the place panel actually rendered.
        string? placeName = null;
        var navigated = false;
        for (var attempt = 1; attempt <= 2 && !navigated; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await page.GotoAsync(WithEnglishUi(placeUrl),
                    new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = 30000 });
                var heading = page.GetByRole(AriaRole.Heading).First;
                await heading.WaitForAsync(new() { Timeout = 20000 });
                placeName = (await heading.TextContentAsync())?.Trim();
                navigated = true;
            }
            catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
            {
                logger.LogWarning("Navigation attempt {Attempt}/2 failed for {Url}: {Msg}",
                    attempt, placeUrl, ex.Message);
            }
        }
        if (!navigated)
        {
            await DumpActionBarAsync(page, "place panel did not render after retries");
            return new ExportPlaceResult(placeUrl, placeName, ExportOutcome.Failed, "Place panel did not render after retries.");
        }

        // Let the action bar finish wiring up before looking for Save.
        await page.WaitForTimeoutAsync(800);

        // Open the Save control and wait for the list picker. The Save button's
        // label is locale- AND state-dependent (Save/Saved/Сохранить/Сохранено).
        // We wait for the LIST ITEMS (menuitemradio) directly rather than a menu
        // wrapper — the map Layers menu is also role=menu (with menuitemCHECKBOX
        // children) and earlier in the DOM, so picking a menu by .First grabbed
        // the wrong one (spike finding). For an already-saved place ("Saved")
        // the picker can be slow/finicky to surface, so retry the click once.
        var menuOpened = false;
        for (var attempt = 1; attempt <= 2 && !menuOpened; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var clickedSave = await TryClickNamedAsync(
                n => page.GetByRole(AriaRole.Button, new() { Name = n, Exact = true }),
                SaveLabels, 8000);
            if (!clickedSave)
            {
                await DumpActionBarAsync(page, $"Save button not found among candidate labels (attempt {attempt})");
                return new ExportPlaceResult(placeUrl, placeName, ExportOutcome.Failed, "Save button not found/clickable.");
            }

            try
            {
                await page.GetByRole(AriaRole.Menuitemradio).First
                    .WaitForAsync(new() { Timeout = 12000 });
                menuOpened = true;
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Save picker did not open (attempt {Attempt}) for {Url}", attempt, placeUrl);
                // Close whatever (if anything) opened and let the loop re-click Save.
                await page.Keyboard.PressAsync("Escape");
                await page.WaitForTimeoutAsync(600);
            }
        }
        if (!menuOpened)
        {
            await DumpMenuAsync(page, "save list menu did not open (no menuitemradio appeared)");
            return new ExportPlaceResult(placeUrl, placeName, ExportOutcome.Failed, "Save menu did not open.");
        }

        return await ResolveAndSaveAsync(page, listName, placeUrl, placeName);
    }

    /// <summary>
    /// Idempotent list resolution: find the target list among the open Save menu's
    /// <c>menuitemradio</c> items by a distinctive prefix of its name; tick it (or
    /// report AlreadySaved). Only create it if absent — looking-up-before-creating
    /// prevents duplicate same-named lists (spike finding).
    /// </summary>
    private async Task<ExportPlaceResult> ResolveAndSaveAsync(
        IPage page, string listName, string placeUrl, string? placeName)
    {
        // Read every list radio's accessible name + checked state in ONE pass.
        // Each name is the (often TRUNCATED) list name glued to a "{visibility} ·
        // N place(s)" suffix. We must not match exact (truncation breaks it),
        // substring-anywhere (picks unrelated lists that merely contain the name),
        // or a fixed-length prefix (misses hard-truncated names). Instead choose
        // the radio whose longest-common-prefix with listName is largest and meets
        // a threshold — robust to truncation AND to unrelated similarly-named lists.
        // List rows render as menuitemradio OR menuitemcheckbox depending on the
        // Maps build/locale, so capture both (in DOM order).
        var radios = await page.EvaluateAsync<string[]>(@"
            () => Array.from(document.querySelectorAll('" + ListItemsSelector + @"')).map(e =>
                (e.getAttribute('aria-checked') === 'true' ? '1' : '0') +
                ((e.getAttribute('aria-label') || e.textContent || '').replace(/\s+/g, ' ').trim()))");

        // Log what the Save menu actually offered — the single most useful signal
        // when a place unexpectedly creates a new list instead of reusing one.
        logger.LogInformation("Save-menu list candidates for '{List}' ({Count}): {Items}",
            listName, radios.Length, string.Join(" | ", radios));

        var threshold = Math.Min(listName.Length, 16);
        var bestIndex = -1;
        var bestScore = -1;
        for (var i = 0; i < radios.Length; i++)
        {
            var name = radios[i].Length > 0 ? radios[i][1..] : string.Empty;
            var score = MatchScore(name, listName, threshold);
            if (score < 0)
            {
                continue;
            }
            // Prefer the longest matched prefix; tie-break to the shorter candidate
            // (closest to "listName + suffix", not a longer list that shares a prefix).
            if (bestIndex < 0 || score > bestScore ||
                (score == bestScore && name.Length < radios[bestIndex][1..].Length))
            {
                bestIndex = i;
                bestScore = score;
            }
        }

        if (bestIndex >= 0)
        {
            // First char of the captured entry is the aria-checked flag.
            if (radios[bestIndex][0] == '1')
            {
                await page.Keyboard.PressAsync("Escape");
                return new ExportPlaceResult(placeUrl, placeName, ExportOutcome.AlreadySaved, "Already in the list.");
            }

            // Click the matched row by its DOM index over the SAME selector used to
            // capture it, so the index aligns whether the rows are radios or
            // checkboxes (Playwright role-Nth would mismatch a mixed set).
            var clicked = await page.EvaluateAsync<bool>(
                @"(idx) => { const els = document.querySelectorAll('" + ListItemsSelector + @"');
                    const el = els[idx]; if (!el) return false; el.click(); return true; }",
                bestIndex);
            if (!clicked)
            {
                await DumpMenuAsync(page, "matched list row vanished before click");
                return new ExportPlaceResult(placeUrl, placeName, ExportOutcome.Failed, "Matched list row could not be clicked.");
            }
            await page.WaitForTimeoutAsync(800);
            await page.Keyboard.PressAsync("Escape");
            return new ExportPlaceResult(placeUrl, placeName, ExportOutcome.Added, "Added to the list.");
        }

        logger.LogInformation(
            "No existing list matched '{List}' (threshold {Threshold}); creating it.", listName, threshold);

        // List absent → create it. (Normally only the first place overall hits this.)
        var newListClicked =
            await TryClickNamedAsync(n => page.GetByRole(AriaRole.Menuitem, new() { Name = n }), NewListLabels, 8000)
            || await TryClickNamedAsync(n => page.GetByRole(AriaRole.Button, new() { Name = n }), NewListLabels, 8000);
        if (!newListClicked)
        {
            await DumpMenuAsync(page, "list absent and no 'New list' action found");
            return new ExportPlaceResult(placeUrl, placeName, ExportOutcome.Failed, "No 'New list' action in the Save menu.");
        }

        var nameField = page.GetByRole(AriaRole.Textbox).First;
        await nameField.WaitForAsync(new() { Timeout = 8000 });
        await nameField.FillAsync(listName);

        var confirmed = await TryClickNamedAsync(
            n => page.GetByRole(AriaRole.Button, new() { Name = n }), ConfirmLabels, 8000);
        if (confirmed)
        {
            await page.WaitForTimeoutAsync(1500);
            return new ExportPlaceResult(placeUrl, placeName, ExportOutcome.Created, $"Created list '{listName}' and saved.");
        }

        await DumpMenuAsync(page, "could not find a confirm button after typing the list name");
        return new ExportPlaceResult(placeUrl, placeName, ExportOutcome.Failed, "No confirm button after naming the list.");
    }

    // ---- Discovery aids: dump candidate selectors to the log on failure ----

    private async Task DumpActionBarAsync(IPage page, string reason)
    {
        try
        {
            var labels = await page.EvaluateAsync<string[]>(@"
                (() => Array.from(document.querySelectorAll('button[aria-label]'))
                    .map(b => b.getAttribute('aria-label'))
                    .filter(Boolean)
                    .slice(0, 60))()");
            logger.LogWarning("Action-bar dump ({Reason}): {Labels}", reason, string.Join(" | ", labels));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to dump action bar");
        }
    }

    private async Task DumpMenuAsync(IPage page, string reason)
    {
        try
        {
            var items = await page.EvaluateAsync<string[]>(@"
                (() => Array.from(document.querySelectorAll('[role=menu] [role], [role=menuitem], [role=menuitemcheckbox], [role=menuitemradio]'))
                    .map(e => (e.getAttribute('role') || '') + ':' + (e.getAttribute('aria-label') || e.textContent || '').trim() + ' [checked=' + (e.getAttribute('aria-checked') || '') + ']')
                    .filter(s => s.length > 3)
                    .slice(0, 60))()");
            logger.LogWarning("Save-menu dump ({Reason}): {Items}", reason, string.Join(" | ", items));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to dump save menu");
        }
    }
}

/// <summary>Coarse progress emitted after each place during an export.</summary>
public readonly record struct ExportProgress(
    int Done, int Total, string? CurrentName, int Added, int Created, int AlreadySaved, int Failed);

/// <summary>Per-place outcome of an export run.</summary>
public enum ExportOutcome
{
    /// <summary>The list was created on this place and the place saved into it.</summary>
    Created,
    /// <summary>The place was ticked into the (already-existing) list.</summary>
    Added,
    /// <summary>The place was already in the list — left untouched (idempotency).</summary>
    AlreadySaved,
    /// <summary>This place failed; see the note. Other places still ran.</summary>
    Failed
}

/// <summary>Result for one place URL.</summary>
public record ExportPlaceResult(string Url, string? Name, ExportOutcome Outcome, string? Note);

/// <summary>Export run report: the list name, per-place results, and counts.</summary>
public record ExportRunReport(string ListName, IReadOnlyList<ExportPlaceResult> Results)
{
    public int Total => Results.Count;
    public int Created => Results.Count(r => r.Outcome == ExportOutcome.Created);
    public int Added => Results.Count(r => r.Outcome == ExportOutcome.Added);
    public int AlreadySaved => Results.Count(r => r.Outcome == ExportOutcome.AlreadySaved);
    public int Failed => Results.Count(r => r.Outcome == ExportOutcome.Failed);
}
