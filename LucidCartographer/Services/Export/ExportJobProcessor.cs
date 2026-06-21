namespace LucidCartographer.Services.Export;

/// <summary>
/// Runs a single Google Saved-List export. Scoped per job with a fresh DbContext
/// so the processor is independent of any Blazor circuit that triggered the enqueue.
/// </summary>
public sealed class ExportJobProcessor(
    IPoiService poiService,
    IGoogleMapsListExporter exporter,
    ExportJobStatusService status,
    ILogger<ExportJobProcessor> logger)
{
    public async Task RunAsync(ExportJobPayload payload, CancellationToken ct)
    {
        var listName = payload.ListName;
        var collectionId = payload.CollectionId;
        logger.LogInformation("Export job: collection {Id} -> list '{List}'", collectionId, listName);

        try
        {
            status.Publish(new ExportJobStatus(
                ExportJobState.Running, $"Preparing export to '{listName}'…", CollectionId: collectionId));

            var pois = await poiService.GetPoisByCollectionAsync(collectionId, ct);
            var urls = pois
                .Where(p => !string.IsNullOrWhiteSpace(p.GoogleMapsUrl))
                .Select(p => p.GoogleMapsUrl!)
                .ToList();

            if (urls.Count == 0)
            {
                status.Publish(new ExportJobStatus(
                    ExportJobState.Completed,
                    $"Nothing to export to '{listName}': no places have a Google Maps link.",
                    CollectionId: collectionId));
                return;
            }

            var report = await exporter.ExportAsync(
                listName, urls,
                onProgress: p => status.Publish(new ExportJobStatus(
                    ExportJobState.Running,
                    $"Saving {p.Done}/{p.Total} to '{listName}'… " +
                    $"({p.Created + p.Added} saved, {p.AlreadySaved} already there, {p.Failed} failed)" +
                    (p.CurrentName is null ? "" : $" — {p.CurrentName}"),
                    CollectionId: collectionId)),
                ct: ct);

            status.Publish(new ExportJobStatus(
                ExportJobState.Completed,
                $"Export to '{listName}' done: {report.Created} created, {report.Added} added, " +
                $"{report.AlreadySaved} already there, {report.Failed} failed (of {report.Total}).",
                Result: report, CollectionId: collectionId));
            logger.LogInformation(
                "Export job complete '{List}' — created={Created}, added={Added}, already={Already}, failed={Failed}",
                listName, report.Created, report.Added, report.AlreadySaved, report.Failed);
        }
        catch (OperationCanceledException)
        {
            status.Publish(new ExportJobStatus(
                ExportJobState.Failed, $"Export to '{listName}' was cancelled.", Error: "cancelled",
                CollectionId: collectionId));
            throw; // let the background service distinguish shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Export job failed for collection {Id}", collectionId);
            status.Publish(new ExportJobStatus(
                ExportJobState.Failed, $"Export to '{listName}' failed: {ex.Message}", Error: ex.Message,
                CollectionId: collectionId));
        }
    }
}
