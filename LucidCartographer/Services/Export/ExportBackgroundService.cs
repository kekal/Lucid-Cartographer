using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LucidCartographer.Services.Export;

/// <summary>
/// Single-consumer service that drains <see cref="ExportJobQueue"/>, serialized one job at a time,
/// cancellable on shutdown and never pinning the shared Coravel queue.
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
        }
    }
}
