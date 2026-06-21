namespace LucidCartographer.Services.Export;

/// <summary>
/// Pushes places into a Google Maps Saved List. Abstraction enables unit-testing
/// the export pipeline without launching a browser.
/// </summary>
public interface IGoogleMapsListExporter
{
    Task<ExportRunReport> ExportAsync(
        string listName,
        IReadOnlyList<string> placeUrls,
        Action<ExportProgress>? onProgress = null,
        CancellationToken ct = default);
}
