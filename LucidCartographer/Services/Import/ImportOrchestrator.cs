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

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var persister = new ImportPersister(
            db, logger, validParsed, parsed.Count,
            collectionName, color, sourceType, sourceFileName, cancellationToken);

        var result = await persister.RunAsync();

        // Wake the background enrichment service immediately instead of
        // making the user wait for its next poll tick. New rows land with
        // IsEnriched=false, so the BG loop picks them up as soon as it
        // observes the signal.
        if (persister.AddedAny)
        {
            enrichmentTrigger.Signal();
        }

        return result;
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
