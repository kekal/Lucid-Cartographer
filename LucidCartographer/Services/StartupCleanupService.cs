using LucidCartographer.Data;
using LucidCartographer.Services.Auth;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Services;

/// <summary>
/// Runs once on app startup, in order:
///   1. Sweep orphaned lucid-import-* temp files older than 1h.
///   2. Enforce auth-secret configuration (refuse to start in non-Development
///      when no secret is set; warn in Development).
///   3. Apply EF Core migrations (ARCH-CRIT-01: MigrateAsync, not EnsureCreatedAsync).
/// </summary>
public sealed class StartupCleanupService(
    IServiceProvider services,
    IHostEnvironment environment,
    ILoggerFactory loggerFactory,
    IConfiguration configuration)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        SweepOrphanedTempFiles();
        EnforceAuthSecretConfigured();
        await ApplyMigrationsAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void SweepOrphanedTempFiles()
    {
        // Sweep orphaned lucid-import-* temp files left by a previous crash that
        // died between "file streamed to disk" and "Coravel invocable ran +
        // deleted it in finally". Cheap and safe: only files matching the specific
        // pattern we wrote ourselves, older than 1h, are removed.
        var logger = loggerFactory.CreateLogger("TempFileSweep");
        try
        {
            var tempRoot = Path.GetTempPath();
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            var swept = 0;
            foreach (var path in Directory.EnumerateFiles(tempRoot, "lucid-import-*"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                        swept++;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not remove orphaned temp file {Path}", path);
                }
            }
            if (swept > 0)
            {
                logger.LogInformation("Removed {Count} orphaned lucid-import-* temp files from {Path}", swept, tempRoot);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Startup temp-file sweep failed; continuing");
        }
    }

    private void EnforceAuthSecretConfigured()
    {
        // In Production, refuse to start if no auth secret is configured — the app
        // will be exposed behind a Zero Trust proxy that does NOT authenticate, so
        // the app's own auth is the only gate. In Development, warn only.
        var logger = loggerFactory.CreateLogger<StartupCleanupService>();
        if (string.IsNullOrEmpty(AuthSecretReader.GetConfiguredAuthSecret(configuration)))
        {
            if (environment.IsDevelopment())
            {
                logger.LogWarning("Auth:Password/Auth:PasswordHash not set — authentication is DISABLED (Development only)");
            }
            else
            {
                throw new InvalidOperationException(
                    "Auth:Password/Auth:PasswordHash is not set. Configure an auth secret before starting the application in a non-Development environment.");
            }
        }
    }

    private async Task ApplyMigrationsAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
    }
}
