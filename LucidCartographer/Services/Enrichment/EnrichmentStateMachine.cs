using System.Linq.Expressions;
using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Enrichment;

/// <summary>The terminal result of a single enrichment attempt.</summary>
internal enum EnrichmentOutcome
{
    /// <summary>A real Google place was resolved and data scraped.</summary>
    Resolved,
    /// <summary>Page loaded cleanly but produced no place data — needs a manual URL.</summary>
    SoftFailure,
    /// <summary>An exception during the page/persist work — retryable until the cap.</summary>
    HardFailure,
}

/// <summary>
/// Pure decision logic for the enrichment queue, extracted from
/// <see cref="PoiEnrichmentBackgroundService"/> so the queue predicate and the
/// terminal-state transitions are unit-testable without a Playwright browser.
/// </summary>
internal static class EnrichmentStateMachine
{
    /// <summary>
    /// The queue membership predicate: a row is processed iff it explicitly
    /// requested enrichment and has not exhausted its retries. This is the single
    /// source of truth shared by every count/candidate query in the worker — the
    /// decoupling hinges on it keying off <see cref="Poi.EnrichmentRequested"/>,
    /// NOT <see cref="Poi.IsEnriched"/>.
    /// </summary>
    public static Expression<Func<Poi, bool>> QueuePredicate(int maxRetries) =>
        p => p.EnrichmentRequested && p.EnrichmentFailureCount < maxRetries;

    /// <summary>
    /// Applies a terminal outcome to a POI's enrichment state. Clears
    /// <see cref="Poi.EnrichmentRequested"/> on every terminal outcome (success,
    /// soft-fail, or reaching the retry cap) and keeps it true only on a retryable
    /// hard failure. Does NOT touch <see cref="Poi.LastEnrichmentAttemptAt"/> — the
    /// caller stamps that with the wall clock.
    /// </summary>
    public static void ApplyOutcome(Poi poi, EnrichmentOutcome outcome, int maxRetries)
    {
        switch (outcome)
        {
            case EnrichmentOutcome.Resolved:
                poi.IsEnriched = true;
                poi.EnrichmentFailureCount = 0;
                poi.EnrichmentNeedsManualUrl = false;
                poi.EnrichmentRequested = false;
                break;

            case EnrichmentOutcome.SoftFailure:
                poi.IsEnriched = true;
                poi.EnrichmentNeedsManualUrl = true;
                poi.EnrichmentFailureCount = 0;
                poi.EnrichmentRequested = false;
                break;

            case EnrichmentOutcome.HardFailure:
                poi.EnrichmentFailureCount++;
                if (poi.EnrichmentFailureCount >= maxRetries)
                {
                    // Give up — leave the queue. IsEnriched stays false.
                    poi.EnrichmentRequested = false;
                }
                break;
        }
    }
}
