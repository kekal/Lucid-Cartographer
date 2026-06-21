using Microsoft.Extensions.Options;

namespace LucidCartographer.Services.Operations;

/// <summary>
/// Runs whole-database deduplication off the request thread: once at startup, then triggered
/// by <see cref="DedupTrigger"/> (fired when enrichment batches drain) or at regular
/// <see cref="DeduplicationOptions.IntervalMinutes"/> intervals as a safety net.
/// Resolves <see cref="IPoiDeduplicationService"/> from a fresh DI scope per pass
/// since it is request-scoped.
/// </summary>
public sealed class PoiDeduplicationBackgroundService(
    IServiceScopeFactory scopeFactory,
    DedupTrigger trigger,
    IOptions<DeduplicationOptions> options,
    ILogger<PoiDeduplicationBackgroundService> logger) : BackgroundService
{
    private readonly DeduplicationOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Background deduplication disabled (Deduplication:Enabled=false)");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.IntervalMinutes));
        var startupDelay = TimeSpan.FromSeconds(Math.Max(0, _options.StartupDelaySeconds));

        try { await Task.Delay(startupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        logger.LogInformation(
            "PoiDeduplicationBackgroundService started (interval {Minutes} min)",
            _options.IntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IPoiDeduplicationService>();
                await service.DeduplicateAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Deduplication pass failed; will retry on next tick");
            }

            // Sleep until the interval elapses OR the enrichment worker signals
            // that a batch just drained, whichever comes first.
            try { await trigger.WaitAsync(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        logger.LogInformation("PoiDeduplicationBackgroundService stopping");
    }
}
