using LucidCartographer.Services.Import;
using Microsoft.Playwright;

namespace LucidCartographer.Tests.Integration;

/// <summary>
/// Unit tests for the GoogleMapsScraperScripts.Discover JS pass. These run
/// against fake HTML fixtures (data: URLs), so they don't touch Google at all
/// and can't be broken by Google rotating class names.
///
/// The fixtures model the two shapes that were historically brittle:
///   1. A scrollable list panel (the common case).
///   2. A SHORT list that fits without overflow — the regression case
///      where the previous "must be scrollable" heuristic returned 0.
/// Plus a noise fixture (page chrome masquerading as rows) to confirm the
/// heuristic picks the right container.
/// </summary>
public class ScraperDiscoveryTests : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private IBrowserContext _context = null!;
    private IPage _page = null!;

    public async Task InitializeAsync()
    {
        // Don't pin PLAYWRIGHT_BROWSERS_PATH — the main project's bootstrap
        // installs Chromium into the default user cache (%LOCALAPPDATA%\ms-playwright),
        // and we want to reuse that rather than a package-local copy.
        // (IntegrationTestBase sets it to "0" so other tests in the same run
        // may have leaked the env var into this process — explicitly clear it.)
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", null);
        // Shared one-shot bootstrap — same code path as the runtime
        // scraper and IntegrationTestBase. No logger available here.
        await PlaywrightBootstrap.EnsureBrowsersInstalledAsync();
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
        _context = await _browser.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 } });
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

    private async Task<DiscoveryResult> RunDiscoveryAsync()
    {
        var json = await _page.EvaluateAsync<System.Text.Json.JsonElement>(GoogleMapsScraperScripts.Discover);
        return new DiscoveryResult
        {
            Count = json.GetProperty("count").GetInt32(),
            Total = json.GetProperty("total").GetInt32(),
            ScrollFound = json.GetProperty("scrollFound").GetBoolean(),
            Diag = json.TryGetProperty("diag", out var d) ? d.GetRawText() : null
        };
    }

    private record DiscoveryResult
    {
        public int Count { get; init; }
        public int Total { get; init; }
        public bool ScrollFound { get; init; }
        public string? Diag { get; init; }
    }

    [Fact]
    public async Task Discover_FindsCards_InScrollablePanel()
    {
        // Mimics the classic "tall list, inner scroll" shape.
        var html = """
                   <!DOCTYPE html><html><head><style>
                     body { margin: 0; font-family: sans-serif; }
                     .panel { height: 400px; overflow-y: auto; width: 300px; }
                     .card { height: 80px; padding: 10px; border-bottom: 1px solid #ccc; }
                   </style></head><body>
                   <div class="panel">
                     <div class="card"><strong>Wawel Castle</strong><p>Krakow</p></div>
                     <div class="card"><strong>Palace of Culture</strong><p>Warsaw</p></div>
                     <div class="card"><strong>Market Square</strong><p>Wroclaw</p></div>
                     <div class="card"><strong>Main Square</strong><p>Krakow</p></div>
                     <div class="card"><strong>Old Town</strong><p>Torun</p></div>
                   </div>
                   </body></html>
                   """;
        await LoadHtmlAsync(html);

        var result = await RunDiscoveryAsync();

        Assert.True(result.ScrollFound, $"Should find container. Diag: {result.Diag}");
        Assert.Equal(5, result.Count);
        Assert.Equal(5, result.Total);

        // Verify tags actually landed
        var tagged = await _page.Locator("[data-scraper-idx]").CountAsync();
        Assert.Equal(5, tagged);
        var scrollContainer = await _page.Locator("[data-scraper-scroll='1']").CountAsync();
        Assert.Equal(1, scrollContainer);
    }

    [Fact]
    public async Task Discover_FindsCards_InNonScrollableShortList()
    {
        // Regression: a list of 4 items fits entirely in the panel without
        // triggering overflow. Old code required scrollHeight > clientHeight
        // and returned 0 here.
        var html = """
                   <!DOCTYPE html><html><head><style>
                     body { margin: 0; font-family: sans-serif; }
                     .panel { width: 300px; }
                     .card { height: 60px; padding: 10px; }
                   </style></head><body>
                   <div class="panel">
                     <div class="card"><strong>Tatra Mountains</strong><p>Zakopane</p></div>
                     <div class="card"><strong>Masurian Lakes</strong><p>Olsztyn</p></div>
                     <div class="card"><strong>Baltic Dunes</strong><p>Leba</p></div>
                     <div class="card"><strong>Bieszczady</strong><p>Sanok</p></div>
                   </div>
                   </body></html>
                   """;
        await LoadHtmlAsync(html);

        var result = await RunDiscoveryAsync();

        Assert.True(result.ScrollFound, $"Should find container even without overflow. Diag: {result.Diag}");
        Assert.Equal(4, result.Count);
        Assert.Equal(4, result.Total);
    }

    [Fact]
    public async Task Discover_IgnoresPageChrome_PicksTightestCardContainer()
    {
        // Header nav bar and footer both have multiple children, but not
        // card-shaped. The inner list should win.
        var html = """
                   <!DOCTYPE html><html><head><style>
                     body { margin: 0; font-family: sans-serif; }
                     nav { display: flex; height: 50px; }
                     nav a { flex: 1; padding: 10px; height: 30px; }
                     .list { width: 300px; }
                     .row { height: 70px; padding: 12px; border-bottom: 1px solid #eee; }
                     footer { display: flex; height: 40px; }
                     footer span { flex: 1; padding: 8px; height: 24px; }
                   </style></head><body>
                   <nav>
                     <a href="#">Map</a><a href="#">Data</a><a href="#">Ops</a>
                   </nav>
                   <div class="list">
                     <div class="row"><strong>Black Lake</strong><p>Szczyrk</p></div>
                     <div class="row"><strong>Green Valley</strong><p>Krynica</p></div>
                     <div class="row"><strong>Red Rocks</strong><p>Tatra</p></div>
                     <div class="row"><strong>White Cliffs</strong><p>Hel</p></div>
                     <div class="row"><strong>Blue Springs</strong><p>Polanica</p></div>
                   </div>
                   <footer>
                     <span>(c) 2026</span><span>Terms</span><span>Privacy</span>
                   </footer>
                   </body></html>
                   """;
        await LoadHtmlAsync(html);

        var result = await RunDiscoveryAsync();

        Assert.True(result.ScrollFound, $"Diag: {result.Diag}");
        Assert.Equal(5, result.Count);

        // The chosen container must be the list, not the nav or footer
        var chosen = _page.Locator("[data-scraper-scroll='1']").First;
        var className = await chosen.GetAttributeAsync("class");
        Assert.Equal("list", className);
    }

    [Fact]
    public async Task Discover_IsIdempotent_DoesNotDoubleTag()
    {
        var html = """
                   <!DOCTYPE html><html><head><style>
                     .panel { height: 400px; overflow-y: auto; }
                     .card { height: 80px; padding: 10px; }
                   </style></head><body>
                   <div class="panel">
                     <div class="card">One place here</div>
                     <div class="card">Two places here</div>
                     <div class="card">Three places here</div>
                   </div>
                   </body></html>
                   """;
        await LoadHtmlAsync(html);

        var first = await RunDiscoveryAsync();
        Assert.Equal(3, first.Count);
        Assert.Equal(3, first.Total);

        var second = await RunDiscoveryAsync();
        Assert.Equal(0, second.Count);  // nothing newly tagged
        Assert.Equal(3, second.Total);  // total unchanged

        // Indices are still 0,1,2 — no duplicates, no gaps
        for (var i = 0; i < 3; i++)
        {
            var n = await _page.Locator($"[data-scraper-idx='{i}']").CountAsync();
            Assert.Equal(1, n);
        }
    }

    [Fact]
    public async Task Discover_NewCards_AppendWithStableIndices()
    {
        // Simulates scroll-driven lazy rendering: start with 3 cards, run
        // discovery, then add 2 more to the DOM and run again. The original
        // 3 keep their indices, new cards get 3 and 4.
        var html = """
                   <!DOCTYPE html><html><head><style>
                     .panel { height: 400px; overflow-y: auto; }
                     .card { height: 80px; padding: 10px; }
                   </style></head><body>
                   <div class="panel" id="p">
                     <div class="card">First place here</div>
                     <div class="card">Second place here</div>
                     <div class="card">Third place here</div>
                   </div>
                   </body></html>
                   """;
        await LoadHtmlAsync(html);

        var first = await RunDiscoveryAsync();
        Assert.Equal(3, first.Total);

        // Append two new cards (as Google would on scroll)
        await _page.EvaluateAsync(@"() => {
                const p = document.getElementById('p');
                for (let i = 0; i < 2; i++) {
                    const d = document.createElement('div');
                    d.className = 'card';
                    d.textContent = 'Appended place ' + i;
                    d.style.height = '80px';
                    d.style.padding = '10px';
                    p.appendChild(d);
                }
            }");

        var second = await RunDiscoveryAsync();
        Assert.Equal(2, second.Count);  // 2 newly tagged
        Assert.Equal(5, second.Total);  // 5 total

        // First three indices preserved
        for (var i = 0; i < 5; i++)
        {
            var n = await _page.Locator($"[data-scraper-idx='{i}']").CountAsync();
            Assert.Equal(1, n);
        }
    }

    [Fact]
    public async Task Discover_EmptyPage_ReturnsDiagnostics()
    {
        var html = "<!DOCTYPE html><html><body><div><p>Nothing here</p></div></body></html>";
        await LoadHtmlAsync(html);

        var result = await RunDiscoveryAsync();

        Assert.False(result.ScrollFound);
        Assert.Equal(0, result.Total);
        // diag payload should exist on failure so we can debug the real page
        Assert.NotNull(result.Diag);
    }
}