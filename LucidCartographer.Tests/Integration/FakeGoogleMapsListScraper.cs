using LucidCartographer.Services.Import;

namespace LucidCartographer.Tests.Integration;

/// <summary>
/// Fake scraper for integration tests. Returns configurable results
/// without making real HTTP calls to Google Maps.
/// </summary>
public class FakeGoogleMapsListScraper : IGoogleMapsListScraper
{
    public ScrapeResult? ResultToReturn { get; set; } = new()
    {
        ListName = "Test Scraped List",
        Pois = (List<ImportedPoi>)
        [
            new("Scraped Place 1", 50.06, 19.94, "https://maps.google.com/place/1", "Kraków, Poland", "Museum", Rating: 4.5, ReviewCount: 1200),
            new("Scraped Place 2", 52.23, 21.01, "https://maps.google.com/place/2", "Warsaw, Poland", "Park", Rating: 4.2, ReviewCount: 800),
            new("Scraped Place 3", 51.11, 17.04, "https://maps.google.com/place/3", "Wrocław, Poland", "Restaurant", Rating: 4.8, ReviewCount: 350)
        ]
    };

    public Exception? ExceptionToThrow { get; set; }
    public int DelayMs { get; set; } = 100;
    public string? LastUrl { get; set; }

    // Default: return a small list with 3 POIs

    public bool HasBrowserProfile => false;

    public Task ResetBrowserProfileAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<SavedListInfo>> FetchSavedListsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SavedListInfo> result = (List<SavedListInfo>)
        [
            new("Favorites", "https://www.google.com/maps/list/1", 12),
            new("Want to go", "https://www.google.com/maps/list/2", 5),
            new("Custom List", "https://www.google.com/maps/list/3", 23)
        ];
        return Task.FromResult(result);
    }

    public async Task<ScrapeResult> ScrapeAsync(string listUrl, Action<int>? onProgress = null, CancellationToken cancellationToken = default)
    {
        LastUrl = listUrl;

        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }

        await Task.Delay(DelayMs);

        // Simulate progress callbacks
        if (onProgress != null && ResultToReturn != null)
        {
            for (var i = 1; i <= ResultToReturn.Pois.Count; i++)
            {
                onProgress(i);
                await Task.Delay(10);
            }
        }

        return ResultToReturn ?? new ScrapeResult();
    }
}