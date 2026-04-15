using System.Threading.Channels;
using Unit = System.Reactive.Unit;

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
        // Bounded(1) + DropWrite collapses a burst of signals into exactly
        // one wake-up, matching the original SemaphoreSlim(0,1) + swallowed
        // SemaphoreFullException behavior.
        private readonly Channel<Unit> _channel = Channel.CreateBounded<Unit>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

        public void Signal() => _channel.Writer.TryWrite(Unit.Default);

        /// <summary>
        /// Waits for a signal or until <paramref name="timeout"/> elapses,
        /// whichever comes first. Returns true on signal, false on timeout.
        /// </summary>
        public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await _channel.Reader.ReadAsync(cts.Token); return true; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        }
    }
}
