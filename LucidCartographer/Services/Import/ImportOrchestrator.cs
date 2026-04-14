using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Extensions;
using LucidCartographer.Services.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LucidCartographer.Services.Import
{
    public class ImportOrchestrator : IImportOrchestrator
    {
        /// <summary>
        /// IE-25: Default color constant shared with the interface default parameter.
        /// When changing this value, update the interface default parameter as well.
        /// </summary>
        internal const string DefaultColor = "#005bbf";
        private const string ImportedStatus = "imported";
        private const double ProximityThresholdMeters = 100;

        private readonly IDbContextFactory<AppDbContext> _factory;
        private readonly IEnumerable<IFileImporter> _importers;
        private readonly ILogger<ImportOrchestrator> _logger;

        public ImportOrchestrator(IDbContextFactory<AppDbContext> factory, IEnumerable<IFileImporter> importers, ILogger<ImportOrchestrator> logger)
        {
            _factory = factory;
            _importers = importers;
            _logger = logger;
        }

        /// <summary>
        /// IE-08: Replaced GetImporter on interface with CanImport. This method is now internal-only.
        /// </summary>
        private IFileImporter? GetImporter(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return _importers.FirstOrDefault(i => i.SupportedExtensions.Contains(ext));
        }

        public bool CanImport(string fileName)
        {
            return GetImporter(fileName) != null;
        }

        public async Task<ImportResult> ImportAsync(Stream fileStream, string fileName, string collectionName, string color = DefaultColor, CancellationToken cancellationToken = default)
        {
            var importer = GetImporter(fileName)
                ?? throw new ArgumentException($"No importer found for file: {fileName}");

            var parsed = await importer.ParseAsync(fileStream, fileName, cancellationToken);
            _logger.LogInformation("Import {FileName}: parsed {Count} POIs using {Format} importer",
                fileName, parsed.Count, importer.FormatName);

            return await PersistImportedPoisAsync(
                parsed,
                collectionName,
                color,
                $"{importer.FormatName.ToLowerInvariant()}_import",
                fileName,
                cancellationToken);
        }

        public async Task<ImportResult> ImportFromScrapedAsync(IReadOnlyList<ImportedPoi> parsed, string collectionName, string color = DefaultColor, CancellationToken cancellationToken = default)
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
        /// IE-12: Returns error if no valid POIs after parsing (does not create empty collection).
        /// IE-13: Uses PoiMatcher.NormalizeUrl for URL normalization (replaces naive method).
        /// IE-18: Uses ToLowerInvariant instead of ToLower.
        /// </summary>
        private async Task<ImportResult> PersistImportedPoisAsync(
            IReadOnlyList<ImportedPoi> parsed,
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

            // IE-12: Don't create an empty collection if parsing yields 0 valid POIs
            if (validParsed.Count == 0)
            {
                _logger.LogWarning("Import for '{CollectionName}': 0 valid POIs after parsing {Total} items. No collection created.",
                    collectionName, parsed.Count);
                return new ImportResult
                {
                    AddedCount = 0,
                    SkippedCount = 0,
                    TotalParsed = parsed.Count,
                    CollectionId = 0,
                    CollectionName = collectionName
                };
            }

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
            // IE-13: Use PoiMatcher.NormalizeUrl for proper URL normalization
            var importedUrls = validParsed
                .Where(p => !string.IsNullOrEmpty(p.GoogleMapsUrl))
                .Select(p => PoiMatcher.NormalizeUrl(p.GoogleMapsUrl!))
                .Distinct()
                .ToHashSet();

            var existingByUrl = importedUrls.Count > 0
                ? await db.Pois
                    .Where(p => p.GoogleMapsUrl != null && importedUrls.Contains(p.GoogleMapsUrl))
                    .ToDictionaryAsync(p => p.GoogleMapsUrl!, cancellationToken)
                : new Dictionary<string, Poi>();

            // IE-18: Use ToLowerInvariant to avoid locale-dependent case conversion
            var importedNames = validParsed
                .Select(p => p.Name.ToLowerInvariant().Trim())
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

                // Tier 1: Match by Google Maps URL (using proper normalization)
                if (!string.IsNullOrEmpty(imported.GoogleMapsUrl))
                {
                    var normalizedUrl = PoiMatcher.NormalizeUrl(imported.GoogleMapsUrl);
                    existingByUrl.TryGetValue(normalizedUrl, out existing);
                }

                // Tier 2: Match by name + proximity
                if (existing == null)
                {
                    var nameLower = imported.Name.ToLowerInvariant().Trim();
                    existing = existingByName.FirstOrDefault(c =>
                        c.Name.ToLowerInvariant() == nameLower &&
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

                    // Narrow backfill on dedup: refresh the image bytes whenever
                    // the new scrape produced any, and the existing record is
                    // either empty or came from a Google source URL (safe to
                    // overwrite). Non-Google source URLs are treated as user-set
                    // and left alone. Other metadata (address/rating/phone) is
                    // also left untouched to preserve user edits.
                    var existingIsGoogleSourced = string.IsNullOrEmpty(existing.ImageUrl)
                        || existing.ImageUrl.Contains("googleusercontent.com");
                    if (imported.ImageData is { Length: > 0 } && existingIsGoogleSourced)
                    {
                        // Load or create the companion PoiImage row. Using Find
                        // pulls from the tracker first, so concurrent backfills
                        // in the same batch don't duplicate-insert.
                        var existingImage = await db.PoiImages.FindAsync(new object[] { existing.Id }, cancellationToken);
                        if (existingImage is null)
                        {
                            db.PoiImages.Add(new PoiImage
                            {
                                PoiId = existing.Id,
                                Data = imported.ImageData,
                                ContentType = imported.ImageContentType
                            });
                        }
                        else
                        {
                            existingImage.Data = imported.ImageData;
                            existingImage.ContentType = imported.ImageContentType;
                        }
                        existing.ImageUrl = imported.ImageUrl;
                    }
                    else if (!string.IsNullOrEmpty(imported.ImageUrl) && existingIsGoogleSourced)
                    {
                        // URL-only update as a weaker fallback (e.g. if the bytes
                        // download failed but we still captured the source URL).
                        existing.ImageUrl = imported.ImageUrl;
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
                            ? PoiMatcher.NormalizeUrl(imported.GoogleMapsUrl)
                            : null,
                        Address = imported.Address,
                        Category = imported.Category,
                        Notes = imported.Description,
                        GoogleRating = imported.Rating,
                        ReviewCount = imported.ReviewCount,
                        Website = imported.Website,
                        Phone = imported.Phone,
                        ImageUrl = imported.ImageUrl,
                        Image = imported.ImageData is { Length: > 0 }
                            ? new PoiImage { Data = imported.ImageData, ContentType = imported.ImageContentType }
                            : null,
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

            await db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Import complete for collection '{CollectionName}' (ID={CollectionId}): {Added} added, {Skipped} duplicates linked, {Total} total parsed",
                collectionName, collection.Id, added, skipped, parsed.Count);

            return new ImportResult
            {
                AddedCount = added,
                SkippedCount = skipped,
                TotalParsed = parsed.Count,
                CollectionId = collection.Id,
                CollectionName = collectionName
            };
        }
    }
}
