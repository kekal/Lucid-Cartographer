using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Services.Import;

public class ImportResult
{
    public int AddedCount { get; set; }
    public int SkippedCount { get; set; }
    public int TotalParsed { get; set; }
    public int CollectionId { get; set; }
    public string CollectionName { get; set; } = string.Empty;
}

public class ImportOrchestrator
{
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

    public async Task<ImportResult> ImportAsync(Stream fileStream, string fileName, string collectionName, string color = "#005bbf")
    {
        var importer = GetImporter(fileName)
            ?? throw new ArgumentException($"No importer found for file: {fileName}");

        var parsed = await importer.ParseAsync(fileStream, fileName);

        await using var db = await _factory.CreateDbContextAsync();

        var collection = new PoiCollection
        {
            Name = collectionName,
            Color = color,
            SourceType = $"{importer.FormatName.ToLower()}_import",
            SourceFileName = fileName,
            CreatedDate = DateTime.UtcNow
        };
        db.PoiCollections.Add(collection);
        await db.SaveChangesAsync();

        var added = 0;
        var skipped = 0;

        foreach (var imported in parsed)
        {
            // Try to find existing POI
            Poi? existing = null;

            // Tier 1: Match by Google Maps URL
            if (!string.IsNullOrEmpty(imported.GoogleMapsUrl))
            {
                var normalizedUrl = NormalizeGoogleMapsUrl(imported.GoogleMapsUrl);
                existing = await db.Pois
                    .FirstOrDefaultAsync(p => p.GoogleMapsUrl != null && p.GoogleMapsUrl == normalizedUrl);
            }

            // Tier 2: Match by name + proximity (100m)
            if (existing == null)
            {
                var candidates = await db.Pois
                    .Where(p => p.Name.ToLower() == imported.Name.ToLower().Trim())
                    .ToListAsync();

                existing = candidates.FirstOrDefault(c =>
                    GeoUtils.HaversineDistance(c.Latitude, c.Longitude, imported.Latitude, imported.Longitude) < 100);
            }

            if (existing != null)
            {
                // POI exists -- just link to collection if not already linked
                var alreadyLinked = await db.PoiCollectionItems
                    .AnyAsync(ci => ci.PoiId == existing.Id && ci.PoiCollectionId == collection.Id);
                if (!alreadyLinked)
                {
                    db.PoiCollectionItems.Add(new PoiCollectionItem
                    {
                        PoiId = existing.Id,
                        PoiCollectionId = collection.Id
                    });
                }
                skipped++;
            }
            else
            {
                // Create new POI
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
                    Status = "imported",
                    AddedDate = DateTime.UtcNow
                };
                db.Pois.Add(poi);
                await db.SaveChangesAsync(); // Save to get the ID

                db.PoiCollectionItems.Add(new PoiCollectionItem
                {
                    PoiId = poi.Id,
                    PoiCollectionId = collection.Id
                });
                added++;
            }
        }

        collection.PoiCount = added + skipped; // Total POIs in this collection
        await db.SaveChangesAsync();

        return new ImportResult
        {
            AddedCount = added,
            SkippedCount = skipped,
            TotalParsed = parsed.Count,
            CollectionId = collection.Id,
            CollectionName = collectionName
        };
    }

    public async Task<ImportResult> ImportFromScrapedAsync(List<ImportedPoi> parsed, string collectionName, string color = "#005bbf")
    {
        await using var db = await _factory.CreateDbContextAsync();

        var collection = new PoiCollection
        {
            Name = collectionName,
            Color = color,
            SourceType = "google_maps_scrape",
            CreatedDate = DateTime.UtcNow
        };
        db.PoiCollections.Add(collection);
        await db.SaveChangesAsync();

        var added = 0;
        var skipped = 0;

        foreach (var imported in parsed)
        {
            Poi? existing = null;

            if (!string.IsNullOrEmpty(imported.GoogleMapsUrl))
            {
                var normalizedUrl = NormalizeGoogleMapsUrl(imported.GoogleMapsUrl);
                existing = await db.Pois
                    .FirstOrDefaultAsync(p => p.GoogleMapsUrl != null && p.GoogleMapsUrl == normalizedUrl);
            }

            if (existing == null)
            {
                var candidates = await db.Pois
                    .Where(p => p.Name.ToLower() == imported.Name.ToLower().Trim())
                    .ToListAsync();
                existing = candidates.FirstOrDefault(c =>
                    GeoUtils.HaversineDistance(c.Latitude, c.Longitude, imported.Latitude, imported.Longitude) < 100);
            }

            if (existing != null)
            {
                var alreadyLinked = await db.PoiCollectionItems
                    .AnyAsync(ci => ci.PoiId == existing.Id && ci.PoiCollectionId == collection.Id);
                if (!alreadyLinked)
                {
                    db.PoiCollectionItems.Add(new PoiCollectionItem
                    {
                        PoiId = existing.Id,
                        PoiCollectionId = collection.Id
                    });
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
                    Status = "imported",
                    AddedDate = DateTime.UtcNow
                };
                db.Pois.Add(poi);
                await db.SaveChangesAsync();

                db.PoiCollectionItems.Add(new PoiCollectionItem
                {
                    PoiId = poi.Id,
                    PoiCollectionId = collection.Id
                });
                added++;
            }
        }

        collection.PoiCount = added + skipped;
        await db.SaveChangesAsync();

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
        // Remove tracking parameters, normalize protocol
        url = url.Trim();
        if (url.StartsWith("http://"))
            url = "https://" + url[7..];

        // Remove trailing slashes
        url = url.TrimEnd('/');

        return url;
    }
}
