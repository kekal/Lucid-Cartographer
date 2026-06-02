using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Services.Operations;

/// <summary>
/// Executes set operations (subtract, intersect, union, dedup) on POI collections.
/// Uses <see cref="IPoiMatcher"/> for POI comparison with URL pre-indexing for O(N+M) binary operations.
/// </summary>
public class SetOperationService(IDbContextFactory<AppDbContext> factory, IPoiMatcher matcher) : ISetOperationService
{
    public async Task<OperationResult> ExecuteAsync(
        SetOperation operation,
        int collectionAId,
        int? collectionBId,
        double toleranceMeters = IPoiMatcher.DefaultToleranceMeters,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var poisA = await GetCollectionPois(db, collectionAId, cancellationToken);

        return operation switch
        {
            SetOperation.Dedup => ExecuteDedup(poisA, toleranceMeters),
            _ when collectionBId == null => throw new ArgumentException("Collection B is required for this operation"),
            _ => await ExecuteBinaryOp(db, operation, poisA, collectionBId.Value, toleranceMeters, cancellationToken)
        };
    }

    private async Task<OperationResult> ExecuteBinaryOp(
        AppDbContext db,
        SetOperation operation,
        List<Poi> poisA,
        int collectionBId,
        double toleranceMeters,
        CancellationToken cancellationToken)
    {
        var poisB = await GetCollectionPois(db, collectionBId, cancellationToken);

        // Matching runs directly against the candidate lists via
        // PoiIdentity.AreSamePlace (name + proximity). The old URL-index
        // optimisation is gone because URL is no longer part of the
        // identity rule — two franchise branches at different coords
        // must stay distinct.
        return operation switch
        {
            SetOperation.Subtract => ExecuteSubtract(poisA, poisB, toleranceMeters),
            SetOperation.Intersect => ExecuteIntersect(poisA, poisB, toleranceMeters),
            SetOperation.Union => ExecuteUnion(poisA, poisB, toleranceMeters),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    private OperationResult ExecuteSubtract(List<Poi> poisA, List<Poi> poisB, double toleranceMeters)
    {
        var result = poisA.Where(a => matcher.FindMatch(a, poisB, toleranceMeters) == null).ToList();
        return new OperationResult
        {
            Pois = result,
            Description = $"A - B: {result.Count} POIs from {poisA.Count} not found in {poisB.Count}"
        };
    }

    /// <summary>
    /// OPS-R06: Intersect merges B-side metadata into A-side POIs.
    /// When A has a match in B, any fields that A is missing but B has are filled in.
    /// </summary>
    private OperationResult ExecuteIntersect(List<Poi> poisA, List<Poi> poisB, double toleranceMeters)
    {
        var result = new List<Poi>();
        foreach (var a in poisA)
        {
            var matchB = matcher.FindMatch(a, poisB, toleranceMeters);
            if (matchB != null)
            {
                MergeBSideData(a, matchB);
                result.Add(a);
            }
        }
        return new OperationResult
        {
            Pois = result,
            Description = $"A intersect B: {result.Count} common POIs between {poisA.Count} and {poisB.Count}"
        };
    }

    /// <summary>
    /// Merges useful fields from B into A where A is missing data.
    /// </summary>
    private static void MergeBSideData(Poi a, Poi b)
    {
        if (string.IsNullOrEmpty(a.Address) && !string.IsNullOrEmpty(b.Address))
        {
            a.Address = b.Address;
        }

        if (string.IsNullOrEmpty(a.GoogleMapsUrl) && !string.IsNullOrEmpty(b.GoogleMapsUrl))
        {
            a.GoogleMapsUrl = b.GoogleMapsUrl;
        }

        if (string.IsNullOrEmpty(a.Category) && !string.IsNullOrEmpty(b.Category))
        {
            a.Category = b.Category;
        }

        if (string.IsNullOrEmpty(a.Notes) && !string.IsNullOrEmpty(b.Notes))
        {
            a.Notes = b.Notes;
        }

        if (string.IsNullOrEmpty(a.Website) && !string.IsNullOrEmpty(b.Website))
        {
            a.Website = b.Website;
        }

        if (string.IsNullOrEmpty(a.Phone) && !string.IsNullOrEmpty(b.Phone))
        {
            a.Phone = b.Phone;
        }

        if (string.IsNullOrEmpty(a.ImageUrl) && !string.IsNullOrEmpty(b.ImageUrl))
        {
            a.ImageUrl = b.ImageUrl;
        }

        if (!a.GoogleRating.HasValue && b.GoogleRating.HasValue)
        {
            a.GoogleRating = b.GoogleRating;
        }

        if (!a.ReviewCount.HasValue && b.ReviewCount.HasValue)
        {
            a.ReviewCount = b.ReviewCount;
        }
    }

    /// <summary>
    /// OPS-R05: Deduplicates within A first, then adds unique items from B.
    /// A true set union contains each unique element exactly once.
    /// </summary>
    private OperationResult ExecuteUnion(List<Poi> poisA, List<Poi> poisB, double toleranceMeters)
    {
        // Dedup within A first so the union result contains no internal duplicates.
        var dedupGroups = matcher.FindDuplicateGroups(poisA, toleranceMeters);
        var dupIdsInA = dedupGroups.SelectMany(g => g.Skip(1).Select(p => p.Id)).ToHashSet();
        var dedupedA = poisA.Where(p => !dupIdsInA.Contains(p.Id)).ToList();

        List<Poi> result = [..dedupedA];
        result.AddRange(poisB.Where(b => matcher.FindMatch(b, dedupedA, toleranceMeters) == null));
        return new OperationResult
        {
            Pois = result,
            Description = $"A union B: {result.Count} unique POIs from {poisA.Count} + {poisB.Count} (removed {dupIdsInA.Count} duplicates within A)",
        };
    }

    private OperationResult ExecuteDedup(List<Poi> pois, double toleranceMeters)
    {
        var groups = matcher.FindDuplicateGroups(pois, toleranceMeters);
        var duplicateIds = groups.SelectMany(g => g.Skip(1).Select(p => p.Id)).ToHashSet();

        return new OperationResult
        {
            Pois = pois.Where(p => !duplicateIds.Contains(p.Id)).ToList(),
            DuplicateGroups = groups,
            Description = $"Dedup: found {groups.Count} duplicate groups in {pois.Count} POIs"
        };
    }

    /// <summary>
    /// Saves operation result as a new collection.
    /// </summary>
    public async Task<PoiCollection> CommitResultAsync(
        List<Poi> pois,
        string name,
        string color = "#7c3aed",
        CancellationToken cancellationToken = default)
    {
        // OPS-R14: Validate inputs
        ArgumentNullException.ThrowIfNull(pois);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Collection name cannot be empty.", nameof(name));
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Defence-in-depth: the supplied rows may be stale (e.g. a whole-DB
        // dedup pass deleted a previewed Poi between the operation and the
        // commit). Inserting a dangling PoiId would violate the FK to Poi.Id
        // and abort the transaction, so only link ids that still exist.
        var requestedIds = pois.Select(p => p.Id).Distinct().ToList();
        var existingIds = await db.Pois
            .AsNoTracking()
            .Where(p => requestedIds.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        var collection = new PoiCollection
        {
            Name = name,
            Color = color,
            SourceType = "operation_result",
            CreatedDate = DateTime.UtcNow
        };
        db.PoiCollections.Add(collection);
        await db.SaveChangesAsync(cancellationToken);

        db.PoiCollectionItems.AddRange(existingIds.Select(id => new PoiCollectionItem
        {
            PoiId = id,
            PoiCollectionId = collection.Id
        }));
        collection.PoiCount = existingIds.Count;
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return collection;
    }

    /// <summary>
    /// OPS-R04: Filters out null navigation properties from orphaned FKs.
    /// </summary>
    private static async Task<List<Poi>> GetCollectionPois(AppDbContext db, int collectionId, CancellationToken cancellationToken = default)
    {
        return await db.PoiCollectionItems
            .AsNoTracking()
            .Where(ci => ci.PoiCollectionId == collectionId && ci.Poi != null)
            .Select(ci => ci.Poi)
            .ToListAsync(cancellationToken);
    }
}
