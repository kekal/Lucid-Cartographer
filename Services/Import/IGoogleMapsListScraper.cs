namespace LucidCartographer.Services.Import
{
    /// <summary>
    /// Scrapes a shared Google Maps list URL and extracts place data.
    /// </summary>
    public interface IGoogleMapsListScraper
    {
        /// <summary>
        /// Scrapes a Google Maps list URL and returns the extracted places.
        /// </summary>
        /// <param name="listUrl">Must be a Google Maps URL (https://www.google.com/maps/ or https://maps.google.com/ or https://maps.app.goo.gl/).</param>
        /// <param name="onProgress">Optional callback invoked with the current count of places found.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task<ScrapeResult> ScrapeAsync(string listUrl, Action<int>? onProgress = null, CancellationToken cancellationToken = default);
    }
}
