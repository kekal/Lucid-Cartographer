namespace LucidCartographer.Services;

/// <summary>
/// Process-wide serializer for SQLite write transactions. The DB is a
/// single file with no <c>busy_timeout</c> configured, so two writers
/// committing at the same instant surface as a "database is locked"
/// error. The enrichment worker and the deduplication pass both mutate
/// rows from their own background contexts and can run concurrently
/// (e.g. the hourly dedup tick firing mid-enrichment), so they share
/// this one gate to take turns committing.
///
/// Registered as a singleton — every caller that wraps a
/// <c>SaveChangesAsync</c> in <see cref="Gate"/> participates in the
/// same mutual exclusion. SQLite serializes writers across processes
/// too, but within this process the gate avoids the busy error entirely
/// rather than relying on retry.
/// </summary>
public sealed class SqliteWriteLock : IDisposable
{
    /// <summary>
    /// Shared write gate; callers must <c>await WaitAsync(ct)</c> before <c>SaveChangesAsync</c> and <c>Release()</c> in a finally block.
    /// </summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public void Dispose() => Gate.Dispose();
}
