using System.Threading.Channels;
using Unit = System.Reactive.Unit;

namespace LucidCartographer.Services.Operations;

/// <summary>
/// Wake signal for <see cref="PoiDeduplicationBackgroundService"/>. The
/// background loop awaits <see cref="WaitAsync"/> between its hourly ticks;
/// the enrichment worker calls <see cref="Signal"/> the moment a pipeline's
/// enrichment queue drains so a full-DB dedup pass runs promptly instead of
/// waiting up to an hour for the next timer tick. Mirrors
/// <c>EnrichmentTrigger</c>.
/// </summary>
public sealed class DedupTrigger
{
    // Bounded(1) + DropWrite collapses a burst of signals (several batches
    // draining in quick succession) into exactly one pending wake-up.
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
