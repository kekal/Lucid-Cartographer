namespace LucidCartographer.Services.Enrichment
{
    /// <summary>
    /// Wake signal for <see cref="PoiEnrichmentBackgroundService"/>. The
    /// background loop awaits <see cref="WaitAsync"/> between poll cycles;
    /// anyone who adds unenriched Pois (importers, scrapers) calls
    /// <see cref="Signal"/> to kick it immediately instead of making the
    /// user wait up to IdlePollInterval for the next timer tick.
    /// </summary>
    public class EnrichmentTrigger
    {
        private readonly SemaphoreSlim _sem = new(0, 1);

        public void Signal()
        {
            // Release is a no-op if the semaphore is already at its max count,
            // so back-to-back signals collapse into a single wake-up.
            try { _sem.Release(); } catch (SemaphoreFullException) { }
        }

        /// <summary>
        /// Waits for a signal or until <paramref name="timeout"/> elapses,
        /// whichever comes first. Returns true on signal, false on timeout.
        /// </summary>
        public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
            => _sem.WaitAsync(timeout, ct);
    }
}
