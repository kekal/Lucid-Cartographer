namespace LucidCartographer.Services.Operations;

/// <summary>
/// Tunables for the background deduplication pass, bound from the
/// "Deduplication" section of appsettings.json.
/// </summary>
public sealed class DeduplicationOptions
{
    /// <summary>
    /// How often the safety-net pass runs even when nothing signals it.
    /// The pass also runs promptly whenever an enrichment batch drains, so
    /// this interval only bounds the worst case for changes the worker can't
    /// observe (e.g. a manual DB edit). Defaults to 60 minutes.
    /// </summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Delay before the first pass after startup, giving migrations, startup
    /// revive and the first enrichment cycle a chance to settle. Seconds.
    /// </summary>
    public int StartupDelaySeconds { get; set; } = 30;

    /// <summary>Set false to disable the background pass entirely.</summary>
    public bool Enabled { get; set; } = true;
}
