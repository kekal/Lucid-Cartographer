using System.Threading.RateLimiting;
using Polly;
using Polly.Retry;

namespace LucidCartographer.Configuration;

public static class ResilienceExtensions
{
    /// <summary>
    /// Polly v8 resilience pipelines.
    /// Replaces hand-rolled SemaphoreSlim in GoogleMapsListScraper and adds
    /// retry/timeout to Playwright-based scraping + enrichment. Pipelines are
    /// registered by name and resolved via ResiliencePipelineProvider&lt;string&gt;.
    ///   - "scraper": single-flight (concurrency=1) + timeout + retry. Used for
    ///     list scrapes so at most one Chromium instance runs at a time.
    ///   - "enrichment": retry + timeout for per-POI enrichment work.
    /// </summary>
    public static IServiceCollection AddAppResiliencePipelines(this IServiceCollection services)
    {
        services.AddResiliencePipeline("scraper", pipeline =>
        {
            pipeline
                .AddConcurrencyLimiter(new ConcurrencyLimiterOptions
                {
                    PermitLimit = 1,
                    QueueLimit = int.MaxValue
                })
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 2,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = TimeSpan.FromSeconds(2)
                })
                .AddTimeout(TimeSpan.FromMinutes(10));
        });

        services.AddResiliencePipeline("enrichment", pipeline =>
        {
            pipeline
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = TimeSpan.FromSeconds(1)
                })
                .AddTimeout(TimeSpan.FromMinutes(2));
        });

        // TRIP-TRAVELTIME-01: per-leg travel-time provider calls. Same retry +
        // timeout shape as "enrichment"; the haversine Mock never fails, but a
        // real routing provider (Epic 4) gets transient-fault handling for free.
        services.AddResiliencePipeline("travel-time", pipeline =>
        {
            pipeline
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = TimeSpan.FromSeconds(1)
                })
                .AddTimeout(TimeSpan.FromMinutes(2));
        });

        return services;
    }
}
