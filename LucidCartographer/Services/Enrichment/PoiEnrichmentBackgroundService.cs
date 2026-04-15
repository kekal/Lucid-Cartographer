using LucidCartographer.Data;
using LucidCartographer.Services.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Polly;
using Polly.Registry;

namespace LucidCartographer.Services.Enrichment
{
    /// <summary>
    /// Polls the Poi table for rows with IsEnriched=false and fills in
    /// address / website / phone by opening each place URL in a headless
    /// Playwright tab. Enrichment runs <see cref="EnrichmentConcurrency"/>
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
        private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(30);
        private const int EnrichmentConcurrency = 4;
        private const int BatchSize = 16;
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly EnrichmentProgressService _progress;
        private readonly EnrichmentTrigger _trigger;
        private readonly ILogger<PoiEnrichmentBackgroundService> _logger;
        private readonly ResiliencePipeline _pipeline;

        public PoiEnrichmentBackgroundService(
            IDbContextFactory<AppDbContext> factory,
            EnrichmentProgressService progress,
            EnrichmentTrigger trigger,
            ResiliencePipelineProvider<string> pipelineProvider,
            ILogger<PoiEnrichmentBackgroundService> logger)
        {
            _factory = factory;
            _progress = progress;
            _trigger = trigger;
            _logger = logger;
            _pipeline = pipelineProvider.GetPipeline("enrichment");
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
                        try { await _trigger.WaitAsync(IdlePollInterval, stoppingToken); }
                        catch (OperationCanceledException) { break; }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Enrichment batch failed; sleeping before retry");
                    try { await Task.Delay(IdlePollInterval, stoppingToken); }
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
                remaining = await db.Pois.CountAsync(p => !p.IsEnriched, ct);
            }
            _progress.Set(remaining);

            if (remaining == 0) return 0;

            _logger.LogInformation("Enriching queue: {Remaining} Pois pending", remaining);

            int processed = 0;

            // Pull a batch of pending IDs, fan them out across
            // `EnrichmentConcurrency` parallel Playwright tabs (all sharing
            // the same BrowserContext), then loop until the queue drains.
            // Each worker owns its own DbContext because EF Core contexts
            // are not thread-safe.
            while (!ct.IsCancellationRequested)
            {
                List<int> batchIds;
                await using (var loadDb = await _factory.CreateDbContextAsync(ct))
                {
                    batchIds = await loadDb.Pois
                        .Where(p => !p.IsEnriched)
                        .OrderBy(p => p.Id)
                        .Take(BatchSize)
                        .Select(p => p.Id)
                        .ToListAsync(ct);
                }

                if (batchIds.Count == 0) break;

                await Parallel.ForEachAsync(
                    batchIds,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = EnrichmentConcurrency,
                        CancellationToken = ct
                    },
                    async (poiId, innerCt) =>
                    {
                        await EnrichOneAsync(context, poiId, innerCt);
                    });

                processed += batchIds.Count;

                // One progress refresh per batch is enough; the per-POI
                // updates inside EnrichOneAsync already tick the counter
                // down as workers complete.
                await using (var progressDb = await _factory.CreateDbContextAsync(ct))
                {
                    var newRemaining = await progressDb.Pois.CountAsync(p => !p.IsEnriched, ct);
                    _progress.Set(newRemaining);
                }
            }

            return processed;
        }

        private async Task EnrichOneAsync(IBrowserContext context, int poiId, CancellationToken ct)
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            var poi = await db.Pois.FirstOrDefaultAsync(p => p.Id == poiId, ct);
            if (poi == null || poi.IsEnriched) return;

            var page = await context.NewPageAsync();
            try
            {
                // Pick entry point based on what the scraper captured:
                //   - if GoogleMapsUrl already contains /maps/place/, open it directly;
                //   - otherwise run a Google Maps name search (scraper left
                //     coords at 0,0 because the list card was anchor-less).
                // "enrichment" Polly pipeline: retry (3 attempts,
                // jittered exponential backoff) + 2-minute per-attempt
                // timeout. Transient Playwright failures are retried
                // in-place; terminal failures leave IsEnriched=false so
                // the next idle poll cycle re-picks the row.
                var details = await _pipeline.ExecuteAsync(async innerCt =>
                {
                    if (!string.IsNullOrEmpty(poi.GoogleMapsUrl) && poi.GoogleMapsUrl.Contains("/maps/place/"))
                    {
                        return await PoiDetailEnricher.EnrichAsync(page, poi.GoogleMapsUrl!, innerCt);
                    }
                    return await PoiDetailEnricher.EnrichByNameAsync(page, poi.Name, poi.Category, innerCt);
                }, ct);

                // Fill empty fields only — never overwrite user edits.
                if (string.IsNullOrEmpty(poi.Address)) poi.Address = details.Address;
                if (string.IsNullOrEmpty(poi.Website)) poi.Website = details.Website;
                if (string.IsNullOrEmpty(poi.Phone)) poi.Phone = details.Phone;

                if (poi.Latitude == 0 && poi.Longitude == 0 && details.Latitude.HasValue && details.Longitude.HasValue)
                {
                    poi.Latitude = details.Latitude.Value;
                    poi.Longitude = details.Longitude.Value;
                }

                if (string.IsNullOrEmpty(poi.GoogleMapsUrl) && !string.IsNullOrEmpty(details.GoogleMapsUrl))
                    poi.GoogleMapsUrl = details.GoogleMapsUrl;

                poi.IsEnriched = true;

                _logger.LogInformation(
                    "Enriched Poi {Id} '{Name}' (addr={Addr} web={Web} phone={Phone})",
                    poi.Id, poi.Name,
                    details.Address is null ? "-" : "y",
                    details.Website is null ? "-" : "y",
                    details.Phone is null ? "-" : "y");
            }
            catch (Exception ex)
            {
                // Leave IsEnriched=false — next poll cycle will retry.
                _logger.LogWarning(ex,
                    "Enrichment failed for Poi {Id} '{Name}' — will retry next cycle",
                    poi.Id, poi.Name);
                return;
            }
            finally
            {
                try { await page.CloseAsync(); } catch { }
            }

            try
            {
                await db.SaveChangesAsync(ct);

                // Post-enrichment dedup against the enriched cohort.
                // The just-enriched row now has real coords + (usually)
                // a real Google Maps URL, so it becomes dedup-eligible.
                // If an older enriched row already represents this place,
                // fold collection links onto it and delete this row.
                // "Smaller Id wins" so parallel workers can't race.
                var merged = await PoiPostEnrichmentDedup.MergeIfDuplicateAsync(db, poi, ct);
                if (merged)
                {
                    _logger.LogInformation(
                        "Post-enrich dedup: Poi {Id} '{Name}' merged into an older canonical row",
                        poi.Id, poi.Name);
                }

                // Per-POI tick: re-read the counter so the map page
                // header updates as each worker finishes.
                var newRemaining = await db.Pois.CountAsync(p => !p.IsEnriched, ct);
                _progress.Set(newRemaining);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Persisting enrichment for Poi {Id} '{Name}' failed — will retry next cycle",
                    poi.Id, poi.Name);
            }
        }
    }
}
