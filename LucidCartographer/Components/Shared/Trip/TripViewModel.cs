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
    ITravelTimeProvider? travelTimeProvider = null) : IAsyncDisposable
{
    /// <summary>
    /// The active travel-time provider's routing-data attribution HTML, or null if the provider
    /// is not licence-bound (haversine Mock) or not supplied.
    /// </summary>
    public string? RoutingAttributionHtml => travelTimeProvider?.Attribution;

    private static readonly IReadOnlyDictionary<int, int> NoStops =
        new ReadOnlyDictionary<int, int>(new Dictionary<int, int>());

    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    private IDisposable? _progressSubscription;

    public event Action? StateChanged;

    private void Notify() => StateChanged?.Invoke();

    /// <summary>The collection a Trip is scoped to, or null when none is in scope.</summary>
    public int? ActiveCollectionId { get; private set; }

    /// <summary>
    /// The single UX-DR1 placeable-count threshold for the toggle. Both
    /// <see cref="IsToggleAvailable"/> and <see cref="AutoDisableBelowGateAsync"/>
    /// gate on this one value so the "offer" and "auto-disable" edges can never
    /// drift apart.
    /// </summary>
    private const int MinPlaceableForToggle = 1;

    /// <summary>
    /// Number of placeable POIs in scope; drives the ≥1 availability gate. Fed the
    /// single visible collection's FULL placeable membership (viewport-independent)
    /// by the host, so the gate tracks the collection, not the map viewport.
    /// </summary>
    public int PlaceableCount { get; private set; }

    /// <summary>Whether Trip View is currently on for the active collection.</summary>
    public bool IsTripViewEnabled { get; private set; }

    /// <summary>
    /// The toggle is only offered when a single collection is in scope and it has
    /// at least one placeable POI (UX-DR1 / FR-17). Below that (an empty collection)
    /// it is absent — never an error or broken affordance (UX-DR10). The count is
    /// the collection's full membership, so the toggle does not hide when the map
    /// is panned away from the POIs.
    /// </summary>
    public bool IsToggleAvailable => ActiveCollectionId is not null && PlaceableCount >= MinPlaceableForToggle;

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
    /// Full-membership stop-list rows: every placeable stop (with its routed number,
    /// contiguous 1..M) followed by every unplaceable POI ("Not placeable" treatment).
    /// Unplaceable POIs excluded from markers, legs, routing. Empty when Trip View is off.
    /// </summary>
    public IReadOnlyList<TripStopRow> StopRows { get; private set; } = [];

    /// <summary>
    /// Straight connecting legs between consecutive placeable stops, plus the
    /// closing leg back to Start on a Roundtrip (no distinct Finish). Empty
    /// when Trip View is off or fewer than two placeable stops exist.
    /// </summary>
    public IReadOnlyList<TripLeg> OrderedLegs { get; private set; } = [];

    /// <summary>
    /// Trip's total travel time in seconds (sum of all legs' durations), or null
    /// when any leg is uncomputed. Null total renders as em-dash to avoid false precision.
    /// </summary>
    public int? TotalTravelTimeSeconds { get; private set; }

    /// <summary>True when at least one leg has no cache row yet (computing state).</summary>
    public bool IsAnyLegComputing { get; private set; }

    /// <summary>
    /// True when any leg is backed by provider-down fallback (EstimatedFallback source).
    /// Distinct from mock-Estimated legs, which do not trip this.
    /// </summary>
    public bool IsShowingApproximateEstimates => OrderedLegs.Any(l => l.IsFallback);

    /// <summary>
    /// True when no measured provider is configured (null/haversine Mock) AND the trip has
    /// at least one normally-Estimated leg (not a fallback). Distinct from fallback estimates
    /// (provider-down); the two notes describe different states.
    /// </summary>
    public bool RecommendsOsrm =>
        travelTimeProvider?.Source != TravelTimeSource.Osrm
        && OrderedLegs.Any(l => l.Fidelity == Data.Entities.Fidelity.Estimated && !l.IsFallback);

    /// <summary>
    /// Itinerary timeline with per-stop arrivals, finish readout, whole-trip total (travel + dwell),
    /// and soft budget-overrun flag. Empty when Trip View is off or fewer than two placeable stops.
    /// </summary>
    public ItineraryTimelineResult Timeline { get; private set; } = ItineraryTimelineResult.Empty;

    /// <summary>Active collection's persisted wall-clock start time, or null for relative offsets only.</summary>
    public DateTime? TripStartTime { get; private set; }

    /// <summary>Active collection's persisted soft time budget in minutes, or null for no overrun flag.</summary>
    public int? TimeBudgetMinutes { get; private set; }

    public string? Announcement { get; private set; }

    /// <summary>Active collection's persisted travel mode (defaults AnyAir).</summary>
    public string TravelMode { get; private set; } = Data.Entities.TravelMode.AnyAir;

    /// <summary>The currently-selected Stop's PoiId, or null when nothing is selected.</summary>
    public int? SelectedStopPoiId { get; private set; }

    /// <summary>The selected Stop projection (name/order/coords), or null.</summary>
    public TripStop? SelectedStop { get; private set; }

    /// <summary>Which surface made the latest selection — drives pan (List) vs scroll (Map).</summary>
    public TripSelectionSource LastSelectionSource { get; private set; }

    /// <summary>
    /// Monotonic counter bumped on every SelectStop invocation, including idempotent re-selects.
    /// Lets the host distinguish a genuine selection from unrelated StateChanged notifications.
    /// </summary>
    public long SelectionTick { get; private set; }

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
        // A selection is only meaningful while Trip View is on; ignore otherwise.
        if (!IsTripViewEnabled)
        {
            return;
        }

        // Only placeable stops are selectable — unplaceable rows have no marker.
        if (poiId is { } requested && OrderedStops.All(s => s.PoiId != requested))
        {
            return;
        }

        SelectedStopPoiId = poiId;
        SelectedStop = poiId is { } id ? OrderedStops.FirstOrDefault(s => s.PoiId == id) : null;
        LastSelectionSource = source;
        SelectionTick++;
        SelectionAnnouncement = SelectedStop is { } s
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                UiStrings.TripStopSelectedAnnouncement, s.OrderIndex, s.Name)
            : null;
        Notify();
    }

    /// <summary>
    /// Localized "name moved to stop X of Y" text for the reorder aria-live
    /// region; null until the first successful move. Not set on no-op moves.
    /// </summary>
    public string? LastReorderAnnouncement { get; private set; }

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

    /// <summary>The PoiId pinned as Start (Order 1), or null.</summary>
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

    /// <summary>Whether the Stop may be designated Start. False on the current Finish.</summary>
    public bool CanSetStart(int poiId) => poiId != FinishPoiId;

    /// <summary>Whether the Stop may be designated Finish. False on the current Start.</summary>
    public bool CanSetFinish(int poiId) => poiId != StartPoiId;

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

    /// <summary>
    /// Sets the active-collection scope and restores persisted Trip View state.
    /// </summary>
    public async Task LoadAsync(int? collectionId, int placeableCount)
    {
        ActiveCollectionId = collectionId;
        PlaceableCount = placeableCount;

        if (collectionId is null)
        {
            IsTripViewEnabled = false;
            ClearProjections();
            CanonicalStopOrder = NoStops;
            Notify();
            return;
        }

        try
        {
            IsTripViewEnabled = await ReadTripViewEnabledAsync(collectionId.Value);
            await RefreshCanonicalStopOrderAsync();
            if (IsTripViewEnabled)
            {
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

    /// <summary>Updates only the placeable count without re-reading persisted state.</summary>
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
    /// Auto-disables Trip View when the collection falls below the ≥1-placeable
    /// availability gate. Returns true when disabled. Caller raises StateChanged.
    /// </summary>
    private async Task<bool> AutoDisableBelowGateAsync()
    {
        if (!IsTripViewEnabled || ActiveCollectionId is not { } collectionId || PlaceableCount >= MinPlaceableForToggle)
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
        }

        IsTripViewEnabled = false;
        ClearProjections();
        Announcement = UiStrings.TripViewAutoDisabledAnnouncement;
        return true;
    }

    /// <summary>
    /// Flips Trip View on/off for the active collection and persists the flag.
    /// First enable of a never-ordered collection seeds the Stop Order. No-op
    /// when the toggle is not available (below the ≥1 placeable gate).
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

    /// <summary>Called after a membership mutation. Reconciles Stop Order and refreshes projections when Trip View is on.</summary>
    public async Task RefreshAfterMembershipChangeAsync(int placeableCount)
    {
        PlaceableCount = placeableCount;

        if (ActiveCollectionId is not { } collectionId || !IsTripViewEnabled)
        {
            Notify();
            return;
        }

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

    /// <summary>
    /// Refreshes the cached CanonicalStopOrder from the persisted entity order,
    /// independent of IsTripViewEnabled. Empty when no single collection is in scope
    /// or the collection has no explicit order.
    /// </summary>
    private async Task RefreshCanonicalStopOrderAsync()
    {
        if (ActiveCollectionId is not { } collectionId)
        {
            CanonicalStopOrder = NoStops;
            return;
        }

        var order = await ordering.GetStopOrderAsync(collectionId, _cts.Token);
        CanonicalStopOrder = order.Count == 0 ? NoStops : order;
    }

    private void ClearProjections()
    {
        StopOrders = NoStops;
        OrderedStops = [];
        StopRows = [];
        OrderedLegs = [];
        TotalTravelTimeSeconds = null;
        IsAnyLegComputing = false;
        Timeline = ItineraryTimelineResult.Empty;
        TripStartTime = null;
        TimeBudgetMinutes = null;
        TravelMode = Data.Entities.TravelMode.AnyAir;
        SelectedStopPoiId = null;
        SelectedStop = null;
        SelectionAnnouncement = null;
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
        // TRIP-LEGMODE-01 (Story 3.2): legs are now per-leg-mode driven — the cache is
        // read across all modes and each leg selects its own (From, To, Mode) row. The
        // trip-wide travelMode read above no longer drives leg lookup (removal is 3.4).
        var cache = await ReadRouteSegmentsAsync(collectionId);
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
    /// Reads the active collection's full membership and splits it into placeable
    /// ordered stops (markers, legs, routing) and stop-list rows (placeable first, then
    /// unplaceable "Not placeable" items). Routed numbers recomputed contiguously 1..M.
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
                ci.DwellMinutes,
                ci.OutgoingTravelMode,
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
                r.PoiId == finishPoiId,
                r.OutgoingTravelMode))
            .ToList();

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

        var unplaceable = members
            .Where(r => !StopPlaceability.IsPlaceable(r.Latitude, r.Longitude))
            .OrderBy(r => r.AddedDate)
            .ThenBy(r => r.PoiId)
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
    /// Reads the collection's persisted Trip settings (travel mode and timeline inputs)
    /// in one pass to avoid extra DB round-trips.
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
    /// Reads cached RouteSegment rows for this collection's stops, keyed by directional
    /// (From, To, Mode) tuple. Legs select their own cache rows by mode.
    /// </summary>
    private async Task<IReadOnlyDictionary<(int From, int To, string Mode), RouteSegment>> ReadRouteSegmentsAsync(
        int collectionId)
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
            .Where(r => poiIds.Contains(r.FromPoiId)
                        && poiIds.Contains(r.ToPoiId))
            .ToListAsync(_cts.Token);

        return rows.ToDictionary(r => (r.FromPoiId, r.ToPoiId, r.TravelMode));
    }

    private static readonly IReadOnlyDictionary<(int From, int To, string Mode), RouteSegment> EmptyCache =
        new ReadOnlyDictionary<(int, int, string), RouteSegment>(new Dictionary<(int, int, string), RouteSegment>());

    /// <summary>Idempotently subscribes to background compute progress changes to re-read projections.</summary>
    private void EnsureProgressSubscription()
    {
        _progressSubscription ??= System.Reactive.Linq.Observable
            .Skip(travelTimeProgress.Changes, 1)
            .Subscribe(onNext: _ => RefreshLegsFromCacheFireAndForget());
    }

    private void RefreshLegsFromCacheFireAndForget()
    {
        _ = RefreshLegsFromCacheAsync();
    }

    /// <summary>Re-reads cached travel times and rebuilds legs + total on progress updates.</summary>
    private async Task RefreshLegsFromCacheAsync()
    {
        if (_disposed || ActiveCollectionId is not { } collectionId || !IsTripViewEnabled)
        {
            return;
        }

        try
        {
            var (_, tripStartTime, budgetMinutes) = await ReadTripSettingsAsync(collectionId);
            TripStartTime = tripStartTime;
            TimeBudgetMinutes = budgetMinutes;
            var cache = await ReadRouteSegmentsAsync(collectionId);
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
    /// Builds straight connecting legs from ordered stops. Consecutive pairs give N-1 legs;
    /// Roundtrip adds closing leg back to Start (N legs). Distinct Finish leaves path open (N-1 legs).
    /// </summary>
    private static IReadOnlyList<TripLeg> BuildLegs(
        IReadOnlyList<TripStop> stops,
        int? finishPoiId,
        IReadOnlyDictionary<(int From, int To, string Mode), RouteSegment> cache)
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

        var finishIsDistinctStop = finishPoiId is { } fid
            && fid != stops[0].PoiId
            && stops.Any(s => s.PoiId == fid);
        if (!finishIsDistinctStop)
        {
            legs.Add(MakeLeg(stops[^1], stops[0], cache));
        }

        return legs;
    }

    /// <summary>Builds a leg with cached duration/distance/fidelity, or null fields if uncomputed.</summary>
    private static TripLeg MakeLeg(
        TripStop from, TripStop to, IReadOnlyDictionary<(int From, int To, string Mode), RouteSegment> cache)
    {
        var legMode = from.OutgoingTravelMode ?? Data.Entities.TravelMode.AnyAir;
        cache.TryGetValue((from.PoiId, to.PoiId, legMode), out var seg);
        var fidelity = seg?.Fidelity;
        var displayDuration = fidelity == Fidelity.Placeholder ? null : seg?.DurationSeconds;
        var isFallback = seg?.Source == TravelTimeSource.EstimatedFallback;
        return new TripLeg(
            from.PoiId, to.PoiId, from.Lat, from.Lon, to.Lat, to.Lon,
            IsMeasured: fidelity == Fidelity.Measured,
            DurationSeconds: displayDuration,
            DistanceMeters: seg?.DistanceMeters,
            Fidelity: fidelity,
            IsFallback: isFallback,
            GeometryPolyline: seg?.GeometryPolyline,
            Mode: legMode);
    }

    /// <summary>
    /// Sum of all legs' durations (rendered "—" when any leg lacks a known duration).
    /// IsAnyLegComputing is driven by fidelity (computing ⇔ null fidelity), not duration.
    /// </summary>
    private void RecomputeTotal()
    {
        IsAnyLegComputing = OrderedLegs.Any(l => l.Fidelity is null);
        var allDurationsKnown = OrderedLegs.Count > 0 && OrderedLegs.All(l => l.DurationSeconds is not null);
        TotalTravelTimeSeconds = allDurationsKnown
            ? OrderedLegs.Sum(l => TravelTimeFormatting.DisplayMinutes(l.DurationSeconds!.Value)) * 60
            : null;
    }

    /// <summary>
    /// Recomputes the itinerary timeline from already-built projections, trip shape, and
    /// persisted start time / budget. Pure presentation-only.
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

        var legs = OrderedLegs
            .Select(l => new ItineraryLegInput(l.DurationSeconds, l.Fidelity))
            .ToList();

        var unplaceableDwell = StopRows
            .Where(r => !r.IsPlaceable)
            .Select(r => r.DwellMinutes)
            .ToList();

        var hasClosingLeg = legs.Count >= stops.Count;

        Timeline = ItineraryTimeline.Compute(
            stops, legs, unplaceableDwell, hasClosingLeg, TripStartTime, TimeBudgetMinutes);
    }

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
    /// Persists a new travel mode and triggers a recompute.
    /// No-op when mode is invalid or already active.
    /// </summary>
    public async Task SetTravelModeAsync(string mode)
    {
        if (ActiveCollectionId is not { } collectionId || !IsTripViewEnabled)
        {
            return;
        }

        if (!Data.Entities.TravelMode.IsValid(mode) || mode == TravelMode)
        {
            return;
        }

        try
        {
            await PersistTravelModeAsync(collectionId, mode);
            await RefreshProjectionsAsync(collectionId);
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

    /// <summary>
    /// Sets ONE leg's travel mode and refreshes projections. Ground modes signal
    /// background compute; Any/Air is manual-only. Guards: active collection + Trip View on.
    /// </summary>
    public async Task SetLegModeAsync(int fromPoiId, string mode)
    {
        if (ActiveCollectionId is not { } collectionId || !IsTripViewEnabled)
        {
            return;
        }

        try
        {
            await ordering.SetOutgoingTravelModeAsync(collectionId, fromPoiId, mode, _cts.Token);
            await RefreshProjectionsAsync(collectionId);

            if (mode is Data.Entities.TravelMode.Walk
                or Data.Entities.TravelMode.Drive
                or Data.Entities.TravelMode.Cycle)
            {
                travelTimeTrigger.Signal();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set leg mode {Mode} for POI {PoiId} in collection {CollectionId}", mode, fromPoiId, collectionId);
            return;
        }

        Notify();
    }

    /// <summary>
    /// Assigns one travel mode to all trip legs at once. Ground modes signal background compute;
    /// Any/Air manual-only. When overwriteExisting is false, only unset (Any/Air) legs change.
    /// </summary>
    public async Task SetAllLegsModeAsync(string mode, bool overwriteExisting)
    {
        if (ActiveCollectionId is not { } collectionId || !IsTripViewEnabled)
        {
            return;
        }

        try
        {
            await ordering.SetAllOutgoingTravelModesAsync(collectionId, mode, overwriteExisting, _cts.Token);
            await RefreshProjectionsAsync(collectionId);

            if (mode is Data.Entities.TravelMode.Walk
                or Data.Entities.TravelMode.Drive
                or Data.Entities.TravelMode.Cycle)
            {
                travelTimeTrigger.Signal();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set all leg modes to {Mode} (overwrite={Overwrite}) in collection {CollectionId}", mode, overwriteExisting, collectionId);
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

    /// <summary>Upper bound on a manual leg time: 60 days in minutes.</summary>
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
        var seconds = minutes * 60;
        var mode = from.OutgoingTravelMode ?? Data.Entities.TravelMode.AnyAir;

        try
        {
            await UpsertManualSegmentAsync(fromPoiId, toPoiId, mode, seconds, meters);
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

    /// <summary>Upper bound on a per-stop dwell time: 60 days in minutes.</summary>
    internal const int MaxDwellMinutes = TripOrderingService.MaxDwellMinutes;

    /// <summary>Sets the dwell time on the active collection's membership for a POI.</summary>
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

    private Task PersistDwellMinutesAsync(int collectionId, int poiId, int? minutes) =>
        ordering.SetDwellMinutesAsync(collectionId, poiId, minutes, _cts.Token);

    /// <summary>Upper bound on the soft time budget, in minutes: 60 days.</summary>
    internal const int MaxBudgetMinutes = 60 * 24 * 60;

    /// <summary>Sets the per-trip wall-clock start time; null clears it (relative offsets only).</summary>
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

    /// <summary>Sets the per-trip soft time budget in minutes; null clears it (no overrun flag).</summary>
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
    /// Clears a manual leg time on any leg. Ground legs revert to re-computed values;
    /// Any/Air reverts to "—". No-op when no row exists.
    /// </summary>
    public async Task ClearManualLegTimeAsync(int fromPoiId, int toPoiId)
    {
        if (ActiveCollectionId is not { } collectionId || !IsTripViewEnabled)
        {
            return;
        }

        var mode = OrderedStops.FirstOrDefault(s => s.PoiId == fromPoiId)?.OutgoingTravelMode
                   ?? Data.Entities.TravelMode.AnyAir;

        try
        {
            await DeleteSegmentAsync(fromPoiId, toPoiId, mode);
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
    /// User-initiated recompute of travel times. Invalidates recompute-eligible cached rows
    /// (not Manual or higher-fidelity rows), then refreshes projections.
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

    /// <summary>Explicit on-demand sort in Traveling Salesman order.</summary>
    public async Task SortTravelingSalesmanAsync()
    {
        if (ActiveCollectionId is not { } collectionId || !IsTripViewEnabled)
        {
            return;
        }

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

    private async Task UpsertManualSegmentAsync(int fromPoiId, int toPoiId, string mode, int seconds, double meters)
    {
        await using var db = await factory.CreateDbContextAsync(_cts.Token);
        var existing = await db.RouteSegments.FirstOrDefaultAsync(
            r => r.FromPoiId == fromPoiId
                 && r.ToPoiId == toPoiId
                 && r.TravelMode == mode,
            _cts.Token);

        if (existing is null)
        {
            db.RouteSegments.Add(new RouteSegment
            {
                FromPoiId = fromPoiId,
                ToPoiId = toPoiId,
                TravelMode = mode,
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

    private async Task DeleteSegmentAsync(int fromPoiId, int toPoiId, string mode)
    {
        await using var db = await factory.CreateDbContextAsync(_cts.Token);
        var existing = await db.RouteSegments.FirstOrDefaultAsync(
            r => r.FromPoiId == fromPoiId
                 && r.ToPoiId == toPoiId
                 && r.TravelMode == mode,
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
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _progressSubscription?.Dispose();
        _progressSubscription = null;
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
