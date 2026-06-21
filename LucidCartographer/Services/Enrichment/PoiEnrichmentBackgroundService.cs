using System.Collections.Concurrent;
using System.Threading.Channels;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Import;
using LucidCartographer.Services.Operations;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Polly;
using Polly.Registry;

namespace LucidCartographer.Services.Enrichment;

/// <summary>
/// Polls the Poi table for rows that explicitly requested enrichment
/// (<see cref="Poi.EnrichmentRequested"/> == true) and fills in
/// address / website / phone by opening each place URL in a headless
/// Playwright tab. Creating a POI does NOT enqueue it — enrichment is
/// requested by the import pipeline, the MCP enrich tools, the re-enrich
/// service methods, and startup revive. Enrichment runs
/// <see cref="EnrichmentOptions.Concurrency"/> POIs in parallel; all
/// workers share a single <see cref="IBrowserContext"/> so cookies /
/// consent state are reused across tabs and iterations. Each worker
/// gets its own <see cref="AppDbContext"/> from the factory — EF Core
/// contexts are not thread-safe, but SQLite handles concurrent readers
/// and serializes writers for us.
///
/// A hard failure is retried with exponential backoff up to
/// <see cref="EnrichmentOptions.MaxRetries"/>; the row keeps
/// EnrichmentRequested=true between attempts. On every terminal outcome
/// — success, soft-fail (needs manual URL), or reaching the retry cap —
/// the worker clears EnrichmentRequested so the row leaves the queue.
/// </summary>
public class PoiEnrichmentBackgroundService : BackgroundService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly EnrichmentProgressService _progress;
    private readonly EnrichmentTrigger _trigger;
    private readonly DedupTrigger _dedupTrigger;
    private readonly SqliteWriteLock _writeLock;
    // Invalidation service is Scoped, so resolve per-write from a fresh scope.
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PoiEnrichmentBackgroundService> _logger;
    private readonly ResiliencePipeline _pipeline;
    private readonly EnrichmentOptions _options;
    private readonly TimeSpan _idlePollInterval;
    private readonly TimeSpan _baseRetryDelay;
    private readonly SemaphoreSlim _pageConcurrencyLock;
    // Prevents two workers from claiming the same POI in overlapping batch queries.
    private readonly ConcurrentDictionary<int, byte> _inFlight = new();

    public PoiEnrichmentBackgroundService(
        IDbContextFactory<AppDbContext> factory,
        EnrichmentProgressService progress,
        EnrichmentTrigger trigger,
        DedupTrigger dedupTrigger,
        SqliteWriteLock writeLock,
        IServiceScopeFactory scopeFactory,
        ResiliencePipelineProvider<string> pipelineProvider,
        IOptions<EnrichmentOptions> options,
        ILogger<PoiEnrichmentBackgroundService> logger)
    {
        _factory = factory;
        _progress = progress;
        _trigger = trigger;
        _dedupTrigger = dedupTrigger;
        _writeLock = writeLock;
        _scopeFactory = scopeFactory;
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
        // _writeLock is a DI singleton — the container owns its lifetime.
        _pageConcurrencyLock.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

        // Anchor page prevents window from closing in headed mode when workers open/close tabs.
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
                if (processed > 0)
                {
                    // The queue just drained after real work — this is the
                    // moment a pipeline (file/list import, single-POI add, a
                    // URL/id change) finishes enriching its rows. Per-row
                    // dedup already folded the obvious bbox matches; signal a
                    // full-DB pass to catch cross-batch races and place-id
                    // matches whose coordinates sit outside the bbox.
                    _dedupTrigger.Signal();
                }
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
                EnrichmentStateMachine.QueuePredicate(_options.MaxRetries),
                ct);
        }
        _progress.Set(remaining);

        if (remaining == 0)
        {
            return 0;
        }

        var processed = 0;
        var loggedQueueDepth = false;

        while (!ct.IsCancellationRequested)
        {
            List<int> batchIds;
            await using (var loadDb = await _factory.CreateDbContextAsync(ct))
            {
                var now = DateTime.UtcNow;
                var candidates = await loadDb.Pois
                    .Where(EnrichmentStateMachine.QueuePredicate(_options.MaxRetries))
                    .OrderBy(p => p.Id)
                    .Take(_options.BatchSize * 4)
                    .Select(p => new { p.Id, p.EnrichmentFailureCount, p.LastEnrichmentAttemptAt })
                    .ToListAsync(ct);

                batchIds = candidates
                    .Where(p => IsRetryDue(p.EnrichmentFailureCount, p.LastEnrichmentAttemptAt, now))
                    .Select(p => p.Id)
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

            await using (var progressDb = await _factory.CreateDbContextAsync(ct))
            {
                var newRemaining = await progressDb.Pois.CountAsync(
                    p => p.EnrichmentRequested && p.EnrichmentFailureCount < _options.MaxRetries,
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
        var persistTasks = new List<(int PoiId, Task Task)>();
        try
        {
            page = await context.NewPageAsync();
            await foreach (var poiId in reader.ReadAllAsync(ct))
            {
                if (!_inFlight.TryAdd(poiId, 0))
                {
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
        Poi snapshot;
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            var poi = await db.Pois.AsNoTracking().FirstOrDefaultAsync(p => p.Id == poiId, ct);
            if (poi == null || !poi.EnrichmentRequested || poi.EnrichmentFailureCount >= _options.MaxRetries)
            {
                return Task.CompletedTask;
            }
            snapshot = poi;
        }

        EnrichedDetails details;
        try
        {
            details = await _pipeline.ExecuteAsync(async innerCt =>
            {
                if (!string.IsNullOrEmpty(snapshot.GoogleMapsUrl) && PoiUrlHelper.IsGoogleMapsUrl(snapshot.GoogleMapsUrl!))
                {
                    return await PoiDetailEnricher.EnrichAsync(page, snapshot.GoogleMapsUrl!, innerCt, _logger);
                }
                return await PoiDetailEnricher.EnrichByNameAsync(page, snapshot.Name, snapshot.Category, snapshot.Latitude, snapshot.Longitude, innerCt, _logger);
            }, ct);
        }
        catch (Exception ex)
        {
            return PersistFailureAsync(poiId, ex, ct);
        }

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

            // Capture pre-write coords to invalidate cached legs only if enrichment actually moves the point.
            var oldLatitude = poi.Latitude;
            var oldLongitude = poi.Longitude;
            if (details is { Latitude: not null, Longitude: not null })
            {
                poi.Latitude = details.Latitude.Value;
                poi.Longitude = details.Longitude.Value;
            }
            var coordsChanged = oldLatitude != poi.Latitude || oldLongitude != poi.Longitude;

            // Upgrade to a proper /maps/place/ URL when the enricher found one.
            if (!string.IsNullOrEmpty(details.GoogleMapsUrl)
                && (string.IsNullOrEmpty(poi.GoogleMapsUrl) || !poi.GoogleMapsUrl.Contains("/maps/place/")))
            {
                poi.GoogleMapsUrl = details.GoogleMapsUrl;
            }

            await BackfillImageAsync(context, db, poi, details.ImageUrl, ct);

            var hasUsefulData = details.ResolvedPlace;
            poi.LastEnrichmentAttemptAt = DateTime.UtcNow;
            EnrichmentStateMachine.ApplyOutcome(
                poi,
                hasUsefulData ? EnrichmentOutcome.Resolved : EnrichmentOutcome.SoftFailure,
                _options.MaxRetries);

            _logger.LogInformation(
                "Enriched Poi {Id} '{Name}' (addr={Addr} web={Web} phone={Phone}{ManualHint})",
                poi.Id, poi.Name,
                details.Address is null ? "-" : "y",
                details.Website is null ? "-" : "y",
                details.Phone is null ? "-" : "y",
                hasUsefulData ? "" : " — needs manual URL");

            await SaveChangesWithWriteLockAsync(db, ct);

            // Invalidate stale cached legs when coords actually change.
            if (coordsChanged)
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var invalidation = scope.ServiceProvider.GetRequiredService<IRouteSegmentInvalidationService>();
                await invalidation.InvalidateForPoiAsync(poiId, ct);
            }

            var merged = await PoiPostEnrichmentDedup.MergeIfDuplicateAsync(db, poi, ct, _writeLock.Gate);
            if (merged)
            {
                _logger.LogInformation(
                    "Post-enrich dedup: Poi {Id} '{Name}' merged into an older canonical row",
                    poi.Id, poi.Name);
            }

            var newRemaining = await db.Pois.CountAsync(
                EnrichmentStateMachine.QueuePredicate(_options.MaxRetries),
                ct);
            _progress.Set(newRemaining);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (DbUpdateConcurrencyException)
        {
            // Dedup may have deleted this row mid-enrichment; re-enqueue the surviving canonical.
            await ReenqueueSurvivingCanonicalAsync(poiId, details, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Persisting enrichment for Poi {PoiId} failed — will retry next cycle", poiId);
        }
    }

    /// <summary>
    /// When dedup deletes a POI mid-enrichment, finds the surviving canonical by place ID or
    /// bounding box and re-enqueues it so the fresh scrape isn't lost.
    /// </summary>
    private async Task ReenqueueSurvivingCanonicalAsync(int poiId, EnrichedDetails details, CancellationToken ct)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);

            if (await db.Pois.AsNoTracking().AnyAsync(p => p.Id == poiId, ct))
            {
                _logger.LogWarning(
                    "Concurrent write to Poi {PoiId} during enrichment persist; row still exists, " +
                    "leaving it for the next cycle", poiId);
                return;
            }

            var ftid = PoiUrlHelper.ExtractFeatureId(details.GoogleMapsUrl);
            Poi? survivor = null;

            if (ftid is not null)
            {
                var idCandidates = await db.Pois
                    .Where(p => p.GoogleMapsUrl != null && p.GoogleMapsUrl.Contains("/maps/place/"))
                    .ToListAsync(ct);
                survivor = idCandidates.Find(p => PoiUrlHelper.ExtractFeatureId(p.GoogleMapsUrl) == ftid);
            }

            // Fall back to coordinate bounding box.
            if (survivor is null && details is { Latitude: not null, Longitude: not null })
            {
                const double box = 0.002;
                var latLo = details.Latitude.Value - box;
                var latHi = details.Latitude.Value + box;
                var lonLo = details.Longitude.Value - box;
                var lonHi = details.Longitude.Value + box;
                survivor = await db.Pois
                    .Where(p => p.Latitude != null && p.Longitude != null
                                && p.Latitude >= latLo && p.Latitude <= latHi
                                && p.Longitude >= lonLo && p.Longitude <= lonHi)
                    .OrderBy(p => p.Id)
                    .FirstOrDefaultAsync(ct);
            }

            if (survivor is null)
            {
                _logger.LogWarning(
                    "Poi {PoiId} was deleted by a concurrent dedup mid-enrichment and no surviving " +
                    "canonical could be located to re-enqueue; its fresh scrape is lost", poiId);
                return;
            }

            if (!survivor.EnrichmentRequested)
            {
                survivor.EnrichmentRequested = true;
                await SaveChangesWithWriteLockAsync(db, ct);
            }

            _logger.LogWarning(
                "Poi {PoiId} was deleted by a concurrent dedup mid-enrichment; re-enqueued surviving " +
                "canonical Poi {Survivor} so the place's fresh data is re-fetched", poiId, survivor.Id);
            _trigger.Signal();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to re-enqueue surviving canonical after Poi {PoiId} was deleted mid-enrichment", poiId);
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

            poi.LastEnrichmentAttemptAt = DateTime.UtcNow;
            // Retryable: increments the counter and only clears EnrichmentRequested
            // once the cap is reached (row stays IsEnriched=false either way).
            EnrichmentStateMachine.ApplyOutcome(poi, EnrichmentOutcome.HardFailure, _options.MaxRetries);
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
        await _writeLock.Gate.WaitAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _writeLock.Gate.Release();
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
    }

    private static IEnumerable<string> BuildImageFetchCandidates(string imageUrl)
    {
        // Upscale Google Maps thumbnail (e.g. =w86-h86-k-no) to full size first; fall back to original if needed.
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
