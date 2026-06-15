using System.Collections.ObjectModel;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Components.Shared.Trip;

/// <summary>
/// View-side state for the Trip View toggle and Stop Order badges. Owns the
/// per-collection Trip View on/off flag and the active collection's Stop Order
/// projection; all order mutation is delegated to <see cref="ITripOrderingService"/>
/// (the single <c>OrderIndex</c> write-path). Mirrors the MapPageViewModel
/// conventions: sealed, primary-constructor DI, registered Transient,
/// <see cref="StateChanged"/> + private <see cref="Notify"/>, state with
/// <c>private set</c>, owns a <see cref="CancellationTokenSource"/>,
/// <see cref="IAsyncDisposable"/>.
///
/// "Active collection" = the single collection a Trip is scoped to. The map page
/// passes it (the lone visible collection); per-collection persistence
/// (TripViewEnabled + OrderIndex) is only coherent against one collection, so the
/// toggle is unavailable unless exactly one collection is in scope.
/// </summary>
public sealed class TripViewModel(
    ITripOrderingService ordering,
    IDbContextFactory<AppDbContext> factory,
    SqliteWriteLock writeLock,
    TravelTimeTrigger travelTimeTrigger,
    TravelTimeProgressService travelTimeProgress,
    IRouteSegmentInvalidationService routeSegmentInvalidation,
    ILogger<TripViewModel> logger,
    // TRIP-OSRM-02 (Story 4.2, AC4): the active travel-time provider, read ONLY for its
    // declared routing Attribution. OPTIONAL with a null default so the parameterless
    // construction paths (unit/component tests that compose the VM by hand) keep working
    // unchanged — DI still injects the registered provider in the app and the integration
    // host (AddTripServices registers the haversine Mock, which declares null
    // attribution). A null provider here simply means no routing attribution is surfaced.
    ITravelTimeProvider? travelTimeProvider = null) : IAsyncDisposable
{
    /// <summary>
    /// TRIP-OSRM-02 (Story 4.2, AC4): the active travel-time provider's routing-data
    /// attribution HTML, or <c>null</c> when the provider's data is not licence-bound
    /// (the haversine Mock) or no provider was supplied. The page pushes it to Leaflet's
    /// attribution control once after map init so an OSM-based routing provider (OSRM)
    /// surfaces its OSM/ODbL obligation on top of the base tile attribution (NFR8); under
    /// the default Mock it is null ⇒ nothing is added. Surfaced on the VM (rather than the
    /// page sniffing the provider/config) to keep the Component → ViewModel → Service
    /// layering, and read off <see cref="ITravelTimeProvider.Attribution"/> — the data
    /// source declares its own licence.
    /// </summary>
    public string? RoutingAttributionHtml => travelTimeProvider?.Attribution;

    private static readonly IReadOnlyDictionary<int, int> NoStops =
        new ReadOnlyDictionary<int, int>(new Dictionary<int, int>());

    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    // TRIP-TRAVELTIME-01: subscription to the background compute progress. When
    // the pending count changes (a leg just landed in the cache), re-read the
    // projections off the circuit thread and Notify — never poll, never block.
    private IDisposable? _progressSubscription;

    public event Action? StateChanged;

    private void Notify() => StateChanged?.Invoke();

    // --- State ---

    /// <summary>The collection a Trip is scoped to, or null when none is in scope.</summary>
    public int? ActiveCollectionId { get; private set; }

    /// <summary>Number of placeable POIs in scope; drives the ≥2 availability gate.</summary>
    public int PlaceableCount { get; private set; }

    /// <summary>Whether Trip View is currently on for the active collection.</summary>
    public bool IsTripViewEnabled { get; private set; }

    /// <summary>
    /// The toggle is only offered when a single collection is in scope and it has
    /// at least two placeable POIs (UX-DR1 / FR-17). Below that it is absent — never
    /// an error or broken affordance (UX-DR10).
    /// </summary>
    public bool IsToggleAvailable => ActiveCollectionId is not null && PlaceableCount >= 2;

    /// <summary>
    /// <c>PoiId → Stop number</c> for the active collection when Trip View is on;
    /// empty when off. Drives the list and marker order badges.
    /// </summary>
    public IReadOnlyDictionary<int, int> StopOrders { get; private set; } = NoStops;

    /// <summary>
    /// Story 1.4 (FR-4): the SINGLE canonical Stop Order for the active collection
    /// as <c>PoiId → OrderIndex</c>, cached from the persisted entity order
    /// (<see cref="ITripOrderingService.GetStopOrderAsync"/>). Unlike
    /// <see cref="StopOrders"/> this is populated REGARDLESS of
    /// <see cref="IsTripViewEnabled"/> — the order lives on the entity whether or
    /// not Trip View is toggled on — so the plain Filtered Results list can render
    /// in the same sequence as the Trip list (no divergence). Empty when no single
    /// collection is in scope (<see cref="ActiveCollectionId"/> is null ⇒ AC3
    /// multi-collection) or the collection has no explicit order (AC3 never-ordered).
    /// Cached (refreshed only in the async load/refresh/reorder paths, not per
    /// render) because the plain list's source (<c>MapPageViewModel.FilteredPois</c>)
    /// recomputes on every viewport move; the per-render apply
    /// (<see cref="ApplyCanonicalOrder"/>) is a cheap pure sort against this cache.
    /// </summary>
    public IReadOnlyDictionary<int, int> CanonicalStopOrder { get; private set; } = NoStops;

    /// <summary>
    /// Story 1.4 (FR-4 / AC2/AC3, NFR1): pure, in-memory ordering of the plain
    /// Filtered Results list by the cached <see cref="CanonicalStopOrder"/>. The map
    /// page's OFF-state branch calls this so the plain list never disagrees with the
    /// Trip list about sequence. No DB access — it sorts the already-built
    /// <c>FilteredPois</c> against the cached order map.
    ///
    /// When <see cref="CanonicalStopOrder"/> is empty (no single in-scope collection,
    /// or a never-ordered collection) the input is returned UNCHANGED so the plain
    /// list keeps its normal default sort (AC3). Otherwise POIs that ARE in the order
    /// map come first, ascending by their OrderIndex; POIs NOT in the map (unplaceable
    /// / unordered) are kept stably AFTER them, preserving their incoming relative
    /// order — a single stable <c>OrderBy</c> keyed so non-members sort last.
    /// </summary>
    public IReadOnlyList<Poi> ApplyCanonicalOrder(IReadOnlyList<Poi> pois)
    {
        ArgumentNullException.ThrowIfNull(pois);

        if (CanonicalStopOrder.Count == 0)
        {
            return pois;
        }

        // LINQ OrderBy is a documented STABLE sort, so members that share no key
        // collisions keep their order by OrderIndex, and every non-member (keyed
        // int.MaxValue ⇒ sorts last) preserves its incoming relative position.
        return pois
            .OrderBy(p => CanonicalStopOrder.TryGetValue(p.Id, out var order) ? order : int.MaxValue)
            .ToList();
    }

    /// <summary>
    /// Ordered, placeable-only stop projection (1-based, ascending) for the
    /// stop-list panel and numbered markers; empty when Trip View is off.
    /// </summary>
    public IReadOnlyList<TripStop> OrderedStops { get; private set; } = [];

    /// <summary>
    /// Full-membership stop-list rows: every placeable stop (carrying its
    /// presented routed number, contiguous 1..M) followed by every unplaceable
    /// POI (no routed number — the "Not placeable" treatment). Unplaceable POIs
    /// are kept in the collection and in this list but excluded from
    /// <see cref="OrderedStops"/>/<see cref="OrderedLegs"/>/<see cref="StopOrders"/>
    /// (markers, legs, routing). Empty when Trip View is off.
    /// [TRIP-PLACE-04][TRIP-ORDER-UNPLACE-01]
    /// </summary>
    public IReadOnlyList<TripStopRow> StopRows { get; private set; } = [];

    /// <summary>
    /// Straight connecting legs between consecutive placeable stops, plus the
    /// closing leg back to the Start on a Roundtrip (no distinct Finish). Empty
    /// when Trip View is off or fewer than two placeable stops exist. Every leg
    /// is non-Measured in Phase 1 (TRIP-LEG-01).
    /// </summary>
    public IReadOnlyList<TripLeg> OrderedLegs { get; private set; } = [];

    /// <summary>
    /// TRIP-TRAVELTIME-01 (AC5): the trip's total travel time in seconds — the Σ
    /// of every leg's <see cref="TripLeg.DurationSeconds"/> — or <c>null</c> when
    /// any leg is uncomputed (no cache row yet). A null total renders as an
    /// em-dash so the UI never presents false precision over a partial trip.
    /// </summary>
    public int? TotalTravelTimeSeconds { get; private set; }

    /// <summary>
    /// TRIP-TRAVELTIME-01: true while at least one leg has no cache row yet, so
    /// the UI can show the per-leg / total computing state via aria-live.
    /// </summary>
    public bool IsAnyLegComputing { get; private set; }

    /// <summary>
    /// TRIP-DEGRADE-01 (Story 2.3, AC2): true when any current leg is backed by
    /// the provider-down straight-line fallback (its <see cref="RouteSegment.Source"/>
    /// is <see cref="TravelTimeSource.EstimatedFallback"/>). Drives the honest
    /// "couldn't reach the routing engine — showing straight-line estimates" note
    /// on both surfaces. Cleared automatically once no fallback legs remain (a
    /// later successful recompute replaces the row), via the existing refresh path
    /// — no polling. A normal Mock-Estimated leg does NOT trip this.
    /// </summary>
    public bool IsShowingApproximateEstimates => OrderedLegs.Any(l => l.IsFallback);

    /// <summary>
    /// TRIP-TIMELINE-01 (Story 2.6): the honest itinerary timeline — per-stop arrivals
    /// (relative offset always, wall-clock only with a start time), the finish/return
    /// readout, the whole-trip total (travel + every dwell) and the soft budget-overrun
    /// flag. Recomputed in both refresh paths from the already-loaded stops/dwell/legs +
    /// the collection's TripStartTime/TimeBudgetMinutes. <see cref="ItineraryTimelineResult.Empty"/>
    /// when Trip View is off or fewer than two placeable stops exist.
    /// </summary>
    public ItineraryTimelineResult Timeline { get; private set; } = ItineraryTimelineResult.Empty;

    /// <summary>
    /// TRIP-TIMELINE-01: the active collection's persisted wall-clock start time, or null
    /// ⇒ relative offsets only. Drives the header start-time input's active value.
    /// </summary>
    public DateTime? TripStartTime { get; private set; }

    /// <summary>
    /// TRIP-TIMELINE-01: the active collection's persisted soft time budget in minutes, or
    /// null ⇒ no overrun flag is ever shown. Drives the header budget input's active value.
    /// </summary>
    public int? TimeBudgetMinutes { get; private set; }

    /// <summary>Localized on/off text for the aria-live announcement region; null until first toggle.</summary>
    public string? Announcement { get; private set; }

    /// <summary>
    /// TRIP-TRAVELMODE-01: the active collection's persisted travel mode (one of
    /// <see cref="TravelMode"/>; defaults <see cref="TravelMode.AnyAir"/>). Drives
    /// the segmented selector's active segment and gates the per-leg manual entry
    /// (only shown under Any/Air). Cleared (back to AnyAir) when projections clear.
    /// </summary>
    public string TravelMode { get; private set; } = Data.Entities.TravelMode.AnyAir;

    // TRIP-SELECT-01: bidirectional list ↔ map selection. A single transient
    // (never persisted) selection that both surfaces read and write through
    // SelectStop. Independent of MapPageViewModel.SelectedPoiId (the non-trip
    // POI-detail selection) — turning Trip View off restores that untouched.

    /// <summary>The currently-selected Stop's PoiId, or null when nothing is selected.</summary>
    public int? SelectedStopPoiId { get; private set; }

    /// <summary>The selected Stop projection (name/order/coords), or null.</summary>
    public TripStop? SelectedStop { get; private set; }

    /// <summary>Which surface made the latest selection — drives pan (List) vs scroll (Map).</summary>
    public TripSelectionSource LastSelectionSource { get; private set; }

    /// <summary>
    /// Monotonic counter bumped on every <see cref="SelectStop"/> invocation — even an
    /// idempotent re-select of the already-selected Stop. Lets the host distinguish a
    /// genuine (re-)selection from unrelated <see cref="StateChanged"/> notifications, so
    /// re-tapping the current row can still re-pan its marker into view (TRIP-SELECT-03).
    /// </summary>
    public long SelectionTick { get; private set; }

    /// <summary>Localized "Selected stop N: name" text for the aria-live region; null until first select.</summary>
    public string? SelectionAnnouncement { get; private set; }

    /// <summary>
    /// Selects a Stop (or clears with null) and raises <see cref="StateChanged"/>
    /// so both the list row emphasis and the map marker emphasis/pan/scroll react
    /// through one path. Re-selecting the same Stop keeps it selected (idempotent);
    /// selecting a different Stop replaces the prior one, so at most one Stop is
    /// ever selected. <paramref name="source"/> records who initiated it.
    /// </summary>
    public void SelectStop(int? poiId, TripSelectionSource source = TripSelectionSource.List)
    {
        // A selection is only meaningful while Trip View is on; ignore otherwise
        // so a stray call can't leave a stale selection on a plain collection.
        if (!IsTripViewEnabled)
        {
            return;
        }

        // [TRIP-PLACE-04] Only placeable stops are selectable — an unplaceable
        // row has no marker to pan to, so a selection of it is meaningless.
        // (The row components don't wire selection on unplaceable rows; this
        // guard keeps the VM honest against any other caller.)
        if (poiId is { } requested && OrderedStops.All(s => s.PoiId != requested))
        {
            return;
        }

        SelectedStopPoiId = poiId;
        SelectedStop = poiId is { } id ? OrderedStops.FirstOrDefault(s => s.PoiId == id) : null;
        LastSelectionSource = source;
        // Bump even on an idempotent re-select so the host re-runs the directional
        // follow-up (re-pan a List selection whose marker the user has scrolled away).
        SelectionTick++;
        SelectionAnnouncement = SelectedStop is { } s
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                UiStrings.TripStopSelectedAnnouncement, s.OrderIndex, s.Name)
            : null;
        Notify();
    }

    // --- Stop reorder (Story 1.5) ---

    // TRIP-ORDER-03: drag and keyboard both surface here and delegate to the
    // single OrderIndex writer (ITripOrderingService.ReorderStopAsync, AR-11).
    // The VM never mutates OrderIndex itself; after a successful move it
    // re-reads the projections and raises StateChanged, which the host page
    // already turns into the incremental Story-1.3 leg redraw (no full reload).

    /// <summary>
    /// Localized "name moved to stop X of Y" text for the reorder aria-live
    /// region; null until the first successful move. Not set on no-op moves.
    /// </summary>
    public string? LastReorderAnnouncement { get; private set; }

    /// <summary>
    /// TRIP-TSP-01 (Story 3.1): localized "stops sorted into travel order" text for
    /// the aria-live region after a successful TSP-Sort; null until the first sort.
    /// </summary>
    public string? LastSortAnnouncement { get; private set; }

    /// <summary>
    /// Whether the Stop can move one position up. False on a pinned Start/Finish
    /// and on the topmost movable Stop (mirrors the service's interior window so
    /// the buttons disable instead of throwing; the service stays authoritative).
    /// </summary>
    public bool CanMoveUp(TripStop stop) =>
        !stop.IsStart && !stop.IsFinish && stop.OrderIndex > MinMovableOrder;

    /// <summary>
    /// Whether the Stop can move one position down. False on a pinned
    /// Start/Finish and on the bottommost movable Stop.
    /// </summary>
    public bool CanMoveDown(TripStop stop) =>
        !stop.IsStart && !stop.IsFinish && stop.OrderIndex < MaxMovableOrder;

    private int MinMovableOrder =>
        OrderedStops.Count > 0 && OrderedStops[0].IsStart ? 2 : 1;

    private int MaxMovableOrder =>
        OrderedStops.Count > 0 && OrderedStops[^1].IsFinish
            ? OrderedStops.Count - 1
            : OrderedStops.Count;

    /// <summary>Moves the Stop one position earlier in the order (keyboard path).</summary>
    public Task MoveStopUpAsync(int poiId) => MoveStopByAsync(poiId, -1);

    /// <summary>Moves the Stop one position later in the order (keyboard path).</summary>
    public Task MoveStopDownAsync(int poiId) => MoveStopByAsync(poiId, +1);

    private async Task MoveStopByAsync(int poiId, int delta)
    {
        var stop = OrderedStops.FirstOrDefault(s => s.PoiId == poiId);
        if (stop is null)
        {
            return;
        }

        await MoveStopToAsync(poiId, stop.OrderIndex + delta);
    }

    /// <summary>
    /// Moves the Stop to the target 1-based slot (drag path; also backs the
    /// one-step keyboard moves). Delegates to the single OrderIndex writer,
    /// which clamps into the pin-aware movable window and short-circuits no-ops
    /// without a DB write. On a successful move, refreshes the projections,
    /// sets <see cref="LastReorderAnnouncement"/> and raises
    /// <see cref="StateChanged"/> (host redraws legs incrementally).
    /// </summary>
    public async Task MoveStopToAsync(int poiId, int targetOrderIndex)
    {
        if (ActiveCollectionId is not { } collectionId || !IsTripViewEnabled)
        {
            return;
        }

        var before = OrderedStops.FirstOrDefault(s => s.PoiId == poiId);
        if (before is null)
        {
            return;
        }

        try
        {
            await ordering.ReorderStopAsync(collectionId, poiId, targetOrderIndex, _cts.Token);
            await RefreshProjectionsAsync(collectionId);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reorder stop {PoiId} in collection {CollectionId}", poiId, collectionId);
            return;
        }

        var after = OrderedStops.FirstOrDefault(s => s.PoiId == poiId);
        if (after is not null && after.OrderIndex != before.OrderIndex)
        {
            // Announce only genuine moves — a clamped/own-position no-op stays
            // silent so the live region never reports a move that didn't happen.
            LastReorderAnnouncement = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                UiStrings.TripStopMovedAnnouncement,
                after.Name, after.OrderIndex, OrderedStops.Count);
        }

        Notify();
    }

    // --- Start/Finish designation (Story 1.7) ---

    // TRIP-STARTFINISH-01: the four designation intents delegate to the single
    // ordering write path (ITripOrderingService.Set/Clear Start/Finish, AR-11) —
    // the VM never writes StartPoiId/FinishPoiId or OrderIndex itself. After a
    // successful change it re-reads the projections (stops, rows, legs — the
    // closing-leg presence recomputes in BuildLegs) and raises StateChanged, which
    // the host page turns into the existing incremental Story-1.3 redraw.

    /// <summary>The PoiId pinned as Start (Order 1), or null. Read in RefreshProjections.</summary>
    public int? StartPoiId { get; private set; }

    /// <summary>The PoiId pinned as Finish (Order N), or null ⇒ Roundtrip.</summary>
    public int? FinishPoiId { get; private set; }

    /// <summary>
    /// Roundtrip is the default Trip shape: no distinct Finish ⇒ the closing leg
    /// returns from Order N to the Start (N legs). A distinct Finish opens the
    /// path (N−1 legs).
    /// </summary>
    public bool IsRoundtrip => FinishPoiId is null;

    /// <summary>Per-stop Start/Finish role — both surfaces pick badge/marker glyphs from this.</summary>
    public TripStopRole StopRole(int poiId) =>
        poiId == StartPoiId ? TripStopRole.Start
        : poiId == FinishPoiId ? TripStopRole.Finish
        : TripStopRole.None;

    /// <summary>
    /// Whether the Stop may be designated Start. False on the current Finish —
    /// a stop cannot be both (AC-6 rejection surfaced as a disabled control).
    /// </summary>
    public bool CanSetStart(int poiId) => poiId != FinishPoiId;

    /// <summary>Whether the Stop may be designated Finish. False on the current Start.</summary>
    public bool CanSetFinish(int poiId) => poiId != StartPoiId;

    /// <summary>
    /// Localized designation/shape announcement for the aria-live region
    /// ("X set as start", "Open path — ends at X", "Roundtrip — returns to
    /// start"); null until the first designation change.
    /// </summary>
    public string? StartFinishAnnouncement { get; private set; }

    /// <summary>Designates the Stop as Start (pinned to Order 1).</summary>
    public Task SetStartAsync(int poiId) => ChangePinAsync(poiId, setStart: true);

    /// <summary>Designates the Stop as Finish (pinned to Order N — open path).</summary>
    public Task SetFinishAsync(int poiId) => ChangePinAsync(poiId, setStart: false);

    /// <summary>Clears the Start designation (order stays contiguous, no pinned first).</summary>
    public Task ClearStartAsync() => ChangePinAsync(poiId: null, setStart: true);

    /// <summary>Clears the Finish designation — the Trip returns to a Roundtrip.</summary>
    public Task ClearFinishAsync() => ChangePinAsync(poiId: null, setStart: false);

    private async Task ChangePinAsync(int? poiId, bool setStart)
    {
        if (ActiveCollectionId is not { } collectionId || !IsTripViewEnabled)
        {
            return;
        }

        // Designating: the target must be a placeable, ordered Stop (unplaceable
        // POIs hold OrderIndex 0 and are excluded from routing — never a
        // Start/Finish candidate) and must not be the opposite endpoint.
        TripStop? stop = null;
        if (poiId is { } id)
        {
            stop = OrderedStops.FirstOrDefault(s => s.PoiId == id);
            if (stop is null || (setStart ? !CanSetStart(id) : !CanSetFinish(id)))
            {
                return;
            }
        }
        else if ((setStart ? StartPoiId : FinishPoiId) is null)
        {
            // Clearing an already-clear pin — nothing to do, no announcement.
            return;
        }

        try
        {
            var task = (poiId, setStart) switch
            {
                ({ } pin, true) => ordering.SetStartAsync(collectionId, pin, _cts.Token),
                ({ } pin, false) => ordering.SetFinishAsync(collectionId, pin, _cts.Token),
                (null, true) => ordering.ClearStartAsync(collectionId, _cts.Token),
                (null, false) => ordering.ClearFinishAsync(collectionId, _cts.Token),
            };
            await task;
            await RefreshProjectionsAsync(collectionId);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to change Start/Finish designation for collection {CollectionId}", collectionId);
            return;
        }

        StartFinishAnnouncement = (stop, setStart) switch
        {
            ({ } s, true) => string.Format(System.Globalization.CultureInfo.CurrentCulture,
                UiStrings.TripStartSetAnnouncement, s.Name),
            ({ } s, false) => string.Format(System.Globalization.CultureInfo.CurrentCulture,
                UiStrings.TripOpenPathAnnounce, s.Name),
            (null, true) => UiStrings.TripStartClearedAnnouncement,
            (null, false) => UiStrings.TripRoundtripAnnounce,
        };
        Notify();
    }

    // --- Lifecycle / loading ---

    /// <summary>
    /// Sets the active-collection scope and restores persisted Trip View state.
    /// Called by the host page after collections load and whenever the visible
    /// set or its placeable count changes.
    /// </summary>
    public async Task LoadAsync(int? collectionId, int placeableCount)
    {
        ActiveCollectionId = collectionId;
        PlaceableCount = placeableCount;

        if (collectionId is null)
        {
            IsTripViewEnabled = false;
            ClearProjections();
            // Story 1.4 (AC3): no single collection in scope (e.g. multi-collection)
            // ⇒ no forced order on the plain list.
            CanonicalStopOrder = NoStops;
            Notify();
            return;
        }

        try
        {
            IsTripViewEnabled = await ReadTripViewEnabledAsync(collectionId.Value);
            // Story 1.4 (FR-4): the canonical order lives on the entity regardless
            // of the Trip View toggle, so cache it here for BOTH branches — when
            // Trip View is ON, RefreshProjectionsAsync refreshes it again (cheap,
            // idempotent); when OFF, this is the only populate point so the plain
            // list still renders in the persisted Stop Order (AC2).
            await RefreshCanonicalStopOrderAsync();
            if (IsTripViewEnabled)
            {
                // Reopening a Trip-enabled collection: heal any Stop Order drift
                // from membership changes made while it was last viewed/off, so the
                // restored badges are contiguous and cover every placeable POI.
                // Idempotent — only writes when the order actually changed.
                // [Review][Patch]
                await ordering.ReconcileOrderAsync(collectionId.Value, _cts.Token);
                await RefreshProjectionsAsync(collectionId.Value);
            }
            else
            {
                ClearProjections();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load Trip View state for collection {CollectionId}", collectionId);
            IsTripViewEnabled = false;
            ClearProjections();
        }

        Notify();
    }

    /// <summary>
    /// Updates only the placeable count (e.g. as the viewport filter changes)
    /// without re-reading persisted state. Re-evaluates the availability gate.
    /// Does NOT auto-disable Trip View: the count fed here is the transient
    /// visible/filtered count (0 mid-load, dropping when a search narrows the
    /// set), so acting on a dip would persist Trip View off on a reopen or
    /// search. The genuine "content dropped below the gate" signal is a
    /// membership change — see <see cref="RefreshAfterMembershipChangeAsync"/>.
    /// </summary>
    public void UpdatePlaceableCount(int placeableCount)
    {
        if (PlaceableCount == placeableCount)
        {
            return;
        }

        PlaceableCount = placeableCount;
        Notify();
    }

    /// <summary>
    /// [TRIP-GATE-01] When the active collection falls below the ≥2-placeable
    /// availability gate (UX-DR1) while Trip View is on, turn Trip View off and
    /// persist the flag — otherwise the overlays (badges, legs, Start/Finish
    /// controls) would strand on a sub-trip with the toggle itself gone, leaving
    /// no way to dismiss them. Returns true when it disabled the view. Caller
    /// raises <see cref="StateChanged"/>.
    /// </summary>
    private async Task<bool> AutoDisableBelowGateAsync()
    {
        if (!IsTripViewEnabled || ActiveCollectionId is not { } collectionId || PlaceableCount >= 2)
        {
            return false;
        }

        try
        {
            await PersistTripViewEnabledAsync(collectionId, enabled: false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist Trip View off below the placeable gate for collection {CollectionId}", collectionId);
            // Fall through: still drop the in-memory overlays so nothing is
            // stranded; the next LoadAsync re-applies the gate against the flag.
        }

        IsTripViewEnabled = false;
        ClearProjections();
        Announcement = UiStrings.TripViewAutoDisabledAnnouncement;
        return true;
    }

    /// <summary>
    /// Flips Trip View on/off for the active collection and persists the flag.
    /// First enable of a never-ordered collection seeds the Stop Order. No-op
    /// when the toggle is not available (below the ≥2 placeable gate).
    /// </summary>
    public async Task ToggleAsync()
    {
        if (ActiveCollectionId is not { } collectionId || !IsToggleAvailable)
        {
            return;
        }

        var enable = !IsTripViewEnabled;

        try
        {
            if (enable)
            {
                if (!await ordering.HasOrderAsync(collectionId, _cts.Token))
                {
                    await ordering.SeedOrderAsync(collectionId, _cts.Token);
                }
                else
                {
                    // Already seeded in a prior session — heal any drift from
                    // membership changes made while Trip View was off (removals
                    // leave gaps, new placeable POIs have no order) before we
                    // surface the badges. Idempotent: writes only if changed.
                    // [Review][Patch]
                    await ordering.ReconcileOrderAsync(collectionId, _cts.Token);
                }
            }

            await PersistTripViewEnabledAsync(collectionId, enable);
            IsTripViewEnabled = enable;
            if (enable)
            {
                await RefreshProjectionsAsync(collectionId);
            }
            else
            {
                ClearProjections();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to toggle Trip View for collection {CollectionId}", collectionId);
            return;
        }

        Announcement = enable
            ? UiStrings.TripViewEnabledAnnouncement
            : UiStrings.TripViewDisabledAnnouncement;
        Notify();
    }

    /// <summary>
    /// Called by the host page after a membership mutation (POI added/removed).
    /// When Trip View is on for the active collection, reconciles the Stop Order
    /// (append new placeable Stops, re-compact after removals) and refreshes the
    /// badge projection.
    /// </summary>
    public async Task RefreshAfterMembershipChangeAsync(int placeableCount)
    {
        PlaceableCount = placeableCount;

        if (ActiveCollectionId is not { } collectionId || !IsTripViewEnabled)
        {
            // FR-4: while Trip View is OFF, no reorder/reconcile runs, so surviving
            // members' OrderIndex values don't change — CanonicalStopOrder stays valid
            // (a removed POI's stale map entry simply won't appear in FilteredPois, and
            // an added POI gets OrderIndex 0 ⇒ sorts after as a non-member). If a future
            // change ever reconciles order while OFF, refresh the canonical cache here.
            Notify();
            return;
        }

        // [TRIP-GATE-01] A removal / un-enrichment that drops below the ≥2 gate
        // auto-disables Trip View rather than reconciling a sub-trip the user
        // can no longer toggle off.
        if (await AutoDisableBelowGateAsync())
        {
            Notify();
            return;
        }

        try
        {
            await ordering.ReconcileOrderAsync(collectionId, _cts.Token);
            await RefreshProjectionsAsync(collectionId);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reconcile Stop Order for collection {CollectionId}", collectionId);
        }

        Notify();
    }

    // --- Stop Order projections (badges + legs + panel rows) ---

    /// <summary>
    /// Story 1.4 (FR-4): refreshes the cached <see cref="CanonicalStopOrder"/> from
    /// the persisted entity order, INDEPENDENT of <see cref="IsTripViewEnabled"/>.
    /// Empty when no single collection is in scope (<see cref="ActiveCollectionId"/>
    /// is null) or the collection has no explicit order. Called from every async
    /// load/refresh/reorder path so the plain list's cached order tracks the entity.
    /// </summary>
    private async Task RefreshCanonicalStopOrderAsync()
    {
        if (ActiveCollectionId is not { } collectionId)
        {
            CanonicalStopOrder = NoStops;
            return;
        }

        var order = await ordering.GetStopOrderAsync(collectionId, _cts.Token);
        // GetStopOrderAsync returns only placeable, ordered items; an empty map
        // (never-ordered collection) leaves the plain list in its default sort (AC3).
        CanonicalStopOrder = order.Count == 0 ? NoStops : order;
    }

    private void ClearProjections()
    {
        StopOrders = NoStops;
        OrderedStops = [];
        StopRows = [];
        OrderedLegs = [];
        // TRIP-TRAVELTIME-01: travel-time totals are scoped to an enabled Trip.
        TotalTravelTimeSeconds = null;
        IsAnyLegComputing = false;
        // TRIP-TIMELINE-01 (Story 2.6): the timeline + its inputs are read state scoped to
        // an enabled Trip (the persisted TripStartTime/TimeBudgetMinutes are untouched) —
        // reset the surfaced values alongside the projections.
        Timeline = ItineraryTimelineResult.Empty;
        TripStartTime = null;
        TimeBudgetMinutes = null;
        // TRIP-TRAVELMODE-01: the surfaced mode is read state scoped to an enabled
        // Trip (the persisted PoiCollection.TravelMode is untouched) — reset the
        // selector's active segment to the default alongside the projections.
        TravelMode = Data.Entities.TravelMode.AnyAir;
        // TRIP-SELECT-01: selection is transient and only valid while Trip View
        // is on — drop it (and its announcement) whenever the projections clear
        // so toggling off never leaves a stale SelectedStopPoiId (AC4).
        SelectedStopPoiId = null;
        SelectedStop = null;
        SelectionAnnouncement = null;
        // TRIP-STARTFINISH-01: the pins are read state scoped to an enabled Trip
        // (the persisted StartPoiId/FinishPoiId are untouched) — clear the
        // surfaced values and the stale announcement alongside the projections.
        StartPoiId = null;
        FinishPoiId = null;
        StartFinishAnnouncement = null;
    }

    /// <summary>
    /// Reads the persisted Stop Order with coordinates in one pass and rebuilds
    /// the three projections (<see cref="StopOrders"/>, <see cref="OrderedStops"/>,
    /// <see cref="OrderedLegs"/>) consistently. Call only when Trip View is on.
    /// </summary>
    private async Task RefreshProjectionsAsync(int collectionId)
    {
        // TRIP-TRAVELTIME-01: subscribe once (lazily) to the background compute
        // progress so freshly-cached legs re-read without polling the circuit.
        EnsureProgressSubscription();

        // Story 1.4 (FR-4): refresh the shared canonical order cache on every
        // reorder/toggle/membership/designation refresh so the plain list (read via
        // ApplyCanonicalOrder) tracks the same sequence as the Trip list.
        await RefreshCanonicalStopOrderAsync();

        var (startPoiId, finishPoiId) = await ReadStartFinishAsync(collectionId);
        // TRIP-STARTFINISH-01: surface the pins so the UI can derive per-stop
        // roles (StopRole) and the Roundtrip/open-path shape (IsRoundtrip).
        StartPoiId = startPoiId;
        FinishPoiId = finishPoiId;
        var (stops, rows) = await ReadStopsAndRowsAsync(collectionId, startPoiId, finishPoiId);

        OrderedStops = stops;
        StopRows = rows;
        StopOrders = stops.Count == 0
            ? NoStops
            : stops.ToDictionary(s => s.PoiId, s => s.OrderIndex);

        // TRIP-TRAVELTIME-01: read the collection's persisted TravelMode and the
        // matching RouteSegment cache rows, then build the legs with whatever
        // duration/distance/fidelity has been computed so far. A leg with no
        // cache row keeps null fields ⇒ the UI shows "—" + computing.
        // TRIP-TIMELINE-01 (Story 2.6): read the persisted travel mode AND the timeline
        // inputs (TripStartTime / TimeBudgetMinutes) in one collection read alongside the
        // existing mode read — no extra DB round-trip for the recompute.
        var (travelMode, tripStartTime, budgetMinutes) = await ReadTripSettingsAsync(collectionId);
        // TRIP-TRAVELMODE-01: surface the persisted mode so the selector restores
        // its active segment and the per-leg manual entry gates on Any/Air.
        TravelMode = travelMode;
        TripStartTime = tripStartTime;
        TimeBudgetMinutes = budgetMinutes;
        var cache = await ReadRouteSegmentsAsync(collectionId, travelMode);
        OrderedLegs = BuildLegs(stops, finishPoiId, cache);
        RecomputeTotal();
        // TRIP-TIMELINE-01: recompute the honest timeline from the freshly-built stops/
        // dwell/legs + the persisted start/budget (presentation-only, no DB).
        RecomputeTimeline();

        // TRIP-TRAVELTIME-01: any uncomputed leg ⇒ kick the off-circuit compute.
        if (IsAnyLegComputing)
        {
            travelTimeTrigger.Signal();
        }

        // TRIP-SELECT-01: if the selected Stop was removed (membership change) it
        // is no longer a valid selection — clear it; otherwise refresh the cached
        // projection so SelectedStop tracks any rename/reorder.
        if (SelectedStopPoiId is { } selectedId)
        {
            SelectedStop = stops.FirstOrDefault(s => s.PoiId == selectedId);
            if (SelectedStop is null)
            {
                SelectedStopPoiId = null;
                SelectionAnnouncement = null;
            }
        }
    }

    /// <summary>
    /// Reads the active collection's FULL membership in one pass and splits it
    /// through the canonical <see cref="StopPlaceability"/> predicate
    /// ([TRIP-PLACE-01]) into:
    /// <list type="bullet">
    /// <item>the placeable, ordered stops (OrderIndex &gt; 0, both coordinates
    /// non-null) — the only inputs to markers, legs and routing
    /// ([TRIP-PLACE-02]/[TRIP-PLACE-03]); and</item>
    /// <item>the stop-list rows over everything: placeable rows first (with the
    /// presented routed number), then the unplaceable POIs (kept visible with
    /// the "Not placeable" treatment, never silently dropped — UX-DR10).</item>
    /// </list>
    /// [TRIP-ORDER-UNPLACE-01] Stored <c>OrderIndex</c> (written only by
    /// TripOrderingService over the placeable membership; unplaceable items hold
    /// 0 = "not a stop") is read, never written, here. The user-facing routed
    /// number is recomputed contiguously 1..M over the placeable subset so the
    /// presented badges can never show a gap, whatever the stored values are.
    /// </summary>
    private async Task<(IReadOnlyList<TripStop> Stops, IReadOnlyList<TripStopRow> Rows)> ReadStopsAndRowsAsync(
        int collectionId, int? startPoiId, int? finishPoiId)
    {
        await using var db = await factory.CreateDbContextAsync(_cts.Token);
        var members = await db.PoiCollectionItems
            .AsNoTracking()
            .Where(ci => ci.PoiCollectionId == collectionId)
            .Select(ci => new
            {
                ci.PoiId,
                ci.OrderIndex,
                ci.Poi.Name,
                ci.Poi.Latitude,
                ci.Poi.Longitude,
                ci.Poi.AddedDate,
                // TRIP-DWELL-01 (Story 2.5): carry the per-membership dwell minutes.
                ci.DwellMinutes,
                // Story 1.2 (FR-2): the Name-column + Actions presentation fields,
                // read from the already-loaded Poi (no extra round-trip, no new ctor
                // dependency). Address backs the sub-line; the enrichment flags pick
                // the state icon; GoogleMapsUrl/Category feed PoiUrlHelper below.
                ci.Poi.Address,
                ci.Poi.IsEnriched,
                ci.Poi.EnrichmentNeedsManualUrl,
                ci.Poi.GoogleMapsUrl,
                ci.Poi.Category,
            })
            .ToListAsync(_cts.Token);

        var stops = members
            .Where(r => r.OrderIndex > 0 && StopPlaceability.IsPlaceable(r.Latitude, r.Longitude))
            .OrderBy(r => r.OrderIndex)
            // Presented routed number = position in the placeable subset (1..M),
            // independent of the stored OrderIndex values. [TRIP-ORDER-UNPLACE-01]
            .Select((r, i) => new TripStop(
                i + 1,
                r.PoiId,
                r.Name,
                r.Latitude!.Value,
                r.Longitude!.Value,
                r.PoiId == startPoiId,
                r.PoiId == finishPoiId))
            .ToList();

        // Story 1.2 (FR-2): per-PoiId presentation fields for the Name column +
        // Actions. The Google Maps URL is resolved HERE (projection edge) via the
        // shared PoiUrlHelper over a lightweight Poi carrying only the fields the
        // helper reads — keeping the component free of Poi/helper logic (NFR1).
        var presentationByPoiId = members.ToDictionary(
            r => r.PoiId,
            r => (
                Dwell: r.DwellMinutes,
                r.Address,
                r.IsEnriched,
                r.EnrichmentNeedsManualUrl,
                GoogleMapsUrl: PoiUrlHelper.GetGoogleMapsUrl(new Poi
                {
                    Name = r.Name,
                    Category = r.Category,
                    GoogleMapsUrl = r.GoogleMapsUrl,
                })));

        // Unplaceable rows trail the routed stops in a stable, deterministic
        // order (AddedDate, then PoiId — the same tie-break the ordering service
        // uses). They carry no routed number. [TRIP-PLACE-04]
        var unplaceable = members
            .Where(r => !StopPlaceability.IsPlaceable(r.Latitude, r.Longitude))
            .OrderBy(r => r.AddedDate)
            .ThenBy(r => r.PoiId)
            // TRIP-DWELL-01: an unplaceable stop still carries its membership dwell.
            // Story 1.2: it also carries the Name-column presentation fields (address,
            // enrichment) even though it renders the "Not placeable" treatment.
            .Select(r =>
            {
                var p = presentationByPoiId[r.PoiId];
                return new TripStopRow(
                    DisplayOrder: null, r.PoiId, r.Name, IsPlaceable: false, p.Dwell,
                    p.Address, p.IsEnriched, p.EnrichmentNeedsManualUrl, p.GoogleMapsUrl);
            });

        var rows = stops
            .Select(s =>
            {
                var p = presentationByPoiId[s.PoiId];
                return new TripStopRow(
                    s.OrderIndex, s.PoiId, s.Name, IsPlaceable: true, p.Dwell,
                    p.Address, p.IsEnriched, p.EnrichmentNeedsManualUrl, p.GoogleMapsUrl);
            })
            .Concat(unplaceable)
            .ToList();

        return (stops, rows);
    }

    private async Task<(int? StartPoiId, int? FinishPoiId)> ReadStartFinishAsync(int collectionId)
    {
        await using var db = await factory.CreateDbContextAsync(_cts.Token);
        var row = await db.PoiCollections
            .Where(c => c.Id == collectionId)
            .Select(c => new { c.StartPoiId, c.FinishPoiId })
            .FirstOrDefaultAsync(_cts.Token);
        return (row?.StartPoiId, row?.FinishPoiId);
    }

    /// <summary>
    /// TRIP-TRAVELTIME-01 / TRIP-TIMELINE-01: the collection's persisted Trip settings —
    /// the travel mode (entity default AnyAir) and the timeline inputs (TripStartTime,
    /// TimeBudgetMinutes) — read in one pass so the timeline recompute adds no extra DB
    /// round-trip beyond the pre-existing mode read.
    /// </summary>
    private async Task<(string TravelMode, DateTime? TripStartTime, int? BudgetMinutes)> ReadTripSettingsAsync(
        int collectionId)
    {
        await using var db = await factory.CreateDbContextAsync(_cts.Token);
        var row = await db.PoiCollections
            .Where(c => c.Id == collectionId)
            .Select(c => new { c.TravelMode, c.TripStartTime, c.TimeBudgetMinutes })
            .FirstOrDefaultAsync(_cts.Token);
        return (row?.TravelMode ?? Data.Entities.TravelMode.AnyAir, row?.TripStartTime, row?.TimeBudgetMinutes);
    }

    /// <summary>
    /// TRIP-TRAVELTIME-01: reads the cached <see cref="RouteSegment"/> rows for
    /// this collection's stops under <paramref name="travelMode"/>, keyed by the
    /// directional (From, To) pair so <see cref="MakeLeg"/> can fold them in.
    /// </summary>
    private async Task<IReadOnlyDictionary<(int From, int To), RouteSegment>> ReadRouteSegmentsAsync(
        int collectionId, string travelMode)
    {
        await using var db = await factory.CreateDbContextAsync(_cts.Token);

        var poiIds = await db.PoiCollectionItems
            .AsNoTracking()
            .Where(ci => ci.PoiCollectionId == collectionId)
            .Select(ci => ci.PoiId)
            .ToListAsync(_cts.Token);

        if (poiIds.Count == 0)
        {
            return EmptyCache;
        }

        var rows = await db.RouteSegments
            .AsNoTracking()
            .Where(r => r.TravelMode == travelMode
                        && poiIds.Contains(r.FromPoiId)
                        && poiIds.Contains(r.ToPoiId))
            .ToListAsync(_cts.Token);

        return rows.ToDictionary(r => (r.FromPoiId, r.ToPoiId));
    }

    private static readonly IReadOnlyDictionary<(int From, int To), RouteSegment> EmptyCache =
        new ReadOnlyDictionary<(int, int), RouteSegment>(new Dictionary<(int, int), RouteSegment>());

    /// <summary>
    /// TRIP-TRAVELTIME-01: idempotently subscribes to the background compute
    /// progress. On a change (the pending count dropped because a leg landed in
    /// the cache) it re-reads the projections off the circuit thread via
    /// <see cref="Notify"/>'s host-driven redraw path — no polling, no blocking.
    /// </summary>
    private void EnsureProgressSubscription()
    {
        // Skip(1): Changes is a BehaviorSubject that replays its current value on
        // subscribe. That initial replay is not a real progress event — reacting
        // to it would race a fire-and-forget leg rebuild against the projection
        // refresh that just ran. Only subsequent changes (a leg landed in the
        // cache) should trigger a re-read.
        _progressSubscription ??= System.Reactive.Linq.Observable
            .Skip(travelTimeProgress.Changes, 1)
            .Subscribe(onNext: _ => RefreshLegsFromCacheFireAndForget());
    }

    // Fire-and-forget bridge: the Rx subscription is synchronous, so kick the
    // async cache re-read without awaiting (errors are logged inside).
    private void RefreshLegsFromCacheFireAndForget()
    {
        _ = RefreshLegsFromCacheAsync();
    }

    /// <summary>
    /// TRIP-TRAVELTIME-01: re-reads only the cached travel times for the current
    /// stops and rebuilds the legs + total, then Notifies. Called when the
    /// background service reports progress; a no-op when Trip View is off.
    /// </summary>
    private async Task RefreshLegsFromCacheAsync()
    {
        if (_disposed || ActiveCollectionId is not { } collectionId || !IsTripViewEnabled)
        {
            return;
        }

        try
        {
            // TRIP-TIMELINE-01: re-read the timeline inputs alongside the mode so a leg
            // landing in the cache recomputes arrivals/total too (both refresh paths).
            var (travelMode, tripStartTime, budgetMinutes) = await ReadTripSettingsAsync(collectionId);
            TripStartTime = tripStartTime;
            TimeBudgetMinutes = budgetMinutes;
            var cache = await ReadRouteSegmentsAsync(collectionId, travelMode);
            OrderedLegs = BuildLegs(OrderedStops, FinishPoiId, cache);
            RecomputeTotal();
            RecomputeTimeline();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to refresh travel-time legs for collection {CollectionId}", collectionId);
            return;
        }

        Notify();
    }

    /// <summary>
    /// TRIP-LEG-02: builds the straight connecting legs from the ordered stops.
    /// Consecutive pairs (k → k+1) give N−1 legs; when there is no distinct
    /// Finish (Roundtrip — <paramref name="finishPoiId"/> null or equal to Start,
    /// which is Order 1) the closing leg from the last stop back to the Start is
    /// appended, giving N legs. A distinct Finish leaves the path open (N−1 legs,
    /// no closing leg). Every leg is non-Measured in Phase 1 (TRIP-LEG-01).
    /// </summary>
    private static IReadOnlyList<TripLeg> BuildLegs(
        IReadOnlyList<TripStop> stops,
        int? finishPoiId,
        IReadOnlyDictionary<(int From, int To), RouteSegment> cache)
    {
        if (stops.Count < 2)
        {
            return [];
        }

        var legs = new List<TripLeg>(stops.Count);
        for (var k = 0; k < stops.Count - 1; k++)
        {
            legs.Add(MakeLeg(stops[k], stops[k + 1], cache));
        }

        // Roundtrip closes the loop with a leg from the last stop back to the
        // Start (Order 1). The path is left OPEN (no closing leg) only when a
        // distinct Finish resolves to a real placeable stop other than the first.
        // A Finish that is null, equals the first stop, or points at a POI that is
        // not a placeable stop (coordinate-less, or no longer in the collection)
        // cannot terminate a drawn path, so it falls back to Roundtrip rather than
        // silently dropping the closing leg. (Start/Finish editing is Story 1.7;
        // Phase-1 Finish is always null.)
        var finishIsDistinctStop = finishPoiId is { } fid
            && fid != stops[0].PoiId
            && stops.Any(s => s.PoiId == fid);
        if (!finishIsDistinctStop)
        {
            legs.Add(MakeLeg(stops[^1], stops[0], cache));
        }

        return legs;
    }

    /// <summary>
    /// TRIP-TRAVELTIME-01: builds a leg, folding in the cached
    /// duration/distance/fidelity when a directional RouteSegment row exists for
    /// the (from→to) pair; otherwise the travel-time fields stay null (the leg is
    /// "computing"). <see cref="TripLeg.IsMeasured"/> is derived solely from the
    /// cached fidelity (Measured only) — the Mock yields Estimated, so legs stay
    /// non-Measured for now.
    /// </summary>
    private static TripLeg MakeLeg(
        TripStop from, TripStop to, IReadOnlyDictionary<(int From, int To), RouteSegment> cache)
    {
        cache.TryGetValue((from.PoiId, to.PoiId), out var seg);
        var fidelity = seg?.Fidelity;
        // TRIP-TRAVELMODE-01 (Story 2.2, AC4): a Placeholder leg (Any/Air with no
        // Manual entry) carries an internal straight-line air estimate, but it must
        // NEVER surface as a real door-to-door time. Null its DURATION at the
        // projection edge so the time slot renders "—" and the trip total stays
        // unknown ("—"), while keeping the distance (a real haversine value) and the
        // Placeholder fidelity (the badge renders nothing for it). A leg that simply
        // has no cache row yet keeps a null fidelity — that is the "computing" state,
        // kept distinct from Placeholder so the aria-live computing announcement does
        // not fire forever on Any/Air.
        var displayDuration = fidelity == Fidelity.Placeholder ? null : seg?.DurationSeconds;
        // TRIP-DEGRADE-01 (Story 2.3): a leg backed by the provider-down fallback
        // (Source == EstimatedFallback) is "degraded" — it keeps its real Estimated
        // duration but flags the trip as showing straight-line estimates. A normal
        // Mock Estimated leg does NOT set this.
        var isFallback = seg?.Source == TravelTimeSource.EstimatedFallback;
        return new TripLeg(
            from.PoiId, to.PoiId, from.Lat, from.Lon, to.Lat, to.Lon,
            IsMeasured: fidelity == Fidelity.Measured,
            DurationSeconds: displayDuration,
            DistanceMeters: seg?.DistanceMeters,
            Fidelity: fidelity,
            IsFallback: isFallback,
            // TRIP-OSRM-02 (Story 4.2): carry the measured road geometry through to
            // the map projection. Already null for non-Measured rows (only OSRM
            // writes it for a Measured leg); the JS side decodes the precision-5
            // encoded polyline and gates "solid road" on its presence (AC1/AC5).
            GeometryPolyline: seg?.GeometryPolyline);
    }

    /// <summary>
    /// TRIP-TRAVELTIME-01 (AC5): Σ of the legs' durations. The total is null
    /// (rendered "—" — no false precision) whenever any leg lacks a known duration,
    /// which covers both an uncomputed leg and a Placeholder Any/Air leg (both carry
    /// a null <see cref="TripLeg.DurationSeconds"/> at the projection edge).
    /// <see cref="IsAnyLegComputing"/> is driven by row PRESENCE (null fidelity =
    /// no cache row yet), NOT by a null duration — a computed Placeholder leg is not
    /// "computing" (TRIP-TRAVELMODE-01 / AC4).
    /// </summary>
    private void RecomputeTotal()
    {
        IsAnyLegComputing = OrderedLegs.Any(l => l.Fidelity is null);
        var allDurationsKnown = OrderedLegs.Count > 0 && OrderedLegs.All(l => l.DurationSeconds is not null);
        // TRIP-RECONCILE-01 (Story 2.1): the displayed total is Σ of the ROUND-ONCE
        // per-leg minutes (×60 to keep this seconds-typed field), NOT Duration(Σ raw
        // seconds). The per-leg connector shows Duration(legSeconds) = DisplayMinutes(leg)
        // and the total now sums those same rounded minutes, so the displayed total equals
        // Σ of the displayed per-leg times (FR-13). A leg with DisplayMinutes==0 && seconds>0
        // shows "<1 min" but contributes 0 — consistent (a 0-contribution annotation), not
        // special-cased. Null total (partial em-dash) whenever any leg is uncomputed/Any —
        // unchanged.
        TotalTravelTimeSeconds = allDurationsKnown
            ? OrderedLegs.Sum(l => TravelTimeFormatting.DisplayMinutes(l.DurationSeconds!.Value)) * 60
            : null;
    }

    /// <summary>
    /// TRIP-TIMELINE-01 (Story 2.6): recomputes the honest itinerary timeline from the
    /// already-built projections (ordered placeable stops + their dwell, the ordered
    /// legs with duration/fidelity, the unplaceable stops' dwell), the trip shape
    /// (<see cref="IsRoundtrip"/>), and the persisted <see cref="TripStartTime"/> /
    /// <see cref="TimeBudgetMinutes"/>. Pure, presentation-only — no DB. The dwell is
    /// carried per-PoiId in <see cref="StopRows"/>; placeable rows feed the routed walk
    /// (in OrderedStops order) and unplaceable rows feed the total-only dwell list.
    /// </summary>
    private void RecomputeTimeline()
    {
        if (OrderedStops.Count < 2)
        {
            Timeline = ItineraryTimelineResult.Empty;
            return;
        }

        // Dwell minutes per PoiId from the stop-list rows (placeable + unplaceable alike).
        var dwellByPoiId = StopRows.ToDictionary(r => r.PoiId, r => r.DwellMinutes);

        var stops = OrderedStops
            .Select(s => new ItineraryStopInput(
                s.PoiId,
                dwellByPoiId.TryGetValue(s.PoiId, out var dwell) ? dwell : null))
            .ToList();

        // Legs in the SAME order BuildLegs produced: N−1 consecutive legs, then the
        // closing leg on a Roundtrip. The timeline walk consumes them positionally.
        var legs = OrderedLegs
            .Select(l => new ItineraryLegInput(l.DurationSeconds, l.Fidelity))
            .ToList();

        // Unplaceable rows contribute dwell to the total only (no leg, no arrival).
        var unplaceableDwell = StopRows
            .Where(r => !r.IsPlaceable)
            .Select(r => r.DwellMinutes)
            .ToList();

        // TRIP-TIMELINE-01: derive the roundtrip shape from the ACTUAL leg set
        // (BuildLegs emits N legs for a roundtrip, N−1 for an open path) rather than
        // from IsRoundtrip directly. The two normally agree, but a stale/Start-equal
        // FinishPoiId makes BuildLegs fall back to a closing leg while IsRoundtrip is
        // still false — deriving from the legs keeps the timeline total consistent with
        // the rendered legs no matter what.
        var hasClosingLeg = legs.Count >= stops.Count;

        Timeline = ItineraryTimeline.Compute(
            stops, legs, unplaceableDwell, hasClosingLeg, TripStartTime, TimeBudgetMinutes);
    }

    // --- TripViewEnabled persistence (collection-level Trip state) ---

    private async Task<bool> ReadTripViewEnabledAsync(int collectionId)
    {
        await using var db = await factory.CreateDbContextAsync(_cts.Token);
        return await db.PoiCollections
            .Where(c => c.Id == collectionId)
            .Select(c => c.TripViewEnabled)
            .FirstOrDefaultAsync(_cts.Token);
    }

    private async Task PersistTripViewEnabledAsync(int collectionId, bool enabled)
    {
        await using var db = await factory.CreateDbContextAsync(_cts.Token);
        var collection = await db.PoiCollections.FirstOrDefaultAsync(c => c.Id == collectionId, _cts.Token);
        if (collection is null || collection.TripViewEnabled == enabled)
        {
            return;
        }

        collection.TripViewEnabled = enabled;

        // Serialize with the background enrichment / dedup writers via the shared
        // gate so this user-initiated commit never hits "database is locked".
        await writeLock.Gate.WaitAsync(_cts.Token);
        try
        {
            await db.SaveChangesAsync(_cts.Token);
        }
        finally
        {
            writeLock.Gate.Release();
        }
    }

    // --- Travel Mode + manual Any/Air time (Story 2.2) ---

    /// <summary>
    /// TRIP-TRAVELMODE-01: persists a new travel mode for the active collection
    /// and triggers a recompute. Mirrors the <see cref="PersistTripViewEnabledAsync"/>
    /// write path (factory + <see cref="SqliteWriteLock"/>). No-ops when the mode
    /// is invalid or already active (selecting the active segment does nothing —
    /// no write, no recompute). After a change: re-read projections (the directional
    /// cache key naturally selects the new mode's rows; legs with no row yet render
    /// "—" + computing), signal the background trigger to fill missing legs, Notify.
    /// </summary>
    public async Task SetTravelModeAsync(string mode)
    {
        if (ActiveCollectionId is not { } collectionId || !IsTripViewEnabled)
        {
            return;
        }

        // Reject unknown modes and short-circuit the already-active mode (SM-C2:
        // recomputation stays rare — re-selecting the current mode is a no-op).
        if (!Data.Entities.TravelMode.IsValid(mode) || mode == TravelMode)
        {
            return;
        }

        try
        {
            await PersistTravelModeAsync(collectionId, mode);
            // Re-read projections under the new mode; the cache read now filters
            // r.TravelMode == mode so the legs switch to the new mode's rows.
            await RefreshProjectionsAsync(collectionId);
            // Any leg missing a row under the new mode is already kicked by
            // RefreshProjectionsAsync (IsAnyLegComputing ⇒ Signal); signal once
            // more unconditionally so a switch to an all-cached mode still wakes
            // the loop to compute any newly-needed closing leg. Cheap + idempotent.
            travelTimeTrigger.Signal();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set travel mode {Mode} for collection {CollectionId}", mode, collectionId);
            return;
        }

        Notify();
    }

    private async Task PersistTravelModeAsync(int collectionId, string mode)
    {
        await using var db = await factory.CreateDbContextAsync(_cts.Token);
        var collection = await db.PoiCollections.FirstOrDefaultAsync(c => c.Id == collectionId, _cts.Token);
        if (collection is null || collection.TravelMode == mode)
        {
            return;
        }

        collection.TravelMode = mode;

        await writeLock.Gate.WaitAsync(_cts.Token);
        try
        {
            await db.SaveChangesAsync(_cts.Token);
        }
        finally
        {
            writeLock.Gate.Release();
        }
    }

    /// <summary>
    /// TRIP-MANUAL-01 (AC5): upserts a manual travel time for an Any/Air leg. The
    /// minutes entered at the UI edge are converted to canonical seconds (×60,
    /// AR-11) and stored on the directional <c>(from, to, AnyAir)</c>
    /// <see cref="RouteSegment"/> row with <see cref="Fidelity.Manual"/>,
    /// <c>Source = "Manual"</c>, <c>GeometryPolyline = null</c>, and the haversine
    /// distance for display. Persisted under the shared write lock so it survives
    /// reorder and recompute. After the write: refresh + Notify.
    /// </summary>
    /// <summary>
    /// Upper bound on a manual leg time: 60 days in minutes. Generous enough for any
    /// real flight/overnight-haul entry, but rejects absurd input so the ×60 seconds
    /// conversion can never overflow <see cref="int"/> or poison the cached total.
    /// </summary>
    internal const int MaxManualLegMinutes = 60 * 24 * 60;

    public async Task SetManualLegTimeAsync(int fromPoiId, int toPoiId, int minutes)
    {
        if (ActiveCollectionId is not { } collectionId || !IsTripViewEnabled
            || minutes < 0 || minutes > MaxManualLegMinutes)
        {
            return;
        }

        var from = OrderedStops.FirstOrDefault(s => s.PoiId == fromPoiId);
        var to = OrderedStops.FirstOrDefault(s => s.PoiId == toPoiId);
        if (from is null || to is null)
        {
            return;
        }

        var meters = GeoUtils.HaversineDistance(from.Lat, from.Lon, to.Lat, to.Lon);
        // Convert minutes ↔ seconds only here, at the UI edge (AR-11).
        var seconds = minutes * 60;

        try
        {
            await UpsertManualSegmentAsync(fromPoiId, toPoiId, seconds, meters);
            await RefreshProjectionsAsync(collectionId);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set manual leg time {From}->{To} for collection {CollectionId}", fromPoiId, toPoiId, collectionId);
            return;
        }

        Notify();
    }

    /// <summary>
    /// Upper bound on a per-stop dwell time: 60 days in minutes — generous enough for
    /// any real overnight/multi-day stay, but rejects absurd input so the future
    /// minutes→seconds conversion (Story 2.6) can never overflow <see cref="int"/>.
    /// Mirrors the <see cref="MaxManualLegMinutes"/> precedent.
    /// </summary>
    internal const int MaxDwellMinutes = TripOrderingService.MaxDwellMinutes;

    /// <summary>
    /// TRIP-DWELL-01 (Story 2.5): sets the dwell time (minutes) on the active
    /// collection's membership for <paramref name="poiId"/>. <paramref name="minutes"/>
    /// is stored verbatim on <c>PoiCollectionItem.DwellMinutes</c>; <c>null</c> clears
    /// it (unset ⇒ contributes zero). Written under the shared write lock so it survives
    /// reorder/recompute, then refresh + Notify. Dwell is independent of route segments:
    /// this path itself touches NO <see cref="RouteSegment"/> and never invalidates or
    /// recomputes a cached leg (it makes no provider call). The shared
    /// <c>RefreshProjectionsAsync</c> it calls may still wake the compute loop if some
    /// leg is genuinely uncomputed (the pre-existing <c>IsAnyLegComputing</c> behavior) —
    /// that is a harmless no-op re-check, not a dwell-driven recompute. Guards: active
    /// collection + Trip View on; rejects out-of-range minutes (negative or
    /// &gt; <see cref="MaxDwellMinutes"/>).
    /// </summary>
    public async Task SetDwellMinutesAsync(int poiId, int? minutes)
    {
        if (ActiveCollectionId is not { } collectionId || !IsTripViewEnabled
            || minutes is < 0 or > MaxDwellMinutes)
        {
            return;
        }

        try
        {
            await PersistDwellMinutesAsync(collectionId, poiId, minutes);
            await RefreshProjectionsAsync(collectionId);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set dwell minutes for POI {PoiId} in collection {CollectionId}", poiId, collectionId);
            return;
        }

        Notify();
    }

    // TRIP-DWELL-01: the dwell DB-write now lives on ITripOrderingService
    // (SetDwellMinutesAsync) so the UI and the MCP set_dwell_time tool (Story 3.2)
    // share one validated, write-locked implementation. The VM keeps its guard,
    // RefreshProjectionsAsync and Notify; only the persistence is delegated.
    private Task PersistDwellMinutesAsync(int collectionId, int poiId, int? minutes) =>
        ordering.SetDwellMinutesAsync(collectionId, poiId, minutes, _cts.Token);

    // --- Trip start time + soft time budget (Story 2.6, TRIP-TIMELINE-01) ---

    /// <summary>
    /// Upper bound on the soft time budget, in minutes: 60 days. Generous enough for any
    /// real multi-day trip, but rejects absurd input so the ×60 seconds comparison in the
    /// pure timeline can never overflow <see cref="int"/>. Mirrors the
    /// <see cref="MaxDwellMinutes"/> / <see cref="MaxManualLegMinutes"/> precedent.
    /// </summary>
    internal const int MaxBudgetMinutes = 60 * 24 * 60;

    /// <summary>
    /// TRIP-TIMELINE-01 (Story 2.6, AC2): sets the per-trip wall-clock start time on the
    /// active collection (<c>PoiCollection.TripStartTime</c>); <c>null</c> clears it (⇒
    /// relative offsets only). Mirrors the dwell/mode persistence (factory +
    /// <see cref="SqliteWriteLock"/>), then refresh + Notify. Start time does NOT affect
    /// route segments, so this never signals the travel-time trigger.
    /// </summary>
    public async Task SetTripStartTimeAsync(DateTime? start)
    {
        if (ActiveCollectionId is not { } collectionId || !IsTripViewEnabled)
        {
            return;
        }

        try
        {
            await PersistTripStartTimeAsync(collectionId, start);
            await RefreshProjectionsAsync(collectionId);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set trip start time for collection {CollectionId}", collectionId);
            return;
        }

        Notify();
    }

    /// <summary>
    /// TRIP-TIMELINE-01 (Story 2.6, AC5): sets the per-trip soft time budget in minutes on
    /// the active collection (<c>PoiCollection.TimeBudgetMinutes</c>); <c>null</c> clears it
    /// (⇒ no overrun flag is ever shown). Range-guarded (<c>&gt;= 0</c>,
    /// <c>&lt;= <see cref="MaxBudgetMinutes"/></c>). Mirrors the dwell/mode persistence,
    /// then refresh + Notify. The budget does NOT affect route segments, so this never
    /// signals the travel-time trigger.
    /// </summary>
    public async Task SetTimeBudgetMinutesAsync(int? minutes)
    {
        if (ActiveCollectionId is not { } collectionId || !IsTripViewEnabled
            || minutes is < 0 or > MaxBudgetMinutes)
        {
            return;
        }

        try
        {
            await PersistTimeBudgetMinutesAsync(collectionId, minutes);
            await RefreshProjectionsAsync(collectionId);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set time budget for collection {CollectionId}", collectionId);
            return;
        }

        Notify();
    }

    private async Task PersistTripStartTimeAsync(int collectionId, DateTime? start)
    {
        await using var db = await factory.CreateDbContextAsync(_cts.Token);
        var collection = await db.PoiCollections.FirstOrDefaultAsync(c => c.Id == collectionId, _cts.Token);
        if (collection is null || collection.TripStartTime == start)
        {
            return;
        }

        collection.TripStartTime = start;

        await writeLock.Gate.WaitAsync(_cts.Token);
        try
        {
            await db.SaveChangesAsync(_cts.Token);
        }
        finally
        {
            writeLock.Gate.Release();
        }
    }

    private async Task PersistTimeBudgetMinutesAsync(int collectionId, int? minutes)
    {
        await using var db = await factory.CreateDbContextAsync(_cts.Token);
        var collection = await db.PoiCollections.FirstOrDefaultAsync(c => c.Id == collectionId, _cts.Token);
        if (collection is null || collection.TimeBudgetMinutes == minutes)
        {
            return;
        }

        collection.TimeBudgetMinutes = minutes;

        await writeLock.Gate.WaitAsync(_cts.Token);
        try
        {
            await db.SaveChangesAsync(_cts.Token);
        }
        finally
        {
            writeLock.Gate.Release();
        }
    }

    /// <summary>
    /// TRIP-MANUAL-01: clears a manual Any/Air leg time. Deletes the
    /// <c>(from, to, AnyAir)</c> cache row so the leg reverts to the Placeholder
    /// the Mock recomputes (shown "—"). The background trigger is signalled to
    /// refill the now-missing row. No-op when no row exists.
    /// </summary>
    public async Task ClearManualLegTimeAsync(int fromPoiId, int toPoiId)
    {
        if (ActiveCollectionId is not { } collectionId || !IsTripViewEnabled)
        {
            return;
        }

        try
        {
            await DeleteSegmentAsync(fromPoiId, toPoiId);
            await RefreshProjectionsAsync(collectionId);
            travelTimeTrigger.Signal();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clear manual leg time {From}->{To} for collection {CollectionId}", fromPoiId, toPoiId, collectionId);
            return;
        }

        Notify();
    }

    /// <summary>
    /// TRIP-RECOMPUTE-01 (Story 2.4, AC4/5/6): user-initiated "Recompute travel
    /// times" for the active trip. Invalidates the recompute-eligible cached rows
    /// (Estimated/Placeholder/EstimatedFallback — never the user's Manual entries,
    /// never a higher-fidelity Measured row) then refreshes the projections. The
    /// now-missing rows make a leg "computing", so <see cref="RefreshProjectionsAsync"/>
    /// already signals the off-circuit compute (no unconditional Signal added) — and
    /// when the background service writes a higher-fidelity row, the existing
    /// progress→<see cref="RefreshLegsFromCacheAsync"/> subscription upgrades the leg
    /// (Estimated→Measured: solid line + secondary badge) via <see cref="StateChanged"/>,
    /// never a silent mutation on a stale screen. On-demand only — never automatic.
    /// Mirrors the 2.2 Set/ClearManualLegTimeAsync write-then-refresh-then-Notify shape.
    /// </summary>
    public async Task RecomputeTravelTimesAsync()
    {
        if (ActiveCollectionId is not { } collectionId || !IsTripViewEnabled)
        {
            return;
        }

        try
        {
            await routeSegmentInvalidation.InvalidateRecomputableForCollectionAsync(collectionId, _cts.Token);
            // Refresh rebuilds the legs from the now-thinned cache; any leg without a
            // row flips IsAnyLegComputing ⇒ RefreshProjectionsAsync Signal()s the
            // background compute. No unconditional Signal here (AC1 stays intact).
            await RefreshProjectionsAsync(collectionId);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to recompute travel times for collection {CollectionId}", collectionId);
            return;
        }

        Notify();
    }

    /// <summary>
    /// TRIP-TSP-01 (Story 3.1, AR-6): explicit on-demand "Sort in Traveling Salesman
    /// order". Delegates to the single ordering write path
    /// (<see cref="ITripOrderingService.SortTravelingSalesmanAsync"/>) — the VM never
    /// computes or writes order itself — then re-reads the projections (stops, legs,
    /// timeline) and Notifies, which the host page turns into the existing incremental
    /// redraw. On-demand ONLY: nothing else calls this, so the system never reorders
    /// without the explicit press (AC2). Mirrors the RecomputeTravelTimesAsync
    /// guard → service → refresh → notify shape.
    /// </summary>
    public async Task SortTravelingSalesmanAsync()
    {
        if (ActiveCollectionId is not { } collectionId || !IsTripViewEnabled)
        {
            return;
        }

        // Snapshot the order so a sort that the never-worse guard leaves unchanged
        // stays silent — the live region must never report a reorder that didn't
        // happen (mirrors MoveStopToAsync's no-op-move silence).
        var before = OrderedStops.Select(s => s.PoiId).ToList();

        try
        {
            await ordering.SortTravelingSalesmanAsync(collectionId, _cts.Token);
            await RefreshProjectionsAsync(collectionId);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to TSP-sort collection {CollectionId}", collectionId);
            return;
        }

        var after = OrderedStops.Select(s => s.PoiId).ToList();
        if (!before.SequenceEqual(after))
        {
            LastSortAnnouncement = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                UiStrings.TripSortTspAnnouncement,
                OrderedStops.Count);
        }
        Notify();
    }

    private async Task UpsertManualSegmentAsync(int fromPoiId, int toPoiId, int seconds, double meters)
    {
        await using var db = await factory.CreateDbContextAsync(_cts.Token);
        var existing = await db.RouteSegments.FirstOrDefaultAsync(
            r => r.FromPoiId == fromPoiId
                 && r.ToPoiId == toPoiId
                 && r.TravelMode == Data.Entities.TravelMode.AnyAir,
            _cts.Token);

        if (existing is null)
        {
            db.RouteSegments.Add(new RouteSegment
            {
                FromPoiId = fromPoiId,
                ToPoiId = toPoiId,
                TravelMode = Data.Entities.TravelMode.AnyAir,
                DurationSeconds = seconds,
                DistanceMeters = meters,
                GeometryPolyline = null,
                Fidelity = Data.Entities.Fidelity.Manual,
                Source = "Manual",
                ComputedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.DurationSeconds = seconds;
            existing.DistanceMeters = meters;
            existing.GeometryPolyline = null;
            existing.Fidelity = Data.Entities.Fidelity.Manual;
            existing.Source = "Manual";
            existing.ComputedAt = DateTime.UtcNow;
        }

        await writeLock.Gate.WaitAsync(_cts.Token);
        try
        {
            await db.SaveChangesAsync(_cts.Token);
        }
        finally
        {
            writeLock.Gate.Release();
        }
    }

    private async Task DeleteSegmentAsync(int fromPoiId, int toPoiId)
    {
        await using var db = await factory.CreateDbContextAsync(_cts.Token);
        var existing = await db.RouteSegments.FirstOrDefaultAsync(
            r => r.FromPoiId == fromPoiId
                 && r.ToPoiId == toPoiId
                 && r.TravelMode == Data.Entities.TravelMode.AnyAir,
            _cts.Token);
        if (existing is null)
        {
            return;
        }

        db.RouteSegments.Remove(existing);

        await writeLock.Gate.WaitAsync(_cts.Token);
        try
        {
            await db.SaveChangesAsync(_cts.Token);
        }
        finally
        {
            writeLock.Gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Idempotent: the host page disposes this VM explicitly, and the DI
        // container disposes the same Transient instance again at circuit
        // teardown. Without this guard the second CancelAsync would throw
        // ObjectDisposedException on the already-disposed CTS. [Review][Patch]
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        // TRIP-TRAVELTIME-01: stop reacting to compute progress before teardown.
        _progressSubscription?.Dispose();
        _progressSubscription = null;
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
