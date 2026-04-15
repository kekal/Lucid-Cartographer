namespace LucidCartographer.Services.Import
{
    /// <summary>
    /// Data-only envelope for a queued import. The invocable resolves its
    /// own <see cref="IImportOrchestrator"/> from a DI scope, so the
    /// payload only carries what Coravel needs to hand across the queue
    /// boundary.
    ///
    /// Two modes:
    ///   * File import     — <see cref="TempFilePath"/> + <see cref="FileName"/> set, <see cref="ScrapedPois"/> null.
    ///   * Scraped import  — <see cref="ScrapedPois"/> set, file fields null.
    /// </summary>
    public sealed class ImportJobPayload
    {
        public string CollectionName { get; init; } = string.Empty;
        public string Color { get; init; } = ImportOrchestrator.DefaultColor;

        // File-import mode
        public string? TempFilePath { get; init; }
        public string? FileName { get; init; }

        // Scraped mode
        public IReadOnlyList<ImportedPoi>? ScrapedPois { get; init; }

        public bool IsFileImport => TempFilePath != null && FileName != null;
    }
}
