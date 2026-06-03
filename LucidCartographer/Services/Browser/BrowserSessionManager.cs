using LucidCartographer.Services.Import;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace LucidCartographer.Services.Browser;

/// <summary>
/// Owns the single shared persistent <see cref="IBrowserContext"/> (headful
/// Chromium on the persistent Chrome profile) for the whole process lifetime.
/// Launched lazily on first use and reused by the exporter, "Fetch My Lists",
/// and the authenticated scrape — so a sign-in done once (via the noVNC view on
/// the server, or a real window in dev) carries across every automation.
///
/// Concurrency: the context is shared, so callers that drive a multi-step flow
/// must hold <see cref="GoogleBrowserLock"/> for the duration. The interactive
/// status / sign-in / reset operations here use <see cref="GoogleBrowserLock.TryAcquireAsync"/>
/// and report "busy" rather than colliding with a running job.
/// </summary>
public sealed class BrowserSessionManager : IBrowserSession, IAsyncDisposable
{
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const string MapsUrl = "https://www.google.com/maps";
    private const string SignInUrl =
        "https://accounts.google.com/ServiceLogin?continue=https%3A%2F%2Fwww.google.com%2Fmaps";

    private readonly ILogger<BrowserSessionManager> _logger;
    private readonly GoogleBrowserLock _browserLock;
    private readonly BrowserOptions _options;

    private readonly SemaphoreSlim _initGate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private bool _disposed;

    public BrowserSessionManager(
        ILogger<BrowserSessionManager> logger,
        GoogleBrowserLock browserLock,
        IOptions<BrowserOptions> options)
    {
        _logger = logger;
        _browserLock = browserLock;
        _options = options.Value;

        // Resolution precedence: CHROME_PROFILE_PATH env → Browser:ProfilePath →
        // dev default under the app binary. The dedicated CHROME_PROFILE_PATH env
        // var wins so the Docker image can point at the persisted /data volume
        // without a section-qualified name.
        var fromEnv = Environment.GetEnvironmentVariable("CHROME_PROFILE_PATH");
        ProfilePath = !string.IsNullOrWhiteSpace(fromEnv)
            ? fromEnv
            : !string.IsNullOrWhiteSpace(_options.ProfilePath)
                ? _options.ProfilePath!
                : Path.Combine(AppContext.BaseDirectory, "data", "chrome-profile");
    }

    public string ProfilePath { get; }

    public bool HasProfile
    {
        get
        {
            try
            {
                return Directory.Exists(ProfilePath) &&
                       Directory.EnumerateFileSystemEntries(ProfilePath).Any();
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task<IPage> NewPageAsync(CancellationToken ct = default)
    {
        var context = await EnsureContextAsync(ct);
        return await context.NewPageAsync();
    }

    public async Task<GoogleSessionStatus> GetStatusAsync(CancellationToken ct = default)
    {
        using var lease = await _browserLock.TryAcquireAsync(ct);
        if (lease is null)
        {
            return new GoogleSessionStatus(false, Busy: true, "A Google browser operation is already running.");
        }

        try
        {
            var page = await GetOrCreateInteractivePageAsync(ct);
            await page.GotoAsync(MapsUrl,
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
            await page.WaitForTimeoutAsync(3000);
            await GoogleConsent.DismissAsync(page, _logger);

            var signedIn = await GoogleSignIn.IsSignedInAsync(page, _logger);
            return new GoogleSessionStatus(signedIn, Busy: false,
                signedIn ? "Signed in to Google." : "Not signed in.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Google session status");
            return new GoogleSessionStatus(false, Busy: false, $"Could not read status: {ex.Message}");
        }
    }

    public async Task<GoogleSessionStatus> NavigateToSignInAsync(CancellationToken ct = default)
    {
        using var lease = await _browserLock.TryAcquireAsync(ct);
        if (lease is null)
        {
            return new GoogleSessionStatus(false, Busy: true, "A Google browser operation is already running.");
        }

        try
        {
            var page = await GetOrCreateInteractivePageAsync(ct);
            await page.GotoAsync(SignInUrl,
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
            await GoogleConsent.DismissAsync(page, _logger);
            _logger.LogInformation("Navigated shared session to Google sign-in (URL: {Url})", page.Url);
            // Login is completed by the user in the remote view; don't block here.
            return new GoogleSessionStatus(false, Busy: false,
                "Sign-in page opened. Complete the login in the view below, then Refresh status.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open the Google sign-in page");
            return new GoogleSessionStatus(false, Busy: false, $"Could not open sign-in: {ex.Message}");
        }
    }

    public async Task ResetProfileAsync(CancellationToken ct = default)
    {
        using var lease = await _browserLock.TryAcquireAsync(ct)
            ?? throw new InvalidOperationException(
                "A Google browser operation is running. Wait for it to finish, then reset the profile.");

        await CloseContextAsync();

        try
        {
            if (Directory.Exists(ProfilePath))
            {
                Directory.Delete(ProfilePath, recursive: true);
                _logger.LogInformation("Browser profile reset: {Path}", ProfilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete browser profile at {Path}", ProfilePath);
            throw;
        }
    }

    /// <summary>
    /// Reuse the session's front tab for interactive (status / sign-in) work so the
    /// noVNC view always shows one stable window rather than spawning tabs.
    /// </summary>
    private async Task<IPage> GetOrCreateInteractivePageAsync(CancellationToken ct)
    {
        var context = await EnsureContextAsync(ct);
        return context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();
    }

    private async Task<IBrowserContext> EnsureContextAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_context is not null)
        {
            return _context;
        }

        await _initGate.WaitAsync(ct);
        try
        {
            if (_context is not null)
            {
                return _context;
            }

            await PlaywrightBootstrap.EnsureBrowsersInstalledAsync(_logger, ct);
            Directory.CreateDirectory(ProfilePath);

            _logger.LogInformation(
                "Launching shared Chromium session (headless={Headless}, profile={Path})",
                _options.Headless, ProfilePath);

            _playwright = await Playwright.CreateAsync();
            _context = await _playwright.Chromium.LaunchPersistentContextAsync(
                ProfilePath,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = _options.Headless,
                    Locale = "en-US",
                    UserAgent = DefaultUserAgent,
                    Args = ["--disable-blink-features=AutomationControlled"]
                });

            return _context;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task CloseContextAsync()
    {
        if (_context is not null)
        {
            try { await _context.CloseAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error closing shared browser context"); }
            _context = null;
        }
        _playwright?.Dispose();
        _playwright = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await CloseContextAsync();
        _initGate.Dispose();
    }
}
