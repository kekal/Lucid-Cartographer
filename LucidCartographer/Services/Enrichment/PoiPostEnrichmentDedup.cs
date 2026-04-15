using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Operations;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Services.Enrichment
{
    /// <summary>
    /// Post-enrichment dedup: called right after <see cref="PoiEnrichmentBackgroundService"/>
    /// persists a newly-enriched row. If the row is now a duplicate of an
    /// already-enriched row with a smaller Id, this helper folds it into
    /// the older row (moving collection links, dropping the duplicate's
    /// image, deleting the row itself).
    ///
    /// The "smaller Id wins" rule lets multiple parallel enrichment workers
    /// cooperate without any cross-worker coordination: only the worker
    /// holding the larger-Id row acts; the worker holding the smaller-Id
    /// row finds no candidate and is a no-op. No lock, no race.
    ///
    /// Unenriched rows are never touched — they carry placeholder (0,0)
    /// coordinates which would trivially collapse distinct places; they
    /// will be re-checked when they themselves finish enrichment.
    /// </summary>
    internal static class PoiPostEnrichmentDedup
    {
        private const double ProximityThresholdMeters = 100;

        /// <summary>
        /// Returns true if <paramref name="justEnriched"/> was merged into an
        /// older canonical row and removed from the database.
        /// </summary>
        public static async Task<bool> MergeIfDuplicateAsync(
            AppDbContext db,
            Poi justEnriched,
            CancellationToken ct)
        {
            if (!justEnriched.IsEnriched)
                return false;

            var canonical = await FindCanonicalAsync(db, justEnriched, ct);
            if (canonical == null)
                return false;

            await RewriteCollectionLinksAsync(db, justEnriched, canonical, ct);
            await DropDuplicateImageAsync(db, justEnriched, ct);
            db.Pois.Remove(justEnriched);
            await db.SaveChangesAsync(ct);
            return true;
        }

        // ---- Match strategy ------------------------------------------------------

        private static async Task<Poi?> FindCanonicalAsync(
            AppDbContext db, Poi justEnriched, CancellationToken ct)
        {
            // Tier 1: exact normalized Google Maps URL. Rows reach this
            // helper already-normalized because ImportPersister calls
            // PoiMatcher.NormalizeUrl on insert.
            if (!string.IsNullOrEmpty(justEnriched.GoogleMapsUrl))
            {
                var byUrl = await db.Pois
                    .Where(p => p.Id < justEnriched.Id
                             && p.IsEnriched
                             && p.GoogleMapsUrl == justEnriched.GoogleMapsUrl)
                    .OrderBy(p => p.Id)
                    .FirstOrDefaultAsync(ct);
                if (byUrl != null) return byUrl;
            }

            // Tier 2: exact name + real coords + proximity. Skipped when the
            // just-enriched row lacks real coordinates (should not happen
            // post-enrichment, but belt-and-suspenders for the enricher
            // that legitimately returns null coords).
            if (justEnriched.Latitude == 0 && justEnriched.Longitude == 0)
                return null;

            var nameLower = justEnriched.Name.ToLowerInvariant().Trim();

            // Pull the by-name candidate set from SQLite with a cheap LOWER
            // comparison, then apply the Haversine filter in memory — EF
            // can't translate the math and the candidate set for a single
            // name is tiny in practice.
            var candidates = await db.Pois
                .Where(p => p.Id < justEnriched.Id
                         && p.IsEnriched
                         && p.Name.ToLower() == nameLower
                         && p.Latitude != 0
                         && p.Longitude != 0)
                .OrderBy(p => p.Id)
                .ToListAsync(ct);

            foreach (var c in candidates)
            {
                var distance = GeoUtils.HaversineDistance(
                    c.Latitude, c.Longitude,
                    justEnriched.Latitude, justEnriched.Longitude);
                if (distance < ProximityThresholdMeters)
                    return c;
            }

            return null;
        }

        // ---- Merge mechanics -----------------------------------------------------

        private static async Task RewriteCollectionLinksAsync(
            AppDbContext db, Poi duplicate, Poi canonical, CancellationToken ct)
        {
            // Collect the canonical row's existing collection membership so
            // we can drop (not duplicate) a link that already exists there.
            var canonicalCollections = await db.PoiCollectionItems
                .Where(ci => ci.PoiId == canonical.Id)
                .Select(ci => ci.PoiCollectionId)
                .ToListAsync(ct);
            var alreadyLinked = new HashSet<int>(canonicalCollections);

            var duplicateLinks = await db.PoiCollectionItems
                .Where(ci => ci.PoiId == duplicate.Id)
                .ToListAsync(ct);

            foreach (var link in duplicateLinks)
            {
                // PoiId + PoiCollectionId is a composite primary key in EF,
                // so we can't UPDATE PoiId in place — remove the old link
                // and either re-add it under the canonical row or drop it
                // entirely if the canonical is already in this collection.
                db.PoiCollectionItems.Remove(link);
                if (!alreadyLinked.Contains(link.PoiCollectionId))
                {
                    db.PoiCollectionItems.Add(new PoiCollectionItem
                    {
                        PoiId = canonical.Id,
                        PoiCollectionId = link.PoiCollectionId
                    });
                    alreadyLinked.Add(link.PoiCollectionId);
                }
            }
        }

        private static async Task DropDuplicateImageAsync(
            AppDbContext db, Poi duplicate, CancellationToken ct)
        {
            // PoiImage.PoiId is the primary key, so we can't reassign it —
            // we'd need remove+add. For now, unconditionally drop the
            // duplicate's image; the canonical keeps whatever it already
            // has (which will itself have been enriched before this
            // merge runs, so it's almost certainly populated).
            var image = await db.PoiImages.FindAsync(new object?[] { duplicate.Id }, ct);
            if (image != null)
                db.PoiImages.Remove(image);
        }
    }
}
