using System.Text.RegularExpressions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LucidCartographer.Services
{
    public class PoiService : IPoiService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly ILogger<PoiService> _logger;

        private static readonly Regex HexColorRegex = new(@"^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

        public PoiService(IDbContextFactory<AppDbContext> factory, ILogger<PoiService> logger)
        {
            _factory = factory;
            _logger = logger;
        }

        public async Task<List<PoiCollection>> GetCollectionsAsync(CancellationToken cancellationToken = default)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var collections = await db.PoiCollections
                .OrderByDescending(c => c.CreatedDate)
                .ToListAsync(cancellationToken);

            // Compute PoiCount from DB instead of relying on denormalized field
            var counts = await db.PoiCollectionItems
                .GroupBy(ci => ci.PoiCollectionId)
                .Select(g => new { CollectionId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.CollectionId, x => x.Count, cancellationToken);

            foreach (var col in collections)
            {
                counts.TryGetValue(col.Id, out var count);
                col.PoiCount = count;
            }

            return collections;
        }

        public async Task<List<Poi>> GetPoisByCollectionAsync(int collectionId, CancellationToken cancellationToken = default)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            return await db.PoiCollectionItems
                .Where(ci => ci.PoiCollectionId == collectionId)
                .Select(ci => ci.Poi)
                .ToListAsync(cancellationToken);
        }

        /// <summary>
        /// Returns POIs grouped by visible collection ID using a single joined query (fixes N+1).
        /// </summary>
        public async Task<Dictionary<int, List<Poi>>> GetVisiblePoisGroupedAsync(CancellationToken cancellationToken = default)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var items = await db.PoiCollectionItems
                .Where(ci => ci.PoiCollection.IsVisible)
                .Include(ci => ci.Poi)
                .ToListAsync(cancellationToken);

            return items
                .GroupBy(ci => ci.PoiCollectionId)
                .ToDictionary(g => g.Key, g => g.Select(ci => ci.Poi).ToList());
        }

        public async Task ToggleVisibilityAsync(int collectionId, CancellationToken cancellationToken = default)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var collection = await db.PoiCollections.FindAsync(new object[] { collectionId }, cancellationToken);
            if (collection == null)
            {
                _logger.LogWarning("ToggleVisibilityAsync: Collection {CollectionId} not found", collectionId);
                throw new InvalidOperationException($"Collection {collectionId} not found");
            }

            collection.IsVisible = !collection.IsVisible;
            await db.SaveChangesAsync(cancellationToken);
        }

        public async Task<Poi?> GetPoiAsync(int poiId, CancellationToken cancellationToken = default)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            return await db.Pois.FindAsync(new object[] { poiId }, cancellationToken);
        }

        /// <summary>
        /// Updates a POI by attaching and marking only changed properties as modified,
        /// instead of overwriting all columns.
        /// </summary>
        public async Task UpdatePoiAsync(Poi poi, CancellationToken cancellationToken = default)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var existing = await db.Pois.FindAsync(new object[] { poi.Id }, cancellationToken);
            if (existing == null)
            {
                _logger.LogWarning("UpdatePoiAsync: POI {PoiId} not found", poi.Id);
                throw new InvalidOperationException($"POI {poi.Id} not found");
            }

            // Map changed properties from the incoming entity
            existing.Name = poi.Name;
            existing.Latitude = poi.Latitude;
            existing.Longitude = poi.Longitude;
            existing.GoogleMapsUrl = poi.GoogleMapsUrl;
            existing.Address = poi.Address;
            existing.Category = poi.Category;
            existing.Status = poi.Status;
            existing.Tags = poi.Tags;
            existing.Notes = poi.Notes;
            existing.Rating = poi.Rating;
            existing.GoogleRating = poi.GoogleRating;
            existing.ReviewCount = poi.ReviewCount;
            existing.Website = poi.Website;
            existing.Phone = poi.Phone;
            existing.ImageUrl = poi.ImageUrl;
            existing.Country = poi.Country;
            existing.Region = poi.Region;
            existing.VisitedDate = poi.VisitedDate;

            await db.SaveChangesAsync(cancellationToken);
        }

        /// <summary>
        /// Deletes a collection and cleans up orphaned POIs in a single transaction.
        /// </summary>
        public async Task DeleteCollectionAsync(int collectionId, CancellationToken cancellationToken = default)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                var collection = await db.PoiCollections
                    .Include(c => c.CollectionItems)
                    .FirstOrDefaultAsync(c => c.Id == collectionId, cancellationToken);

                if (collection == null)
                {
                    _logger.LogWarning("DeleteCollectionAsync: Collection {CollectionId} not found", collectionId);
                    return;
                }

                // Collect POI IDs that belong to this collection before removal
                var poiIdsInCollection = collection.CollectionItems.Select(ci => ci.PoiId).ToList();

                db.PoiCollections.Remove(collection);
                await db.SaveChangesAsync(cancellationToken);

                // Clean up orphaned POIs (not in any remaining collection)
                if (poiIdsInCollection.Any())
                {
                    var orphanedPois = await db.Pois
                        .Where(p => poiIdsInCollection.Contains(p.Id) && !p.CollectionItems.Any())
                        .ToListAsync(cancellationToken);

                    if (orphanedPois.Any())
                    {
                        _logger.LogInformation("Removing {Count} orphaned POIs after deleting collection {CollectionId}",
                            orphanedPois.Count, collectionId);
                        db.Pois.RemoveRange(orphanedPois);
                        await db.SaveChangesAsync(cancellationToken);
                    }
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        public async Task<List<Poi>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<Poi>();

            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            // Escape LIKE metacharacters to prevent LIKE injection
            var escaped = query.ToLowerInvariant()
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_")
                .Replace("[", "\\[");
            return await db.Pois
                .Where(p => EF.Functions.Like(p.Name, $"%{escaped}%", "\\")
                    || (p.Address != null && EF.Functions.Like(p.Address, $"%{escaped}%", "\\"))
                    || (p.Tags != null && EF.Functions.Like(p.Tags, $"%{escaped}%", "\\"))
                    || (p.Notes != null && EF.Functions.Like(p.Notes, $"%{escaped}%", "\\")))
                .Take(100)
                .ToListAsync(cancellationToken);
        }

        public async Task UpdateCollectionColorAsync(int collectionId, string color, CancellationToken cancellationToken = default)
        {
            if (!HexColorRegex.IsMatch(color))
                throw new ArgumentException($"Invalid hex color format: '{color}'. Expected format: #RRGGBB.", nameof(color));

            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var collection = await db.PoiCollections.FindAsync(new object[] { collectionId }, cancellationToken);
            if (collection == null)
            {
                _logger.LogWarning("UpdateCollectionColorAsync: Collection {CollectionId} not found", collectionId);
                throw new InvalidOperationException($"Collection {collectionId} not found");
            }

            collection.Color = color;
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
