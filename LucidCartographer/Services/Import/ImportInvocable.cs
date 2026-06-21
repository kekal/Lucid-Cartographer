using Coravel.Invocable;
using Coravel.Queuing.Interfaces;

namespace LucidCartographer.Services.Import;

/// <summary>
/// Runs a single import job off the request thread. Each Coravel dequeue
/// creates a fresh <see cref="IServiceScope"/>, so injected scoped services
/// get their own <c>DbContext</c> per job, independent of the triggering Blazor circuit.
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
                // Scraping runs inside the job so the user can navigate away without killing it.
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
                    // Error=null so UI shows Message (user-friendly) instead of a sentinel value.
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
            // Job owns the temp file; clean up after completion or failure.
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
/// Adapts library-agnostic <see cref="IImportJobQueue"/> to Coravel's queue.
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
