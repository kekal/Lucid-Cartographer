using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Enrichment;
using LucidCartographer.Services.Operations;
using Microsoft.EntityFrameworkCore;

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
        private readonly EnrichmentTrigger _enrichmentTrigger;
        private readonly ILogger<ImportOrchestrator> _logger;

        public ImportOrchestrator(IDbContextFactory<AppDbContext> factory, IEnumerable<IFileImporter> importers, EnrichmentTrigger enrichmentTrigger, ILogger<ImportOrchestrator> logger)
        {
            _factory = factory;
            _importers = importers;
            _enrichmentTrigger = enrichmentTrigger;
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
                return EmptyImportResult(parsed.Count, collectionName);

            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var persister = new ImportPersister(
                db, _logger, validParsed, parsed.Count,
                collectionName, color, sourceType, sourceFileName, cancellationToken);

            var result = await persister.RunAsync();

            // Wake the background enrichment service immediately instead of
            // making the user wait for its next poll tick. New rows land with
            // IsEnriched=false, so the BG loop picks them up as soon as it
            // observes the signal.
            if (persister.AddedAny) _enrichmentTrigger.Signal();

            return result;
        }

        private static List<ImportedPoi> FilterValidCoordinates(IReadOnlyList<ImportedPoi> parsed)
        {
            return parsed
                .Where(p => p.Latitude >= -90 && p.Latitude <= 90
                         && p.Longitude >= -180 && p.Longitude <= 180)
                .ToList();
        }

        private ImportResult EmptyImportResult(int totalParsed, string collectionName)
        {
            _logger.LogWarning(
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
}
