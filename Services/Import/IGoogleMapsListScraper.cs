namespace LucidCartographer.Services.Import
{
    public interface IGoogleMapsListScraper
    {
        Task<ScrapeResult> ScrapeAsync(string listUrl, Action<int>? onProgress = null);
    }
}
