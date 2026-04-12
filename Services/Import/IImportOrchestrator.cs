namespace LucidCartographer.Services.Import
{
    /// <summary>
    /// Orchestrates file-based and scraped POI imports: parsing, deduplication, and persistence.
    /// </summary>
    public interface IImportOrchestrator
    {
        /// <summary>Returns a matching importer for the given file name, or null if unsupported.</summary>
        IFileImporter? GetImporter(string fileName);

        /// <summary>Imports POIs from a file stream into a new collection.</summary>
        Task<ImportResult> ImportAsync(Stream fileStream, string fileName, string collectionName, string color = "#005bbf", CancellationToken cancellationToken = default);

        /// <summary>Imports pre-parsed POIs (e.g. from a scraper) into a new collection.</summary>
        Task<ImportResult> ImportFromScrapedAsync(List<ImportedPoi> parsed, string collectionName, string color = "#005bbf", CancellationToken cancellationToken = default);
    }
}
