using Coravel.Invocable;
using Coravel.Queuing.Interfaces;

namespace LucidCartographer.Services.Import
{
    /// <summary>
    /// Coravel invocable that runs a single import job off the request
    /// thread. Coravel resolves this from its own <see cref="IServiceScope"/>
    /// on every queue dequeue, so injecting the scoped
    /// <see cref="IImportOrchestrator"/> directly is safe — we get a fresh
    /// <c>DbContext</c> per job, independent of whichever Blazor circuit
    /// triggered the enqueue (the user may have already navigated away).
    /// </summary>
    public sealed class ImportInvocable
        : IInvocable, IInvocableWithPayload<ImportJobPayload>
    {
        private readonly IImportOrchestrator _orchestrator;
        private readonly ImportJobStatusService _status;
        private readonly ILogger<ImportInvocable> _logger;

        public ImportJobPayload Payload { get; set; } = null!;

        public ImportInvocable(
            IImportOrchestrator orchestrator,
            ImportJobStatusService status,
            ILogger<ImportInvocable> logger)
        {
            _orchestrator = orchestrator;
            _status = status;
            _logger = logger;
        }

        public async Task Invoke()
        {
            var label = Payload.IsFileImport
                ? Payload.FileName!
                : $"scraped ({Payload.ScrapedPois?.Count ?? 0} POIs)";
            _status.Publish(new ImportJobStatus(
                ImportJobState.Running,
                $"Importing '{label}' into '{Payload.CollectionName}'..."));
            _logger.LogInformation("ImportInvocable: running {Label} -> {Collection}",
                label, Payload.CollectionName);

            try
            {
                ImportResult result;
                if (Payload.IsFileImport)
                {
                    await using var stream = File.OpenRead(Payload.TempFilePath!);
                    result = await _orchestrator.ImportAsync(
                        stream, Payload.FileName!, Payload.CollectionName, Payload.Color);
                }
                else
                {
                    result = await _orchestrator.ImportFromScrapedAsync(
                        Payload.ScrapedPois ?? Array.Empty<ImportedPoi>(),
                        Payload.CollectionName, Payload.Color);
                }

                _status.Publish(new ImportJobStatus(
                    ImportJobState.Completed,
                    $"Imported {result.AddedCount} new, {result.SkippedCount} duplicates into '{result.CollectionName}'.",
                    Result: result));
                _logger.LogInformation(
                    "ImportInvocable: completed {Label} — {Added} added, {Skipped} skipped",
                    label, result.AddedCount, result.SkippedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ImportInvocable: failed for {Label}", label);
                _status.Publish(new ImportJobStatus(
                    ImportJobState.Failed,
                    $"Import failed: {ex.Message}",
                    Error: ex.Message));
            }
            finally
            {
                // Temp file is owned by the job once enqueued; clean up.
                if (Payload.IsFileImport && !string.IsNullOrEmpty(Payload.TempFilePath))
                {
                    try { File.Delete(Payload.TempFilePath); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "ImportInvocable: failed to delete temp file {Path}",
                            Payload.TempFilePath);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Coravel-backed queue adapter. Thin on purpose — all it does is
    /// translate our library-agnostic <see cref="IImportJobQueue"/> call
    /// into Coravel's <see cref="IQueue.QueueInvocableWithPayload{TInvocable, TPayload}"/>.
    /// </summary>
    public sealed class CoravelImportJobQueue : IImportJobQueue
    {
        private readonly IQueue _queue;
        private readonly ImportJobStatusService _status;

        public CoravelImportJobQueue(IQueue queue, ImportJobStatusService status)
        {
            _queue = queue;
            _status = status;
        }

        public void Enqueue(ImportJobPayload payload)
        {
            _queue.QueueInvocableWithPayload<ImportInvocable, ImportJobPayload>(payload);
            var label = payload.IsFileImport
                ? payload.FileName!
                : $"scraped ({payload.ScrapedPois?.Count ?? 0} POIs)";
            _status.Publish(new ImportJobStatus(
                ImportJobState.Queued,
                $"Queued '{label}' for import into '{payload.CollectionName}'. You may leave this page."));
        }
    }
}
