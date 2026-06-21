using System.Threading.Channels;
using Unit = System.Reactive.Unit;

namespace LucidCartographer.Services.Operations;

/// <summary>
/// Wake signal for <see cref="PoiDeduplicationBackgroundService"/>—triggers
/// dedup when enrichment queue drains, avoiding hourly-tick latency.
/// </summary>
public sealed class DedupTrigger
{
    // Bounded(1) + DropWrite collapses signal bursts into one pending wake-up.
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
