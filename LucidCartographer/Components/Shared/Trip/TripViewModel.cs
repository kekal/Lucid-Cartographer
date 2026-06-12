using System.Collections.ObjectModel;
using LucidCartographer.Data;
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
    ILogger<TripViewModel> logger) : IAsyncDisposable
{
    private static readonly IReadOnlyDictionary<int, int> NoStops =
        new ReadOnlyDictionary<int, int>(new Dictionary<int, int>());

    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

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
    /// Ordered, placeable-only stop projection (1-based, ascending) for the
    /// stop-list panel and numbered markers; empty when Trip View is off.
    /// </summary>
    public IReadOnlyList<TripStop> OrderedStops { get; private set; } = [];

    /// <summary>
    /// Straight connecting legs between consecutive placeable stops, plus the
    /// closing leg back to the Start on a Roundtrip (no distinct Finish). Empty
    /// when Trip View is off or fewer than two placeable stops exist. Every leg
    /// is non-Measured in Phase 1 (TRIP-LEG-01).
    /// </summary>
    public IReadOnlyList<TripLeg> OrderedLegs { get; private set; } = [];

    /// <summary>Localized on/off text for the aria-live announcement region; null until first toggle.</summary>
    public string? Announcement { get; private set; }

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
            Notify();
            return;
        }

        try
        {
            IsTripViewEnabled = await ReadTripViewEnabledAsync(collectionId.Value);
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

    private void ClearProjections()
    {
        StopOrders = NoStops;
        OrderedStops = [];
        OrderedLegs = [];
        // TRIP-SELECT-01: selection is transient and only valid while Trip View
        // is on — drop it (and its announcement) whenever the projections clear
        // so toggling off never leaves a stale SelectedStopPoiId (AC4).
        SelectedStopPoiId = null;
        SelectedStop = null;
        SelectionAnnouncement = null;
    }

    /// <summary>
    /// Reads the persisted Stop Order with coordinates in one pass and rebuilds
    /// the three projections (<see cref="StopOrders"/>, <see cref="OrderedStops"/>,
    /// <see cref="OrderedLegs"/>) consistently. Call only when Trip View is on.
    /// </summary>
    private async Task RefreshProjectionsAsync(int collectionId)
    {
        var (startPoiId, finishPoiId) = await ReadStartFinishAsync(collectionId);
        var stops = await ReadOrderedStopsAsync(collectionId, startPoiId, finishPoiId);

        OrderedStops = stops;
        StopOrders = stops.Count == 0
            ? NoStops
            : stops.ToDictionary(s => s.PoiId, s => s.OrderIndex);
        OrderedLegs = BuildLegs(stops, finishPoiId);

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
    /// Reads the active collection's placeable, ordered stops (OrderIndex &gt; 0,
    /// both coordinates non-null) ascending by Stop number, projecting the POI
    /// name + coordinates. Mirrors the null-coord filter the ordering service and
    /// <c>LeafletMapService</c> both apply, so coordinate-less stops are excluded
    /// from legs and numbered markers without renumbering (Story 1.6 owns the
    /// "Not placeable" labelling).
    /// </summary>
    private async Task<IReadOnlyList<TripStop>> ReadOrderedStopsAsync(int collectionId, int? startPoiId, int? finishPoiId)
    {
        await using var db = await factory.CreateDbContextAsync(_cts.Token);
        var rows = await db.PoiCollectionItems
            .AsNoTracking()
            .Where(ci => ci.PoiCollectionId == collectionId && ci.OrderIndex > 0
                && ci.Poi.Latitude != null && ci.Poi.Longitude != null)
            .OrderBy(ci => ci.OrderIndex)
            .Select(ci => new
            {
                ci.PoiId,
                ci.OrderIndex,
                ci.Poi.Name,
                Lat = ci.Poi.Latitude!.Value,
                Lon = ci.Poi.Longitude!.Value,
            })
            .ToListAsync(_cts.Token);

        return rows
            .Select(r => new TripStop(
                r.OrderIndex,
                r.PoiId,
                r.Name,
                r.Lat,
                r.Lon,
                r.PoiId == startPoiId,
                r.PoiId == finishPoiId))
            .ToList();
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
    /// TRIP-LEG-02: builds the straight connecting legs from the ordered stops.
    /// Consecutive pairs (k → k+1) give N−1 legs; when there is no distinct
    /// Finish (Roundtrip — <paramref name="finishPoiId"/> null or equal to Start,
    /// which is Order 1) the closing leg from the last stop back to the Start is
    /// appended, giving N legs. A distinct Finish leaves the path open (N−1 legs,
    /// no closing leg). Every leg is non-Measured in Phase 1 (TRIP-LEG-01).
    /// </summary>
    private static IReadOnlyList<TripLeg> BuildLegs(IReadOnlyList<TripStop> stops, int? finishPoiId)
    {
        if (stops.Count < 2)
        {
            return [];
        }

        var legs = new List<TripLeg>(stops.Count);
        for (var k = 0; k < stops.Count - 1; k++)
        {
            legs.Add(MakeLeg(stops[k], stops[k + 1]));
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
            legs.Add(MakeLeg(stops[^1], stops[0]));
        }

        return legs;
    }

    private static TripLeg MakeLeg(TripStop from, TripStop to) =>
        new(from.PoiId, to.PoiId, from.Lat, from.Lon, to.Lat, to.Lon, IsMeasured: false);

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
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
