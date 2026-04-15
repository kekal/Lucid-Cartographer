namespace LucidCartographer.Services.Enrichment
{
    /// <summary>
    /// Tiny singleton that lets the background enrichment service publish
    /// progress ("N of M fetched") to anyone on the UI that cares. Subscribers
    /// use <see cref="Changed"/> to refresh their header counter and (on the
    /// Map page) re-render POIs as they transition from placeholder coords
    /// to real ones.
    ///
    /// <see cref="Total"/> is a high-water mark: it grows with new
    /// <see cref="Set(int)"/> values while work is pending and resets to 0
    /// once the queue drains, so the UI can display "13 / 22 fetched" during
    /// a run and hide itself between runs.
    /// </summary>
    public class EnrichmentProgressService
    {
        public int Remaining { get; private set; }
        public int Total { get; private set; }
        public int Fetched => Total - Remaining;
        public event Action? Changed;

        public void Set(int remaining)
        {
            // Grow the high-water mark when new work arrives.
            if (remaining > Total) Total = remaining;

            // Reset the session when the queue drains so the next batch
            // starts counting from scratch rather than carrying the old
            // total forward.
            if (remaining == 0) Total = 0;

            if (remaining == Remaining) return;
            Remaining = remaining;
            Changed?.Invoke();
        }
    }
}
