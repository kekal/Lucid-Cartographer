using System.Threading.Channels;

namespace LucidCartographer.Services.Export;

/// <summary>Channel-backed export queue; separate from Coravel to avoid blocking import ticks during long browser-driven exports.</summary>
public sealed class ExportJobQueue(ExportJobStatusService status) : IExportJobQueue
{
    private readonly Channel<ExportJobPayload> _channel =
        Channel.CreateUnbounded<ExportJobPayload>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<ExportJobPayload> Reader => _channel.Reader;

    public void Enqueue(ExportJobPayload payload)
    {
        // Unbounded channel always accepts writes.
        _channel.Writer.TryWrite(payload);
        status.Publish(new ExportJobStatus(
            ExportJobState.Queued,
            $"Queued export to '{payload.ListName}'. A browser window will open; you may leave this page.",
            CollectionId: payload.CollectionId));
    }
}
