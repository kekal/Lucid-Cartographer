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
    /// IE-25: Default color constant shared with the interface default parameter.
    /// When changing this value, update the interface default parameter as well.
    /// </summary>
    internal const string DefaultColor = "#005bbf";

    /// <summary>
    /// IE-08: Replaced GetImporter on interface with CanImport. This method is now internal-only.
    /// </summary>
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
    /// Drops rows with out-of-range coordinates, short-circuits if nothing valid
    /// survives, then delegates the heavy lifting to <see cref="ImportPersister"/>.
    /// IE-12: Does not create an empty collection when parsing yields 0 valid POIs.
    /// IE-13 / IE-18: URL normalization and invariant-culture comparisons live in
    /// <see cref="ImportPersister"/>.
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

        // KML files can group Placemarks under <Folder> elements. When the
        // importer surfaces FolderName on any row, split into one collection
        // per folder so the user gets the same structure they see in My Maps.
        // Rows with no FolderName fall back to the user-provided collection name.
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

        // Enqueue atomicity: each newly-created row is born with
        // EnrichmentRequested=true (set in ImportPersister.BuildPoi) and committed
        // in the SAME SaveChanges that inserts it, so a group that commits is
        // always queued — even if a LATER group throws (SQLite-busy, constraint,
        // cancellation between groups). Dedup-linked existing rows are never
        // rebuilt, so they are correctly left un-enqueued. All that remains here
        // is to wake the worker once instead of waiting for the next poll tick.
        if (anyAdded)
        {
            enrichmentTrigger.Signal();
        }

        // For multi-folder imports return an aggregate; the per-collection
        // breakdown is in the logs. Single-group case keeps the original
        // collection id so the UI can navigate to it.
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
        // NULL coords are allowed through — enrichment will fill them in
        // later. A half-null pair (one set, the other missing) is
        // rejected. Non-null coords must be in range.
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
