using System.Diagnostics;
using LucidCartographer.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using LucidCartographer.Components;

namespace LucidCartographer.Tests.Integration;

public abstract class IntegrationTestBase : IAsyncLifetime
{
    private WebApplication _app = null!;
    private IServiceScope _scope = null!;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private IPage _rawPage = null!;
    protected IPage Page => _rawPage;
    protected string BaseUrl = null!;
    private readonly string _dbPath;
    private readonly Stopwatch _sw = new();

    protected IntegrationTestBase()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cartographer_test_{Guid.NewGuid()}.db");
    }

    public async Task InitializeAsync()
    {
        _sw.Start();
        Log("INIT: start");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Environment.EnvironmentName = "Development";

        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Warning);

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.AddDbContextFactory<AppDbContext>(options =>
            options.UseSqlite($"Data Source={_dbPath}"));

        builder.Services.AddScoped<LucidCartographer.Services.IPoiService, LucidCartographer.Services.PoiService>();
        builder.Services.AddScoped<LucidCartographer.Services.Import.IFileImporter, LucidCartographer.Services.Import.GpxImporter>();
        builder.Services.AddScoped<LucidCartographer.Services.Import.IFileImporter, LucidCartographer.Services.Import.KmlImporter>();
        builder.Services.AddScoped<LucidCartographer.Services.Import.IFileImporter, LucidCartographer.Services.Import.GeoJsonImporter>();
        builder.Services.AddScoped<LucidCartographer.Services.Import.IFileImporter, LucidCartographer.Services.Import.CsvImporter>();
        builder.Services.AddScoped<LucidCartographer.Services.Import.IImportOrchestrator, LucidCartographer.Services.Import.ImportOrchestrator>();
        builder.Services.AddSingleton<LucidCartographer.Services.Export.KmlExporter>();
        builder.Services.AddSingleton<LucidCartographer.Services.Export.GpxExporter>();
        builder.Services.AddScoped<LucidCartographer.Services.Operations.IPoiMatcher, LucidCartographer.Services.Operations.PoiMatcher>();
        builder.Services.AddScoped<LucidCartographer.Services.Operations.ISetOperationService, LucidCartographer.Services.Operations.SetOperationService>();
        builder.Services.AddScoped<LucidCartographer.Services.IMapService, StubMapService>();

        RegisterAdditionalServices(builder.Services);
        Log("INIT: services registered");

        _app = builder.Build();
        _app.UseAntiforgery();
        _app.UseStaticFiles();
        _app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        _scope = _app.Services.CreateScope();
        var dbFactory = _scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        Log("INIT: DB created");

        await _app.StartAsync();
        BaseUrl = _app.Urls.First().TrimEnd('/');
        Log($"INIT: app at {BaseUrl}");

        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", "0");
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        _rawPage = await _browser.NewPageAsync();

        // Inject browser-side DOM listeners that log every user interaction.
        // These fire on real DOM events so ALL test actions are captured automatically.
        await _rawPage.AddInitScriptAsync(@"
            document.addEventListener('click', e => {
                const t = e.target;
                const tag = t.tagName.toLowerCase();
                const text = (t.textContent || '').trim().substring(0, 40).replace(/\n/g, ' ');
                const cls = (t.className || '').substring(0, 30);
                console.log(`[UI] CLICK ${tag} ""${text}"" .${cls}`);
            }, true);
            document.addEventListener('input', e => {
                const t = e.target;
                console.log(`[UI] INPUT ${t.name || t.type || 'field'} = ""${(t.value || '').substring(0, 40)}""`);
            }, true);
            document.addEventListener('change', e => {
                const t = e.target;
                const val = t.tagName === 'SELECT' ? t.options[t.selectedIndex]?.text : t.value;
                console.log(`[UI] CHANGE ${t.name || t.tagName.toLowerCase()} = ""${(val || '').substring(0, 40)}""`);
            }, true);
            document.addEventListener('submit', e => {
                const form = e.target;
                const data = new FormData(form);
                const params = [...data.entries()].map(([k,v]) => `${k}=${v}`).join('&');
                console.log(`[UI] SUBMIT ${form.action} ? ${params}`);
            }, true);
        ");

        // Pipe browser console [UI] messages to test log
        _rawPage.Console += (_, msg) =>
        {
            var text = msg.Text;
            if (text.StartsWith("[UI]"))
                Log($"  {text}");
        };

        // Log navigation events
        _rawPage.FrameNavigated += (_, frame) =>
        {
            if (frame == _rawPage.MainFrame)
                Log($"  NAV: {frame.Url}");
        };
        _rawPage.Download += (_, dl) => Log($"  DOWNLOAD: {dl.SuggestedFilename}");

        Log("INIT: browser ready");
    }

    protected virtual void RegisterAdditionalServices(IServiceCollection services)
    {
        services.AddScoped<LucidCartographer.Services.Import.IGoogleMapsListScraper, LucidCartographer.Services.Import.GoogleMapsListScraper>();
    }

    // === Navigation helpers with logging ===

    protected async Task NavigateAndWaitAsync(string path = "/")
    {
        Log($"GO: {path}");
        await Page.GotoAsync($"{BaseUrl}{path}", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 15000
        });
        Log("  loaded, waiting for Blazor circuit");
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('script[src*=\"blazor.web.js\"]') !== null",
            null, new PageWaitForFunctionOptions { Timeout = 10000 });
        await Page.WaitForSelectorAsync("nav a", new() { Timeout = 10000 });
        Log("  Blazor ready");
    }

    protected async Task NavigateToDataSourcesAsync()
    {
        await NavigateAndWaitAsync("/");
        await ClickAsync("nav a:has-text('Data Sources')", "Data Sources tab");
        await Page.WaitForURLAsync("**/datasources");
        Log("  waiting for Data Sources page");
        await Page.WaitForSelectorAsync("h2:has-text('Data & Imports')", new() { Timeout = 10000 });
        Log("  Data Sources ready");
    }

    protected async Task NavigateToOperationsAsync()
    {
        await NavigateAndWaitAsync("/");
        await ClickAsync("nav a:has-text('Operations')", "Operations tab");
        await Page.WaitForURLAsync("**/operations");
        Log("  waiting for Operations page");
        await Page.WaitForSelectorAsync("h3:has-text('Source Selection')", new() { Timeout = 10000 });
        Log("  Operations ready");
    }

    // === Logged Playwright action wrappers ===

    protected async Task ClickAsync(string selector, string label = "")
    {
        var desc = string.IsNullOrEmpty(label) ? selector : label;
        Log($"  CLICK: {desc}");
        await Page.Locator(selector).ClickAsync();
    }

    protected async Task FillAsync(string selector, string value, string label = "")
    {
        var desc = string.IsNullOrEmpty(label) ? selector : label;
        Log($"  FILL: {desc} = \"{value}\"");
        await Page.Locator(selector).FillAsync(value);
    }

    protected async Task SelectAsync(string selector, string value, string label = "")
    {
        var desc = string.IsNullOrEmpty(label) ? selector : label;
        Log($"  SELECT: {desc} = \"{value}\"");
        await Page.Locator(selector).SelectOptionAsync(value);
    }

    protected async Task WaitForSelectorAsync(string selector, int timeoutMs = 10000, string label = "")
    {
        var desc = string.IsNullOrEmpty(label) ? selector : label;
        Log($"  WAIT: {desc}");
        await Page.WaitForSelectorAsync(selector, new() { Timeout = timeoutMs });
        Log($"  FOUND: {desc}");
    }

    protected async Task SleepAsync(int ms, string reason)
    {
        Log($"  SLEEP: {ms}ms ({reason})");
        await Page.WaitForTimeoutAsync(ms);
    }

    // === Data helpers ===

    protected async Task SeedDataAsync(Func<AppDbContext, Task> seedAction)
    {
        Log("SEED: start");
        var dbFactory = _scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using var db = await dbFactory.CreateDbContextAsync();
        await seedAction(db);
        Log("SEED: done");
    }

    protected async Task ImportTestFileAsync(string testDataFile, string collectionName, string color = "#005bbf")
    {
        Log($"IMPORT: {testDataFile} → \"{collectionName}\"");
        var orchestrator = _scope.ServiceProvider.GetRequiredService<LucidCartographer.Services.Import.IImportOrchestrator>();
        var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", testDataFile);
        using var stream = File.OpenRead(filePath);
        await orchestrator.ImportAsync(stream, testDataFile, collectionName, color);
        Log("IMPORT: done");
    }

    protected void Log(string message)
    {
        Console.WriteLine($"[{_sw.ElapsedMilliseconds,6}ms] {message}");
    }

    public async Task DisposeAsync()
    {
        Log("DISPOSE: start");
        try { if (_rawPage != null) await _rawPage.CloseAsync(); } catch { }
        try { if (_browser != null) await _browser.DisposeAsync(); } catch { }
        try { _playwright?.Dispose(); } catch { }
        try { _scope?.Dispose(); } catch { }
        try { if (_app != null) await _app.StopAsync(); } catch { }
        try { if (_app != null) await _app.DisposeAsync(); } catch { }
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        Log("DISPOSE: done");
    }
}
