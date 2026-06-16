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
    /// Set by the host component to its <c>InvokeAsync</c> so the VM can
    /// marshal background-thread callbacks (Rx, JS interop) onto the
    /// renderer's synchronization context. JS interop calls fail outside
    /// the renderer thread in Blazor Server; this hook is mandatory before
    /// <see cref="InitializeAsync"/> is invoked.
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
    /// The single collection a Trip can be scoped to: the lone visible collection
    /// when exactly one is visible and no search is active; otherwise null
    /// (per-collection Trip state is only coherent against one collection).
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
    /// Placeable (lat+lon present) POIs in the single visible collection's FULL
    /// membership — viewport-INDEPENDENT — and drives the Trip toggle's ≥1 gate.
    /// Trip View shows the whole collection regardless of map pan/zoom, so the
    /// gate must too: a pan that empties the viewport never hides the toggle.
    /// Zero when no single collection is in scope (multi-collection or search) so
    /// the gate stays closed exactly as before. <see cref="VisiblePois"/> is the
    /// lone visible collection's members when <see cref="SingleVisibleCollectionId"/>
    /// is set; <see cref="FilteredPois"/> is its viewport subset.
    /// </summary>
    public int CollectionPlaceablePoiCount =>
        SingleVisibleCollectionId is null
            ? 0
            : VisiblePois.Count(p => p is { Latitude: not null, Longitude: not null });

    // --- State ---

    public IReadOnlyList<PoiCollection> Collections { get; private set; } = [];
    public List<CollectionDisplayState> CollectionStates { get; private set; } = [];
    public IReadOnlyList<Poi> VisiblePois { get; private set; } = [];
    public IReadOnlyList<Poi> FilteredPois { get; private set; } = [];
    public Dictionary<int, IReadOnlyList<string>> PoiCollectionNames { get; private set; } = new();
    public Dictionary<int, IReadOnlyList<int>> PoiCollectionMemberships { get; private set; } = new();
    public Dictionary<int, int> PoiCollectionIds { get; private set; } = new();
    public int? SelectedPoiId { get; private set; }
    // Only set while a search is active ("Search: <query>"); shown as a chip
    // override in PoiTable. Collection visibility no longer sets it — the list
    // is the union of all visible collections, never a single selected one.
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

    // Set when the user clicks Enrich; cleared after the BG queue drains and
    // we either confirm success or open the fallback dialog. Used to scope
    // the post-enrichment "did it work?" check to the POI the user asked about.
    private int? _pendingEnrichPoiId;
    public Poi? EnrichFallbackPoi { get; private set; }

    // --- Lifecycle ---

    public async Task InitializeAsync()
    {
        Collections = await poiService.GetCollectionsAsync();
        CollectionStates = Collections.Select(c => new CollectionDisplayState(c)).ToList();
        IsLoading = false;

        // Refresh the map + list when the background enrichment service
        // flips Pois from placeholder (0,0) coords to real ones. Without
        // this, the user has to toggle collection visibility to see new
        // rows show up.
        _enrichmentSubscription = enrichmentProgress.Changes
            .Skip(1)
            .Subscribe(remaining => OnEnrichmentChanged(remaining));

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
            {
                await _map.HideCollectionAsync(SearchLayerId);
            }

            await LoadVisibleCollectionsAsync();
        }
    }

    private void OnEnrichmentChanged(int remaining)
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

        // Offer manual URL entry whenever the (re-)enrichment the user just
        // triggered did not land on a canonical Google place — either the BG
        // service flagged it, or there is still no /maps/place/ URL on the row.
        // This makes the enrich button idempotent: press it again to re-search,
        // and if the place still can't be resolved you get the manual-URL
        // dialog (this also fires for POIs that already had an address, which
        // the old EnrichmentNeedsManualUrl-only check skipped).
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

    /// <summary>
    /// Opens the manual-URL dialog directly, skipping the automatic enrichment
    /// pass. Lets the user paste a Google Maps place URL for a POI whose name
    /// search is hopeless (ambiguous results, no online presence, etc.) without
    /// first waiting for the background scraper to fail.
    /// </summary>
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

    // --- Map population ---

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

    // --- Commands (UI events) ---

    public async Task HandleVisibilityToggledAsync(int collectionId)
    {
        var s = CollectionStates.FirstOrDefault(c => c.Id == collectionId);
        if (s is null)
        {
            return;
        }

        // Persist first; only flip the UI flag once the database
        // confirms. If the call throws the toggle stays where it was
        // and the exception bubbles to the calling event handler.
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
            // Show all immediately; the moveend after fitBounds will
            // re-apply the viewport filter with the new (fitted) bounds,
            // which will naturally include every visible POI.
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

    // Set a POI's collection membership to exactly the chosen set (the membership
    // editor behind the row "Move" action), optionally creating and including a
    // new collection. Adds before removing: removing the POI's last membership
    // first would orphan it, and RemovePoiFromCollectionAsync deletes orphans.
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

    // Batch membership editor: set every selected POI's collections to the same
    // chosen set (checked adds to all, unchecked removes from all).
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

    // Resolve the requested collection ids plus an optional new collection into
    // the final target set, creating the new collection and surfacing it in the
    // sidebar when requested.
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

    // Diff the POI's current membership against the target: add the missing
    // collections first, then remove the extras (add-before-remove avoids a
    // transient orphan that RemovePoiFromCollectionAsync would delete).
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

    // --- Batch commands (act on a set of selected POIs, then refresh once) ---

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

        // A POI can belong to several collections. The list row is shown under —
        // and its marker drawn with the colour of — the *visible* collection it
        // appears in (see LoadVisibleCollectionsAsync). So when resolving the
        // single "owning" collection for icon colour and single-collection
        // delete, prefer a currently-visible membership; only fall back to the
        // first membership when none of the POI's collections are visible.
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

        // Let the Trip layer append/re-compact its Stop Order now that the
        // membership (and the filtered set) reflects the mutation.
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
