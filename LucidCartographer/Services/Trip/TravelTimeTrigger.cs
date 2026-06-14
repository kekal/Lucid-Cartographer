using System.Threading.Channels;
using Unit = System.Reactive.Unit;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-TRAVELTIME-01: wake signal for
/// <see cref="TravelTimeComputationBackgroundService"/> — the travel-time
/// counterpart of <c>EnrichmentTrigger</c>. The background loop awaits
/// <see cref="WaitAsync"/> between poll cycles; the ViewModel calls
/// <see cref="Signal"/> when Trip View turns on or the projections rebuild with
/// missing cache rows, kicking the compute immediately instead of waiting up to
/// the idle poll interval.
/// </summary>
public sealed class TravelTimeTrigger
{
    // Bounded(1) + DropWrite collapses a burst of signals into exactly one
    // wake-up (same shape as EnrichmentTrigger).
    private readonly Channel<Unit> _channel = Channel.CreateBounded<Unit>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Signal() => _channel.Writer.TryWrite(Unit.Default);

    /// <summary>
    /// Waits for a signal or until <paramref name="timeout"/> elapses, whichever
    /// comes first. Returns true on signal, false on timeout.
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await _channel.Reader.ReadAsync(cts.Token); return true; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }
}
