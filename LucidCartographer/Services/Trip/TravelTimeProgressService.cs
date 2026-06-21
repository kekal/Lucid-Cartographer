using System.Reactive.Subjects;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// Publishes travel-time computation progress via a reactive stream instead of polling.
/// </summary>
public sealed class TravelTimeProgressService
{
    private readonly BehaviorSubject<int> _pending = new(0);

    /// <summary>Number of legs awaiting (or mid-) computation across all trips.</summary>
    public int Pending => _pending.Value;

    /// <summary>Replays the latest pending count, then pushes every change.</summary>
    public IObservable<int> Changes => _pending;

    /// <summary>Publishes a new pending count; no-op when unchanged.</summary>
    public void Set(int pending)
    {
        if (pending == _pending.Value)
        {
            return;
        }

        _pending.OnNext(pending);
    }
}
