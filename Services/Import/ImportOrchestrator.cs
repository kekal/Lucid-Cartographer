using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Services.Import
{
    public class ImportOrchestrator : IImportOrchestrator
    {
        private const string DefaultColor = "#005bbf";
        private const string ImportedStatus = "imported";
        private const double ProximityThresholdMeters = 100;

        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly IEnumerable<IFileImporter> _importers;

        public ImportOrchestrator(IDbContextFactory<AppDbContext> factory, IEnumerable<IFileImporter> importers)
        {
            _factory = factory;
            _importers = importers;
        }

        public IFileImporter? GetImporter(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return _importers.FirstOrDefault(i => i.SupportedExtensions.Contains(ext));
        }

        public async Task<ImportResult> ImportAsync(Stream fileStream, string fileName, string collectionName, string color = DefaultColor, CancellationToken cancellationToken = default)
        {
            var importer = GetImporter(fileName)
                ?? throw new ArgumentException($"No importer found for file: {fileName}");

            var parsed = await importer.ParseAsync(fileStream, fileName, cancellationToken);

            return await PersistImportedPoisAsync(
                parsed,
                collectionName,
                color,
                $"{importer.FormatName.ToLower()}_import",
                fileName,
                cancellationToken);
        }

        public async Task<ImportResult> ImportFromScrapedAsync(List<ImportedPoi> parsed, string collectionName, string color = DefaultColor, CancellationToken cancellationToken = default)
        {
            return await PersistImportedPoisAsync(
                parsed,
                collectionName,
                color,
                "google_maps_scrape",
                sourceFileName: null,
                cancellationToken);
        }

        /// <summary>
        /// Shared persistence logic: creates a collection, deduplicates POIs against existing data,
        /// and batch-inserts new POIs. Called by both ImportAsync and ImportFromScrapedAsync.
        /// </summary>
        private async Task<ImportResult> PersistImportedPoisAsync(
            List<ImportedPoi> parsed,
            string collectionName,
            string color,
            string sourceType,
            string? sourceFileName,
            CancellationToken cancellationToken)
        {
            // Validate coordinate ranges -- skip POIs with out-of-range values
            var validParsed = parsed.Where(p =>
                p.Latitude >= -90 && p.Latitude <= 90 &&
                p.Longitude >= -180 && p.Longitude <= 180).ToList();

            await using var db = await _factory.CreateDbContextAsync(cancellationToken);

            var collection = new PoiCollection
            {
                Name = collectionName,
                Color = color,
                SourceType = sourceType,
                SourceFileName = sourceFileName,
                CreatedDate = DateTime.UtcNow
            };
            db.PoiCollections.Add(collection);
            await db.SaveChangesAsync(cancellationToken);

            // Pre-load existing POIs for dedup to avoid N+1 queries
            var importedUrls = validParsed
                .Where(p => !string.IsNullOrEmpty(p.GoogleMapsUrl))
                .Select(p => NormalizeGoogleMapsUrl(p.GoogleMapsUrl!))
                .Distinct()
                .ToHashSet();

            var existingByUrl = importedUrls.Count > 0
                ? await db.Pois
                    .Where(p => p.GoogleMapsUrl != null && importedUrls.Contains(p.GoogleMapsUrl))
                    .ToDictionaryAsync(p => p.GoogleMapsUrl!, cancellationToken)
                : new Dictionary<string, Poi>();

            var importedNames = validParsed
                .Select(p => p.Name.ToLower().Trim())
                .Distinct()
                .ToList();

            var existingByName = importedNames.Count > 0
                ? await db.Pois
                    .Where(p => importedNames.Contains(p.Name.ToLower()))
                    .ToListAsync(cancellationToken)
                : new List<Poi>();

            var existingLinks = await db.PoiCollectionItems
                .Where(ci => ci.PoiCollectionId == collection.Id)
                .Select(ci => ci.PoiId)
                .ToHashSetAsync(cancellationToken);

            var added = 0;
            var skipped = 0;
            var newPois = new List<(Poi poi, int parsedIndex)>();
            var linksToAdd = new List<PoiCollectionItem>();

            for (int i = 0; i < validParsed.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var imported = validParsed[i];

                Poi? existing = null;

                // Tier 1: Match by Google Maps URL
                if (!string.IsNullOrEmpty(imported.GoogleMapsUrl))
                {
                    var normalizedUrl = NormalizeGoogleMapsUrl(imported.GoogleMapsUrl);
                    existingByUrl.TryGetValue(normalizedUrl, out existing);
                }

                // Tier 2: Match by name + proximity
                if (existing == null)
                {
                    var nameLower = imported.Name.ToLower().Trim();
                    existing = existingByName.FirstOrDefault(c =>
                        c.Name.ToLower() == nameLower &&
                        GeoUtils.HaversineDistance(c.Latitude, c.Longitude, imported.Latitude, imported.Longitude) < ProximityThresholdMeters);
                }

                if (existing != null)
                {
                    if (!existingLinks.Contains(existing.Id))
                    {
                        linksToAdd.Add(new PoiCollectionItem
                        {
                            PoiId = existing.Id,
                            PoiCollectionId = collection.Id
                        });
                        existingLinks.Add(existing.Id);
                    }
                    skipped++;
                }
                else
                {
                    var poi = new Poi
                    {
                        Name = imported.Name,
                        Latitude = imported.Latitude,
                        Longitude = imported.Longitude,
                        GoogleMapsUrl = !string.IsNullOrEmpty(imported.GoogleMapsUrl)
                            ? NormalizeGoogleMapsUrl(imported.GoogleMapsUrl)
                            : null,
                        Address = imported.Address,
                        Category = imported.Category,
                        Notes = imported.Description,
                        GoogleRating = imported.Rating,
                        ReviewCount = imported.ReviewCount,
                        Website = imported.Website,
                        Phone = imported.Phone,
                        ImageUrl = imported.ImageUrl,
                        Status = ImportedStatus,
                        AddedDate = DateTime.UtcNow
                    };
                    db.Pois.Add(poi);
                    newPois.Add((poi, i));

                    // Add to in-memory lookup so subsequent items in this batch can dedup against it
                    if (poi.GoogleMapsUrl != null && !existingByUrl.ContainsKey(poi.GoogleMapsUrl))
                        existingByUrl[poi.GoogleMapsUrl] = poi;
                    existingByName.Add(poi);

                    added++;
                }
            }

            // Batch save all new POIs at once to get IDs
            if (newPois.Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            // Now create links for new POIs (IDs are populated after SaveChanges)
            foreach (var (poi, _) in newPois)
            {
                linksToAdd.Add(new PoiCollectionItem
                {
                    PoiId = poi.Id,
                    PoiCollectionId = collection.Id
                });
            }

            if (linksToAdd.Count > 0)
            {
                db.PoiCollectionItems.AddRange(linksToAdd);
            }

            collection.PoiCount = added + skipped;
            await db.SaveChangesAsync(cancellationToken);

            return new ImportResult
            {
                AddedCount = added,
                SkippedCount = skipped,
                TotalParsed = parsed.Count,
                CollectionId = collection.Id,
                CollectionName = collectionName
            };
        }

        private static string NormalizeGoogleMapsUrl(string url)
        {
            url = url.Trim();
            if (url.StartsWith("http://"))
                url = "https://" + url[7..];

            url = url.TrimEnd('/');

            return url;
        }
    }

    internal static class AsyncEnumerableExtensions
    {
        public static async Task<HashSet<T>> ToHashSetAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default)
        {
            var set = new HashSet<T>();
            await foreach (var item in source.AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                set.Add(item);
            }
            return set;
        }
    }
}
