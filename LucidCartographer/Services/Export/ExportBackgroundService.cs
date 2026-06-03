using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LucidCartographer.Services.Export;

/// <summary>
/// Single-consumer hosted service that drains <see cref="ExportJobQueue"/> and
/// runs each export with the host's <c>stoppingToken</c> — so a long headful
/// export is cancellable on shutdown and never pins the shared Coravel queue.
/// One job at a time (serialised), matching the headful browser's single-flight.
/// </summary>
public sealed class ExportBackgroundService(
    ExportJobQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<ExportBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var payload in queue.Reader.ReadAllAsync(stoppingToken))
            {
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<ExportJobProcessor>();
                try
                {
                    await processor.RunAsync(payload, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break; // shutting down — stop draining
                }
                catch (Exception ex)
                {
                    // One bad job must not kill the consumer loop.
                    logger.LogError(ex, "Export job crashed unexpectedly");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
