# Trip Planning (as-built)

_The Trip Planning vertical slice — Epics 1–4, all 17 FRs — built additively on
LucidCartographer. Trip View is a **lens** over an existing `PoiCollection`, not a
new top-level entity. Source: `Services/Trip/`, `Components/Shared/Trip/`,
`Services/Mcp/TripTools.cs`, `Data/Entities/{RouteSegment,Fidelity,TravelMode}.cs`,
`Configuration/TripServicesExtensions.cs`, `Migrations/*_AddTripPlanning.cs`._

Planning artifacts: `_bmad-output/planning-artifacts/architecture.md` (D1–D11),
`_bmad-output/planning-artifacts/epics.md` (Epics 1–4, FR/AR map). This document
records what was **actually built**, flagging where the as-built diverges from the
plan.

## What it does

Flip Trip View on for a Collection and it becomes an ordered, mapped trip:

- **Stop Order** — contiguous 1-based numbering (1..N), seeded by POI added-date,
  drawn as badges in the stop list and on map markers.
- **Legs** — straight connectors between consecutive Stops (plus the closing leg on
  a roundtrip), drawn on the map; road-shaped solid lines only when a Measured
  (OSRM) provider supplied geometry.
- **Reorder** — drag or keyboard move-up/move-down (a11y path), TSP-Sort, or via MCP.
- **Travel times** — per-leg duration/distance with an honest **Fidelity** badge,
  from a pluggable provider; haversine **Mock** is the shipping default.
- **Dwell + timeline** — per-Stop dwell minutes feed an itinerary timeline that
  obeys the aggregate "lowest-fidelity-wins" honesty rule.
- **Start/Finish/roundtrip** — pin a Start (Order 1) and optional Finish (Order N);
  no Finish ⇒ roundtrip with a closing leg.

All affordances exist on both the desktop and `Mobile*` render paths; all copy
routes through `UiStrings`. Design decisions are tagged with greppable `TRIP-*`
comment codes (e.g. `TRIP-CACHE-01`, `TRIP-ORDER-01`, `TRIP-OSRM-01`).

## Data model

The migration `20260611213107_AddTripPlanning` (applied via startup `MigrateAsync`)
adds the trip shape. See [data-models.md](./data-models.md) for the full schema; the
trip-specific shape:

- **`PoiCollectionItem`** (the join) gains `OrderIndex` (int, 1-based; 0 = "not a
  Stop", e.g. unplaceable) and `DwellMinutes` (int?, per-membership so the same POI
  carries different dwell across trips).
- **`PoiCollection`** gains `TravelMode` (string, default `AnyAir`), `StartPoiId`/
  `FinishPoiId` (nullable FK, `SetNull`), `TripStartTime` (nullable), `TimeBudgetMinutes`
  (int?), `TripViewEnabled` (bool, per-collection persistence).
- **`RouteSegment`** (new — the Leg / Distance-Matrix cache): composite PK
  `(FromPoiId, ToPoiId, TravelMode)` — **directional** (`TRIP-CACHE-01`: A→B ≠ B→A,
  never collapsed); columns `DurationSeconds` (int, canonical **seconds**),
  `DistanceMeters` (double, canonical **meters**), `GeometryPolyline` (string?, null
  = no road geometry → dashed render), `Fidelity`, `Source`, `ComputedAt`, and a
  `Version` `[ConcurrencyCheck]` token. Cascade FKs to `Pois`; indexes on both FK
  columns.

**String-persisted enums** (`TRIP-SCHEMA-01`, matching the `PoiCategory` precedent —
NOT int-backed), each guarded by an EF check constraint built from the enum's `All`
list so SQL can never drift:

- `TravelMode` (`Data/Entities/TravelMode.cs`): `AnyAir`, `Drive`, `Walk`, `Cycle`.
- `Fidelity` (`Data/Entities/Fidelity.cs`): `Measured`, `Estimated`, `Placeholder`,
  `Manual`.

The migration backfills existing rows: `TravelMode` defaults to `AnyAir`;
`OrderIndex` is seeded 1..N per collection over placeable members (added-date asc,
tie-broken by PoiId) via a `ROW_NUMBER() … UPDATE…FROM` statement, leaving
non-placeable members at 0.

## The provider seam (`ITravelTimeProvider`)

`Services/Trip/ITravelTimeProvider.cs` is the single gateway to travel data
(`TRIP-TRAVELTIME-01`, AR-2). One active provider is config-selected per deployment;
the haversine Mock is the universal Estimated fallback (FR-10).

```csharp
Task<TravelLegResult> GetLegAsync(
    TravelEndpoint from, TravelEndpoint to, string travelMode, CancellationToken ct);
string  Source { get; }       // written to RouteSegment.Source
string? Attribution { get; }  // OSM/ODbL HTML for the map, or null (TRIP-OSRM-02)
```

- `TravelEndpoint(int PoiId, double Latitude, double Longitude)` — a **layer-local**
  record struct. **Deviation from plan:** the architecture named the parameters as
  the VM's `TripStop`, but Services must not reference Components, so the provider
  takes this Data-layer-friendly endpoint instead. Only placeable stops reach a
  provider (coordinates non-nullable).
- `TravelLegResult(int DurationSeconds, double DistanceMeters, string Fidelity,
  string? GeometryPolyline)` — immutable, canonical units, no UI-edge conversion.

### Implementations (`Services/Trip/`)

- **`MockTravelTimeProvider`** — shipping default, zero infra. Haversine × assumed
  speed → **Estimated**; null geometry; null `Attribution` (a great-circle estimate
  is not OSM-derived). **Deviation:** named `MockTravelTimeProvider`, not the plan's
  `HaversineMockTravelTimeProvider`; and there is no `Providers/` subfolder — all
  providers live directly under `Services/Trip/`.
- **`OsrmTravelTimeProvider`** (`TRIP-OSRM-01`, Story 4.1, optional) — for
  Drive/Walk/Cycle issues a per-leg OSRM `/route` query against the per-profile
  backend (Drive→car, Walk→foot, Cycle→bike) and returns **Measured** with encoded
  road geometry. **Deviations from AR-3:** it calls `/route` per leg only (no
  `/table`; the matrix is built from the cache, not a provider call), and stores an
  **encoded polyline** (`geometries=polyline`, precision 5) verbatim rather than
  GeoJSON (more compact; decodes natively in Leaflet for Story 4.2). Any/Air is never
  routed (returns a straight-line Placeholder, like Mock). Degrades **by throwing**
  `OsrmRouteUnavailableException` on no-route/unreachable/timeout/HTTP-error/missing
  geometry, so the background service substitutes the haversine Estimated value. NFR7:
  self-hosted ⇒ no egress, no consent guard. There is **no** separate
  `ManualTravelTimeProvider` — Manual times are written directly as `RouteSegment`
  rows by the VM, never produced by a provider.

`EstimatedTravelTime.Compute(...)` (`Services/Trip/EstimatedTravelTime.cs`) is the
shared haversine helper used by both the Mock provider and the degradation fallback.
`TravelTimeSource` holds the provider-id constants (`Osrm`, `EstimatedFallback`, …).

## Background compute (`TravelTimeComputationBackgroundService`)

`TRIP-TRAVELTIME-01` / AR-5 — a `BackgroundService` mirroring
`PoiEnrichmentBackgroundService`. It blocks on `TravelTimeTrigger.WaitAsync` (with an
idle poll from `TravelTimeOptions.IdlePollSeconds`); on each wake it loads every
Trip-View-enabled collection's ordered placeable stops, forms the directional leg
pairs (consecutive `k→k+1`, plus the closing leg back to Start on a roundtrip — the
same shape as `TripViewModel.BuildLegs` and `TripTools.GetTrip`), and for each pair
**lacking a cache row** calls the active provider through the Polly **"travel-time"**
pipeline. Results are upserted into `RouteSegment` under the shared `SqliteWriteLock`.

Key behaviours:

- **Compute-on-miss only** — a leg is computed iff no row exists for its
  `(FromPoiId, ToPoiId, TravelMode)` key. (Recompute/invalidation is the separate
  invalidation service below.)
- **Graceful degradation** (`TRIP-DEGRADE-01`, Story 2.3) — a provider exception is
  caught per-leg, substituted with the haversine Estimated value, stamped
  `Source = EstimatedFallback`, and logged at Warning naming the leg + degraded
  fidelity (NFR6). One bad leg never fails the pass.
- **No-downgrade guard** — `UpsertAsync` never overwrites a `Manual` or `Measured`
  row (a degraded estimate can't replace a real measured time; a user's manual entry
  is never clobbered).
- **Progress** — `TravelTimeProgressService` tracks the pending count; the VM
  subscribes and re-reads projections + `Notify()`s when it changes (never polls).

## Cache invalidation (`RouteSegmentInvalidationService`)

`TRIP-INVALIDATE-01` / Story 2.4 (`IRouteSegmentInvalidationService`). **Deviation:**
the plan named a combined `RouteSegmentCacheService` (read + invalidate + upgrade);
the as-built splits responsibilities — the background service owns writes, this
service owns deletes, and reads happen inline where needed. It deletes stale rows
(both directions, all modes, **never** a `Manual` row) under the `SqliteWriteLock`;
the background compute refills them on the next trigger. Entry points:
`InvalidateForPoiAsync(poiId)` (coordinates changed) and
`InvalidateRecomputableForCollectionAsync(collectionId)` (the explicit "Recompute
travel times" action, which clears Estimated/fallback rows so OSRM can upgrade them
to Measured — `Estimated→Measured` is explicit, never silent).

## Ordering (`TripOrderingService`) — the single OrderIndex writer

`Services/Trip/TripOrderingService.cs` is the **sole writer** of
`PoiCollectionItem.OrderIndex` (AR-11). All four ordering paths — drag, keyboard,
TSP-Sort, MCP — funnel through its one private `SetOrderAsync` (commit under
`SqliteWriteLock`, full 1..N renumber so the result is contiguous, gap-free, unique
by construction). **Deviation:** registered **Scoped** (matching the
`IPoiDeduplicationService` per-slice precedent), not the Transient the plan implied
for VM-facing services.

Notable methods: `SeedOrderAsync` (added-date seed), `AppendStopAsync` /
`CompactOrderAsync` / `ReconcileOrderAsync` (membership-change upkeep, including
releasing orphaned Start/Finish pins — `TRIP-STARTFINISH-07`), `ReorderStopAsync`
(drag + keyboard, pin-aware movable window), `Set/Clear Start/Finish` (pins, with a
"a stop can't be both" guard), `AssignOrderAsync` (MCP full-order, validates the
input is exactly the placeable Stop set), `SetDwellMinutesAsync` (shared dwell write,
0..`MaxDwellMinutes` = 60 days), and `SortTravelingSalesmanAsync`.

### TSP-Sort (`TspSolver`)

`TRIP-TSP-01` / AR-6 — a **pure** static `TspSolver` (`Services/Trip/TspSolver.cs`),
no OR-Tools, no I/O: nearest-neighbour construction + 2-opt local search over a
pre-built N×N **seconds** cost matrix, returning a permutation of matrix indices.
Pinned Start fixed at position 0, pinned Finish at the last position; 2-opt only
reverses interior segments. Because the cache key is directional the matrix can be
asymmetric, so 2-opt evaluates the **full** `TourCost` per trial (not the symmetric
boundary-edge delta) — O(n³)/sweep, inside the N≤30 p95≤3s target; capped at 64
sweeps for termination. `SortTravelingSalesmanAsync` enforces the AC4 never-worse
guard: it keeps the optimized tour only when strictly cheaper than the current order,
else retains the existing order — so the result is always ≤ pre-sort.

### Distance Matrix (`DistanceMatrixService`)

`TRIP-MATRIX-01` / D11 — builds the N×N matrix from the **shared** `RouteSegment`
cache (one cache, two readers) under the collection's persisted `TravelMode`,
directionally. Any uncached pair is filled with the haversine straight-line estimate
so the matrix is always complete. **Read-only** input to TSP — it never writes the
estimated fill values back to the cache.

## Itinerary timeline (`ItineraryTimeline`)

`TRIP-TIMELINE-01` / Story 2.6 — a **pure** static `Compute(...)` (no DB/IO/state) so
the honesty rule is exhaustively unit-testable. Walks ordered placeable stops + legs:
`arrival(1) = TripStart` (or offset 0); `departure(k) = arrival(k) + Dwell(k)`;
`arrival(k+1) = departure(k) + Travel(k→k+1)`. The Start's dwell counts once; a
roundtrip adds a distinct return-to-Start arrival via the closing leg. Honesty rules:

- A cumulative arrival inherits the **lowest** fidelity among the legs summed to it.
  Rank: Unknown (Placeholder or null duration) = 0, Estimated = 1, Manual/Measured =
  2. Any rank-0 leg upstream ⇒ the arrival is **Unknown** ("—", no offset/clock) — it
  never guesses across an unknown leg.
- Wall-clock arrivals appear only when `TripStartTime` is set; otherwise relative
  offsets only.
- Unplaceable stops contribute their dwell to the total but no per-stop arrival and no
  travel time.
- A soft over-budget flag fires **only** when a budget is set AND the total is known
  AND exceeds it (never a false overrun); rendered amber `warn`.

## MCP trip tools (`Services/Mcp/TripTools.cs`)

`TRIP-MCP-01` / AR-8 / FR-16 — auto-discovered (`WithToolsFromAssembly`) and served by
the existing authenticated `/mcp` endpoint (three-tier LAN → API key → OAuth; **no**
new unauthenticated surface). Every write delegates to `ITripOrderingService` (the
single OrderIndex writer), so an MCP-assigned order persists identically to a manual
drag and stays drag-editable. Tools (durations in seconds, distances in meters):

- `get_trip(collectionId)` — ordered placeable Stops (1-based, Start/Finish flags,
  dwell) + cached directional Legs under the collection's TravelMode.
- `assign_stop_order(collectionId, orderedPoiIds[])` — full reorder; input must be
  exactly the placeable Stop set (else errors); pinned Start/Finish stay first/last.
- `set_trip_start` / `set_trip_finish` / `clear_trip_start` / `clear_trip_finish`.
- `set_dwell_time(collectionId, poiId, minutes?)` — set/clear; out-of-range ignored.

**Deviation from plan:** tool names are `get_trip` / `assign_stop_order` /
`set_trip_start` / `set_trip_finish` / `set_dwell_time` (snake_case, MCP convention),
not the plan's `GetTripStops` / `SetStartFinish`; and Start/Finish are separate
set/clear tools rather than one `SetStartFinish`.

## UI (`Components/Shared/Trip/`)

`TripViewModel` (sealed, **Transient**, primary-ctor DI, `StateChanged` + `Notify`,
owns a CTS, `IAsyncDisposable`) holds all trip state; the map page composes it. It
delegates all order mutation to `ITripOrderingService`, subscribes to
`TravelTimeProgressService`, and exposes `RoutingAttributionHtml` (read off the active
provider's `Attribution`, pushed to Leaflet's attribution control — OSM/ODbL when OSRM
is active, null under Mock). `TripProjections.cs` holds the VM's read-model record
types.

Razor components (each with a desktop + mobile counterpart per UX-DR12):
`TripToggle` / `MobileTripToggle` (filtered-results-region switch, `aria-pressed`,
≥2-placeable gate), `TripStopList` / `MobileTripPanel` (order badge · name · dwell ·
timeline value · keyboard move up/down), `TravelModeSelector` (segmented control),
`StopOrderBadge`, `FidelityBadge` (Measured/Estimated/Manual pill; "—" em-dash for any
unmeasured/unentered leg — Placeholder is internal-only).

**Deviation from plan:** the as-built component set is `TripToggle`, `TripStopList`,
`StopOrderBadge`, `MobileTripToggle`, `MobileTripPanel` etc., not the plan's
`TripPanel` / `TripViewToggle` / `StopListRow` / `ItineraryTimeline.razor` names; the
timeline renders inside the stop list rather than as a standalone component.

## DI wiring (`Configuration/TripServicesExtensions.cs`)

Two overloads:

- `AddTripServices()` (parameterless) — the VM-facing services the integration host
  composes by hand: `DistanceMatrixService` + `TripOrderingService` (Scoped),
  `RouteSegmentInvalidationService` (Scoped), the `TravelTimeTrigger` +
  `TravelTimeProgressService` singletons, and a default `MockTravelTimeProvider`
  singleton (so the VM ctor can read a provider's attribution). Reuses the shared
  `SqliteWriteLock` singleton, registering a fallback only if no pipeline registered
  one first.
- `AddTripServices(IConfiguration)` — production wiring. Calls the parameterless
  overload, then selects the active provider by `TravelTime:Provider`: `Osrm` swaps in
  `OsrmTravelTimeProvider` (binds `OsrmOptions`, registers the named `"osrm"`
  HttpClient); anything else (missing / `Mock`) keeps the Mock (NFR9: OSRM is opt-in,
  never default — last registration wins on resolve). Adds the hosted
  `TravelTimeComputationBackgroundService` and binds `TravelTimeOptions`. `Program.cs`
  calls this overload.

## Extension points

- **Add a travel-time provider** — implement `ITravelTimeProvider`, register it in the
  `IConfiguration` overload behind a new `TravelTime:Provider` value. The cache,
  matrix, TSP, timeline, and UI all read through the contract — no other layer
  changes. (Per-leg Travel-Mode override / mixed-mode trips is the documented post-v1
  follow-up.)
- **Add an MCP trip tool** — add a `[McpServerTool]` method to `TripTools`; delegate
  writes to `ITripOrderingService`. Auto-discovered, inherits the `/mcp` auth.
- **Swap the map renderer** — leg geometry flows VM → `LeafletMapService` →
  `leafletInterop.js`; the server-side `RouteSegment` cache stays the single source of
  truth (the map widget never calls OSRM directly).

## Operating OSRM

OSRM is an optional, opt-in docker-compose sidecar — **not** a launch dependency; the
default Mock deployment needs none of it. See **[osrm.md](./osrm.md)** for the full
operator guide (preparing per-profile extracts, the `osrm` compose profile, pointing
the app at the backends, and upgrading existing Estimated legs to Measured).
