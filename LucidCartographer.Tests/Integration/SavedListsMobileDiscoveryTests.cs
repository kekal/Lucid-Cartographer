using LucidCartographer.Services.Import;
using Microsoft.Playwright;
using System.Text.Json;

namespace LucidCartographer.Tests.Integration;

/// <summary>
/// Unit tests for the GoogleMapsScraperScripts.DiscoverSavedListsMobile JS pass.
/// Runs against fixture HTML (data: URLs) — does not touch Google.
///
/// Covers:
///   1. EN rows: Private/Shared/Public (with and without count).
///   2. RU rows: Личный / В совместном доступе / Общедоступный.
///   3. F2 edge case: name that BEGINS with a visibility word ("Private spots").
///   4. Non-list buttons (e.g. "Menu") are excluded.
///   5. Leading icon leaf (F11): first leaf is an aria-hidden glyph; real name is leaves[0] after it.
/// </summary>
public class SavedListsMobileDiscoveryTests : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private IBrowserContext _context = null!;
    private IPage _page = null!;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", null);
        await PlaywrightBootstrap.EnsureBrowsersInstalledAsync();
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
        _context = await _browser.NewContextAsync(new() { ViewportSize = new() { Width = 390, Height = 844 } });
        _page = await _context.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        try { await _page.CloseAsync(); } catch { }
        try { await _context.DisposeAsync(); } catch { }
        try { await _browser.DisposeAsync(); } catch { }
        try { _playwright.Dispose(); } catch { }
    }

    private async Task LoadHtmlAsync(string html)
    {
        var dataUrl = "data:text/html;charset=utf-8," + Uri.EscapeDataString(html);
        await _page.GotoAsync(dataUrl);
    }

    private async Task<List<(string Name, int? Count)>> RunDiscoverMobileAsync()
    {
        var json = await _page.EvaluateAsync<JsonElement>(GoogleMapsScraperScripts.DiscoverSavedListsMobile);
        var results = new List<(string Name, int? Count)>();
        foreach (var item in json.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString() ?? "";
            int? count = null;
            if (item.TryGetProperty("count", out var cv) && cv.ValueKind == JsonValueKind.Number)
                count = cv.GetInt32();
            results.Add((name, count));
        }
        return results;
    }

    private static Dictionary<string, int?> ToDict(List<(string Name, int? Count)> list)
        => list.ToDictionary(x => x.Name, x => x.Count);

    [Fact]
    public async Task DiscoverMobile_EN_PrivateSharedPublic_CorrectNamesAndCounts()
    {
        // Topology per doc: each button has leaf children [name, visibility(, count)]
        var html = """
                   <!DOCTYPE html><html><body>
                   <button><span>Berlin Eats</span><span>Private</span><span>5 places</span></button>
                   <button><span>Trip</span><span>Shared</span><span>12 places</span></button>
                   <button><span>City</span><span>Public</span></button>
                   </body></html>
                   """;
        await LoadHtmlAsync(html);

        var results = await RunDiscoverMobileAsync();
        var dict = ToDict(results);

        Assert.Equal(3, results.Count);
        Assert.True(dict.ContainsKey("Berlin Eats"), "Expected 'Berlin Eats'");
        Assert.Equal(5, dict["Berlin Eats"]);
        Assert.True(dict.ContainsKey("Trip"), "Expected 'Trip'");
        Assert.Equal(12, dict["Trip"]);
        Assert.True(dict.ContainsKey("City"), "Expected 'City'");
        Assert.Null(dict["City"]);
    }

    [Fact]
    public async Task DiscoverMobile_RU_AllThreeVisibilities_Matched()
    {
        // RU visibility markers were broken before F1 fix (\b ASCII-only boundary)
        var html = """
                   <!DOCTYPE html><html><body>
                   <button><span>Кафе</span><span>Личный</span><span>5 мест</span></button>
                   <button><span>Маршрут</span><span>В совместном доступе</span><span>12 мест</span></button>
                   <button><span>Город</span><span>Общедоступный</span></button>
                   </body></html>
                   """;
        await LoadHtmlAsync(html);

        var results = await RunDiscoverMobileAsync();
        var dict = ToDict(results);

        Assert.Equal(3, results.Count);
        Assert.True(dict.ContainsKey("Кафе"), "Expected 'Кафе'");
        Assert.Equal(5, dict["Кафе"]);
        Assert.True(dict.ContainsKey("Маршрут"), "Expected 'Маршрут'");
        Assert.Equal(12, dict["Маршрут"]);
        Assert.True(dict.ContainsKey("Город"), "Expected 'Город'");
        Assert.Null(dict["Город"]);
    }

    [Fact]
    public async Task DiscoverMobile_NameStartingWithVisibilityWord_PicksCorrectName()
    {
        // F2 regression: "Private spots" was mistakenly skipped because visRx matched it
        // and the fallback find() would pick count leaf as name instead.
        var html = """
                   <!DOCTYPE html><html><body>
                   <button><span>Private spots</span><span>Private</span><span>3 places</span></button>
                   </body></html>
                   """;
        await LoadHtmlAsync(html);

        var results = await RunDiscoverMobileAsync();
        Assert.Single(results);
        Assert.Equal("Private spots", results[0].Name);
        Assert.Equal(3, results[0].Count);
    }

    [Fact]
    public async Task DiscoverMobile_NonListButton_IsExcluded()
    {
        // A button whose leaves contain no visibility marker must not appear in results
        var html = """
                   <!DOCTYPE html><html><body>
                   <button><span>Menu</span></button>
                   <button><span>Berlin Eats</span><span>Private</span><span>5 places</span></button>
                   </body></html>
                   """;
        await LoadHtmlAsync(html);

        var results = await RunDiscoverMobileAsync();
        Assert.Single(results);
        Assert.Equal("Berlin Eats", results[0].Name);
        Assert.Equal(5, results[0].Count);
    }

    [Fact]
    public async Task DiscoverMobile_LeadingIconLeaf_NameIsFirstRealTextLeaf()
    {
        // F11: a leading aria-hidden icon glyph must not be mistaken for the name.
        // The real name is the first non-icon leaf (leaves[0] after filtering).
        // We model the icon as a span with aria-hidden="true" and a star glyph —
        // its textContent is non-empty, so it appears in leaves[]. The topology
        // fix (prefer leaves[0] when it isn't pure-numeric/visibility) handles this
        // when the icon leaf IS leaves[0]: the icon text is 1 char, filtered by length>1.
        var html = """
                   <!DOCTYPE html><html><body>
                   <button><span aria-hidden="true">★</span><span>Favorites</span><span>Shared</span><span>7 places</span></button>
                   </body></html>
                   """;
        await LoadHtmlAsync(html);

        var results = await RunDiscoverMobileAsync();
        Assert.Single(results);
        Assert.Equal("Favorites", results[0].Name);
        Assert.Equal(7, results[0].Count);
    }
}
