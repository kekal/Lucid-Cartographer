using System.Threading.Channels;

namespace LucidCartographer.Services.Export;

/// <summary>
/// Channel-backed export queue (singleton). Deliberately NOT on the Coravel
/// import queue: a Google Saved-List export drives a headful browser for tens of
/// minutes, and Coravel consumes a batch via <c>Task.WhenAll</c> — so sharing it
/// would block import ticks and graceful shutdown for the whole run. A single
/// reader (<see cref="ExportBackgroundService"/>) drains this channel one job at
/// a time; <see cref="GoogleBrowserLock"/> additionally serialises against the
/// scraper.
/// </summary>
public sealed class ExportJobQueue(ExportJobStatusService status) : IExportJobQueue
{
    private readonly Channel<ExportJobPayload> _channel =
        Channel.CreateUnbounded<ExportJobPayload>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Consumed by <see cref="ExportBackgroundService"/>.</summary>
    public ChannelReader<ExportJobPayload> Reader => _channel.Reader;

    public void Enqueue(ExportJobPayload payload)
    {
        // Unbounded writer never rejects; TryWrite is synchronous and always succeeds.
        _channel.Writer.TryWrite(payload);
        status.Publish(new ExportJobStatus(
            ExportJobState.Queued,
            $"Queued export to '{payload.ListName}'. A browser window will open; you may leave this page.",
            CollectionId: payload.CollectionId));
    }
}
