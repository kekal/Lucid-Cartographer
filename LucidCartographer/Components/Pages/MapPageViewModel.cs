using System.Reactive.Linq;
using LucidCartographer.Components.Shared;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Enrichment;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.JSInterop;

namespace LucidCartographer.Components.Pages;

/// <summary>
/// View-side orchestration for the Map page. Holds collections / POI selection,
/// viewport filter, search-result state, and JS-callback target state.
/// The <see cref="LeafletMap"/> reference is set by the component after first
/// render via <see cref="AttachMap"/> because @ref capture lives on the component.
/// </summary>
public sealed class MapPageViewModel : IAsyncDisposable
{
    public const int SearchLayerId = -1;
    public const string SearchMarkerColor = "#7B61FF";

    private readonly IPoiService _poiService;
    private readonly NavigationManager _navigation;
    private readonly EnrichmentProgressService _enrichmentProgress;
    private readonly ILogger<MapPageViewModel> _logger;

    private LeafletMap? _map;
    private IDisposable? _enrichmentSubscription;

    public MapPageViewModel(
        IPoiService poiService,
        NavigationManager navigation,
        EnrichmentProgressService enrichmentProgress,
        ILogger<MapPageViewModel> logger)
    {
        _poiService = poiService;
        _navigation = navigation;
        _enrichmentProgress = enrichmentProgress;
        _logger = logger;
    }

    public event Action? StateChanged;

    /// <summary>
    /// Set by the host component to its <c>InvokeAsync</c> so the VM can
    /// marshal background-thread callbacks (Rx, JS interop) onto the
    /// renderer's synchronization context. JS interop calls fail outside
    /// the renderer thread in Blazor Server; this hook is mandatory before
    /// <see cref="InitializeAsync"/> is invoked.
    /// </summary>
    public Func<Func<Task>, Task> RendererDispatch { get; set; } = f => f();

    private void Notify() => StateChanged?.Invoke();

    // --- State ---

    public IReadOnlyList<PoiCollection> Collections { get; private set; } = Array.Empty<PoiCollection>();
    public List<CollectionDisplayState> CollectionStates { get; private set; } = new();
    public IReadOnlyList<Poi> VisiblePois { get; private set; } = Array.Empty<Poi>();
    public IReadOnlyList<Poi> FilteredPois { get; private set; } = Array.Empty<Poi>();
    public Dictionary<int, IReadOnlyList<string>> PoiCollectionNames { get; private set; } = new();
    public Dictionary<int, IReadOnlyList<int>> PoiCollectionMemberships { get; private set; } = new();
    public Dictionary<int, int> PoiCollectionIds { get; private set; } = new();
    public int? SelectedCollectionId { get; private set; }
    public int? SelectedPoiId { get; private set; }
    public string? SelectedCollectionName { get; private set; }
    public Poi? SelectedPoi { get; private set; }
    public bool IsLoading { get; private set; } = true;
    public string? PreviousSearchQuery { get; private set; }
    public MapBounds? CurrentBounds { get; private set; }
    public int TableHeight { get; private set; } = 256;
    public bool PendingSearchMapUpdate { get; private set; }

    // --- Lifecycle ---

    public async Task InitializeAsync()
    {
        Collections = await _poiService.GetCollectionsAsync();
        CollectionStates = Collections.Select(c => new CollectionDisplayState(c)).ToList();
        IsLoading = false;

        // Refresh the map + list when the background enrichment service
        // flips Pois from placeholder (0,0) coords to real ones. Without
        // this, the user has to toggle collection visibility to see new
        // rows show up.
        _enrichmentSubscription = _enrichmentProgress.Changes
            .Skip(1)
            .Subscribe(_ => OnEnrichmentChanged());

        Notify();
    }

    /// <summary>
    /// Component must call this after `_leafletMap.WaitForInitAsync()` so the
    /// VM can interact with the live JS map instance.
    /// </summary>
    public void AttachMap(LeafletMap map) => _map = map;

    /// <summary>
    /// Component calls this once after the splitter is wired. Triggers initial
    /// map population (search results or visible collections).
    /// </summary>
    public async Task OnMapInitializedAsync()
    {
        if (PreviousSearchQuery != null)
        {
            await ShowSearchResultsOnMapAsync();
        }
        else
        {
            await LoadVisibleCollectionsAsync();
        }
        PendingSearchMapUpdate = false;
    }

    /// <summary>
    /// Component calls this from <c>OnParametersSetAsync</c>; reads the
    /// current URI from <see cref="NavigationManager"/> and handles
    /// <c>?search=…</c> queries.
    /// </summary>
    public async Task OnNavigationChangedAsync()
    {
        var uri = _navigation.ToAbsoluteUri(_navigation.Uri);
        if (QueryHelpers.ParseQuery(uri.Query).TryGetValue("search", out var searchValues))
        {
            var search = searchValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(search))
            {
                if (search == PreviousSearchQuery) return;
                PreviousSearchQuery = search;
                VisiblePois = await _poiService.SearchAsync(search);
                FilteredPois = VisiblePois;
                SelectedCollectionName = $"Search: {search}";
                PendingSearchMapUpdate = true;
                return;
            }
        }

        if (PreviousSearchQuery != null)
        {
            PreviousSearchQuery = null;
            PendingSearchMapUpdate = true;
        }
    }

    /// <summary>
    /// Component calls this from <c>OnAfterRenderAsync</c> when
    /// <see cref="PendingSearchMapUpdate"/> is true and the map is ready.
    /// </summary>
    public async Task ResolvePendingSearchMapUpdateAsync()
    {
        PendingSearchMapUpdate = false;
        if (PreviousSearchQuery != null)
        {
            await ShowSearchResultsOnMapAsync();
        }
        else
        {
            // Search cleared — restore collection view
            if (_map is not null)
                await _map.HideCollectionAsync(SearchLayerId);
            await LoadVisibleCollectionsAsync();
        }
    }

    private void OnEnrichmentChanged()
    {
        // Rx fires from a background thread. LoadVisibleCollectionsAsync
        // makes JS interop calls (LeafletMap.Show/HideCollectionAsync) which
        // require Blazor Server's renderer dispatcher; marshal there via
        // the host component's InvokeAsync.
        _ = RendererDispatch(async () =>
        {
            try
            {
                await LoadVisibleCollectionsAsync();
                Notify();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Enrichment refresh failed");
            }
        });
    }

    [JSInvokable]
    public Task OnSplitterResizedJs(int height)
    {
        TableHeight = height;
        Notify();
        return Task.CompletedTask;
    }

    // --- Map population ---

    private async Task ShowSearchResultsOnMapAsync()
    {
        if (_map == null) return;

        foreach (var s in CollectionStates)
            await _map.HideCollectionAsync(s.Id);

        var searchPois = VisiblePois.ToList();
        await _map.ShowCollectionAsync(SearchLayerId, searchPois, SearchMarkerColor);

        await RefreshPoiCollectionMapsAsync(searchPois);

        FilteredPois = VisiblePois;
        await _map.FitBoundsAsync();
        Notify();
    }

    private async Task LoadVisibleCollectionsAsync()
    {
        if (_map == null || !_map.IsInitialized) return;

        await _map.HideCollectionAsync(SearchLayerId);

        var grouped = await _poiService.GetVisiblePoisGroupedAsync();
        // A POI can belong to multiple collections (import "links" existing POIs
        // via the M:N CollectionPoi join). When more than one of those collections
        // is visible the same Poi would appear in VisiblePois twice, which would
        // crash PoiTable with "More than one sibling of element 'tr' has the same
        // key value". Dedup by Id while flattening.
        var seen = new HashSet<int>();
        var newPois = new List<Poi>();
        foreach (var s in CollectionStates)
        {
            if (s.IsVisible && grouped.ContainsKey(s.Id))
            {
                await _map.ShowCollectionAsync(s.Id, grouped[s.Id], s.Color);
                foreach (var poi in grouped[s.Id])
                {
                    if (seen.Add(poi.Id))
                        newPois.Add(poi);
                }
            }
            else
            {
                await _map.HideCollectionAsync(s.Id);
            }
        }
        VisiblePois = newPois;
        await RefreshPoiCollectionMapsAsync(newPois);
        ApplyViewportFilter();
    }

    // --- Commands (UI events) ---

    public async Task HandleVisibilityToggledAsync(int collectionId)
    {
        var s = CollectionStates.FirstOrDefault(c => c.Id == collectionId);
        if (s != null) s.IsVisible = !s.IsVisible;

        await _poiService.ToggleVisibilityAsync(collectionId);
        await LoadVisibleCollectionsAsync();
    }

    public async Task HandleCollectionSelectedAsync(int collectionId)
    {
        SelectedCollectionId = collectionId;
        var s = CollectionStates.FirstOrDefault(c => c.Id == collectionId);
        SelectedCollectionName = s?.Name;
        VisiblePois = await _poiService.GetPoisByCollectionAsync(collectionId);
        await RefreshPoiCollectionMapsAsync(VisiblePois);
        ApplyViewportFilter();
    }

    public Task HandleMarkerSelectedAsync(int poiId) => SelectPoiAsync(poiId);

    public async Task HandlePoiSelectedAsync(int poiId)
    {
        await SelectPoiAsync(poiId);
        if (_map != null && SelectedPoi != null)
        {
            await _map.HighlightMarkerAsync(poiId);
        }
    }

    public async Task HandleFocusPoiAsync(int poiId)
    {
        await SelectPoiAsync(poiId);
        if (_map != null && SelectedPoi != null
            && SelectedPoi.Latitude.HasValue && SelectedPoi.Longitude.HasValue)
        {
            await _map.FocusOnPoiAsync(SelectedPoi.Latitude.Value, SelectedPoi.Longitude.Value);
        }
    }

    private async Task SelectPoiAsync(int poiId)
    {
        SelectedPoiId = poiId;
        SelectedPoi = await _poiService.GetPoiAsync(poiId);
    }

    public void CloseDetailPane()
    {
        SelectedPoi = null;
        SelectedPoiId = null;
    }

    public async Task FitMapBoundsAsync()
    {
        if (_map != null)
        {
            // Show all immediately; the moveend after fitBounds will
            // re-apply the viewport filter with the new (fitted) bounds,
            // which will naturally include every visible POI.
            FilteredPois = VisiblePois;
            Notify();
            await _map.FitBoundsAsync();
        }
    }

    public void HandleBoundsChanged(MapBounds bounds)
    {
        CurrentBounds = bounds;
        ApplyViewportFilter();
    }

    private void ApplyViewportFilter()
    {
        if (CurrentBounds == null)
        {
            FilteredPois = VisiblePois;
        }
        else
        {
            FilteredPois = VisiblePois
                .Where(p => p.Latitude.HasValue && p.Longitude.HasValue
                    && CurrentBounds.Contains(p.Latitude.Value, p.Longitude.Value))
                .ToList();
        }
        Notify();
    }

    public async Task ResetSearchAsync()
    {
        PreviousSearchQuery = null;
        SelectedCollectionName = null;
        if (_map is { IsInitialized: true })
        {
            await _map.HideCollectionAsync(SearchLayerId);
            await LoadVisibleCollectionsAsync();
        }
        _navigation.NavigateTo("/", replace: true);
    }

    public async Task HandleDeletePoiAsync(int poiId)
    {
        if (PoiCollectionIds.TryGetValue(poiId, out var collectionId))
        {
            await _poiService.RemovePoiFromCollectionAsync(poiId, collectionId);
        }
        if (SelectedPoiId == poiId)
            CloseDetailPane();
        await RefreshAfterMutationAsync();
    }

    public async Task HandleMovePoiAsync((int PoiId, int TargetCollectionId) args)
    {
        await _poiService.AddPoiToCollectionAsync(args.PoiId, args.TargetCollectionId);

        if (PoiCollectionMemberships.TryGetValue(args.PoiId, out var sourceCollectionIds))
        {
            foreach (var sourceCollectionId in sourceCollectionIds.Where(id => id != args.TargetCollectionId))
            {
                await _poiService.RemovePoiFromCollectionAsync(args.PoiId, sourceCollectionId);
            }
        }
        await RefreshAfterMutationAsync();
    }

    public async Task HandleMoveToNewCollectionAsync((int PoiId, string NewCollectionName) args)
    {
        var newCol = await _poiService.CreateCollectionAsync(args.NewCollectionName);
        await _poiService.AddPoiToCollectionAsync(args.PoiId, newCol.Id);

        if (PoiCollectionMemberships.TryGetValue(args.PoiId, out var sourceCollectionIds))
        {
            foreach (var sourceCollectionId in sourceCollectionIds)
            {
                await _poiService.RemovePoiFromCollectionAsync(args.PoiId, sourceCollectionId);
            }
        }
        CollectionStates.Insert(0, new CollectionDisplayState(newCol));
        await RefreshAfterMutationAsync();
    }

    public async Task HandleCopyPoiAsync((int PoiId, int TargetCollectionId) args)
    {
        await _poiService.AddPoiToCollectionAsync(args.PoiId, args.TargetCollectionId);
        await RefreshAfterMutationAsync();
    }

    public async Task HandleCopyToNewCollectionAsync((int PoiId, string NewCollectionName) args)
    {
        var newCol = await _poiService.CreateCollectionAsync(args.NewCollectionName);
        await _poiService.AddPoiToCollectionAsync(args.PoiId, newCol.Id);
        CollectionStates.Insert(0, new CollectionDisplayState(newCol));
        await RefreshAfterMutationAsync();
    }

    private async Task RefreshPoiCollectionMapsAsync(IEnumerable<Poi> pois)
    {
        var poiIds = pois.Select(p => p.Id).Distinct().ToList();
        if (poiIds.Count == 0)
        {
            PoiCollectionNames = new();
            PoiCollectionMemberships = new();
            PoiCollectionIds = new();
            return;
        }

        PoiCollectionNames = (await _poiService.GetPoiCollectionNamesAsync(poiIds))
            .ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<string>)kvp.Value);

        var memberships = await _poiService.GetPoiCollectionMembershipsAsync(poiIds);
        PoiCollectionMemberships = memberships
            .ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<int>)kvp.Value);

        PoiCollectionIds = memberships
            .Where(kvp => kvp.Value.Count > 0)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value[0]);
    }

    private async Task RefreshAfterMutationAsync()
    {
        Collections = await _poiService.GetCollectionsAsync();
        foreach (var col in Collections)
        {
            var s = CollectionStates.FirstOrDefault(c => c.Id == col.Id);
            if (s != null) s.Collection.PoiCount = col.PoiCount;
        }
        await LoadVisibleCollectionsAsync();
    }

    public ValueTask DisposeAsync()
    {
        _enrichmentSubscription?.Dispose();
        _enrichmentSubscription = null;
        return ValueTask.CompletedTask;
    }
}
