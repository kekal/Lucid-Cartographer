using System.Text.RegularExpressions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Services
{
    public class PoiService : IPoiService
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly ILogger<PoiService> _logger;

        // [REVIEW-5] Regex matches only #RRGGBB (7 chars). MaxLength on entity aligned to 7.
        private static readonly Regex HexColorRegex = new(@"^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

        public PoiService(IDbContextFactory<AppDbContext> factory, ILogger<PoiService> logger)
        {
            _factory = factory;
            _logger = logger;
        }

        public async Task<IReadOnlyList<PoiCollection>> GetCollectionsAsync(CancellationToken cancellationToken = default)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var collections = await db.PoiCollections
                .OrderByDescending(c => c.CreatedDate)
                .ToListAsync(cancellationToken);

            // [REVIEW-2] Compute PoiCount from DB instead of relying on denormalized field
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

        public async Task<IReadOnlyList<Poi>> GetPoisByCollectionAsync(int collectionId, CancellationToken cancellationToken = default)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            return await db.PoiCollectionItems
                .Where(ci => ci.PoiCollectionId == collectionId)
                .Select(ci => ci.Poi)
                .ToListAsync(cancellationToken);
        }

        /// <summary>
        /// Returns POIs grouped by visible collection ID using a single joined query.
        /// [REVIEW-10] Uses projection to load only the fields needed for map markers,
        /// reducing memory pressure on large datasets.
        /// </summary>
        public async Task<Dictionary<int, List<Poi>>> GetVisiblePoisGroupedAsync(CancellationToken cancellationToken = default)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            // Hide Pois that are still waiting for background enrichment.
            // Scraped rows are created with IsEnriched=false + placeholder
            // (0,0) coords; the background service flips the flag once it
            // has pulled real coords + address/website/phone. Showing them
            // on the map before enrichment would put every pending pin at
            // null island off the coast of Africa.
            var items = await db.PoiCollectionItems
                .Where(ci => ci.PoiCollection.IsVisible && ci.Poi.IsEnriched)
                .Select(ci => new { ci.PoiCollectionId, ci.Poi })
                .ToListAsync(cancellationToken);

            return items
                .GroupBy(x => x.PoiCollectionId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Poi).ToList());
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
            return await db.Pois
                .Include(p => p.PoiTags)
                    .ThenInclude(pt => pt.Tag)
                .FirstOrDefaultAsync(p => p.Id == poiId, cancellationToken);
        }

        /// <summary>
        /// Updates a POI by loading the existing entity and applying all incoming values.
        /// EF change tracking generates minimal SQL for only the properties that actually changed.
        /// [REVIEW-3] Validates Status and Category against known constants.
        /// [REVIEW-4] Validates coordinates, name, and numeric ranges before persistence.
        /// [REVIEW-6] Version is NOT copied from the incoming entity; the existing entity's
        /// Version is preserved so that <see cref="AppDbContext.SetTimestamps"/> can increment
        /// it correctly for optimistic concurrency. Never copy Version from external sources.
        /// </summary>
        public async Task UpdatePoiAsync(Poi poi, CancellationToken cancellationToken = default)
        {
            // [REVIEW-4] Validate inputs before touching the DB
            ValidatePoi(poi);

            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var existing = await db.Pois.FindAsync(new object[] { poi.Id }, cancellationToken);
            if (existing == null)
            {
                _logger.LogWarning("UpdatePoiAsync: POI {PoiId} not found", poi.Id);
                throw new InvalidOperationException($"POI {poi.Id} not found");
            }

            // Apply all incoming values; Version and AddedDate are intentionally excluded.
            existing.Name = poi.Name;
            existing.Latitude = poi.Latitude;
            existing.Longitude = poi.Longitude;
            existing.GoogleMapsUrl = poi.GoogleMapsUrl;
            existing.Address = poi.Address;
            existing.Category = poi.Category;
            existing.Status = poi.Status;
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
        /// Creates a new POI and adds it to the specified collection.
        /// [REVIEW-9] Provides a single place for creation validation and collection assignment.
        /// </summary>
        public async Task<Poi> CreatePoiAsync(Poi poi, int collectionId, CancellationToken cancellationToken = default)
        {
            ValidatePoi(poi);

            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var collection = await db.PoiCollections.FindAsync(new object[] { collectionId }, cancellationToken);
            if (collection == null)
                throw new InvalidOperationException($"Collection {collectionId} not found");

            db.Pois.Add(poi);
            await db.SaveChangesAsync(cancellationToken);

            db.PoiCollectionItems.Add(new PoiCollectionItem
            {
                PoiId = poi.Id,
                PoiCollectionId = collectionId
            });
            await db.SaveChangesAsync(cancellationToken);

            return poi;
        }

        /// <summary>
        /// Adds an existing POI to a collection.
        /// [REVIEW-9] Validates both entities exist before creating the association.
        /// </summary>
        public async Task AddPoiToCollectionAsync(int poiId, int collectionId, CancellationToken cancellationToken = default)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);

            var poi = await db.Pois.FindAsync(new object[] { poiId }, cancellationToken);
            if (poi == null)
                throw new InvalidOperationException($"POI {poiId} not found");

            var collection = await db.PoiCollections.FindAsync(new object[] { collectionId }, cancellationToken);
            if (collection == null)
                throw new InvalidOperationException($"Collection {collectionId} not found");

            var exists = await db.PoiCollectionItems
                .AnyAsync(ci => ci.PoiId == poiId && ci.PoiCollectionId == collectionId, cancellationToken);
            if (exists)
                return; // Already in collection, no-op

            db.PoiCollectionItems.Add(new PoiCollectionItem
            {
                PoiId = poiId,
                PoiCollectionId = collectionId
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        /// <summary>
        /// Removes a POI from a specific collection. If the POI becomes orphaned
        /// (not in any collection), it is deleted.
        /// [REVIEW-20] Allows curating individual POIs within a collection.
        /// </summary>
        public async Task RemovePoiFromCollectionAsync(int poiId, int collectionId, CancellationToken cancellationToken = default)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);

            var item = await db.PoiCollectionItems
                .FirstOrDefaultAsync(ci => ci.PoiId == poiId && ci.PoiCollectionId == collectionId, cancellationToken);
            if (item == null)
                throw new InvalidOperationException($"POI {poiId} is not in collection {collectionId}");

            db.PoiCollectionItems.Remove(item);
            await db.SaveChangesAsync(cancellationToken);

            // Clean up orphaned POI
            var isOrphaned = !await db.PoiCollectionItems.AnyAsync(ci => ci.PoiId == poiId, cancellationToken);
            if (isOrphaned)
            {
                var poi = await db.Pois.FindAsync(new object[] { poiId }, cancellationToken);
                if (poi != null)
                {
                    db.Pois.Remove(poi);
                    await db.SaveChangesAsync(cancellationToken);
                }
            }
        }

        /// <summary>
        /// Deletes a collection and cleans up orphaned POIs in a single transaction.
        /// [REVIEW-13] Throws InvalidOperationException when collection is not found,
        /// consistent with other mutation methods.
        /// [REVIEW-14] Explicit transaction is kept because there are two SaveChangesAsync
        /// calls, but the try/catch rollback is removed since disposal handles rollback.
        /// </summary>
        public async Task DeleteCollectionAsync(int collectionId, CancellationToken cancellationToken = default)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var collection = await db.PoiCollections
                .Include(c => c.CollectionItems)
                .FirstOrDefaultAsync(c => c.Id == collectionId, cancellationToken);

            if (collection == null)
            {
                _logger.LogWarning("DeleteCollectionAsync: Collection {CollectionId} not found", collectionId);
                throw new InvalidOperationException($"Collection {collectionId} not found");
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

        public async Task<IReadOnlyList<Poi>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<Poi>();

            await using var db = await _factory.CreateDbContextAsync(cancellationToken);

            // [REVIEW-1] Escape LIKE metacharacters to prevent wildcard abuse.
            // [REVIEW-15] Removed ToLowerInvariant() -- SQLite LIKE is case-insensitive for ASCII.
            var escaped = query.Trim()
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_")
                .Replace("[", "\\[");

            // Search across Name, Address, Notes, and Tags (via join table)
            var byFields = await db.Pois
                .Where(p => EF.Functions.Like(p.Name, $"%{escaped}%", "\\")
                    || (p.Address != null && EF.Functions.Like(p.Address, $"%{escaped}%", "\\"))
                    || (p.Notes != null && EF.Functions.Like(p.Notes, $"%{escaped}%", "\\")))
                .Take(100)
                .ToListAsync(cancellationToken);

            // Search tags via many-to-many join
            var byTags = await db.PoiTags
                .Where(pt => EF.Functions.Like(pt.Tag.Name, $"%{escaped}%", "\\"))
                .Select(pt => pt.Poi)
                .Distinct()
                .Take(100)
                .ToListAsync(cancellationToken);

            // Merge results, deduplicate by Id
            var seen = new HashSet<int>();
            var result = new List<Poi>();
            foreach (var poi in byFields.Concat(byTags))
            {
                if (seen.Add(poi.Id))
                    result.Add(poi);
                if (result.Count >= 100)
                    break;
            }
            return result;
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

        /// <summary>
        /// Validates a POI entity before persistence.
        /// [REVIEW-3] Validates Status against PoiStatus.IsValid and Category against PoiCategory.All.
        /// [REVIEW-4] Validates coordinates, name, and numeric ranges.
        /// </summary>
        private static void ValidatePoi(Poi poi)
        {
            if (string.IsNullOrWhiteSpace(poi.Name))
                throw new ArgumentException("POI name cannot be empty.", nameof(poi));

            if (poi.Name.Length > 500)
                throw new ArgumentException("POI name cannot exceed 500 characters.", nameof(poi));

            if (poi.Latitude < -90.0 || poi.Latitude > 90.0)
                throw new ArgumentOutOfRangeException(nameof(poi), $"Latitude {poi.Latitude} is outside the valid range [-90, 90].");

            if (poi.Longitude < -180.0 || poi.Longitude > 180.0)
                throw new ArgumentOutOfRangeException(nameof(poi), $"Longitude {poi.Longitude} is outside the valid range [-180, 180].");

            if (poi.ReviewCount.HasValue && poi.ReviewCount.Value < 0)
                throw new ArgumentOutOfRangeException(nameof(poi), "ReviewCount cannot be negative.");

            if (poi.Rating.HasValue && (poi.Rating.Value < 1 || poi.Rating.Value > 5))
                throw new ArgumentOutOfRangeException(nameof(poi), "Rating must be between 1 and 5.");

            if (poi.GoogleRating.HasValue && (poi.GoogleRating.Value < 1.0 || poi.GoogleRating.Value > 5.0))
                throw new ArgumentOutOfRangeException(nameof(poi), "GoogleRating must be between 1.0 and 5.0.");

            if (!PoiStatus.IsValid(poi.Status))
                throw new ArgumentException($"Invalid POI status: '{poi.Status}'. Valid values: {string.Join(", ", PoiStatus.All)}.", nameof(poi));

            if (poi.Category != null && !PoiCategory.IsValid(poi.Category))
                throw new ArgumentException($"Invalid POI category: '{poi.Category}'. Valid values: {string.Join(", ", PoiCategory.All)}.", nameof(poi));

            // MaxLength checks for string fields that could exceed DB constraints
            if (poi.Address?.Length > 1000)
                throw new ArgumentException("Address cannot exceed 1000 characters.", nameof(poi));
            if (poi.Notes?.Length > 10000)
                throw new ArgumentException("Notes cannot exceed 10000 characters.", nameof(poi));
            if (poi.GoogleMapsUrl?.Length > 2048)
                throw new ArgumentException("GoogleMapsUrl cannot exceed 2048 characters.", nameof(poi));
            if (poi.Website?.Length > 2048)
                throw new ArgumentException("Website cannot exceed 2048 characters.", nameof(poi));
            if (poi.Phone?.Length > 50)
                throw new ArgumentException("Phone cannot exceed 50 characters.", nameof(poi));
            if (poi.ImageUrl?.Length > 2048)
                throw new ArgumentException("ImageUrl cannot exceed 2048 characters.", nameof(poi));
            if (poi.Country?.Length > 200)
                throw new ArgumentException("Country cannot exceed 200 characters.", nameof(poi));
            if (poi.Region?.Length > 200)
                throw new ArgumentException("Region cannot exceed 200 characters.", nameof(poi));
            if (poi.Category?.Length > 100)
                throw new ArgumentException("Category cannot exceed 100 characters.", nameof(poi));
            if (poi.Status?.Length > 50)
                throw new ArgumentException("Status cannot exceed 50 characters.", nameof(poi));
        }
    }
}
