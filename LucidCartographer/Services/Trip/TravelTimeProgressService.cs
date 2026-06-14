using System.Reactive.Subjects;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-TRAVELTIME-01: tiny singleton that lets
/// <see cref="TravelTimeComputationBackgroundService"/> publish a "legs
/// computing" count to the UI — the travel-time counterpart of
/// <c>EnrichmentProgressService</c>. The ViewModel subscribes to
/// <see cref="Changes"/> (a BehaviorSubject replaying the latest value on
/// subscribe) and refreshes its leg projections + raises StateChanged when the
/// count drops, so freshly-computed legs land without polling the circuit thread.
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
