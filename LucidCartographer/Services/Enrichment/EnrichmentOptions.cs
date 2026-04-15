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
    }
}
