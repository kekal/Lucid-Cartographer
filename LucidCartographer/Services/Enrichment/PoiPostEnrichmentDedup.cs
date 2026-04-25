using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Services.Enrichment;

/// <summary>
/// Post-enrichment dedup: called right after <see cref="PoiEnrichmentBackgroundService"/>
/// persists a newly-enriched row. If the row is now equal (per
/// <see cref="PoiIdentity.AreSamePlace(Poi?, Poi?)"/>) to an older
/// enriched row, this helper folds it into the older row (moving
/// collection links, dropping the duplicate's image, deleting the
/// row itself).
///
/// "Smaller Id wins" lets multiple parallel enrichment workers
/// cooperate without cross-worker coordination: only the worker
/// holding the larger-Id row acts; the worker holding the smaller-Id
/// row finds no candidate and is a no-op. No lock, no race.
///
/// Unenriched rows are never touched — they carry NULL coordinates
/// which <see cref="PoiIdentity"/> explicitly excludes from identity
/// decisions. They'll be re-checked when they themselves finish
/// enrichment.
/// </summary>
internal static class PoiPostEnrichmentDedup
{
    /// <summary>
    /// Returns true if <paramref name="justEnriched"/> was merged into an
    /// older canonical row and removed from the database.
    /// </summary>
    public static async Task<bool> MergeIfDuplicateAsync(
        AppDbContext db,
        Poi justEnriched,
        CancellationToken ct,
        SemaphoreSlim? writeLock = null)
    {
        if (!justEnriched.IsEnriched)
        {
            return false;
        }

        var canonical = await FindCanonicalAsync(db, justEnriched, ct);
        if (canonical == null)
        {
            return false;
        }

        BackfillCanonicalFields(justEnriched, canonical);
        await RewriteCollectionLinksAsync(db, justEnriched, canonical, ct);
        await ReassignOrDropDuplicateImageAsync(db, justEnriched, canonical, ct);
        db.Pois.Remove(justEnriched);
        if (writeLock is null)
        {
            await db.SaveChangesAsync(ct);
        }
        else
        {
            await writeLock.WaitAsync(ct);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            finally
            {
                writeLock.Release();
            }
        }

        return true;
    }

    // ---- Match strategy ------------------------------------------------------

    private static async Task<Poi?> FindCanonicalAsync(
        AppDbContext db, Poi justEnriched, CancellationToken ct)
    {
        // PoiIdentity excludes NULL coords — nothing to find if the
        // enricher didn't return real coords.
        if (justEnriched.Latitude == null || justEnriched.Longitude == null)
        {
            return null;
        }

        // SQL-side pre-filter: a coordinate bounding box around the
        // just-enriched row. Using name-based filtering on SQLite was
        // broken for non-ASCII text because SQLite's built-in LOWER()
        // only lowercases ASCII — `WIEŻA WIDOKOWA` would stay
        // `wieŻa widokowa` and never match `wieża widokowa` produced
        // by C#'s Unicode-aware ToLowerInvariant. The bbox avoids the
        // issue entirely and is itself indexable on lat/lon.
        //
        // The box is intentionally generous (≈222m at 50°N latitude,
        // vs PoiIdentity's 100m threshold) so the in-memory
        // AreSamePlace check still has the final say without missing
        // candidates sitting right on the edge.
        const double boundingBoxDegrees = 0.002;
        var latLo = justEnriched.Latitude.Value - boundingBoxDegrees;
        var latHi = justEnriched.Latitude.Value + boundingBoxDegrees;
        var lonLo = justEnriched.Longitude.Value - boundingBoxDegrees;
        var lonHi = justEnriched.Longitude.Value + boundingBoxDegrees;

        var candidates = await db.Pois
            .Where(p => p.Id < justEnriched.Id
                        && p.Latitude != null
                        && p.Longitude != null
                        && p.Latitude >= latLo
                        && p.Latitude <= latHi
                        && p.Longitude >= lonLo
                        && p.Longitude <= lonHi)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);

        return candidates.Find(c => PoiIdentity.AreSamePlace(c, justEnriched));
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
        HashSet<int> alreadyLinked = [..canonicalCollections];

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

    private static async Task ReassignOrDropDuplicateImageAsync(
        AppDbContext db, Poi duplicate, Poi canonical, CancellationToken ct)
    {
        var duplicateImage = await db.PoiImages.FindAsync([duplicate.Id], ct);
        if (duplicateImage == null)
        {
            return;
        }

        var canonicalImage = await db.PoiImages.FindAsync([canonical.Id], ct);
        if (canonicalImage != null)
        {
            if (!IsLikelyPlacePhotoUrl(canonical.ImageUrl)
                && IsLikelyPlacePhotoUrl(duplicate.ImageUrl))
            {
                canonicalImage.Data = duplicateImage.Data;
                canonicalImage.ContentType = duplicateImage.ContentType;
                canonical.ImageUrl = duplicate.ImageUrl;
                db.PoiImages.Remove(duplicateImage);
                return;
            }

            // Canonical already has an image — drop the duplicate's.
            db.PoiImages.Remove(duplicateImage);
            return;
        }

        // PoiImage.PoiId is the primary key, so we can't UPDATE it in
        // place. Snapshot the bytes, remove the old row, add a new one
        // under the canonical's Id. Both operations land in the same
        // SaveChanges so there is no observable "no image" window.
        var bytes = duplicateImage.Data;
        var contentType = duplicateImage.ContentType;
        db.PoiImages.Remove(duplicateImage);
        db.PoiImages.Add(new PoiImage
        {
            PoiId = canonical.Id,
            Data = bytes,
            ContentType = contentType
        });
    }

    private static bool IsLikelyPlacePhotoUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (url.Contains("/maps/vt", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/vt/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("tile.openstreetmap.org", StringComparison.OrdinalIgnoreCase)
            || (url.Contains("x=", StringComparison.OrdinalIgnoreCase)
                && url.Contains("y=", StringComparison.OrdinalIgnoreCase)
                && url.Contains("z=", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return url.Contains("googleusercontent.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("streetviewpixels-pa.googleapis.com", StringComparison.OrdinalIgnoreCase);
    }

    private static void BackfillCanonicalFields(Poi duplicate, Poi canonical)
    {
        if (string.IsNullOrWhiteSpace(canonical.ImageUrl) && !string.IsNullOrWhiteSpace(duplicate.ImageUrl))
        {
            canonical.ImageUrl = duplicate.ImageUrl;
        }

        if (string.IsNullOrWhiteSpace(canonical.GoogleMapsUrl) && !string.IsNullOrWhiteSpace(duplicate.GoogleMapsUrl))
        {
            canonical.GoogleMapsUrl = duplicate.GoogleMapsUrl;
        }

        if (string.IsNullOrWhiteSpace(canonical.Address) && !string.IsNullOrWhiteSpace(duplicate.Address))
        {
            canonical.Address = duplicate.Address;
        }

        if (string.IsNullOrWhiteSpace(canonical.Website) && !string.IsNullOrWhiteSpace(duplicate.Website))
        {
            canonical.Website = duplicate.Website;
        }

        if (string.IsNullOrWhiteSpace(canonical.Phone) && !string.IsNullOrWhiteSpace(duplicate.Phone))
        {
            canonical.Phone = duplicate.Phone;
        }
    }
}
