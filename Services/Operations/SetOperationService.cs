using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Services.Operations
{
    public enum SetOperation
    {
        Subtract,   // A - B
        Intersect,  // A ∩ B
        Union,      // A ∪ B
        Dedup       // Remove duplicates within A
    }

    public class OperationResult
    {
        public List<Poi> Pois { get; set; } = new();
        public List<List<Poi>>? DuplicateGroups { get; set; } // Only for Dedup
        public string Description { get; set; } = string.Empty;
    }

    public class SetOperationService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly PoiMatcher _matcher;

        public SetOperationService(IDbContextFactory<AppDbContext> factory, PoiMatcher matcher)
        {
            _factory = factory;
            _matcher = matcher;
        }

        public async Task<OperationResult> ExecuteAsync(SetOperation operation, int collectionAId, int? collectionBId, double toleranceMeters = 100)
        {
            await using var db = await _factory.CreateDbContextAsync();

            var poisA = await GetCollectionPois(db, collectionAId);

            return operation switch
            {
                SetOperation.Dedup => ExecuteDedup(poisA, toleranceMeters),
                _ when collectionBId == null => throw new ArgumentException("Collection B is required for this operation"),
                _ => await ExecuteBinaryOp(db, operation, poisA, collectionBId.Value, toleranceMeters)
            };
        }

        private async Task<OperationResult> ExecuteBinaryOp(AppDbContext db, SetOperation operation, List<Poi> poisA, int collectionBId, double toleranceMeters)
        {
            var poisB = await GetCollectionPois(db, collectionBId);

            return operation switch
            {
                SetOperation.Subtract => new OperationResult
                {
                    Pois = poisA.Where(a => _matcher.FindMatch(a, poisB, toleranceMeters) == null).ToList(),
                    Description = $"A − B: {poisA.Count} − {poisB.Count} matches"
                },
                SetOperation.Intersect => new OperationResult
                {
                    Pois = poisA.Where(a => _matcher.FindMatch(a, poisB, toleranceMeters) != null).ToList(),
                    Description = $"A ∩ B: common POIs between {poisA.Count} and {poisB.Count}"
                },
                SetOperation.Union => ExecuteUnion(poisA, poisB, toleranceMeters),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
        }

        private OperationResult ExecuteUnion(List<Poi> poisA, List<Poi> poisB, double toleranceMeters)
        {
            var result = new List<Poi>(poisA);
            foreach (var b in poisB)
            {
                if (_matcher.FindMatch(b, poisA, toleranceMeters) == null)
                {
                    result.Add(b);
                }
            }
            return new OperationResult
            {
                Pois = result,
                Description = $"A ∪ B: merged {poisA.Count} + {poisB.Count} (unique)"
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
        /// Save operation result as a new collection
        /// </summary>
        public async Task<PoiCollection> CommitResultAsync(List<Poi> pois, string name, string color = "#7c3aed")
        {
            await using var db = await _factory.CreateDbContextAsync();

            var collection = new PoiCollection
            {
                Name = name,
                Color = color,
                SourceType = "operation_result",
                CreatedDate = DateTime.UtcNow,
                PoiCount = pois.Count
            };
            db.PoiCollections.Add(collection);
            await db.SaveChangesAsync();

            foreach (var poi in pois)
            {
                var exists = await db.PoiCollectionItems
                    .AnyAsync(ci => ci.PoiId == poi.Id && ci.PoiCollectionId == collection.Id);
                if (!exists)
                {
                    db.PoiCollectionItems.Add(new PoiCollectionItem
                    {
                        PoiId = poi.Id,
                        PoiCollectionId = collection.Id
                    });
                }
            }
            await db.SaveChangesAsync();

            return collection;
        }

        private static async Task<List<Poi>> GetCollectionPois(AppDbContext db, int collectionId)
        {
            return await db.PoiCollectionItems
                .Where(ci => ci.PoiCollectionId == collectionId)
                .Select(ci => ci.Poi)
                .ToListAsync();
        }
    }
}
