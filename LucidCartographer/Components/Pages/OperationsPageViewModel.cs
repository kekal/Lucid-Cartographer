using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Export;
using LucidCartographer.Services.Operations;
using Microsoft.JSInterop;

namespace LucidCartographer.Components.Pages;

/// <summary>
/// View-side orchestration for the Operations page. Owns the source/op
/// selection, the tolerance value, the result/discard state, and the commit
/// dialog. Tolerance debouncing uses a System.Threading.Timer that re-fires
/// the active operation 500ms after the last slider event.
/// </summary>
public sealed class OperationsPageViewModel(
    IPoiService poiService,
    ISetOperationService setOperationService,
    IPoiDeduplicationService deduplicationService,
    IEnumerable<IFileExporter> exporters,
    IJSRuntime js)
    : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Timer? _toleranceDebounce;

    public event Action? StateChanged;

    private void Notify() => StateChanged?.Invoke();

    // --- State ---

    public IReadOnlyList<PoiCollection> Collections { get; private set; } = [];
    public int CollectionAId { get; set; }
    public int CollectionBId { get; set; }
    public double ToleranceMeters { get; set; } = 100;
    public SetOperation? ActiveOp { get; private set; }
    public OperationResult? Result { get; private set; }
    public IReadOnlyList<Poi> ResultPois { get; private set; } = [];
    public HashSet<int> DiscardedIds { get; } = [];
    public bool IsProcessing { get; private set; }
    public bool IsLoading { get; private set; } = true;

    public bool ShowCommitDialog { get; set; }
    public string CommitName { get; set; } = string.Empty;
    public string? CommitSuccess { get; private set; }
    public bool IsDedupMode { get; private set; }
    public string? SelectBHint { get; private set; }

    // Whole-database deduplication (distinct from the within-collection
    // SetOperation.Dedup preview above — this folds duplicate rows across the
    // entire DB and persists immediately).
    public bool IsDeduplicatingDatabase { get; private set; }
    public string? DedupDatabaseMessage { get; private set; }

    // --- Lifecycle ---

    public async Task InitializeAsync()
    {
        Collections = await poiService.GetCollectionsAsync();
        IsLoading = false;
    }

    // --- Commands ---

    public async Task HandleBinaryOpClickAsync(SetOperation operation)
    {
        IsDedupMode = false;
        SelectBHint = null;
        if (CollectionBId > 0)
        {
            await RunOperationAsync(operation);
        }
        else
        {
            SelectBHint = UiStrings.SelectBFirst;
        }
    }

    public async Task RunOperationAsync(SetOperation operation)
    {
        IsDedupMode = operation == SetOperation.Dedup;

        ActiveOp = operation;
        IsProcessing = true;
        Result = null;
        ResultPois = [];
        DiscardedIds.Clear();
        ShowCommitDialog = false;
        CommitSuccess = null;
        Notify();

        try
        {
            Result = await setOperationService.ExecuteAsync(
                operation,
                CollectionAId,
                operation == SetOperation.Dedup ? null : CollectionBId,
                ToleranceMeters,
                _cts.Token);
            ResultPois = Result.Pois;
        }
        catch (Exception ex)
        {
            Result = new OperationResult { Description = $"Error: {ex.Message}" };
        }
        finally
        {
            IsProcessing = false;
        }
    }

    // LOW-09: When tolerance changes and an operation was already run, re-run it with debounce
    public void OnToleranceChanged()
    {
        if (ActiveOp == null || Result == null)
        {
            return;
        }

        _toleranceDebounce?.Dispose();
        // TimerCallback is void-returning; use Task.Run + try/catch so an
        // unhandled async exception cannot crash the process.
        _toleranceDebounce = new Timer(o =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (ActiveOp.HasValue)
                    {
                        await RunOperationAsync(ActiveOp.Value);
                        Notify();
                    }
                }
                catch
                {
                    // Debounce-driven re-runs swallow exceptions; next UI-driven
                    // run will surface a failure if it persists.
                }
            }, _cts.Token);
        }, state: null, 500, Timeout.Infinite);
    }

    /// <summary>
    /// Runs an on-demand full-database deduplication pass (the same engine the
    /// background service uses) and reports the merge count. Merging deletes
    /// the duplicate rows in place, so collection point counts may drop —
    /// reload them afterwards.
    /// </summary>
    public async Task HandleDeduplicateDatabaseAsync()
    {
        if (IsDeduplicatingDatabase)
        {
            return;
        }

        IsDeduplicatingDatabase = true;
        DedupDatabaseMessage = null;
        Notify();
        try
        {
            var result = await deduplicationService.DeduplicateAllAsync(_cts.Token);
            DedupDatabaseMessage = result.PoisMerged > 0
                ? string.Format(UiStrings.DeduplicateDone, result.PoisMerged, result.GroupsMerged)
                : UiStrings.DeduplicateNone;
            Collections = await poiService.GetCollectionsAsync();

            // The whole-DB pass may have physically deleted a Poi that the
            // within-collection Dedup preview still references (the preview
            // keeps group[0]; the DB pass keeps the lowest Id). Invalidate the
            // stale preview so those defunct rows can no longer be committed.
            Result = null;
            ResultPois = [];
            DiscardedIds.Clear();
            ActiveOp = null;
        }
        catch (Exception ex)
        {
            DedupDatabaseMessage = string.Format(UiStrings.DeduplicateFailed, ex.Message);
        }
        finally
        {
            IsDeduplicatingDatabase = false;
            Notify();
        }
    }

    public void DiscardPoi(int poiId) => DiscardedIds.Add(poiId);
    public void RestorePoi(int poiId) => DiscardedIds.Remove(poiId);

    public void CommitToLayer()
    {
        ShowCommitDialog = true;
        CommitSuccess = null;
        var colAName = Collections.FirstOrDefault(c => c.Id == CollectionAId)?.Name ?? "A";
        CommitName = $"{GetOperationLabel()} — {colAName}";
    }

    public void HideCommitDialog() => ShowCommitDialog = false;

    public async Task DoCommitAsync()
    {
        try
        {
            var poisToSave = ResultPois.Where(p => !DiscardedIds.Contains(p.Id)).ToList();
            var saved = await setOperationService.CommitResultAsync(poisToSave, CommitName);
            CommitSuccess = $"Saved \"{saved.Name}\" with {saved.PoiCount} POIs";
            Collections = await poiService.GetCollectionsAsync();
        }
        catch (Exception ex)
        {
            // CommitResultAsync runs in a transaction, so the DB has already
            // rolled back. Surface the failure as a user-visible message rather
            // than letting it propagate and tear down the Blazor circuit
            // (which the MainLayout ErrorBoundary would catch, wiping state).
            CommitSuccess = $"Commit failed: {ex.Message}";
        }
    }

    public async Task ExportResultAsync()
    {
        var poisToExport = ResultPois.Where(p => !DiscardedIds.Contains(p.Id)).ToList();
        if (!poisToExport.Any())
        {
            return;
        }

        var kmlExporter = exporters.First(e => e.FormatName == "KML");
        var exportTitle = string.IsNullOrEmpty(CommitName) ? GetOperationLabel() : CommitName;
        var bytes = kmlExporter.Export(poisToExport, exportTitle);
        var fileName = $"export_{DateTime.Now:yyyyMMdd_HHmmss}.kml";

        await js.InvokeVoidAsync("LucidCartographer.downloadFile",
            fileName, "application/vnd.google-earth.kml+xml", Convert.ToBase64String(bytes));
    }

    public string GetOperationLabel() => ActiveOp switch
    {
        SetOperation.Subtract => "A - B",
        SetOperation.Intersect => "A n B",
        SetOperation.Union => "A u B",
        SetOperation.Dedup => "Dedup",
        _ => ""
    };

    public ValueTask DisposeAsync()
    {
        _toleranceDebounce?.Dispose();
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* token source already disposed */ }
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
