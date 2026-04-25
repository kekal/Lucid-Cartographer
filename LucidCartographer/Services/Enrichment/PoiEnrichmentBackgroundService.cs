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
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

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
            Headless = true
        });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            Locale = "en-US",
            UserAgent = UserAgent
        });

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

        _logger.LogInformation("Enriching queue: {Remaining} Pois pending", remaining);

        var processed = 0;

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
                    .Take(_options.BatchSize)
                    .ToList();
            }

            if (batchIds.Count == 0)
            {
                break;
            }

            var metricsBefore = EnrichmentMetrics.Snapshot();

            await Parallel.ForEachAsync(
                batchIds,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, _options.Concurrency),
                    CancellationToken = ct
                },
                async (poiId, innerCt) =>
                {
                    await EnrichOneAsync(context, poiId, innerCt, ct);
                });

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

    private async Task EnrichOneAsync(IBrowserContext context, int poiId, CancellationToken workerCt, CancellationToken serviceCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(workerCt, serviceCt);
        var ct = linked.Token;

        await using var db = await _factory.CreateDbContextAsync(ct);
        var poi = await db.Pois.FirstOrDefaultAsync(p => p.Id == poiId, ct);
        if (poi == null || poi.IsEnriched || poi.EnrichmentFailureCount >= _options.MaxRetries)
        {
            return;
        }

        await _pageConcurrencyLock.WaitAsync(ct);
        IPage? page = null;
        try
        {
            page = await context.NewPageAsync();

            // Pick entry point based on what the scraper captured:
            //   - if GoogleMapsUrl already contains /maps/place/, open it directly;
            //   - otherwise run a Google Maps name search (scraper left
            //     coords NULL because the list card was anchor-less).
            // "enrichment" Polly pipeline: retry (3 attempts,
            // jittered exponential backoff) + 2-minute per-attempt
            // timeout. Transient Playwright failures are retried
            // in-place; terminal failures leave IsEnriched=false so
            // the next idle poll cycle re-picks the row.
            var details = await _pipeline.ExecuteAsync(async innerCt =>
            {
                if (!string.IsNullOrEmpty(poi.GoogleMapsUrl) && poi.GoogleMapsUrl.Contains("/maps/place/"))
                {
                    return await PoiDetailEnricher.EnrichAsync(page, poi.GoogleMapsUrl!, innerCt, _logger);
                }
                return await PoiDetailEnricher.EnrichByNameAsync(page, poi.Name, poi.Category, innerCt, _logger);
            }, ct);

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
            // than whatever we had (user input, viewport-center fallback,
            // or nothing at all). Overwrite unconditionally when the
            // enricher produced coords.
            if (details is { Latitude: not null, Longitude: not null })
            {
                poi.Latitude = details.Latitude.Value;
                poi.Longitude = details.Longitude.Value;
            }

            // Upgrade to a proper /maps/place/ URL when the enricher found one.
            // The scraper may have stored null (anchor-less card) or a non-place
            // URL; always prefer the canonical place URL from enrichment.
            if (!string.IsNullOrEmpty(details.GoogleMapsUrl)
                && (string.IsNullOrEmpty(poi.GoogleMapsUrl) || !poi.GoogleMapsUrl.Contains("/maps/place/")))
            {
                poi.GoogleMapsUrl = details.GoogleMapsUrl;
            }

            await BackfillImageAsync(context, db, poi, details.ImageUrl, ct);

            var hasCoordinates = poi is { Latitude: not null, Longitude: not null };
            poi.IsEnriched = hasCoordinates;
            poi.LastEnrichmentAttemptAt = DateTime.UtcNow;
            if (hasCoordinates)
            {
                poi.EnrichmentFailureCount = 0;
            }
            else
            {
                poi.EnrichmentFailureCount++;
            }

            _logger.LogInformation(
                "Enriched Poi {Id} '{Name}' (addr={Addr} web={Web} phone={Phone})",
                poi.Id, poi.Name,
                details.Address is null ? "-" : "y",
                details.Website is null ? "-" : "y",
                details.Phone is null ? "-" : "y");

            if (!hasCoordinates)
            {
                var retryDelay = GetRetryDelay(poi.EnrichmentFailureCount);
                _logger.LogWarning(
                    "Enrichment fetched metadata for Poi {Id} '{Name}' but no coordinates were resolved (attempt {Attempt}/{MaxRetries}); retry after {RetryDelay}",
                    poi.Id,
                    poi.Name,
                    poi.EnrichmentFailureCount,
                    _options.MaxRetries,
                    retryDelay);
            }
        }
        catch (Exception ex)
        {
            poi.EnrichmentFailureCount++;
            poi.LastEnrichmentAttemptAt = DateTime.UtcNow;
            var retryDelay = GetRetryDelay(poi.EnrichmentFailureCount);
            try
            {
                await SaveChangesWithWriteLockAsync(db, ct);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx,
                    "Failed to persist enrichment failure tracking for Poi {Id} '{Name}'",
                    poi.Id, poi.Name);
                return;
            }

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
            return;
        }
        finally
        {
            if (page != null)
            {
                try
                {
                    await page.CloseAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to close Playwright page for Poi {PoiId}", poiId);
                }
            }

            _pageConcurrencyLock.Release();
        }

        try
        {
            await SaveChangesWithWriteLockAsync(db, ct);

            // Post-enrichment dedup against the enriched cohort.
            // The just-enriched row now has real coords + (usually)
            // a real Google Maps URL, so it becomes dedup-eligible.
            // If an older enriched row already represents this place,
            // fold collection links onto it and delete this row.
            // "Smaller Id wins" so parallel workers can't race.
            var merged = await PoiPostEnrichmentDedup.MergeIfDuplicateAsync(db, poi, ct, _sqliteWriteLock);
            if (merged)
            {
                _logger.LogInformation(
                    "Post-enrich dedup: Poi {Id} '{Name}' merged into an older canonical row",
                    poi.Id, poi.Name);
            }

            // Per-POI tick: re-read the counter so the map page
            // header updates as each worker finishes.
            var newRemaining = await db.Pois.CountAsync(
                p => !p.IsEnriched && p.EnrichmentFailureCount < _options.MaxRetries,
                ct);
            _progress.Set(newRemaining);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Persisting enrichment for Poi {Id} '{Name}' failed — will retry next cycle",
                poi.Id, poi.Name);
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
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return;
        }

        if (!IsLikelyPlacePhotoUrl(imageUrl))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(poi.ImageUrl))
        {
            poi.ImageUrl = imageUrl;
        }

        var existingImage = await db.PoiImages.FindAsync([poi.Id], ct);
        if (existingImage != null)
        {
            if (IsLikelyPlacePhotoUrl(poi.ImageUrl ?? string.Empty))
            {
                return;
            }

            // Existing bytes appear to be a non-photo artifact (tile/snapshot);
            // replace with a validated place-photo candidate when available.
            db.PoiImages.Remove(existingImage);
        }

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

                db.PoiImages.Add(new PoiImage
                {
                    PoiId = poi.Id,
                    Data = bytes,
                    ContentType = contentType
                });
                poi.ImageUrl = candidateUrl;
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Image download failed for Poi {PoiId} from {ImageUrl}", poi.Id, candidateUrl);
            }
        }
    }

    private static IEnumerable<string> BuildImageFetchCandidates(string imageUrl)
    {
        yield return imageUrl;

        var equalsIdx = imageUrl.LastIndexOf('=');
        if (equalsIdx > 0)
        {
            var upscaled = imageUrl[..equalsIdx] + "=w1024";
            if (!string.Equals(upscaled, imageUrl, StringComparison.Ordinal))
            {
                yield return upscaled;
            }
        }
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
