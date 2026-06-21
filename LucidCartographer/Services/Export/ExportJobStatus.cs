using System.Reactive.Subjects;

namespace LucidCartographer.Services.Export;

public enum ExportJobState
{
    Idle,
    Queued,
    Running,
    Completed,
    Failed
}

public sealed record ExportJobStatus(
    ExportJobState State,
    string Message,
    ExportRunReport? Result = null,
    string? Error = null,
    int? CollectionId = null)
{
    public static ExportJobStatus Idle { get; } = new(ExportJobState.Idle, string.Empty);
}

/// <summary>
/// Singleton Rx bus for export job status lifecycle and per-place progress.
/// Changes replays the latest value on subscribe (BehaviorSubject semantics).
/// </summary>
public sealed class ExportJobStatusService
{
    private readonly BehaviorSubject<ExportJobStatus> _subject = new(ExportJobStatus.Idle);

    public ExportJobStatus Current => _subject.Value;
    public IObservable<ExportJobStatus> Changes => _subject;

    public void Publish(ExportJobStatus status) => _subject.OnNext(status);
}
