namespace LucidCartographer.Services.Import;

/// <summary>
/// Process-wide bootstrap of the Playwright browser binaries so the app
/// and the integration tests work out-of-the-box on a clean machine
/// without the user having to invoke <c>playwright install</c> manually.
///
/// Called by <see cref="GoogleMapsListScraper"/> before launching
/// Chromium and by the test harness (IntegrationTestBase) before
/// <see cref="Microsoft.Playwright.Playwright.CreateAsync"/>.
///
/// Playwright's install command is idempotent and fast when browsers are
/// already present, so running it once per process is cheap. The result
/// is cached in a static flag behind a SemaphoreSlim so concurrent
/// callers don't race the installer.
/// </summary>
public static class PlaywrightBootstrap
{
    private static readonly SemaphoreSlim InstallLock = new(1, 1);
    private static bool _browsersInstalled;

    /// <summary>
    /// Ensures Playwright's Chromium browser is installed on the host.
    /// Safe to call from multiple threads — only the first caller per
    /// process actually runs the installer; subsequent calls no-op.
    /// </summary>
    /// <param name="logger">Optional logger. Tests that don't have a
    /// logger can pass <c>null</c>; messages will be dropped.</param>
    public static async Task EnsureBrowsersInstalledAsync(
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (_browsersInstalled)
        {
            return;
        }

        await InstallLock.WaitAsync(cancellationToken);
        try
        {
            if (_browsersInstalled)
            {
                return;
            }

            logger?.LogInformation("Ensuring Playwright Chromium browser is installed (one-time bootstrap)…");
            // Microsoft.Playwright.Program.Main is synchronous and shells
            // out to the Node-based installer; marshal onto a background
            // thread so we don't block the caller's sync context.
            var exitCode = await Task.Run(
                () => Microsoft.Playwright.Program.Main(["install", "chromium"]),
                cancellationToken);

            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Playwright browser install failed with exit code {exitCode}. " +
                    "Run `playwright install chromium` manually to diagnose.");
            }

            _browsersInstalled = true;
            logger?.LogInformation("Playwright Chromium browser is ready.");
        }
        finally
        {
            InstallLock.Release();
        }
    }
}
