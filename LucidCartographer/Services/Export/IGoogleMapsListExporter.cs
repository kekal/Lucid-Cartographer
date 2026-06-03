namespace LucidCartographer.Services.Export;

/// <summary>
/// Pushes places into a Google Maps Saved List via headful UI automation.
/// Abstracts <see cref="GoogleMapsListExporter"/> so the export job pipeline can
/// be unit-tested without launching a browser.
/// </summary>
public interface IGoogleMapsListExporter
{
    Task<ExportRunReport> ExportAsync(
        string listName,
        IReadOnlyList<string> placeUrls,
        Action<ExportProgress>? onProgress = null,
        CancellationToken ct = default);
}
