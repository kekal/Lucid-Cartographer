namespace LucidCartographer.Services.Import
{
    /// <summary>
    /// Data-only envelope for a queued import. The invocable resolves its
    /// own <see cref="IImportOrchestrator"/> from a DI scope, so the
    /// payload only carries what Coravel needs to hand across the queue
    /// boundary.
    ///
    /// Three modes:
    ///   * File import       — <see cref="TempFilePath"/> + <see cref="FileName"/> set.
    ///   * Shared-list URL   — <see cref="SharedListUrl"/> set; the invocable runs the scraper server-side.
    ///   * Already-scraped   — <see cref="ScrapedPois"/> set (reserved for future re-imports; not used from the UI).
    /// </summary>
    public sealed class ImportJobPayload
    {
        public string CollectionName { get; init; } = string.Empty;
        public string Color { get; init; } = ImportOrchestrator.DefaultColor;

        // File-import mode
        public string? TempFilePath { get; init; }
        public string? FileName { get; init; }

        // Shared Google Maps list URL — scraped inside the job
        public string? SharedListUrl { get; init; }

        // Already-scraped mode (legacy / re-import path)
        public IReadOnlyList<ImportedPoi>? ScrapedPois { get; init; }

        public bool IsFileImport  => TempFilePath != null && FileName != null;
        public bool IsSharedList  => !string.IsNullOrWhiteSpace(SharedListUrl);
    }
}
