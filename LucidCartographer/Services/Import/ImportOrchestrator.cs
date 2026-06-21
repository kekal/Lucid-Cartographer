using LucidCartographer.Data;
using LucidCartographer.Services.Enrichment;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Services.Import;

public class ImportOrchestrator(
    IDbContextFactory<AppDbContext> factory,
    IEnumerable<IFileImporter> importers,
    EnrichmentTrigger enrichmentTrigger,
    ILogger<ImportOrchestrator> logger)
    : IImportOrchestrator
{
    /// <summary>
    /// Default color constant; keep in sync with interface default parameter.
    /// </summary>
    internal const string DefaultColor = "#005bbf";

    private IFileImporter? GetImporter(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return importers.FirstOrDefault(i => i.SupportedExtensions.Contains(ext));
    }

    public bool CanImport(string fileName) => GetImporter(fileName) != null;

    public async Task<ImportResult> ImportAsync(Stream fileStream, string fileName, string collectionName, string color = DefaultColor, CancellationToken cancellationToken = default)
    {
        var importer = GetImporter(fileName)
                       ?? throw new ArgumentException($"No importer found for file: {fileName}");

        var parsed = await importer.ParseAsync(fileStream, fileName, cancellationToken);
        logger.LogInformation("Import {FileName}: parsed {Count} POIs using {Format} importer",
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
    /// Shared persistence entry point for both file imports and Google scrapes.
    /// Drops out-of-range coordinates; does not create an empty collection if no valid POIs remain.
    /// URL normalization and invariant-culture comparisons are in <see cref="ImportPersister"/>.
    /// </summary>
    private async Task<ImportResult> PersistImportedPoisAsync(
        IReadOnlyList<ImportedPoi> parsed,
        string collectionName,
        string color,
        string sourceType,
        string? sourceFileName,
        CancellationToken cancellationToken)
    {
        var validParsed = FilterValidCoordinates(parsed);
        if (validParsed.Count == 0)
        {
            return EmptyImportResult(parsed.Count, collectionName);
        }

        // KML folders are preserved as separate collections; rows without a folder fall back to the user-provided name.
        var groups = validParsed
            .GroupBy(p => string.IsNullOrWhiteSpace(p.FolderName) ? collectionName : p.FolderName!)
            .ToList();

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var anyAdded = false;
        ImportResult? lastResult = null;
        var totalAdded = 0;
        var totalSkipped = 0;

        foreach (var group in groups)
        {
            var groupRows = group.ToList();
            var persister = new ImportPersister(
                db, logger, groupRows, groupRows.Count,
                group.Key, color, sourceType, sourceFileName, cancellationToken);
            var groupResult = await persister.RunAsync();
            anyAdded |= persister.AddedAny;
            totalAdded += groupResult.AddedCount;
            totalSkipped += groupResult.SkippedCount;
            lastResult = groupResult;
        }

        // Newly-created POIs are queued for enrichment atomically with commit; signal worker once to avoid poll latency.
        if (anyAdded)
        {
            enrichmentTrigger.Signal();
        }

        // Single-group case returns the collection ID for UI navigation; multi-folder case returns an aggregate.
        if (groups.Count == 1 && lastResult is not null)
        {
            return lastResult;
        }

        return new ImportResult
        {
            AddedCount = totalAdded,
            SkippedCount = totalSkipped,
            TotalParsed = parsed.Count,
            CollectionId = lastResult?.CollectionId ?? 0,
            CollectionName = groups.Count > 1
                ? $"{collectionName} ({groups.Count} folders)"
                : collectionName
        };
    }

    private static List<ImportedPoi> FilterValidCoordinates(IReadOnlyList<ImportedPoi> parsed)
    {
        // NULL coords allowed (enrichment fills them); half-null pairs and out-of-range rejected.
        return parsed
            .Where(p => (p.Latitude == null && p.Longitude == null)
                        || p is { Latitude: >= -90 and <= 90, Longitude: >= -180 and <= 180 })
            .ToList();
    }

    private ImportResult EmptyImportResult(int totalParsed, string collectionName)
    {
        logger.LogWarning(
            "Import for '{CollectionName}': 0 valid POIs after parsing {Total} items. No collection created.",
            collectionName, totalParsed);
        return new ImportResult
        {
            AddedCount = 0,
            SkippedCount = 0,
            TotalParsed = totalParsed,
            CollectionId = 0,
            CollectionName = collectionName
        };
    }
}
