using System.Collections.Concurrent;
using System.Threading.Channels;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Polly;
using Polly.Registry;

namespace LucidCartographer.Services.Enrichment;

/// <summary>
/// Polls the Poi table for rows with IsEnriched=false and fills in
/// address / website / phone by opening each place URL in a headless
/// Playwright tab. Enrichment runs <see cref="EnrichmentOptions.Concurrency"/>
/// POIs in parallel via <see cref="Parallel.ForEachAsync{T}"/>; all
/// workers share a single <see cref="IBrowserContext"/> so cookies /
/// consent state are reused across tabs and iterations. Each worker
/// gets its own <see cref="AppDbContext"/> from the factory — EF Core
/// contexts are not thread-safe, but SQLite handles concurrent readers
/// and serializes writers for us.
///
/// Failures are not retried with a counter — the row stays
/// IsEnriched=false and the next poll cycle picks it up again. This
/// keeps the data model simple (one bool, no retry state) and matches
/// the user's directive: "if something was pending — we just refetch".
/// </summary>
public class PoiEnrichmentBackgroundService : BackgroundService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly EnrichmentProgressService _progress;
    private readonly EnrichmentTrigger _trigger;
    private readonly ILogger<PoiEnrichmentBackgroundService> _logger;
    private readonly ResiliencePipeline _pipeline;
    private readonly EnrichmentOptions _options;
    private readonly TimeSpan _idlePollInterval;
    private readonly TimeSpan _baseRetryDelay;
    private readonly SemaphoreSlim _sqliteWriteLock = new(1, 1);
    private readonly SemaphoreSlim _pageConcurrencyLock;
    // Tracks POI ids currently being enriched across all workers in this
    // process. Without it, two workers can pick the same id when their
    // batch queries overlap, leading to lost updates and dedup deletions
    // racing each other. Entry held for the lifetime of the worker's
    // page+persist phase.
    private readonly ConcurrentDictionary<int, byte> _inFlight = new();

    public PoiEnrichmentBackgroundService(
        IDbContextFactory<AppDbContext> factory,
        EnrichmentProgressService progress,
        EnrichmentTrigger trigger,
        ResiliencePipelineProvider<string> pipelineProvider,
        IOptions<EnrichmentOptions> options,
        ILogger<PoiEnrichmentBackgroundService> logger)
    {
        _factory = factory;
        _progress = progress;
        _trigger = trigger;
        _logger = logger;
        _pipeline = pipelineProvider.GetPipeline("enrichment");
        _options = options.Value;
        _idlePollInterval = TimeSpan.FromSeconds(Math.Max(1, _options.IdlePollSeconds));
        _baseRetryDelay = TimeSpan.FromSeconds(Math.Max(1, _options.BackoffBaseSeconds));
        var maxPages = Math.Max(1, _options.MaxConcurrentPages);
        _pageConcurrencyLock = new SemaphoreSlim(maxPages, maxPages);
    }

    public override void Dispose()
    {
        _sqliteWriteLock.Dispose();
        _pageConcurrencyLock.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small startup delay so the app has a chance to finish booting
        // (migrations, static file mapping, …) before we hit the DB and
        // — on a cold machine — install Chromium.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch { return; }

        try
        {
            await PlaywrightBootstrap.EnsureBrowsersInstalledAsync(_logger, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playwright bootstrap failed; POI enrichment disabled for this session");
            return;
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = !_options.Headed,
            SlowMo = _options.SlowMoMs > 0 ? _options.SlowMoMs : null
        });
        if (_options.Headed)
        {
            _logger.LogWarning("Playwright launched in HEADED mode (Enrichment:Headed=true). Disable in production.");
        }
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            Locale = "en-US",
            UserAgent = _options.UserAgent
        });

        // In headed mode Chromium closes the window when its last page goes
        // away. Workers open and close pages per POI, so between batches the
        // window would flash closed and re-open. An always-open anchor page
        // (about:blank) keeps the window count >= 1 for the whole session.
        if (_options.Headed)
        {
            var anchorPage = await context.NewPageAsync();
            await anchorPage.GotoAsync("about:blank");
        }

        _logger.LogInformation("PoiEnrichmentBackgroundService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(context, stoppingToken);
                if (processed == 0)
                {
                    // Sleep until either the idle timeout fires OR someone
                    // (importer / scraper) signals that new unenriched Pois
                    // are waiting. This keeps the worst-case latency at
                    // IdlePollInterval for things we can't observe (e.g.
                    // manual DB edits) while reacting instantly to imports.
                    try { await _trigger.WaitAsync(_idlePollInterval, stoppingToken); }
                    catch (OperationCanceledException) { break; }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Enrichment batch failed; sleeping before retry");
                try { await Task.Delay(_idlePollInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("PoiEnrichmentBackgroundService stopping");
    }

    private async Task<int> ProcessBatchAsync(IBrowserContext context, CancellationToken ct)
    {
        int remaining;
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            remaining = await db.Pois.CountAsync(
                p => !p.IsEnriched && p.EnrichmentFailureCount < _options.MaxRetries,
                ct);
        }
        _progress.Set(remaining);

        if (remaining == 0)
        {
            return 0;
        }

        var processed = 0;
        var loggedQueueDepth = false;

        // Pull a batch of pending IDs, fan them out across
        // `_options.Concurrency` parallel Playwright tabs (all sharing
        // the same BrowserContext), then loop until the queue drains.
        // Each worker owns its own DbContext because EF Core contexts
        // are not thread-safe.
        while (!ct.IsCancellationRequested)
        {
            List<int> batchIds;
            await using (var loadDb = await _factory.CreateDbContextAsync(ct))
            {
                var now = DateTime.UtcNow;
                var candidates = await loadDb.Pois
                    .Where(p => !p.IsEnriched && p.EnrichmentFailureCount < _options.MaxRetries)
                    .OrderBy(p => p.Id)
                    .Take(_options.BatchSize * 4)
                    .Select(p => new { p.Id, p.EnrichmentFailureCount, p.LastEnrichmentAttemptAt })
                    .ToListAsync(ct);

                batchIds = candidates
                    .Where(p => IsRetryDue(p.EnrichmentFailureCount, p.LastEnrichmentAttemptAt, now))
                    .Select(p => p.Id)
                    // Skip ids another worker is already enriching this
                    // batch — the in-flight entry stays until the persist
                    // task finishes, so we won't double-claim a row.
                    .Where(id => !_inFlight.ContainsKey(id))
                    .Take(_options.BatchSize)
                    .ToList();
            }

            if (batchIds.Count == 0)
            {
                break;
            }

            if (!loggedQueueDepth)
            {
                _logger.LogInformation("Enriching queue: {Remaining} Pois pending", remaining);
                loggedQueueDepth = true;
            }

            var metricsBefore = EnrichmentMetrics.Snapshot();

            // Per-worker tab pool. Each worker owns a long-lived IPage and
            // pulls IDs off a channel until drained, so we pay the tab
            // open/close cost once per batch instead of per POI. GotoAsync
            // does a cross-document navigation, which tears down the previous
            // document — no state leaks between rows.
            var workerCount = Math.Max(1, _options.Concurrency);
            var queue = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = true
            });
            foreach (var id in batchIds)
            {
                queue.Writer.TryWrite(id);
            }
            queue.Writer.Complete();

            var workers = new Task[workerCount];
            for (var i = 0; i < workerCount; i++)
            {
                workers[i] = RunWorkerAsync(context, queue.Reader, ct);
            }
            await Task.WhenAll(workers);

            var metricsAfter = EnrichmentMetrics.Snapshot();
            var batchMetrics = EnrichmentMetrics.Diff(metricsBefore, metricsAfter);
            _logger.LogInformation(
                "Enrichment batch metrics: address_found={AddressFound}, phone_found={PhoneFound}, website_found={WebsiteFound}, selector_miss={SelectorMisses}",
                batchMetrics.AddressFound,
                batchMetrics.PhoneFound,
                batchMetrics.WebsiteFound,
                batchMetrics.SelectorMisses);

            processed += batchIds.Count;

            // One progress refresh per batch is enough; the per-POI
            // updates inside EnrichOneAsync already tick the counter
            // down as workers complete.
            await using (var progressDb = await _factory.CreateDbContextAsync(ct))
            {
                var newRemaining = await progressDb.Pois.CountAsync(
                    p => !p.IsEnriched && p.EnrichmentFailureCount < _options.MaxRetries,
                    ct);
                _progress.Set(newRemaining);
            }
        }

        return processed;
    }

    private async Task RunWorkerAsync(IBrowserContext context, ChannelReader<int> reader, CancellationToken ct)
    {
        await _pageConcurrencyLock.WaitAsync(ct);
        IPage? page = null;
        // Persist phase (image download, DB writes, dedup) doesn't need the
        // tab. Fire-and-forget into this list so the worker immediately
        // starts the next POI's GotoAsync while the previous POI's
        // housekeeping runs in parallel. We await the list before the
        // worker closes its tab so nothing is lost on shutdown.
        var persistTasks = new List<(int PoiId, Task Task)>();
        try
        {
            page = await context.NewPageAsync();
            await foreach (var poiId in reader.ReadAllAsync(ct))
            {
                if (!_inFlight.TryAdd(poiId, 0))
                {
                    // Another worker grabbed this id already (shouldn't
                    // happen given the dispatch-side filter, but cheap
                    // defensive guard).
                    continue;
                }

                try
                {
                    var persistTask = await EnrichOneAsync(context, page, poiId, ct);
                    persistTasks.Add((poiId, persistTask));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _inFlight.TryRemove(poiId, out _);
                    break;
                }
                catch (Exception ex)
                {
                    _inFlight.TryRemove(poiId, out _);
                    _logger.LogError(ex, "Worker crashed enriching Poi {PoiId}; continuing", poiId);
                }
            }
        }
        finally
        {
            // Await each persist task individually so a failure on row N
            // doesn't hide failures on rows >N. Task.WhenAll only rethrows
            // the first faulted task; we want every POI's outcome logged.
            foreach (var (poiId, task) in persistTasks)
            {
                try { await task; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutdown */ }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Persist phase failed for Poi {PoiId}", poiId);
                }
                finally
                {
                    _inFlight.TryRemove(poiId, out _);
                }
            }

            if (page != null)
            {
                try { await page.CloseAsync(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to close worker page"); }
            }
            _pageConcurrencyLock.Release();
        }
    }

    /// <summary>
    /// Page phase: navigates the worker's tab and extracts EnrichedDetails.
    /// Returns a Task representing the persist phase (image backfill,
    /// DB write, dedup) which the worker accumulates and awaits at exit.
    /// The persist phase doesn't touch the tab, so the worker can start
    /// the next POI's GotoAsync as soon as this method returns.
    /// </summary>
    private async Task<Task> EnrichOneAsync(IBrowserContext context, IPage page, int poiId, CancellationToken ct)
    {
        // Snapshot the row's pre-enrichment state — we only need a few
        // fields for the page-phase entry-point decision plus the failure
        // counter for the early-out check. The persist phase reloads its
        // own copy from a fresh DbContext.
        Poi snapshot;
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            var poi = await db.Pois.AsNoTracking().FirstOrDefaultAsync(p => p.Id == poiId, ct);
            if (poi == null || poi.IsEnriched || poi.EnrichmentFailureCount >= _options.MaxRetries)
            {
                return Task.CompletedTask;
            }
            snapshot = poi;
        }

        EnrichedDetails details;
        try
        {

            // Pick entry point based on what the scraper captured:
            //   - if GoogleMapsUrl already contains /maps/place/, open it directly;
            //   - otherwise run a Google Maps name search (scraper left
            //     coords NULL because the list card was anchor-less).
            // "enrichment" Polly pipeline: retry (3 attempts,
            // jittered exponential backoff) + 2-minute per-attempt
            // timeout. Transient Playwright failures are retried
            // in-place; terminal failures leave IsEnriched=false so
            // the next idle poll cycle re-picks the row.
            details = await _pipeline.ExecuteAsync(async innerCt =>
            {
                // Any URL the row has — canonical /maps/place/, a /maps/search/
                // result page, or a maps.app.goo.gl shortlink — gets navigated
                // directly. Playwright follows redirects and the place-URL wait
                // loop in EnrichCoreAsync handles the post-redirect hydration.
                // Only fall back to a name search when we have no URL at all.
                // Only navigate GoogleMapsUrl if it actually points at Google
                // Maps. Older imports (and a now-fixed GeoJSON branch) used
                // to drop a venue's own website here, which sent the enricher
                // to e.g. termymaltanskie.com.pl — no place selectors, all
                // fields empty, fallback modal. Treat anything else as missing
                // and route through the coord-anchored name search.
                if (!string.IsNullOrEmpty(snapshot.GoogleMapsUrl) && PoiUrlHelper.IsGoogleMapsUrl(snapshot.GoogleMapsUrl!))
                {
                    return await PoiDetailEnricher.EnrichAsync(page, snapshot.GoogleMapsUrl!, innerCt, _logger);
                }
                return await PoiDetailEnricher.EnrichByNameAsync(page, snapshot.Name, snapshot.Category, snapshot.Latitude, snapshot.Longitude, innerCt, _logger);
            }, ct);
        }
        catch (Exception ex)
        {
            // Hard failure (exception during page work). The browser is now
            // free to move on; persist the failure counter in the background.
            return PersistFailureAsync(poiId, ex, ct);
        }

        // Page is done — return a Task for the persist phase so the worker
        // can immediately start the next POI's GotoAsync.
        return PersistSuccessAsync(context, poiId, details, ct);
    }

    private async Task PersistSuccessAsync(IBrowserContext context, int poiId, EnrichedDetails details, CancellationToken ct)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            var poi = await db.Pois.FirstOrDefaultAsync(p => p.Id == poiId, ct);
            if (poi is null)
            {
                return;
            }

            // Fill empty fields only — never overwrite user edits.
            if (string.IsNullOrEmpty(poi.Address))
            {
                poi.Address = details.Address;
            }

            if (string.IsNullOrEmpty(poi.Website))
            {
                poi.Website = details.Website;
            }

            if (string.IsNullOrEmpty(poi.Phone))
            {
                poi.Phone = details.Phone;
            }

            // Enrichment's !3d!4d coords are always more authoritative
            // than whatever we had.
            if (details is { Latitude: not null, Longitude: not null })
            {
                poi.Latitude = details.Latitude.Value;
                poi.Longitude = details.Longitude.Value;
            }

            // Upgrade to a proper /maps/place/ URL when the enricher found one.
            if (!string.IsNullOrEmpty(details.GoogleMapsUrl)
                && (string.IsNullOrEmpty(poi.GoogleMapsUrl) || !poi.GoogleMapsUrl.Contains("/maps/place/")))
            {
                poi.GoogleMapsUrl = details.GoogleMapsUrl;
            }

            await BackfillImageAsync(context, db, poi, details.ImageUrl, ct);

            var hasUsefulData = !string.IsNullOrWhiteSpace(poi.Address)
                                || !string.IsNullOrWhiteSpace(poi.Website)
                                || !string.IsNullOrWhiteSpace(poi.Phone)
                                || (poi.GoogleMapsUrl?.Contains("/maps/place/", StringComparison.OrdinalIgnoreCase) == true);
            poi.LastEnrichmentAttemptAt = DateTime.UtcNow;
            if (hasUsefulData)
            {
                poi.IsEnriched = true;
                poi.EnrichmentFailureCount = 0;
                poi.EnrichmentNeedsManualUrl = false;
            }
            else
            {
                // Soft failure: page loaded fine, no place data. No retries —
                // flip the manual-URL flag so the UI prompts the user.
                poi.IsEnriched = true;
                poi.EnrichmentNeedsManualUrl = true;
                poi.EnrichmentFailureCount = 0;
            }

            _logger.LogInformation(
                "Enriched Poi {Id} '{Name}' (addr={Addr} web={Web} phone={Phone}{ManualHint})",
                poi.Id, poi.Name,
                details.Address is null ? "-" : "y",
                details.Website is null ? "-" : "y",
                details.Phone is null ? "-" : "y",
                hasUsefulData ? "" : " — needs manual URL");

            await SaveChangesWithWriteLockAsync(db, ct);

            var merged = await PoiPostEnrichmentDedup.MergeIfDuplicateAsync(db, poi, ct, _sqliteWriteLock);
            if (merged)
            {
                _logger.LogInformation(
                    "Post-enrich dedup: Poi {Id} '{Name}' merged into an older canonical row",
                    poi.Id, poi.Name);
            }

            var newRemaining = await db.Pois.CountAsync(
                p => !p.IsEnriched && p.EnrichmentFailureCount < _options.MaxRetries,
                ct);
            _progress.Set(newRemaining);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Persisting enrichment for Poi {PoiId} failed — will retry next cycle", poiId);
        }
    }

    private async Task PersistFailureAsync(int poiId, Exception ex, CancellationToken ct)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            var poi = await db.Pois.FirstOrDefaultAsync(p => p.Id == poiId, ct);
            if (poi is null)
            {
                return;
            }

            poi.EnrichmentFailureCount++;
            poi.LastEnrichmentAttemptAt = DateTime.UtcNow;
            var retryDelay = GetRetryDelay(poi.EnrichmentFailureCount);
            await SaveChangesWithWriteLockAsync(db, ct);

            if (poi.EnrichmentFailureCount >= _options.MaxRetries)
            {
                _logger.LogError(ex,
                    "Enrichment failed for Poi {Id} '{Name}' and reached retry cap {MaxRetries}",
                    poi.Id, poi.Name, _options.MaxRetries);
            }
            else
            {
                _logger.LogWarning(ex,
                    "Enrichment failed for Poi {Id} '{Name}' (attempt {Attempt}/{MaxRetries}); retry after {RetryDelay}",
                    poi.Id, poi.Name, poi.EnrichmentFailureCount, _options.MaxRetries, retryDelay);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception saveEx)
        {
            _logger.LogError(saveEx,
                "Failed to persist enrichment failure tracking for Poi {PoiId}", poiId);
        }
    }

    private async Task SaveChangesWithWriteLockAsync(AppDbContext db, CancellationToken ct)
    {
        await _sqliteWriteLock.WaitAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _sqliteWriteLock.Release();
        }
    }

    private bool IsRetryDue(int failureCount, DateTime? lastAttemptAt, DateTime nowUtc)
    {
        if (failureCount <= 0 || !lastAttemptAt.HasValue)
        {
            return true;
        }

        return nowUtc - lastAttemptAt.Value >= GetRetryDelay(failureCount);
    }

    private async Task BackfillImageAsync(
        IBrowserContext context,
        AppDbContext db,
        Poi poi,
        string? imageUrl,
        CancellationToken ct)
    {
        // No usable new photo this pass → keep whatever the POI already has.
        // A failed (or photo-less) re-enrichment must never strip an existing
        // photo; the only way an image leaves the row is being overwritten by a
        // freshly-downloaded one below.
        if (string.IsNullOrWhiteSpace(imageUrl) || !IsLikelyPlacePhotoUrl(imageUrl))
        {
            return;
        }

        var existingImage = await db.PoiImages.FindAsync([poi.Id], ct);

        foreach (var candidateUrl in BuildImageFetchCandidates(imageUrl))
        {
            try
            {
                var resp = await context.APIRequest.GetAsync(candidateUrl);
                if (resp.Status != 200)
                {
                    continue;
                }

                var bytes = await resp.BodyAsync();
                if (bytes.Length == 0)
                {
                    continue;
                }

                var contentType = resp.Headers.TryGetValue("content-type", out var ctHeader)
                    ? ctHeader
                    : "image/jpeg";

                // Swap in the new photo only now that the bytes are in hand.
                // Update the existing row in place (PoiId is the PK, so a
                // remove+add would collide on the key); the old bytes survive
                // until this same SaveChanges commits the new ones, so the
                // replacement is atomic and a download failure leaves the
                // previous photo untouched.
                if (existingImage is not null)
                {
                    existingImage.Data = bytes;
                    existingImage.ContentType = contentType;
                }
                else
                {
                    db.PoiImages.Add(new PoiImage
                    {
                        PoiId = poi.Id,
                        Data = bytes,
                        ContentType = contentType
                    });
                }

                poi.ImageUrl = candidateUrl;
                _logger.LogInformation("Image fetched for Poi {PoiId}: {Bytes} bytes from {ImageUrl}",
                    poi.Id, bytes.Length, candidateUrl);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Image download failed for Poi {PoiId} from {ImageUrl}", poi.Id, candidateUrl);
            }
        }
        // Every candidate failed → leave the existing photo (and ImageUrl) intact.
    }

    private static IEnumerable<string> BuildImageFetchCandidates(string imageUrl)
    {
        // Google Maps' DOM <img src> is a tiny thumbnail (e.g. =w86-h86-k-no).
        // Swap the size suffix to ask the CDN for the full-size photo first;
        // fall back to the original only if the upscaled URL 404s.
        var equalsIdx = imageUrl.LastIndexOf('=');
        if (equalsIdx > 0)
        {
            var upscaled = imageUrl[..equalsIdx] + "=w1024";
            if (!string.Equals(upscaled, imageUrl, StringComparison.Ordinal))
            {
                yield return upscaled;
            }
        }

        yield return imageUrl;
    }

    private static bool IsLikelyPlacePhotoUrl(string url)
    {
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

    private TimeSpan GetRetryDelay(int failureCount)
    {
        if (failureCount <= 0)
        {
            return TimeSpan.Zero;
        }

        var exponent = Math.Max(0, failureCount - 1);
        var factor = Math.Pow(2, exponent);
        var seconds = _baseRetryDelay.TotalSeconds * factor;
        return TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromHours(12).TotalSeconds));
    }
}
