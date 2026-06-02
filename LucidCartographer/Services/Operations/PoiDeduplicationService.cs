using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
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

            // FindDuplicateGroups unions transitively (A~B by place id, B~C by
            // name+proximity), so a group can contain a pair that is neither
            // id-equal nor within tolerance — A and C bridged only through B.
            // Merging is DESTRUCTIVE (the duplicate is deleted and its links
            // reparented), so we never merge a pair IsMatch does not confirm
            // directly. But re-validating only against the single lowest-Id
            // canonical and skipping the rest stranded a genuine same-place
            // subset: an A(far)~B~C(near each other) chain where the canonical A
            // matches neither B nor C would skip BOTH every pass, and since the
            // group re-forms identically next pass, B~C would never collapse.
            //
            // So re-cluster within the group instead of skipping. We grow a list
            // of sub-cluster canonicals in ascending Id order (the scan order),
            // and fold each row into the FIRST canonical it matches — i.e. the
            // lowest-Id canonical it is the same place as. A row matching none
            // becomes a new canonical (its own sub-cluster) and survives. This
            // keeps every invariant intact: the smaller-Id-canonical convention
            // (canonicals are visited Id-ascending, first match wins), place-id-
            // first precedence and the bbox/name+proximity rule (all inside
            // IsMatch), link union + image hand-off (inside MergePairAsync), and
            // idempotence (a clean DB forms no groups; a residual group folds
            // nothing because each row is already its own canonical).
            var ordered = group.OrderBy(p => p.Id).ToList();
            var canonicals = new List<Poi>();
            var mergedInGroup = false;

            foreach (var poi in ordered)
            {
                ct.ThrowIfCancellationRequested();

                // First canonical (lowest Id, since canonicals is Id-ascending)
                // that is the same place as poi. IsMatch checks place id first
                // (equal ftid wins regardless of coord drift, so legitimately-
                // drifted same-place rows still merge), then name+proximity.
                Poi? target = null;
                foreach (var canonical in canonicals)
                {
                    if (matcher.IsMatch(canonical, poi))
                    {
                        target = canonical;
                        break;
                    }
                }

                if (target is null)
                {
                    // poi is a transitive-only bridge relative to every existing
                    // canonical. Start a new sub-cluster rather than deleting it:
                    // a later sibling that matches poi (but not the primary
                    // canonical) now collapses INTO poi, while a truly lone
                    // far-apart row simply survives as a singleton canonical.
                    if (canonicals.Count > 0)
                    {
                        logger.LogWarning(
                            "Poi {Poi} grouped transitively but is not the same place as any established " +
                            "canonical (primary {Primary}); starting a secondary canonical for its sub-cluster",
                            poi.Id, canonicals[0].Id);
                    }
                    canonicals.Add(poi);
                    continue;
                }

                try
                {
                    await PoiPostEnrichmentDedup.MergePairAsync(db, poi, target, ct, writeLock.Gate);
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
                        poi.Id, target.Id, poisMerged);

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
