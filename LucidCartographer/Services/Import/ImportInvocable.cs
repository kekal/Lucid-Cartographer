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
        private readonly IGoogleMapsListScraper _scraper;
        private readonly ImportJobStatusService _status;
        private readonly ILogger<ImportInvocable> _logger;

        public ImportJobPayload Payload { get; set; } = null!;

        public ImportInvocable(
            IImportOrchestrator orchestrator,
            IGoogleMapsListScraper scraper,
            ImportJobStatusService status,
            ILogger<ImportInvocable> logger)
        {
            _orchestrator = orchestrator;
            _scraper = scraper;
            _status = status;
            _logger = logger;
        }

        public async Task Invoke()
        {
            var label = Payload.IsFileImport
                ? Payload.FileName!
                : Payload.IsSharedList
                    ? Payload.SharedListUrl!
                    : $"scraped ({Payload.ScrapedPois?.Count ?? 0} POIs)";

            _logger.LogInformation("ImportInvocable: running {Label} -> {Collection}",
                label, Payload.CollectionName);

            try
            {
                ImportResult result;
                if (Payload.IsFileImport)
                {
                    _status.Publish(new ImportJobStatus(
                        ImportJobState.Running,
                        $"Importing '{label}' into '{Payload.CollectionName}'..."));

                    await using var stream = File.OpenRead(Payload.TempFilePath!);
                    result = await _orchestrator.ImportAsync(
                        stream, Payload.FileName!, Payload.CollectionName, Payload.Color);
                }
                else if (Payload.IsSharedList)
                {
                    // Full scrape-then-persist pipeline runs inside the job,
                    // so the user can navigate away during the 20–40s scrape
                    // without killing it with their Blazor circuit.
                    _status.Publish(new ImportJobStatus(
                        ImportJobState.Running,
                        "Scraping Google Maps list..."));

                    var scrape = await _scraper.ScrapeAsync(
                        Payload.SharedListUrl!,
                        onProgress: count => _status.Publish(new ImportJobStatus(
                            ImportJobState.Running,
                            $"Scraping Google Maps list… {count} place(s) found")));

                    if (scrape.Pois.Count == 0)
                    {
                        _status.Publish(new ImportJobStatus(
                            ImportJobState.Failed,
                            "No places found. Make sure the URL is a valid Google Maps list.",
                            Error: "empty scrape"));
                        return;
                    }

                    var collectionName = !string.IsNullOrWhiteSpace(Payload.CollectionName)
                        ? Payload.CollectionName
                        : scrape.ListName ?? $"Shared List ({scrape.Pois.Count} places)";

                    _status.Publish(new ImportJobStatus(
                        ImportJobState.Running,
                        $"Importing {scrape.Pois.Count} place(s) into '{collectionName}'..."));

                    result = await _orchestrator.ImportFromScrapedAsync(
                        scrape.Pois, collectionName, Payload.Color);
                }
                else
                {
                    _status.Publish(new ImportJobStatus(
                        ImportJobState.Running,
                        $"Importing {Payload.ScrapedPois?.Count ?? 0} place(s) into '{Payload.CollectionName}'..."));

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
                : payload.IsSharedList
                    ? "Google Maps list"
                    : $"scraped ({payload.ScrapedPois?.Count ?? 0} POIs)";
            var dest = string.IsNullOrWhiteSpace(payload.CollectionName)
                ? "a new collection"
                : $"'{payload.CollectionName}'";
            _status.Publish(new ImportJobStatus(
                ImportJobState.Queued,
                $"Queued {label} for import into {dest}. You may leave this page."));
        }
    }
}
