namespace LucidCartographer.Services.Enrichment
{
    /// <summary>
    /// Tunables for <see cref="PoiEnrichmentBackgroundService"/>. Bound
    /// from the <c>Enrichment</c> section of <c>appsettings.json</c> so
    /// the service's parallelism and batching can change without a
    /// recompile. Defaults here match what was previously hard-coded.
    /// </summary>
    public sealed class EnrichmentOptions
    {
        /// <summary>
        /// Maximum number of Playwright tabs enriching POIs in parallel.
        /// Higher values reduce wall-clock time on a 200-row list but
        /// increase the chance of tripping Google Maps' bot detection.
        /// Default: 4.
        /// </summary>
        public int Concurrency { get; set; } = 4;

        /// <summary>
        /// Hard cap for concurrently opened Playwright pages across all
        /// enrichment workers. Keeps Chromium stable when concurrency is
        /// tuned aggressively.
        /// Default: 8.
        /// </summary>
        public int MaxConcurrentPages { get; set; } = 8;

        /// <summary>
        /// Number of POI IDs pulled from the database per enrichment cycle.
        /// A larger batch amortises the query overhead; a smaller batch
        /// gives finer progress feedback. Default: 16.
        /// </summary>
        public int BatchSize { get; set; } = 16;

        /// <summary>
        /// Idle poll interval in seconds when the enrichment queue is
        /// empty. The background service still wakes instantly on
        /// <see cref="EnrichmentTrigger.Signal"/>; this is the upper bound
        /// on latency for rows added without going through the trigger
        /// (e.g. a manual SQL edit). Default: 30.
        /// </summary>
        public int IdlePollSeconds { get; set; } = 30;

        /// <summary>
        /// Maximum consecutive enrichment failures allowed before a row is
        /// paused from automatic retries until manually reset.
        /// Default: 5.
        /// </summary>
        public int MaxRetries { get; set; } = 5;

        /// <summary>
        /// Exponential backoff base in seconds between failed retries.
        /// Cooldown formula: base * 2^(failureCount - 1).
        /// Default: 30.
        /// </summary>
        public int BackoffBaseSeconds { get; set; } = 30;
    }
}
