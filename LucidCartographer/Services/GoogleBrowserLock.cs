namespace LucidCartographer.Services;

/// <summary>
/// Process-wide single-flight lock for concurrent Chromium sessions sharing the same profile.
/// Chromium locks the profile directory, so only one session can run at a time.
/// Background jobs should <see cref="AcquireAsync"/> to wait; interactive flows should
/// <see cref="TryAcquireAsync"/> to fail fast with a friendly message.
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
