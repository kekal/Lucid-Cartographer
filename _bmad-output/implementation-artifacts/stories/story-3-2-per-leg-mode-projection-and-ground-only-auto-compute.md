# Story 3.2: Per-leg mode projection & ground-only auto-compute

Status: done

Adversarial review: 0 CRIT / 0 HIGH / 0 MED / 0 LOW (one non-actionable observation) → SHIP.
Verified per-leg `(From,To,Mode)` cache selection (wrong-mode row never matches), ground-only
enqueue (AnyAir/null never auto-estimated), null≡AnyAir normalization everywhere, NFR3 cache
unchanged, NFR10 no ctor dep, and that every re-expressed test faithfully (two cases strengthened)
preserves its guarantee with no lost coverage. 853 fast + 20 Trip integration + 53 mobile green.

## Story

As a trip planner, I want each leg's time to come from its own mode, so that a Drive leg and a Walk
leg are timed differently instead of all the same.

## Acceptance Criteria

1. **Given** the trip is projected, **When** `TripViewModel.BuildLegs` runs, **Then** it reads each leg's `OutgoingTravelMode` (from the From-stop membership) and looks the leg up in the cache by its own directional `(From, To, mode)` key (TRIP-CACHE-01); `TripLeg` carries a `Mode` field (FR-19).
2. **Given** a leg's mode is a ground mode (Walk / Drive / Cycle), **When** the background compute pass runs, **Then** it enqueues that leg and yields an automatic time (Estimated, or Measured under OSRM) (FR-21).
3. **Given** a leg's mode is Any/Air (incl. null), **When** the compute pass runs, **Then** that leg is **never** auto-estimated and reads "—" until a mode/manual time is set (FR-21, TRIP-LEGMODE-01).
4. **Given** this is data + projection + compute (no new VM/service ctor dependency), **When** the change lands, **Then** the `AddTripServices()` overload pair is untouched; if any dependency is nonetheless added it is registered in BOTH overloads and the Trip integration filter is re-run (NFR10, NFR8).

## Architecture & Code Context (RD2, TRIP-LEGMODE-01/CACHE-01)

Depends on Story 3.1 (the `OutgoingTravelMode` column). This story makes leg time per-leg-mode
driven, on the VM projection + the background compute pass. `PoiCollection.TravelMode` is NOT
dropped here (deferred per readiness Major #1) — but it STOPS driving leg computation/projection.

**Today (trip-wide mode):**
- `TripViewModel.ReadRouteSegmentsAsync(collectionId, travelMode)` reads cache rows
  `WHERE r.TravelMode == travelMode` (ONE trip mode) into a `(From,To)`→RouteSegment dict; `MakeLeg`
  folds them in. The trip mode comes from `ReadTripSettingsAsync` (`PoiCollection.TravelMode`).
- `TravelTimeComputationBackgroundService` builds `PendingLeg`s using the collection's single
  `c.TravelMode` for every consecutive pair (and closing leg) that lacks a cache row, and computes
  them (provider or `EstimatedTravelTime.Compute`).

**Required reshape (RD2):**
1. **Per-leg projection (`TripViewModel`):**
   - Carry each stop's `OutgoingTravelMode` into the projection. The ordered stops / `TripStop` (or
     the membership read `ReadStopsAndRowsAsync`) must expose the From-stop's `OutgoingTravelMode`
     (null ≡ AnyAir). Read it from `PoiCollectionItem.OutgoingTravelMode`.
   - `TripLeg` gains a `Mode` field (string, one of `TravelMode.All`; null normalized to AnyAir).
   - `BuildLegs` sets each leg's `Mode` from its From-stop's `OutgoingTravelMode` and looks the leg
     up in the cache by its own `(From, To, Mode)` key — NOT a single trip mode. So
     `ReadRouteSegmentsAsync` must read cache rows for the **set of (From,To,Mode) keys the legs
     actually need** (the per-leg modes), not `WHERE TravelMode == oneMode`. (Read all rows for the
     collection's poi set across the needed modes, then `MakeLeg` selects by `(From,To,legMode)`.)
   - An **Any/Air (null) leg is never auto-timed**: it has no cache row (no ground compute) →
     `DurationSeconds` null → connector reads "—". (A Manual override on an Any/Air leg still shows,
     as today — unchanged.)
2. **Ground-only auto-compute (`TravelTimeComputationBackgroundService`):**
   - When building `PendingLeg`s, use each consecutive pair's From-stop `OutgoingTravelMode` (null ≡
     AnyAir) as the leg's mode, NOT the collection's trip mode.
   - **Enqueue a leg iff its mode ∈ {Walk, Drive, Cycle}** (ground). **AnyAir (incl. null) legs are
     never enqueued / never auto-estimated** (FR-21). The missing-row detection must be per the
     leg's own mode key.
3. **No new ctor dependency (NFR10):** this is data/projection/compute on existing services. Do NOT
   add a VM/service constructor dependency. If unavoidable, register in BOTH `AddTripServices()`
   overloads and re-run the Trip integration filter.
4. **Do NOT** add the per-leg mode pill UI (Story 3.4), the reorder-reset rule (Story 3.3), the MCP
   change (3.6), or drop `PoiCollection.TravelMode`. This story is the data→projection→compute spine
   only. The trip-wide `TravelModeSelector` may still exist and write `PoiCollection.TravelMode`; it
   simply no longer drives legs (its removal is Story 3.4). To keep the app coherent in the interim,
   ensure existing trips still render: a fresh trip's legs are all AnyAir(null) → "—" until 3.4 adds
   per-leg mode setting. (Existing Any/Air manual entries keep working.) NOTE: pre-existing trips
   that had a non-AnyAir `PoiCollection.TravelMode` will now show "—" legs because per-leg modes
   are null — that is the intended new model (FR-20: newly-appeared/unset legs are Any/Air); the
   trip-wide selector becoming inert is expected and finalized in 3.4.

## Constraints (NFRs)

- NFR1 — projection/compute logic in VM/service; `.razor` unchanged.
- NFR3 — `RouteSegment` cache semantics and the directional `(From,To,Mode)` key UNCHANGED; per-leg
  modes simply select different existing cache rows.
- NFR8/NFR10 — Trip integration filter after this VM/compute change; both `AddTripServices`
  overloads if any dependency is added.
- TRIP-LEGMODE-01 — null ≡ AnyAir, one state; never auto-estimate AnyAir.

## Testing

- VM unit: `BuildLegs` reads per-leg `OutgoingTravelMode` and looks up the cache by `(From,To,Mode)`;
  `TripLeg.Mode` is set; a leg with a ground-mode From-stop and a matching cache row shows its time;
  an AnyAir/null leg has null duration ("—"); two legs with different modes resolve to different
  cache rows.
- Compute service unit: ground-mode legs are enqueued; AnyAir/null legs are NOT enqueued (never
  auto-estimated); missing-row detection is per the leg's own mode.
- Run the Trip integration filter (VM/compute change) — must stay green. Mobile green.

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`

## Dev Notes

Core of the per-leg mode spine. The connector pill (3.4) and reorder-reset (3.3) build on this;
3.3 also keeps TSP mode-invariant. Watch the cache read: it must fetch rows for the per-leg modes
the legs need, keyed `(From,To,Mode)`.

## Dev Agent Record

Implemented the per-leg-mode spine: each leg's time now comes from its own mode (the
From-stop's `OutgoingTravelMode`, null ≡ AnyAir). Ground modes (Walk/Drive/Cycle)
auto-compute; AnyAir/null legs are never auto-estimated → "—". The RouteSegment cache
shape and its directional `(From,To,Mode)` key are unchanged; per-leg modes simply select
different existing rows. `PoiCollection.TravelMode` is not dropped and no longer drives
legs. No new VM/service ctor dependency was added (both `AddTripServices` overloads
untouched).

- Projection: added `TripStop.OutgoingTravelMode` (read from `PoiCollectionItem`) and
  `TripLeg.Mode` (string, null normalized to AnyAir).
- `ReadRouteSegmentsAsync(collectionId)` now reads the collection's RouteSegment rows
  across ALL modes keyed `(From,To,Mode)`; `MakeLeg` selects by the leg's own
  `(From,To,legMode)` where `legMode = from.OutgoingTravelMode ?? AnyAir`. An AnyAir leg
  has no ground row → null duration → "—".
- Background compute: `LoadPendingLegsAsync`/`DirectionalPairs` derive each leg's mode
  from its From-stop's `OutgoingTravelMode`; a leg is enqueued IFF its mode ∈
  {Walk,Drive,Cycle}; missing-row detection is per the leg's own `(From,To,Mode)` key.

Existing tests with the now-invalid "trip-wide mode drives legs" premise were re-expressed
to the per-leg model (set `OutgoingTravelMode` on the From-stops instead of relying on
`PoiCollection.TravelMode`): `TripViewModelTravelModeTests`,
`TripViewModelRecommendsOsrmTests`, `TripViewModelRecomputeTests`,
`TravelTimeComputationBackgroundServiceTests`, `TripMockEstimateNoteRenderTests`,
`LegConnectorTests`. Manual-on-Any/Air tests are unchanged (Manual rows at AnyAir match
AnyAir legs). Build clean (0 warnings); fast suite 853 passed; Trip integration 20 passed.

## File List

- LucidCartographer/Components/Shared/Trip/TripProjections.cs (TripLeg.Mode, TripStop.OutgoingTravelMode)
- LucidCartographer/Components/Shared/Trip/TripViewModel.cs (ReadStopsAndRowsAsync, ReadRouteSegmentsAsync, BuildLegs, MakeLeg, call sites)
- LucidCartographer/Services/Trip/TravelTimeComputationBackgroundService.cs (per-leg mode + ground-only enqueue)
- LucidCartographer.Tests/ViewModels/TripViewModelPerLegModeTests.cs (new)
- LucidCartographer.Tests/Services/TravelTimeComputationBackgroundServiceTests.cs (new ground-only tests + seed updates)
- LucidCartographer.Tests/ViewModels/TripViewModelTravelModeTests.cs (re-expressed per-leg)
- LucidCartographer.Tests/ViewModels/TripViewModelRecommendsOsrmTests.cs (seed per-leg mode)
- LucidCartographer.Tests/ViewModels/TripViewModelRecomputeTests.cs (seed per-leg mode)
- LucidCartographer.Tests/Components/Trip/TripMockEstimateNoteRenderTests.cs (seed per-leg mode)
- LucidCartographer.Tests/Components/Trip/LegConnectorTests.cs (seed per-leg mode)
