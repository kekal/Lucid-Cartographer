namespace LucidCartographer.Services.Import
{
    /// <summary>
    /// Result of scraping a Google Maps list, containing the list name and extracted POIs.
    /// </summary>
    public record ScrapeResult
    {
        public string? ListName { get; init; }
        public IReadOnlyList<ImportedPoi> Pois { get; init; } = Array.Empty<ImportedPoi>();
    }
}
