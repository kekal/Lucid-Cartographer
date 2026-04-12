using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Services.Operations
{
    /// <summary>
    /// Set operation types for POI collections.
    /// </summary>
    public enum SetOperation
    {
        /// <summary>A - B: POIs in A that are not in B.</summary>
        Subtract,
        /// <summary>A intersect B: POIs present in both A and B.</summary>
        Intersect,
        /// <summary>A union B: All unique POIs from both collections.</summary>
        Union,
        /// <summary>Remove duplicates within A.</summary>
        Dedup
    }

    /// <summary>
    /// Result of a set operation, containing the resulting POIs and metadata.
    /// </summary>
    public class OperationResult
    {
        /// <summary>The resulting POIs from the operation.</summary>
        public List<Poi> Pois { get; init; } = new();

        /// <summary>Duplicate groups found (only populated for Dedup operations).</summary>
        public List<List<Poi>>? DuplicateGroups { get; init; }

        /// <summary>Human-readable description of the operation result.</summary>
        public string Description { get; init; } = string.Empty;
    }

    /// <summary>
    /// Executes set operations (subtract, intersect, union, dedup) on POI collections.
    /// Uses <see cref="IPoiMatcher"/> for POI comparison with URL pre-indexing for O(N+M) binary operations.
    /// </summary>
    public class SetOperationService : ISetOperationService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly IPoiMatcher _matcher;

        /// <summary>
        /// Creates a new SetOperationService.
        /// </summary>
        /// <param name="factory">Database context factory.</param>
        /// <param name="matcher">POI matching service.</param>
        public SetOperationService(IDbContextFactory<AppDbContext> factory, IPoiMatcher matcher)
        {
            _factory = factory;
            _matcher = matcher;
        }

        /// <summary>
        /// Executes a set operation on one or two POI collections.
        /// </summary>
        public async Task<OperationResult> ExecuteAsync(
            SetOperation operation,
            int collectionAId,
            int? collectionBId,
            double toleranceMeters = PoiMatcher.DefaultToleranceMeters,
            CancellationToken cancellationToken = default)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);

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

            // Pre-build URL indexes for O(1) lookup (OPS-C02)
            var urlIndexB = _matcher.BuildUrlIndex(poisB);
            var urlIndexA = _matcher.BuildUrlIndex(poisA);

            return operation switch
            {
                SetOperation.Subtract => ExecuteSubtract(poisA, poisB, urlIndexB, toleranceMeters),
                SetOperation.Intersect => ExecuteIntersect(poisA, poisB, urlIndexB, toleranceMeters),
                SetOperation.Union => ExecuteUnion(poisA, poisB, urlIndexA, toleranceMeters),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
        }

        private OperationResult ExecuteSubtract(List<Poi> poisA, List<Poi> poisB, Dictionary<string, Poi> urlIndexB, double toleranceMeters)
        {
            var result = poisA.Where(a => _matcher.FindMatch(a, urlIndexB, poisB, toleranceMeters) == null).ToList();
            return new OperationResult
            {
                Pois = result,
                Description = $"A - B: {result.Count} POIs from {poisA.Count} not found in {poisB.Count}"
            };
        }

        private OperationResult ExecuteIntersect(List<Poi> poisA, List<Poi> poisB, Dictionary<string, Poi> urlIndexB, double toleranceMeters)
        {
            var result = poisA.Where(a => _matcher.FindMatch(a, urlIndexB, poisB, toleranceMeters) != null).ToList();
            return new OperationResult
            {
                Pois = result,
                Description = $"A intersect B: {result.Count} common POIs between {poisA.Count} and {poisB.Count}"
            };
        }

        private OperationResult ExecuteUnion(List<Poi> poisA, List<Poi> poisB, Dictionary<string, Poi> urlIndexA, double toleranceMeters)
        {
            var result = new List<Poi>(poisA);
            foreach (var b in poisB)
            {
                if (_matcher.FindMatch(b, urlIndexA, poisA, toleranceMeters) == null)
                {
                    result.Add(b);
                }
            }
            return new OperationResult
            {
                Pois = result,
                Description = $"A union B: {result.Count} unique POIs from {poisA.Count} + {poisB.Count}"
            };
        }

        private OperationResult ExecuteDedup(List<Poi> pois, double toleranceMeters)
        {
            var groups = _matcher.FindDuplicateGroups(pois, toleranceMeters);
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
        /// Uses a transaction to ensure atomicity (OPS-M05).
        /// Batch-adds all items with AddRange instead of per-POI AnyAsync (OPS-C01).
        /// </summary>
        public async Task<PoiCollection> CommitResultAsync(
            List<Poi> pois,
            string name,
            string color = "#7c3aed",
            CancellationToken cancellationToken = default)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var collection = new PoiCollection
            {
                Name = name,
                Color = color,
                SourceType = "operation_result",
                CreatedDate = DateTime.UtcNow,
                PoiCount = pois.Count
            };
            db.PoiCollections.Add(collection);
            await db.SaveChangesAsync(cancellationToken);

            // Batch-add all items (OPS-C01: no per-POI AnyAsync, collection is new so no duplicates possible)
            db.PoiCollectionItems.AddRange(pois.Select(p => new PoiCollectionItem
            {
                PoiId = p.Id,
                PoiCollectionId = collection.Id
            }));
            await db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return collection;
        }

        /// <summary>
        /// Loads POIs for a collection using AsNoTracking for read-only performance (OPS-M04).
        /// </summary>
        private static async Task<List<Poi>> GetCollectionPois(AppDbContext db, int collectionId, CancellationToken cancellationToken = default)
        {
            return await db.PoiCollectionItems
                .AsNoTracking()
                .Where(ci => ci.PoiCollectionId == collectionId)
                .Select(ci => ci.Poi)
                .ToListAsync(cancellationToken);
        }
    }
}
