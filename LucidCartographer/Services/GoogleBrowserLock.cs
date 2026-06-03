namespace LucidCartographer.Services;

/// <summary>
/// Process-wide single-flight lock around headful Chromium sessions that use the
/// shared persistent profile (<c>data/chrome-profile</c>). Chromium locks the
/// profile directory, so the Google scraper ("Fetch My Lists" / shared-list
/// scrape) and the Saved-List exporter must never launch concurrently — the
/// second launch would fail with a profile-in-use error. Every persistent-profile
/// launch acquires this first.
///
/// Background jobs (export, shared-list scrape) should <see cref="AcquireAsync"/>
/// and wait their turn; the interactive inline "Fetch My Lists" flow should
/// <see cref="TryAcquireAsync"/> and surface a friendly "busy" message instead of
/// freezing the circuit.
/// </summary>
public sealed class GoogleBrowserLock
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Wait for the lock; returns a lease that releases on dispose.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        return new Releaser(_gate);
    }

    /// <summary>Try to acquire immediately; returns null if another Google browser
    /// operation is already running.</summary>
    public async Task<IDisposable?> TryAcquireAsync(CancellationToken ct = default)
    {
        return await _gate.WaitAsync(0, ct) ? new Releaser(_gate) : null;
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
