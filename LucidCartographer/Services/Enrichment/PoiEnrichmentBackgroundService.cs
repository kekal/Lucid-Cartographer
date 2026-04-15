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
    /// tab. Two parallel tabs share a single Playwright BrowserContext so
    /// cookies / consent state are reused across tabs and iterations.
    ///
    /// Failures are not retried with a counter — the row stays
    /// IsEnriched=false and the next poll cycle picks it up again. This
    /// keeps the data model simple (one bool, no retry state) and matches
    /// the user's directive: "if something was pending — we just refetch".
    /// </summary>
    public class PoiEnrichmentBackgroundService : BackgroundService
    {
        private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(30);
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
            await using var db = await _factory.CreateDbContextAsync(ct);

            var remaining = await db.Pois
                .CountAsync(p => !p.IsEnriched, ct);
            _progress.Set(remaining);

            if (remaining == 0) return 0;

            _logger.LogInformation("Enriching queue: {Remaining} Pois pending", remaining);

            int processed = 0;
            // One POI per iteration: save + publish progress after each so the
            // map page reloads incrementally and the header counter ticks up
            // per-POI. Simpler than batching + parallel tabs, and the user
            // sees motion immediately.
            while (!ct.IsCancellationRequested)
            {
                var poi = await db.Pois
                    .Where(p => !p.IsEnriched)
                    .OrderBy(p => p.Id)
                    .FirstOrDefaultAsync(ct);

                if (poi == null) break;

                var page = await context.NewPageAsync();
                try
                {
                    // Pick entry point based on what the scraper captured:
                    //   - if GoogleMapsUrl already contains /maps/place/, open it directly;
                    //   - otherwise run a Google Maps name search (scraper left
                    //     coords at 0,0 because the list card was anchor-less).
                    // "enrichment" Polly pipeline: retry (3 attempts,
                    // jittered exponential backoff) + 2-minute per-attempt
                    // timeout. Previously there were no retries — transient
                    // Playwright failures left IsEnriched=false and the POI
                    // waited for the next idle poll cycle (up to 30s) before
                    // being retried from scratch.
                    var details = await _pipeline.ExecuteAsync(async innerCt =>
                    {
                        if (!string.IsNullOrEmpty(poi.GoogleMapsUrl) && poi.GoogleMapsUrl.Contains("/maps/place/"))
                        {
                            return await PoiDetailEnricher.EnrichAsync(page, poi.GoogleMapsUrl!, innerCt);
                        }
                        // Use the category as a disambiguation hint; if the
                        // card had no category, fall back to the first line
                        // of the description.
                        return await PoiDetailEnricher.EnrichByNameAsync(page, poi.Name, poi.Category, innerCt);
                    }, ct);

                    // Fill empty fields only — never overwrite user edits. A
                    // manual edit on a previously enriched row wouldn't come
                    // through here anyway (IsEnriched=true filters it out),
                    // but this keeps the intent explicit.
                    if (string.IsNullOrEmpty(poi.Address)) poi.Address = details.Address;
                    if (string.IsNullOrEmpty(poi.Website)) poi.Website = details.Website;
                    if (string.IsNullOrEmpty(poi.Phone)) poi.Phone = details.Phone;

                    // If the scraper left placeholder (0,0) coords, fill real
                    // ones from the enrichment result. Leave non-zero values
                    // alone so KML/GPX imports that already had coords are
                    // not overwritten.
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
                }
                finally
                {
                    try { await page.CloseAsync(); } catch { }
                }

                // Persist this POI and publish progress before moving on.
                await db.SaveChangesAsync(ct);
                processed++;

                var newRemaining = await db.Pois
                    .CountAsync(p => !p.IsEnriched, ct);
                _progress.Set(newRemaining);
            }

            return processed;
        }
    }
}
