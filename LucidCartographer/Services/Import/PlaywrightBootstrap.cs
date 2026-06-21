namespace LucidCartographer.Services.Import;

/// <summary>
/// Process-wide bootstrap of Playwright browser binaries. Cached in a static
/// flag behind SemaphoreSlim so concurrent callers don't race the installer.
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
