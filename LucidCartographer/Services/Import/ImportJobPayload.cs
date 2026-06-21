namespace LucidCartographer.Services.Import;

/// <summary>
/// Queue-transportable import envelope; invocable resolves <see cref="IImportOrchestrator"/> from a DI scope (file, shared-list URL, or already-scraped POIs).
/// </summary>
public sealed class ImportJobPayload
{
    public string CollectionName { get; init; } = string.Empty;
    public string Color { get; init; } = ImportOrchestrator.DefaultColor;

    public string? TempFilePath { get; init; }
    public string? FileName { get; init; }

    // Scraped server-side by the invocable.
    public string? SharedListUrl { get; init; }

    public IReadOnlyList<ImportedPoi>? ScrapedPois { get; init; }

    public bool IsFileImport  => TempFilePath != null && FileName != null;
    public bool IsSharedList  => !string.IsNullOrWhiteSpace(SharedListUrl);
}