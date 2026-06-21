namespace LucidCartographer.Services.Import;

/// <summary>
/// Orchestrates file-based and scraped POI imports: parsing, deduplication, and persistence.
/// </summary>
public interface IImportOrchestrator
{
    /// <summary>
    /// Returns true if the given file name has a supported importer.
    /// </summary>
    bool CanImport(string fileName);

    /// <summary>Imports POIs from a file stream into a new collection.</summary>
    Task<ImportResult> ImportAsync(Stream fileStream, string fileName, string collectionName, string color = "#005bbf", CancellationToken cancellationToken = default);

    /// <summary>Imports pre-parsed POIs (e.g. from a scraper) into a new collection.</summary>
    Task<ImportResult> ImportFromScrapedAsync(IReadOnlyList<ImportedPoi> parsed, string collectionName, string color = "#005bbf", CancellationToken cancellationToken = default);
}