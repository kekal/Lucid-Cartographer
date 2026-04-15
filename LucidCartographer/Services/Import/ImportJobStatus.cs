using System.Reactive.Subjects;

namespace LucidCartographer.Services.Import
{
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
    /// Singleton Rx bus the UI subscribes to for background-import lifecycle
    /// updates. The invocable publishes; Blazor pages observe via
    /// <see cref="Changes"/>, which replays the latest value on subscribe
    /// (BehaviorSubject semantics, matching <c>EnrichmentProgressService</c>).
    ///
    /// Deliberately tiny — real progress during import is not reported,
    /// because the library author mandate is "rely on enrichment progress
    /// for live feedback, don't reinvent". This service only carries
    /// lifecycle transitions (queued → running → completed/failed).
    /// </summary>
    public sealed class ImportJobStatusService
    {
        private readonly BehaviorSubject<ImportJobStatus> _subject = new(ImportJobStatus.Idle);

        public ImportJobStatus Current => _subject.Value;
        public IObservable<ImportJobStatus> Changes => _subject;

        public void Publish(ImportJobStatus status) => _subject.OnNext(status);
    }
}
