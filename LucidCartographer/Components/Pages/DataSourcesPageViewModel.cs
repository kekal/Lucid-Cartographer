using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Enrichment;
using LucidCartographer.Services.Export;
using LucidCartographer.Services.Import;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace LucidCartographer.Components.Pages;

/// <summary>
/// View-side orchestration for the Data Sources page. Holds all UI state and
/// dispatches to services. Raises <see cref="StateChanged"/> when external
/// signals (Rx import-status stream) mutate state. Methods triggered by UI
/// events rely on Blazor's automatic re-render after async event handlers.
/// </summary>
public sealed class DataSourcesPageViewModel(
    IImportJobQueue importJobQueue,
    ImportJobStatusService importJobStatus,
    IPoiService poiService,
    IGoogleMapsListScraper scraper,
    IEnumerable<IFileExporter> exporters,
    IJSRuntime js,
    EnrichmentTrigger enrichmentTrigger,
    ILogger<DataSourcesPageViewModel> logger)
    : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private IDisposable? _statusSubscription;

    public event Action? StateChanged;

    private void Notify() => StateChanged?.Invoke();

    // --- State ---

    public IReadOnlyList<PoiCollection> Collections { get; private set; } = [];
    public bool ShowUpload { get; private set; }
    public string ActiveCard { get; private set; } = "file";
    public string CollectionName { get; set; } = string.Empty;
    public string SelectedColor { get; set; } = "#005bbf";
    public bool IsImporting { get; private set; }
    public string? ImportingFileName { get; private set; }
    public ImportResult? ImportResult { get; private set; }
    public string? ImportError { get; private set; }
    public string SharedListUrl { get; set; } = string.Empty;
    public string? QueuedMessage { get; private set; }
    public bool IsFetchingLists { get; private set; }
    public IReadOnlyList<SavedListInfo>? SavedLists { get; private set; }
    public string? FetchListsError { get; private set; }
    public int FailedEnrichmentCount { get; private set; }
    public string? MaintenanceMessage { get; private set; }

    // Color picker modal
    public int? ColorPickerCollectionId { get; private set; }
    public string ColorPickerValue { get; set; } = "#005bbf";
    public bool IsSavingColorPicker { get; private set; }
    public string? ColorPickerError { get; private set; }

    // Rename modal
    public int? RenameCollectionId { get; private set; }
    public string RenameValue { get; set; } = string.Empty;
    public bool IsSavingRename { get; private set; }
    public string? RenameError { get; private set; }

    // Add-POI modal
    public int? AddPoiCollectionId { get; private set; }
    public string AddPoiUrl { get; set; } = string.Empty;
    public string? AddPoiError { get; private set; }
    public bool AddPoiSuccess { get; private set; }
    public bool AddPoiSaving { get; private set; }

    // Export indicator (per-collection)
    public int? ExportingId { get; private set; }

    // Delete confirmation
    public int? PendingDeleteId { get; private set; }

    public bool HasGoogleProfile => scraper.HasBrowserProfile;

    public string[] AvailableColors { get; } =
    [
        "#005bbf", "#006e2c", "#b81d17", "#7c3aed", "#ca8a04", "#0891b2", "#be185d", "#4b5563"
    ];

    public string UploadTitle => ActiveCard switch
    {
        "takeout" => UiStrings.ImportFromTakeout,
        "shared" => UiStrings.ImportSharedList,
        _ => UiStrings.ImportFile
    };

    public string AcceptExtensions => ActiveCard switch
    {
        "takeout" => ".geojson,.json",
        "shared" => ".gpx",
        _ => ".gpx,.kml,.kmz,.geojson,.json,.csv"
    };

    public string AcceptDescription => ActiveCard switch
    {
        "takeout" => "Upload Saved Places.json from Google Takeout export",
        "shared" => "Upload GPX file from the Firefox extension",
        _ => "Supports GPX, KML, KMZ, GeoJSON, CSV"
    };

    // --- Lifecycle ---

    public async Task InitializeAsync()
    {
        await LoadCollectionsAsync();

        // Subscribe to background-import lifecycle events. BehaviorSubject
        // replays the latest value on subscribe, so we pick up a job that
        // was enqueued on another circuit / by an earlier visit and is
        // still running. This is the whole point of the Coravel refactor:
        // the user can navigate away and come back and still see status.
        _statusSubscription = importJobStatus.Changes.Subscribe(OnImportJobStatusChanged);
    }

    private void OnImportJobStatusChanged(ImportJobStatus status)
    {
        // The Rx callback is invoked off the Blazor circuit. We mutate state
        // synchronously and signal the view via StateChanged; the view
        // marshals to InvokeAsync(StateHasChanged).
        switch (status.State)
        {
            case ImportJobState.Queued:
                IsImporting = true;
                QueuedMessage = status.Message;
                ImportError = null;
                ImportResult = null;
                break;
            case ImportJobState.Running:
                IsImporting = true;
                QueuedMessage = status.Message;
                break;
            case ImportJobState.Completed:
                IsImporting = false;
                ImportResult = status.Result;
                QueuedMessage = null;
                _ = ReloadAfterCompletionWithLoggingAsync();
                return;
            case ImportJobState.Failed:
                IsImporting = false;
                ImportError = status.Error ?? status.Message;
                QueuedMessage = null;
                break;
        }
        Notify();
    }

    private async Task ReloadAfterCompletionAsync()
    {
        await LoadCollectionsAsync();
        Notify();
    }

    // The Rx callback that schedules this is synchronous (Action<T>),
    // so the only way to surface errors is to log + signal them here.
    // Without this wrapper, a reload failure would only reach the
    // global UnobservedTaskException handler when the GC finalizes
    // the discarded Task — far too late to be useful.
    private async Task ReloadAfterCompletionWithLoggingAsync()
    {
        try
        {
            await ReloadAfterCompletionAsync();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // VM is being disposed; nothing to surface.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reloading collections after import completion failed");
            MaintenanceMessage = $"Refresh after import failed: {ex.Message}";
            Notify();
        }
    }

    private async Task LoadCollectionsAsync()
    {
        Collections = await poiService.GetCollectionsAsync();
        FailedEnrichmentCount = await poiService.GetFailedEnrichmentCountAsync();
    }

    // --- Commands ---

    public int? EnrichingCollectionId { get; private set; }

    public async Task HandleEnrichCollectionAsync(int collectionId)
    {
        EnrichingCollectionId = collectionId;
        Notify();
        try
        {
            var count = await poiService.MarkCollectionForReEnrichmentAsync(collectionId, _cts.Token);
            MaintenanceMessage = $"Queued {count} POIs for re-enrichment.";
            enrichmentTrigger.Signal();
            await LoadCollectionsAsync();
        }
        catch (Exception ex)
        {
            MaintenanceMessage = $"Failed to queue re-enrichment: {ex.Message}";
        }
        finally
        {
            EnrichingCollectionId = null;
        }
    }

    public async Task HandleResetFailedEnrichmentAsync()
    {
        var reset = await poiService.ResetFailedEnrichmentAsync(_cts.Token);
        MaintenanceMessage = string.Format(UiStrings.FailedEnrichmentReset, reset);
        await LoadCollectionsAsync();
        enrichmentTrigger.Signal();
    }

    public void ShowUploadFor(string card)
    {
        ActiveCard = card;
        ShowUpload = true;
        ImportResult = null;
        ImportError = null;
    }

    public void CloseUpload() => ShowUpload = false;

    public async Task HandleFileSelectedAsync(InputFileChangeEventArgs e)
    {
        if (e.File is not { } file)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(CollectionName))
        {
            CollectionName = Path.GetFileNameWithoutExtension(file.Name);
        }

        IsImporting = true;
        ImportingFileName = file.Name;
        ImportResult = null;
        ImportError = null;
        Notify();

        try
        {
            const long maxUploadSizeBytes = 10 * 1024 * 1024; // 10MB max

            // The browser-side stream only lives for the duration of this
            // SignalR invocation, so we drop the bytes onto disk before
            // enqueuing. The invocable owns the temp file afterwards and
            // deletes it once the job completes (success OR failure).
            var tempPath = Path.Combine(Path.GetTempPath(),
                $"lucid-import-{Guid.NewGuid():N}{Path.GetExtension(file.Name)}");
            await using (var tempStream = File.Create(tempPath))
            await using (var upload = file.OpenReadStream(maxAllowedSize: maxUploadSizeBytes))
            {
                await upload.CopyToAsync(tempStream, _cts.Token);
            }

            importJobQueue.Enqueue(new ImportJobPayload
            {
                TempFilePath = tempPath,
                FileName = file.Name,
                CollectionName = CollectionName,
                Color = SelectedColor
            });
            CollectionName = string.Empty;
            // IsImporting / QueuedMessage are driven by the status
            // subscription from here on; leave them alone.
        }
        catch (Exception ex)
        {
            ImportError = ex.Message;
            IsImporting = false;
        }
    }

    public void ScrapeSharedList()
    {
        if (string.IsNullOrWhiteSpace(SharedListUrl))
        {
            return;
        }

        // The whole scrape-then-persist pipeline now runs inside the
        // background job, so the user can navigate away during the 20–40s
        // scrape without killing the circuit.
        ImportResult = null;
        ImportError = null;

        try
        {
            importJobQueue.Enqueue(new ImportJobPayload
            {
                SharedListUrl = SharedListUrl,
                CollectionName = CollectionName,
                Color = SelectedColor
            });
            CollectionName = string.Empty;
            SharedListUrl = string.Empty;
        }
        catch (Exception ex)
        {
            ImportError = $"Failed to queue scrape: {ex.Message}";
        }
    }

    public async Task HandleFetchMyListsAsync()
    {
        IsFetchingLists = true;
        FetchListsError = null;
        SavedLists = null;
        Notify();

        try
        {
            SavedLists = await scraper.FetchSavedListsAsync(_cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            FetchListsError = ex.Message;
        }
        finally
        {
            IsFetchingLists = false;
        }
    }

    public void HandleImportSelectedLists(IReadOnlyList<SavedListInfo> selected)
    {
        foreach (var list in selected)
        {
            importJobQueue.Enqueue(new ImportJobPayload
            {
                SharedListUrl = list.Url,
                CollectionName = list.Name,
                Color = SelectedColor
            });
        }
        SavedLists = null;
    }

    public void HandleResetProfile()
    {
        try
        {
            scraper.ResetBrowserProfile();
            SavedLists = null;
            FetchListsError = null;
        }
        catch (Exception ex)
        {
            FetchListsError = $"Failed to reset profile: {ex.Message}";
        }
    }

    // --- Add POI by URL modal ---

    public void OpenAddPoi(int collectionId)
    {
        AddPoiCollectionId = collectionId;
        AddPoiUrl = string.Empty;
        AddPoiError = null;
        AddPoiSuccess = false;
    }

    public void CloseAddPoi()
    {
        AddPoiCollectionId = null;
        AddPoiError = null;
        AddPoiSuccess = false;
    }

    public async Task SaveNewPoiAsync()
    {
        AddPoiError = null;
        AddPoiSuccess = false;

        var url = AddPoiUrl.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            AddPoiError = "Paste a Google Maps link.";
            return;
        }

        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            AddPoiError = "Please paste a valid URL starting with http.";
            return;
        }

        AddPoiSaving = true;
        Notify();

        try
        {
            // Resolve short URLs (maps.app.goo.gl/...) to full Google Maps URLs
            // so we can extract place name and coordinates immediately.
            if (url.Contains("goo.gl/", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var handler = new HttpClientHandler();
                    using var resolveClient = new HttpClient(handler);
                    handler.AllowAutoRedirect = true;
                    resolveClient.Timeout = TimeSpan.FromSeconds(10);
                    using var req = new HttpRequestMessage(HttpMethod.Head, url);
                    var resp = await resolveClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                    var resolvedUrl = resp.RequestMessage?.RequestUri?.ToString();
                    if (!string.IsNullOrEmpty(resolvedUrl) && resolvedUrl.Contains("google.com/maps", StringComparison.Ordinal))
                    {
                        url = resolvedUrl;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Best-effort short-URL expansion: any non-cancellation
                    // failure just leaves the original short URL in place.
                    logger.LogDebug(ex, "Short-URL HEAD resolution failed; using original URL");
                }
            }

            // Only accept the place marker (!3d!4d) — never trust the
            // `/@lat,lon` viewport-center fallback for Add-POI, otherwise
            // a map that was panned elsewhere would bind the wrong coords
            // to the new row. Enrichment will fill coords from the detail
            // page if the URL has no place marker.
            var coords = PoiUrlHelper.ExtractPlaceCoordinatesFromUrl(url);

            // Derive a placeholder name from the URL
            var placeholderName = "Pending enrichment";
            if (url.Contains("/maps/place/", StringComparison.Ordinal))
            {
                var placeStart = url.IndexOf("/maps/place/", StringComparison.Ordinal) + "/maps/place/".Length;
                var placeEnd = url.IndexOf('/', placeStart);
                if (placeEnd < 0)
                {
                    placeEnd = url.IndexOf('?', placeStart);
                }

                if (placeEnd < 0)
                {
                    placeEnd = url.Length;
                }

                var rawName = url[placeStart..placeEnd];
                rawName = Uri.UnescapeDataString(rawName).Replace('+', ' ');
                if (!string.IsNullOrWhiteSpace(rawName))
                {
                    placeholderName = rawName;
                }
            }

            var poi = new Poi
            {
                Name = placeholderName,
                Latitude = coords?.lat,
                Longitude = coords?.lon,
                GoogleMapsUrl = url,
                IsEnriched = false  // enrichment service will fill details
            };

            await poiService.CreatePoiAsync(poi, AddPoiCollectionId!.Value);

            // Wake the enrichment service so it picks up the new POI immediately
            enrichmentTrigger.Signal();

            AddPoiSuccess = true;
            AddPoiUrl = string.Empty;
            await LoadCollectionsAsync();
        }
        catch (Exception ex)
        {
            AddPoiError = ex.Message;
        }
        finally
        {
            AddPoiSaving = false;
        }
    }

    // --- Export ---

    public async Task HandleExportToMyMapsAsync(int collectionId)
    {
        var col = Collections.FirstOrDefault(c => c.Id == collectionId);
        if (col == null)
        {
            return;
        }

        ExportingId = collectionId;
        Notify();

        try
        {
            var pois = await poiService.GetPoisByCollectionAsync(collectionId);
            if (!pois.Any())
            {
                return;
            }

            var kmlExporter = exporters.First(e => e.FormatName == "KML");
            var bytes = kmlExporter.Export(pois, col.Name);

            // Push the KML to the client's browser as a download. The app may
            // run in a container, where a server-side file path (the previous
            // approach) is meaningless to the user. The browser never exposes
            // the saved location to JS, so we copy the file NAME to the
            // clipboard instead — the user pastes it into the OS file picker
            // when importing into My Maps to jump straight to it. Then open
            // Google My Maps so they can import the just-downloaded file.
            var fileName = $"{SanitizeFileName(col.Name)}.kml";
            await js.InvokeVoidAsync("LucidCartographer.downloadFile",
                fileName, "application/vnd.google-earth.kml+xml", Convert.ToBase64String(bytes));
            await js.InvokeVoidAsync("navigator.clipboard.writeText", fileName);
            await js.InvokeVoidAsync("window.open", "https://www.google.com/maps/d/", "_blank");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error exporting collection {CollectionId} to KML", collectionId);
            MaintenanceMessage = $"Export failed: {ex.Message}";
        }
        finally
        {
            ExportingId = null;
        }
    }

    /// <summary>
    /// Strips characters that are invalid in file names (and ':' / '/' which the
    /// browser's download attribute mishandles) so a collection name like
    /// "4-7.06: Запад → Kalisz" yields a usable .kml download. Unicode letters
    /// are preserved; only the unsafe punctuation is replaced with '_'.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':']).ToHashSet();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "export" : sanitized;
    }

    // --- Delete confirmation ---

    public void RequestDelete(int id) => PendingDeleteId = id;
    public void CancelDelete() => PendingDeleteId = null;

    public async Task ConfirmDeleteAsync(int id)
    {
        PendingDeleteId = null;
        await poiService.DeleteCollectionAsync(id);
        await LoadCollectionsAsync();
    }

    // --- Color picker modal ---

    public void OpenColorPicker(PoiCollection collection)
    {
        ColorPickerCollectionId = collection.Id;
        ColorPickerValue = string.IsNullOrWhiteSpace(collection.Color) ? "#005bbf" : collection.Color;
        ColorPickerError = null;
    }

    public void CloseColorPicker()
    {
        ColorPickerCollectionId = null;
        ColorPickerError = null;
    }

    public async Task SaveCollectionColorAsync()
    {
        if (ColorPickerCollectionId == null)
        {
            return;
        }

        IsSavingColorPicker = true;
        ColorPickerError = null;

        try
        {
            await poiService.UpdateCollectionColorAsync(ColorPickerCollectionId.Value, ColorPickerValue, _cts.Token);
            await LoadCollectionsAsync();
            CloseColorPicker();
        }
        catch (InvalidOperationException ex)
        {
            ColorPickerError = ex.Message;
        }
        catch (ArgumentException ex)
        {
            ColorPickerError = ex.Message;
        }
        finally
        {
            IsSavingColorPicker = false;
        }
    }

    // --- Rename modal ---

    public void OpenRename(PoiCollection collection)
    {
        RenameCollectionId = collection.Id;
        RenameValue = collection.Name;
        RenameError = null;
    }

    public void CloseRename()
    {
        RenameCollectionId = null;
        RenameError = null;
    }

    public async Task SaveCollectionRenameAsync()
    {
        if (RenameCollectionId == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(RenameValue))
        {
            RenameError = "Name cannot be empty.";
            return;
        }

        IsSavingRename = true;
        RenameError = null;

        try
        {
            await poiService.RenameCollectionAsync(RenameCollectionId.Value, RenameValue, _cts.Token);
            await LoadCollectionsAsync();
            CloseRename();
        }
        catch (InvalidOperationException ex)
        {
            RenameError = ex.Message;
        }
        catch (ArgumentException ex)
        {
            RenameError = ex.Message;
        }
        finally
        {
            IsSavingRename = false;
        }
    }

    // --- Disposal ---

    public ValueTask DisposeAsync()
    {
        _statusSubscription?.Dispose();
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* token source already disposed */ }
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
