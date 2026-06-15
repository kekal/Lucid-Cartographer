# Trip Planning (as-built)

_The Trip Planning vertical slice on LucidCartographer. Trip View is a **lens** over
an existing `PoiCollection`, not a new top-level entity. Source: `Services/Trip/`,
`Components/Shared/Trip/`, `Services/Mcp/TripTools.cs`,
`Data/Entities/{RouteSegment,Fidelity,TravelMode}.cs`,
`Configuration/TripServicesExtensions.cs`,
`Migrations/*_AddTripPlanning.cs` + `*_AddOutgoingTravelMode.cs`._

This slice shipped in two feature waves:

- **Wave 1 — Trip Planning** (original Epics 1–4, 17 FRs): the lens, the directional
  `RouteSegment` leg cache, the provider seam, off-circuit compute, TSP, the
  itinerary timeline, and the MCP trip tools. Planning archive:
  `_bmad-output/archive/trip-planning/planning-artifacts/` (D1–D11).
- **Wave 2 — Trip View: Layout Realignment & Honest Schedule** (a brownfield delta,
  new Epics 1–4, 19 stories, all 33 FRs, decisions **RD1–RD13**): desktop Trip View
  takeover, reconciled "honest" times, **per-leg travel modes end-to-end** (the one
  new migration), and a multi-day schedule. Planning: the **current**
  `_bmad-output/planning-artifacts/{architecture.md (RD1–RD13), epics.md, prds/}`.
  Retros: `_bmad-output/implementation-artifacts/epic-{1,2,3,4}-retro-2026-06-15.md`.

This document records what was **actually built**, flagging where the as-built
diverges from the plan and which Wave-2 controls are still **desktop-only** (the
mobile mirror phase is deferred).

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

Wave-1 affordances exist on both the desktop and `Mobile*` render paths. Wave-2 added
desktop-only **controls** (per-leg mode pill, leg-time inline edit, datetime-local
start, HH:MM dwell/time-limit pickers) while its **shared logic** (the reconciled
display model, per-leg-mode VM projection, date-aware formatting, `UiStrings`) reaches
mobile by nature — `MobileTripPanel` stays correct, only its new controls are deferred
to the mirror phase (see "Deferred / tech-debt"). All copy routes through `UiStrings`.
Design decisions are tagged with greppable `TRIP-*` comment codes (e.g. `TRIP-CACHE-01`,
`TRIP-ORDER-01`, `TRIP-OSRM-01`, and the Wave-2 codes `TRIP-LEGMODE-01`,
`TRIP-RECONCILE-01`, `TRIP-SCHEDULE-01`, `TRIP-MANUAL-01`).

## Wave 2 — Trip View realignment & honest schedule (RD1–RD13)

The Wave-2 delta reused the Wave-1 seams unchanged in shape (provider, directional
cache, sole `OrderIndex` writer, background compute) and added:

- **Desktop takeover (RD8, Epic 1).** When `TripVm.IsTripViewEnabled`, the desktop
  **filtered-results region renders `TripStopList` instead of `PoiTable`** — a
  replacement, not the old additive `w-64` side column (which is gone, along with the
  selection batch toolbar). Toggling off restores `PoiTable` unchanged. The wide trip
  table is a CSS-grid of aligned columns: reorder gutter (drag + ▲▼) · Stop # badge
  (Start/Finish glyph) · full Name + address sub-line + enrichment icon · Dwell
  (HH:MM) · date-aware Arrival · Start/Finish · Actions (Focus on map + Open in Google
  Maps only). Per-leg travel info is **not** a row column — it lives on the connector.
  See `Components/Pages/MapPage.razor` (the `IsTripViewEnabled` branch ~line 329) and
  `TripStopList.razor`.
- **Single canonical Stop Order (RD8/FR-4, Epic 1).** The plain Filtered Results list
  (Trip View off) renders in the same `OrderIndex` via `TripViewModel.CanonicalStopOrder`
  + `ApplyCanonicalOrder(...)` — a cached map + a pure stable sort applied by `MapPage`
  to `Vm.FilteredPois` (no DB access per render). A never-ordered / multi-collection
  scope keeps the default sort.
- **Inter-row `LegConnector` (RD9, Epic 1/3).** A compact, presentational single-line
  strip on the shared edge between two consecutive rows (and a closing connector after
  the last row): `↓` glyph · click-to-edit travel time ("min") · `·` distance ·
  `FidelityBadge` · the per-leg `LegModePill` · a reset (↺) shown only for a Manual
  leg (hover/focus-revealed). It raises VM commands only — never touches services/DB.
- **Honest / reconciled times (RD4/RD5, Epic 2, shared layer).** `TravelTimeFormatting.DisplayMinutes(seconds)`
  is the **single** rounding edge (nearest minute, round-half-up); both the per-leg
  display and the timeline accumulation round through it, so **displayed total == Σ
  displayed per-leg minutes** (TRIP-RECONCILE-01; the 90+90s → 4 min, not 3, invariant
  is unit-tested). Canonical seconds are never mutated. The minute unit is **"min"**
  (`UiStrings.TripDuration*`), disambiguated from distance **"m"**. `FidelityBadge`
  tooltips self-explain in plain language. `TripViewModel.RecommendsOsrm` drives a
  quiet Mock-default note ("all straight-line estimates — enable OSRM for measured road
  times", linking `docs/osrm.md`) — distinct from the engine-unreachable fallback note
  (`IsShowingApproximateEstimates`). That link is served by `Endpoints/DocsEndpoints.cs`
  (`GET /docs/osrm.md`, embedded operator guide — see [api-contracts.md](./api-contracts.md));
  it is **not** a wwwroot static file (`.md` is unserved and Docker-stripped). Icon-only controls carry native `title` tooltips
  at `aria-label` parity.
- **Per-leg travel modes end-to-end (RD1/2/3/6/7, Epic 3).** See the dedicated
  "Per-leg travel modes" section below.
- **Multi-day schedule + honest finish (RD10/13, Epic 4, no schema change).** Start is
  a native `datetime-local` writing the existing `TripStartTime`; wall-clock arrivals
  roll across midnight and a later-day arrival shows its locale-driven date
  (`TravelTimeFormatting.WallClockText`). The renamed **Time limit** is entered as an
  HH:MM duration OR a finish-by deadline computed **once** as `deadline − start` and
  stored as `TimeBudgetMinutes` (TRIP-SCHEDULE-01 — never recomputed); a soft amber
  **"Over limit"** chip shows when the known total exceeds it. Dwell uses an HH:MM
  picker. A designated Finish reads "Finish" + its dated arrival; roundtrip default
  reads "Return to start" (`IsRoundtrip => FinishPoiId is null`). All HH:MM/date ↔
  canonical conversions happen only at the UI edge.

## Data model

Two migrations (both applied via startup `MigrateAsync`) build the trip shape:
`20260611213107_AddTripPlanning` (Wave 1) and `20260615160622_AddOutgoingTravelMode`
(Wave 2). See [data-models.md](./data-models.md) for the full schema; the
trip-specific shape:

- **`PoiCollectionItem`** (the join) gains `OrderIndex` (int, 1-based; 0 = "not a
  Stop", e.g. unplaceable), `DwellMinutes` (int?, per-membership so the same POI
  carries different dwell across trips), and — Wave 2 — **`OutgoingTravelMode`**
  (string?, max-len 20, one of `TravelMode.All`; **null ≡ AnyAir**, one state, no
  separate "unset" sentinel — TRIP-LEGMODE-01). It is the mode of the leg **leaving**
  this stop toward the next Stop in order. Added by `AddOutgoingTravelMode` and guarded
  by the check constraint `CK_PoiCollectionItem_OutgoingTravelMode`
  (`OutgoingTravelMode IS NULL OR OutgoingTravelMode IN ('AnyAir','Drive','Walk','Cycle')`),
  built from `NullableEnumCheckSql(...)` over `TravelMode.All`.
- **`PoiCollection`** has `StartPoiId`/`FinishPoiId` (nullable FK, `SetNull`),
  `TripStartTime` (nullable), `TimeBudgetMinutes` (int?), `TripViewEnabled` (bool,
  per-collection persistence), and the legacy `TravelMode` (string, default `AnyAir`).
  **`PoiCollection.TravelMode` is no longer the leg driver** (FR-23): per-leg modes
  replaced it. Per RD1a it was kept as a **dead-ish column** (NOT dropped) — the
  `AddOutgoingTravelMode` migration only adds the new column. It is still written by
  the inert mobile `TravelModeSelector` and survives as the RD1a fallback until the
  mobile mirror phase removes that selector.
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

## Per-leg travel modes (Wave 2, RD1/2/3/6/7)

Travel mode is a property of **each leg**, not the trip (the trip-wide selector is
gone). A leg is always keyed/identified by its **From stop**:
`PoiCollectionItem.OutgoingTravelMode` on the From membership; `TripLeg.Mode` and
`TripLeg.FromPoiId` in the projection; the From `PoiId` in the MCP tool.

- **Projection (`TripViewModel.BuildLegs`, RD2).** Each leg reads its From-stop's
  `OutgoingTravelMode` (null normalized to `AnyAir`) and looks its cache row up by its
  **own** `(From, To, Mode)` key — not one trip-wide mode. `TripStop` carries
  `OutgoingTravelMode`; `TripLeg` carries `Mode` (default `AnyAir`).
- **Ground-only auto-compute (RD2/FR-21).** The background compute pass enqueues a leg
  **iff** its mode ∈ {Walk, Drive, Cycle}; an **Any/Air leg is never auto-estimated**
  — it reads "—" until the user picks a ground mode or types a manual time.
- **Reset-on-reorder (RD2/FR-20/22).** `TripOrderingService.SetOrderAsync` nulls
  `OutgoingTravelMode` **only** for stops whose **successor changed**; a leg whose
  `(From→To)` pair is unchanged retains its mode + cached time (the directional cache
  preserves it). A subtle as-built fix (Epic-3 retro C1): the prior trip **shape**
  `(bool Provided, int? Finish)` is captured before the caller mutates the pin, because
  setting/clearing Finish flips the closing leg in/out — a plain `int? ?? current`
  conflates a real roundtrip (`null`) with "not supplied".
- **Mode-invariant TSP (RD3).** TSP-Sort must order stops before per-leg modes exist,
  so `DistanceMatrixService` builds its cost matrix from a mode-invariant
  haversine/straight-line basis — per-leg `OutgoingTravelMode` is never fed into the
  ordering matrix. The NN+2-opt algorithm is unchanged; newly-appeared legs default to
  Any/Air after sorting.
- **Manual override + reset, any leg (RD7/FR-25, TRIP-MANUAL-01).** Typing a leg time
  writes a `RouteSegment` row at `Fidelity = Manual` (mode-keyed), never
  auto-overwritten or invalidated. Reset (↺) clears it and returns the leg to its auto
  value (Estimated/Measured for ground via delete-then-recompute; "—" for Any/Air).
  Editing is allowed on **any** leg, generalizing Wave-1's Any/Air-only entry. VM
  commands: `SetLegModeAsync`, `SetManualLegTimeAsync`, `ClearManualLegTimeAsync`
  (`MaxManualLegMinutes` cap).
- **Sole writer extended.** `OutgoingTravelMode` is mutated **only** inside
  `TripOrderingService` — the order/reset path plus the new
  `SetOutgoingTravelModeAsync` (validates `null | TravelMode.All`, throws on invalid),
  reused by both the `LegModePill` VM command and the MCP `set_leg_travel_mode` tool.
- **Three mirrored projection sites (tech-debt C3).** The leg set (consecutive pairs +
  roundtrip closing leg), the open/roundtrip shape decision, and the `(From,To,Mode)`
  cache lookup are duplicated byte-for-byte in `TripViewModel.BuildLegs`,
  `TravelTimeComputationBackgroundService.DirectionalPairs`, and `TripTools.GetTrip` —
  any change to leg shape/mode-keying must update all three (a shared helper is a
  candidate refactor, not done).

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
  dwell) + cached directional Legs. **Wave 2 (RD6/FR-24):** each leg DTO now carries
  its **own `travelMode`** (camelCase JSON; the From-stop's mode, null normalized to
  `AnyAir`) and the leg's `(From, To, that-mode)` cache row; the single **trip-level
  `travelMode` field was removed** from `TripDto` (no dead duplicate).
- `assign_stop_order(collectionId, orderedPoiIds[])` — full reorder; input must be
  exactly the placeable Stop set (else errors); pinned Start/Finish stay first/last.
- `set_leg_travel_mode(collectionId, fromPoiId, travelMode)` — **new (Wave 2, RD6).**
  Sets one leg's mode (leg keyed by its From stop, mirroring `set_dwell_time`); one of
  `TravelMode.All` (else errors). A ground mode signals the `TravelTimeTrigger` to
  compute; `AnyAir` leaves it manual-only. Delegates to
  `ITripOrderingService.SetOutgoingTravelModeAsync` (sole writer).
- `set_trip_start` / `set_trip_finish` / `clear_trip_start` / `clear_trip_finish`.
- `set_dwell_time(collectionId, poiId, minutes?)` — set/clear; out-of-range ignored.

**Deviation from plan:** tool names are `get_trip` / `assign_stop_order` /
`set_trip_start` / `set_trip_finish` / `set_dwell_time` / `set_leg_travel_mode`
(snake_case, MCP convention), not the plan's `GetTripStops` / `SetStartFinish`; and
Start/Finish are separate set/clear tools rather than one `SetStartFinish`.

## UI (`Components/Shared/Trip/`)

`TripViewModel` (sealed, **Transient**, primary-ctor DI, `StateChanged` + `Notify`,
owns a CTS, `IAsyncDisposable`) holds all trip state; the map page composes it. It
delegates all order mutation to `ITripOrderingService`, subscribes to
`TravelTimeProgressService`, and exposes `RoutingAttributionHtml` (read off the active
provider's `Attribution`, pushed to Leaflet's attribution control — OSM/ODbL when OSRM
is active, null under Mock). `TripProjections.cs` holds the VM's read-model record
types.

Razor components under `Components/Shared/Trip/`:
`TripToggle` / `MobileTripToggle` (region switch, `aria-pressed`, ≥2-placeable gate),
`TripStopList` (Wave-2 wide CSS-grid trip table) / `MobileTripPanel`, `StopOrderBadge`,
`FidelityBadge` (self-explaining Measured/Estimated/Manual pill; "—" em-dash for any
unmeasured/unentered leg — Placeholder is internal-only), and the **Wave-2 desktop**
components:

- **`LegConnector.razor`** (RD9) — the inter-row leg strip (time/distance/fidelity +
  click-to-edit manual time + reset, hosting the mode pill). Presentational, raises VM
  commands only.
- **`LegModePill.razor`** (RD2/Story 3.4) — per-leg mode control: a rounded pill that
  opens a 4-item Walk/Drive/Cycle/Any-Air menu (active mode checked), neutral "Any —
  set mode" outline for the undefined state (never an error tone). Replaces the
  trip-wide selector. Raises `Vm.SetLegModeAsync` only.
- **`TravelModeSelector.razor`** — the **legacy trip-wide** segmented control, now
  **inert and used only by `MobileTripPanel`** (the desktop pill replaced it). It still
  writes `PoiCollection.TravelMode` (the RD1a dead-ish column) and is slated for removal
  in the mobile mirror phase.

The Wave-2 schedule controls (datetime-local start, HH:MM time-limit / finish-by
deadline, HH:MM dwell, "Over limit" chip, finish/return footer) render inline in
`TripStopList` on desktop; the itinerary timeline renders inside the stop list rather
than as a standalone component.

**Deviation from plan:** there is no separate `TripScheduleControls.razor` — the
schedule affordances are inline in `TripStopList`. The Wave-1 component names also
differ from that wave's plan (`TripPanel`/`TripViewToggle`/`StopListRow`/
`ItineraryTimeline.razor` were never the as-built names).

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
  changes. (Per-leg / mixed-mode trips shipped in Wave 2 — see "Per-leg travel modes".)
- **Add an MCP trip tool** — add a `[McpServerTool]` method to `TripTools`; delegate
  writes to `ITripOrderingService`. Auto-discovered, inherits the `/mcp` auth.
- **Swap the map renderer** — leg geometry flows VM → `LeafletMapService` →
  `leafletInterop.js`; the server-side `RouteSegment` cache stays the single source of
  truth (the map widget never calls OSRM directly).

## Deferred / known tech-debt (Wave 2)

- **Mirror-to-mobile is deferred.** `MobileTripPanel` still carries the Wave-1 controls
  (incl. the inert trip-wide `TravelModeSelector`); the Wave-2 desktop controls (per-leg
  mode pill, connector inline edit, datetime-local start, HH:MM time-limit / finish-by /
  dwell pickers) are NOT yet surfaced on mobile. Shared logic/data already reach mobile
  correctly — only the controls are pending.
- **`PoiCollection.TravelMode` is a dead-ish column** (RD1a fallback): no longer drives
  legs, but still written by the inert mobile selector and not dropped. Removal is tied
  to the mobile mirror phase.
- **Tech-debt A11 (Epic-3 retro).** A per-leg **Manual** `RouteSegment` override row is
  **orphaned** when that leg's mode changes — the old-mode-keyed Manual row is left
  stranded. Harmless to display (the projection keys by the current mode) but a stale
  row + "my manual time vanished" papercut; switching the mode back resurfaces it. A
  follow-up should make `SetOutgoingTravelModeAsync` delete/migrate the leg's Manual row
  on mode change.

## Operating OSRM

OSRM is an optional, opt-in docker-compose sidecar — **not** a launch dependency; the
default Mock deployment needs none of it. See **[osrm.md](./osrm.md)** for the full
operator guide (preparing per-profile extracts, the `osrm` compose profile, pointing
the app at the backends, and upgrading existing Estimated legs to Measured).
