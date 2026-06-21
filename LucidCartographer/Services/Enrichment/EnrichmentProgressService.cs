using System.Reactive.Subjects;

namespace LucidCartographer.Services.Enrichment;

/// <summary>
/// Publishes enrichment progress (remaining count) via a reactive stream so the UI can display fetched vs. total.
/// <see cref="Total"/> resets to 0 when the queue drains, so progress display persists during a run and hides between runs.
/// </summary>
public class EnrichmentProgressService
{
    private readonly BehaviorSubject<int> _remaining = new(0);
    public int Remaining => _remaining.Value;
    public int Total { get; private set; }
    public int Fetched => Total - Remaining;
    public IObservable<int> Changes => _remaining;

    public void Set(int remaining)
    {
        if (remaining > Total)
        {
            Total = remaining;
        }

        // Reset when drained so the next batch starts counting fresh rather than carrying old total forward.
        if (remaining == 0)
        {
            Total = 0;
        }

        if (remaining == _remaining.Value)
        {
            return;
        }

        _remaining.OnNext(remaining);
    }
}
