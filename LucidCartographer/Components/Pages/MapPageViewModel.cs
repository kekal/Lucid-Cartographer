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
public sealed class MapPageViewModel(
    IPoiService poiService,
    NavigationManager navigation,
    EnrichmentProgressService enrichmentProgress,
    EnrichmentTrigger enrichmentTrigger,
    ILogger<MapPageViewModel> logger)
    : IAsyncDisposable
{
    public const int SearchLayerId = -1;
    public const string SearchMarkerColor = "#7B61FF";

    private LeafletMap? _map;
    private IDisposable? _enrichmentSubscription;

    public event Action? StateChanged;

    /// <summary>
    /// Marshals background-thread callbacks (Rx, JS interop) onto the renderer's synchronization context.
    /// JS interop calls fail outside the renderer thread in Blazor Server; this hook is mandatory before InitializeAsync.
    /// </summary>
    public Func<Func<Task>, Task> RendererDispatch { get; set; } = f => f();

    private void Notify() => StateChanged?.Invoke();

    /// <summary>
    /// Invoked after a membership mutation (POI added/removed and the map/list
    /// refreshed) so the Trip layer can append/re-compact the Stop Order. Set by
    /// the host page; null when no Trip layer is wired. Kept separate from
    /// <see cref="StateChanged"/> so the (DB-touching) reconcile only runs on real
    /// membership changes, not on every pan/selection notify.
    /// </summary>
    public Func<Task>? MembershipChanged { get; set; }

    /// <summary>
    /// The lone visible collection when exactly one is visible and no search is active; otherwise null.
    /// Per-collection Trip state is only coherent against one collection.
    /// </summary>
    public int? SingleVisibleCollectionId
    {
        get
        {
            if (PreviousSearchQuery != null)
            {
                return null;
            }
            var visible = CollectionStates.Where(c => c.IsVisible).ToList();
            return visible.Count == 1 ? visible[0].Id : null;
        }
    }

    /// <summary>Placeable (lat+lon present) POIs in the current filtered result set (viewport-dependent).</summary>
    public int PlaceablePoiCount => FilteredPois.Count(p => p is { Latitude: not null, Longitude: not null });

    /// <summary>
    /// Placeable POIs in the single visible collection's full membership (viewport-independent).
    /// Drives Trip toggle gate: Trip View shows whole collection regardless of pan/zoom, so gate must too.
    /// Zero when no single collection in scope (multi-collection or search) to keep gate closed.
    /// </summary>
    public int CollectionPlaceablePoiCount =>
        SingleVisibleCollectionId is null
            ? 0
            : VisiblePois.Count(p => p is { Latitude: not null, Longitude: not null });

    public IReadOnlyList<PoiCollection> Collections { get; private set; } = [];
    public List<CollectionDisplayState> CollectionStates { get; private set; } = [];
    public IReadOnlyList<Poi> VisiblePois { get; private set; } = [];
    public IReadOnlyList<Poi> FilteredPois { get; private set; } = [];
    public Dictionary<int, IReadOnlyList<string>> PoiCollectionNames { get; private set; } = new();
    public Dictionary<int, IReadOnlyList<int>> PoiCollectionMemberships { get; private set; } = new();
    public Dictionary<int, int> PoiCollectionIds { get; private set; } = new();
    public int? SelectedPoiId { get; private set; }
    public string? SelectedCollectionName { get; private set; }
    public Poi? SelectedPoi { get; private set; }
    public bool IsLoading { get; private set; } = true;
    public string? PreviousSearchQuery { get; private set; }
    public MapBounds? CurrentBounds { get; private set; }
    public int TableHeight { get; private set; } = 256;
    public int SidebarWidth { get; private set; } = 240;
    public bool PendingSearchMapUpdate { get; private set; }

    /// <summary>When true, every map marker shows a permanent POI-name label.</summary>
    public bool ShowPoiLabels { get; private set; }

    private int? _pendingEnrichPoiId;
    public Poi? EnrichFallbackPoi { get; private set; }

    public async Task InitializeAsync()
    {
        Collections = await poiService.GetCollectionsAsync();
        CollectionStates = Collections.Select(c => new CollectionDisplayState(c)).ToList();
        IsLoading = false;

        _enrichmentSubscription = enrichmentProgress.Changes
            .Skip(1)
            .Subscribe(remaining => OnEnrichmentChanged(remaining));

        Notify();
    }

    /// <summary>Call after map WaitForInitAsync so the VM can interact with the live JS instance.</summary>
    public void AttachMap(LeafletMap map) => _map = map;

    /// <summary>Triggers initial map population after splitter is wired.</summary>
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

    /// <summary>Reads URI from NavigationManager and handles ?search=… queries.</summary>
    public async Task OnNavigationChangedAsync()
    {
        var uri = navigation.ToAbsoluteUri(navigation.Uri);
        if (QueryHelpers.ParseQuery(uri.Query).TryGetValue("search", out var searchValues))
        {
            var search = searchValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(search))
            {
                if (search == PreviousSearchQuery)
                {
                    return;
                }

                PreviousSearchQuery = search;
                VisiblePois = await poiService.SearchAsync(search);
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

    /// <summary>Updates map when PendingSearchMapUpdate is true and map is ready.</summary>
    public async Task ResolvePendingSearchMapUpdateAsync()
    {
        PendingSearchMapUpdate = false;
        if (PreviousSearchQuery != null)
        {
            await ShowSearchResultsOnMapAsync();
        }
        else
        {
            if (_map is not null)
            {
                await _map.HideCollectionAsync(SearchLayerId);
            }

            await LoadVisibleCollectionsAsync();
        }
    }

    private void OnEnrichmentChanged(int remaining)
    {
        // Rx fires from a background thread; LoadVisibleCollectionsAsync makes JS interop calls
        // which require Blazor Server's renderer dispatcher — marshal via host component's InvokeAsync.
        _ = RendererDispatch(async () =>
        {
            try
            {
                await LoadVisibleCollectionsAsync();
                if (SelectedPoiId is { } selectedId)
                {
                    SelectedPoi = await poiService.GetPoiAsync(selectedId);
                }
                if (remaining == 0 && _pendingEnrichPoiId is { } pendingId)
                {
                    _pendingEnrichPoiId = null;
                    await CheckEnrichOutcomeAsync(pendingId);
                }
                Notify();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Enrichment refresh failed");
            }
        });
    }

    private async Task CheckEnrichOutcomeAsync(int poiId)
    {
        var fresh = await poiService.GetPoiAsync(poiId);
        if (fresh is null)
        {
            return;
        }

        // Offer manual URL entry if enrichment did not land on a canonical Google place.
        // Makes enrich button idempotent: press again to re-search, fallback dialog if still unresolved.
        var hasCanonicalPlace = !string.IsNullOrEmpty(fresh.GoogleMapsUrl)
                                && fresh.GoogleMapsUrl.Contains("/maps/place/", StringComparison.OrdinalIgnoreCase);
        if (fresh.EnrichmentNeedsManualUrl || !hasCanonicalPlace)
        {
            EnrichFallbackPoi = fresh;
        }
    }

    public void CloseEnrichFallback()
    {
        EnrichFallbackPoi = null;
        Notify();
    }

    /// <summary>Opens manual-URL dialog, skipping automatic enrichment for POIs with hopeless name searches.</summary>
    public async Task OpenManualEnrichAsync(int poiId)
    {
        var poi = await poiService.GetPoiAsync(poiId);
        if (poi is null)
        {
            return;
        }

        EnrichFallbackPoi = poi;
        Notify();
    }

    public async Task SubmitEnrichFallbackAsync(string googleMapsUrl)
    {
        if (EnrichFallbackPoi is null)
        {
            return;
        }
        var poiId = EnrichFallbackPoi.Id;
        await poiService.ReplacePoiGoogleMapsUrlAsync(poiId, googleMapsUrl);
        EnrichFallbackPoi = null;
        _pendingEnrichPoiId = poiId;
        enrichmentTrigger.Signal();
        Notify();
    }

    [JSInvokable]
    public Task OnSplitterResizedJs(int height)
    {
        TableHeight = height;
        Notify();
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnSidebarResizedJs(int width)
    {
        SidebarWidth = width;
        Notify();
        return Task.CompletedTask;
    }

    private async Task ShowSearchResultsOnMapAsync()
    {
        if (_map == null)
        {
            return;
        }

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
        if (_map is not { IsInitialized: true })
        {
            return;
        }

        await _map.HideCollectionAsync(SearchLayerId);

        var grouped = await poiService.GetVisiblePoisGroupedAsync();
        // POI can belong to multiple collections; dedup by Id while flattening to avoid duplicate 'tr' keys.
        var seen = new HashSet<int>();
        var newPois = new List<Poi>();
        foreach (var s in CollectionStates)
        {
            if (s.IsVisible && grouped.ContainsKey(s.Id))
            {
                await _map.ShowCollectionAsync(s.Id, grouped[s.Id], s.Color);
                newPois.AddRange(grouped[s.Id].Where(poi => seen.Add(poi.Id)));
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

    public async Task HandleVisibilityToggledAsync(int collectionId)
    {
        var s = CollectionStates.FirstOrDefault(c => c.Id == collectionId);
        if (s is null)
        {
            return;
        }

        await poiService.ToggleVisibilityAsync(collectionId);
        s.IsVisible = !s.IsVisible;
        await LoadVisibleCollectionsAsync();
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
        if (_map != null && SelectedPoi is { Latitude: not null, Longitude: not null })
        {
            await _map.FocusOnPoiAsync(SelectedPoi.Latitude.Value, SelectedPoi.Longitude.Value);
        }
    }

    private async Task SelectPoiAsync(int poiId)
    {
        SelectedPoiId = poiId;
        SelectedPoi = await poiService.GetPoiAsync(poiId);
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
            FilteredPois = VisiblePois;
            Notify();
            await _map.FitBoundsAsync();
        }
    }

    public async Task TogglePoiLabelsAsync()
    {
        ShowPoiLabels = !ShowPoiLabels;
        Notify();
        if (_map is { IsInitialized: true })
        {
            await _map.SetLabelsVisibleAsync(ShowPoiLabels);
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
                .Where(p => p is { Latitude: not null, Longitude: not null }
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
        navigation.NavigateTo("/", replace: true);
    }

    /// <summary>
    /// Renames a POI. Used by the detail pane's inline name editor and its
    /// "use the Google Maps name" action. No-ops on a blank name or when the
    /// name is unchanged. Refreshes the map (markers/labels) and the table so
    /// the new name shows everywhere.
    /// </summary>
    public async Task HandleRenamePoiAsync((int PoiId, string NewName) args)
    {
        var newName = args.NewName?.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        var poi = await poiService.GetPoiAsync(args.PoiId);
        if (poi is null || poi.Name == newName)
        {
            return;
        }

        poi.Name = newName;
        await poiService.UpdatePoiAsync(poi);

        await RefreshAfterMutationAsync();
        if (SelectedPoiId == args.PoiId)
        {
            SelectedPoi = await poiService.GetPoiAsync(args.PoiId);
        }
        Notify();
    }

    public async Task HandleEnrichPoiAsync(int poiId)
    {
        await poiService.MarkPoiForReEnrichmentAsync(poiId);
        _pendingEnrichPoiId = poiId;
        enrichmentTrigger.Signal();
    }

    public async Task HandleDeletePoiAsync(int poiId)
    {
        if (PoiCollectionIds.TryGetValue(poiId, out var collectionId))
        {
            await poiService.RemovePoiFromCollectionAsync(poiId, collectionId);
        }
        if (SelectedPoiId == poiId)
        {
            CloseDetailPane();
        }

        await RefreshAfterMutationAsync();
    }

    public async Task HandleSetMembershipsAsync((int PoiId, IReadOnlyList<int> CollectionIds, string? NewCollectionName) args)
    {
        var target = await ResolveTargetCollectionsAsync(args.CollectionIds, args.NewCollectionName);
        if (target.Count == 0)
        {
            return; // never leave a POI orphaned (which would delete it)
        }

        await ApplyMembershipAsync(args.PoiId, target);
        await RefreshAfterMutationAsync();
    }

    public async Task HandleBatchSetMembershipsAsync((IReadOnlyList<int> PoiIds, IReadOnlyList<int> CollectionIds, string? NewCollectionName) args)
    {
        var target = await ResolveTargetCollectionsAsync(args.CollectionIds, args.NewCollectionName);
        if (target.Count == 0)
        {
            return;
        }

        foreach (var poiId in args.PoiIds)
        {
            await ApplyMembershipAsync(poiId, target);
        }
        await RefreshAfterMutationAsync();
    }

    private async Task<HashSet<int>> ResolveTargetCollectionsAsync(IReadOnlyList<int> collectionIds, string? newCollectionName)
    {
        var target = new HashSet<int>(collectionIds);
        if (!string.IsNullOrWhiteSpace(newCollectionName))
        {
            var newCol = await poiService.CreateCollectionAsync(newCollectionName.Trim());
            target.Add(newCol.Id);
            CollectionStates.Insert(0, new CollectionDisplayState(newCol));
        }
        return target;
    }

    // Add before remove to avoid transient orphan that RemovePoiFromCollectionAsync would delete.
    private async Task ApplyMembershipAsync(int poiId, HashSet<int> target)
    {
        var current = PoiCollectionMemberships.TryGetValue(poiId, out var m)
            ? new HashSet<int>(m)
            : new HashSet<int>();

        foreach (var add in target.Where(id => !current.Contains(id)))
        {
            await poiService.AddPoiToCollectionAsync(poiId, add);
        }
        foreach (var remove in current.Where(id => !target.Contains(id)))
        {
            await poiService.RemovePoiFromCollectionAsync(poiId, remove);
        }
    }

    public async Task HandleCopyPoiAsync((int PoiId, int TargetCollectionId) args)
    {
        await poiService.AddPoiToCollectionAsync(args.PoiId, args.TargetCollectionId);
        await RefreshAfterMutationAsync();
    }

    public async Task HandleCopyToNewCollectionAsync((int PoiId, string NewCollectionName) args)
    {
        var newCol = await poiService.CreateCollectionAsync(args.NewCollectionName);
        await poiService.AddPoiToCollectionAsync(args.PoiId, newCol.Id);
        CollectionStates.Insert(0, new CollectionDisplayState(newCol));
        await RefreshAfterMutationAsync();
    }

    public async Task HandleBatchDeleteAsync(IReadOnlyList<int> poiIds)
    {
        foreach (var poiId in poiIds)
        {
            if (PoiCollectionIds.TryGetValue(poiId, out var collectionId))
            {
                await poiService.RemovePoiFromCollectionAsync(poiId, collectionId);
            }
            if (SelectedPoiId == poiId)
            {
                CloseDetailPane();
            }
        }
        await RefreshAfterMutationAsync();
    }

    public async Task HandleBatchCopyAsync((IReadOnlyList<int> PoiIds, int TargetCollectionId) args)
    {
        foreach (var poiId in args.PoiIds)
        {
            await poiService.AddPoiToCollectionAsync(poiId, args.TargetCollectionId);
        }
        await RefreshAfterMutationAsync();
    }

    public async Task HandleBatchCopyToNewCollectionAsync((IReadOnlyList<int> PoiIds, string NewCollectionName) args)
    {
        var newCol = await poiService.CreateCollectionAsync(args.NewCollectionName);
        foreach (var poiId in args.PoiIds)
        {
            await poiService.AddPoiToCollectionAsync(poiId, newCol.Id);
        }
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

        PoiCollectionNames = (await poiService.GetPoiCollectionNamesAsync(poiIds))
            .ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<string>)kvp.Value);

        var memberships = await poiService.GetPoiCollectionMembershipsAsync(poiIds);
        PoiCollectionMemberships = memberships
            .ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<int>)kvp.Value);

        // Prefer visible membership for icon colour; fall back to first membership if none visible.
        var visibleIds = CollectionStates.Where(c => c.IsVisible).Select(c => c.Id).ToHashSet();
        PoiCollectionIds = memberships
            .Where(kvp => kvp.Value.Count > 0)
            .ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.FirstOrDefault(visibleIds.Contains, kvp.Value[0]));
    }

    private async Task RefreshAfterMutationAsync()
    {
        Collections = await poiService.GetCollectionsAsync();
        foreach (var col in Collections)
        {
            var s = CollectionStates.FirstOrDefault(c => c.Id == col.Id);
            s?.Collection.PoiCount = col.PoiCount;
        }
        await LoadVisibleCollectionsAsync();

        if (MembershipChanged is not null)
        {
            await MembershipChanged();
        }
    }

    public ValueTask DisposeAsync()
    {
        _enrichmentSubscription?.Dispose();
        _enrichmentSubscription = null;
        return ValueTask.CompletedTask;
    }
}
