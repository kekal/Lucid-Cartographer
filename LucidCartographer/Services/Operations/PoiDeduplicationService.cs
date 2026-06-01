using LucidCartographer.Data;
using LucidCartographer.Services.Enrichment;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Services.Operations;

/// <summary>
/// Outcome of a whole-database deduplication pass.
/// <see cref="PoisMerged"/> is the number of duplicate rows folded into a
/// canonical row and deleted; <see cref="GroupsMerged"/> is the number of
/// distinct real-world places that had at least one duplicate.
/// </summary>
public sealed record DedupResult(int GroupsMerged, int PoisMerged);

public interface IPoiDeduplicationService
{
    /// <summary>
    /// Scans the entire POI table for rows that denote the same real-world
    /// place (stable Google place id first, then name + proximity) and folds
    /// each duplicate into the lowest-Id canonical row. Idempotent: running
    /// it on an already-clean DB merges nothing.
    /// </summary>
    Task<DedupResult> DeduplicateAllAsync(CancellationToken ct = default);
}

/// <summary>
/// L7 (application-level) deduplication. Deliberately NOT a DB unique
/// constraint: duplicates are a rare event (they only appear when a place's
/// Google identity changes or two pipelines race), so paying for an index
/// guard on every write is the wrong trade. Instead this runs as a discrete
/// pass — triggered when an enrichment batch drains, once an hour as a
/// safety net, and on demand from the Data Sources page.
///
/// Merge mechanics (collection-link rewrite, image hand-off, field backfill,
/// "smaller Id wins") are shared with the per-row post-enrichment dedup via
/// <see cref="PoiPostEnrichmentDedup.MergePairAsync"/>. Commits are serialized
/// through the shared <see cref="SqliteWriteLock"/> so a concurrent enrichment
/// write never collides.
/// </summary>
public sealed class PoiDeduplicationService(
    IDbContextFactory<AppDbContext> factory,
    IPoiMatcher matcher,
    SqliteWriteLock writeLock,
    ILogger<PoiDeduplicationService> logger) : IPoiDeduplicationService
{
    public async Task<DedupResult> DeduplicateAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // Tracked load: MergePairAsync mutates the canonical and removes the
        // duplicates, so the group members must be the same instances the
        // context tracks. FindDuplicateGroups itself filters out rows without
        // coordinates and decides identity by place id / name+proximity.
        var all = await db.Pois.ToListAsync(ct);
        var groups = matcher.FindDuplicateGroups(all, cancellationToken: ct);
        if (groups.Count == 0)
        {
            return new DedupResult(0, 0);
        }

        var groupsMerged = 0;
        var poisMerged = 0;

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();

            // Smallest Id is canonical — matches the post-enrichment dedup
            // convention so the surviving row is stable regardless of which
            // path merged it.
            var ordered = group.OrderBy(p => p.Id).ToList();
            var canonical = ordered[0];
            var mergedInGroup = false;

            foreach (var duplicate in ordered.Skip(1))
            {
                try
                {
                    await PoiPostEnrichmentDedup.MergePairAsync(db, duplicate, canonical, ct, writeLock.Gate);
                    poisMerged++;
                    mergedInGroup = true;
                }
                catch (DbUpdateException ex)
                {
                    // A concurrent enrichment write changed one of these rows
                    // between our load and our commit (the Version token caught
                    // it). The context is now in an indeterminate state, so we
                    // abort this pass and let the next trigger (drain signal or
                    // hourly tick) start fresh — deduplication is idempotent.
                    logger.LogWarning(ex,
                        "Deduplication aborted mid-pass (concurrent write to Poi {Duplicate} or {Canonical}); " +
                        "merged {Pois} so far, will retry on next pass",
                        duplicate.Id, canonical.Id, poisMerged);

                    if (mergedInGroup)
                    {
                        groupsMerged++;
                    }

                    return new DedupResult(groupsMerged, poisMerged);
                }
            }

            if (mergedInGroup)
            {
                groupsMerged++;
            }
        }

        if (poisMerged > 0)
        {
            logger.LogInformation(
                "Deduplication merged {Pois} POI(s) across {Groups} place group(s)",
                poisMerged, groupsMerged);
        }

        return new DedupResult(groupsMerged, poisMerged);
    }
}
