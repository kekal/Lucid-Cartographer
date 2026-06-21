using System.Reactive.Subjects;

namespace LucidCartographer.Services.Import;

public enum ImportJobState
{
    Idle,
    Queued,
    Running,
    Completed,
    Failed
}

public sealed record ImportJobStatus(
    ImportJobState State,
    string Message,
    ImportResult? Result = null,
    string? Error = null)
{
    public static ImportJobStatus Idle { get; } = new(ImportJobState.Idle, string.Empty);
}

/// <summary>
/// Singleton Rx bus for background-import lifecycle updates. <see cref="Changes"/> replays
/// the latest value on subscribe (BehaviorSubject). Carries only lifecycle transitions
/// (queued → running → completed/failed); real-time progress is reported separately.
/// </summary>
public sealed class ImportJobStatusService
{
    private readonly BehaviorSubject<ImportJobStatus> _subject = new(ImportJobStatus.Idle);

    public ImportJobStatus Current => _subject.Value;
    public IObservable<ImportJobStatus> Changes => _subject;

    public void Publish(ImportJobStatus status) => _subject.OnNext(status);
}