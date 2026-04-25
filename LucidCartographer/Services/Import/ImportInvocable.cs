using Coravel.Invocable;
using Coravel.Queuing.Interfaces;

namespace LucidCartographer.Services.Import;

/// <summary>
/// Coravel invocable that runs a single import job off the request
/// thread. Coravel resolves this from its own <see cref="IServiceScope"/>
/// on every queue dequeue, so injecting the scoped
/// <see cref="IImportOrchestrator"/> directly is safe — we get a fresh
/// <c>DbContext</c> per job, independent of whichever Blazor circuit
/// triggered the enqueue (the user may have already navigated away).
/// </summary>
public sealed class ImportInvocable(
    IImportOrchestrator orchestrator,
    IGoogleMapsListScraper scraper,
    ImportJobStatusService status,
    ILogger<ImportInvocable> logger)
    : IInvocable, IInvocableWithPayload<ImportJobPayload>
{
    public ImportJobPayload Payload { get; set; } = null!;

    public async Task Invoke()
    {
        var label = Payload.IsFileImport
            ? Payload.FileName!
            : Payload.IsSharedList
                ? Payload.SharedListUrl!
                : $"scraped ({Payload.ScrapedPois?.Count ?? 0} POIs)";

        logger.LogInformation("ImportInvocable: running {Label} -> {Collection}",
            label, Payload.CollectionName);

        try
        {
            ImportResult result;
            if (Payload.IsFileImport)
            {
                status.Publish(new ImportJobStatus(
                    ImportJobState.Running,
                    $"Importing '{label}' into '{Payload.CollectionName}'..."));

                await using var stream = File.OpenRead(Payload.TempFilePath!);
                result = await orchestrator.ImportAsync(
                    stream, Payload.FileName!, Payload.CollectionName, Payload.Color);
            }
            else if (Payload.IsSharedList)
            {
                // Full scrape-then-persist pipeline runs inside the job,
                // so the user can navigate away during the 20–40s scrape
                // without killing it with their Blazor circuit.
                status.Publish(new ImportJobStatus(
                    ImportJobState.Running,
                    "Scraping Google Maps list..."));

                var scrape = await scraper.ScrapeAsync(
                    Payload.SharedListUrl!,
                    onProgress: count => status.Publish(new ImportJobStatus(
                        ImportJobState.Running,
                        $"Scraping Google Maps list… {count} place(s) found")));

                if (scrape.Pois.Count == 0)
                {
                    // Error=null so the UI subscription falls back to
                    // Message (the user-friendly sentence). Using a
                    // sentinel like "empty scrape" for Error would
                    // show up as-is in the error block.
                    status.Publish(new ImportJobStatus(
                        ImportJobState.Failed,
                        "No places found. Make sure the URL is a valid Google Maps list."));
                    return;
                }

                var collectionName = !string.IsNullOrWhiteSpace(Payload.CollectionName)
                    ? Payload.CollectionName
                    : scrape.ListName ?? $"Shared List ({scrape.Pois.Count} places)";

                status.Publish(new ImportJobStatus(
                    ImportJobState.Running,
                    $"Importing {scrape.Pois.Count} place(s) into '{collectionName}'..."));

                result = await orchestrator.ImportFromScrapedAsync(
                    scrape.Pois, collectionName, Payload.Color);
            }
            else
            {
                status.Publish(new ImportJobStatus(
                    ImportJobState.Running,
                    $"Importing {Payload.ScrapedPois?.Count ?? 0} place(s) into '{Payload.CollectionName}'..."));

                result = await orchestrator.ImportFromScrapedAsync(
                    Payload.ScrapedPois ?? [],
                    Payload.CollectionName, Payload.Color);
            }

            status.Publish(new ImportJobStatus(
                ImportJobState.Completed,
                $"Imported {result.AddedCount} new, {result.SkippedCount} duplicates into '{result.CollectionName}'.",
                Result: result));
            logger.LogInformation(
                "ImportInvocable: completed {Label} — {Added} added, {Skipped} skipped",
                label, result.AddedCount, result.SkippedCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ImportInvocable: failed for {Label}", label);
            status.Publish(new ImportJobStatus(
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
                    logger.LogWarning(ex,
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
public sealed class CoravelImportJobQueue(IQueue queue, ImportJobStatusService status) : IImportJobQueue
{
    public void Enqueue(ImportJobPayload payload)
    {
        queue.QueueInvocableWithPayload<ImportInvocable, ImportJobPayload>(payload);
        var label = payload.IsFileImport
            ? payload.FileName!
            : payload.IsSharedList
                ? "Google Maps list"
                : $"scraped ({payload.ScrapedPois?.Count ?? 0} POIs)";
        var dest = string.IsNullOrWhiteSpace(payload.CollectionName)
            ? "a new collection"
            : $"'{payload.CollectionName}'";
        status.Publish(new ImportJobStatus(
            ImportJobState.Queued,
            $"Queued {label} for import into {dest}. You may leave this page."));
    }
}
