# Story 3.3: Reset newly-appeared legs on reorder; keep TSP mode-invariant

Status: done

Adversarial review: 1 HIGH / 0 MED / 2 LOW → FIX-THEN-SHIP. **HIGH (H1) fixed by orchestrator:**
flipping the trip shape via Set/Clear Finish appears/vanishes the closing leg but `SetOrderAsync`
read the pin once (already-mutated), so the old vs new successor maps used the same shape and missed
the closing-leg mode reset (AC1 violation). Fixed by threading the prior shape into `SetOrderAsync`
as `(bool Provided, int? Finish) previousShape` (a bool flag is required because a prior Finish of
null is a real roundtrip shape, not "unsupplied") from the pin-flip + reconcile paths; the OLD
successor map uses the prior shape, the NEW map the current Finish. Added two regression tests
(ClearFinish resurrects closing leg → reset; SetFinish vanishes it → reset). LOW#1 (DurationSeconds
now carries haversine distance — misnomer) and LOW#2 (retained `_options`) accepted. 866 fast + 20
Trip integration green.

## Story

As a trip planner, I want reordering to reset only the legs that actually changed and to never
optimise on per-leg modes, so that unchanged legs keep their mode and time, and ordering doesn't
deadlock on modes that don't exist yet.

## Acceptance Criteria

1. **Given** I reorder stops (drag / ▲▼ / TSP-Sort / MCP), **When** `TripOrderingService.SetOrderAsync` commits the new order, **Then** it nulls `OutgoingTravelMode` **only** for stops whose successor changed; a leg whose `(From→To)` pair is unchanged retains its mode and cached time (FR-20, FR-22); **And** newly-appeared legs default to Any/Air and read "—" with the "Any — set mode" pill (FR-20, UX-DR11).
2. **Given** TSP-Sort must order stops before per-leg modes exist, **When** it builds its cost matrix, **Then** the matrix uses a mode-invariant basis (straight-line/haversine distance, or a fixed nominal ground mode), never per-leg `OutgoingTravelMode` (RD3); the NN+2-opt algorithm itself is unchanged; **And** after ordering, the resulting newly-appeared legs default to Any/Air per the reset rule.
3. **Given** the order + mode-reset write-path, **When** any reorder runs, **Then** `OrderIndex` and `OutgoingTravelMode` are mutated only through `TripOrderingService` under `SqliteWriteLock` (no other writer).

## Architecture & Code Context (RD2/RD3, TRIP-LEGMODE-01)

### Part A — successor-changed mode reset in the sole writer (`SetOrderAsync`)

`TripOrderingService.SetOrderAsync(collectionId, desired, ct)` is the single funnel for ALL reorder
paths (drag, ▲▼, TSP, MCP all call it). Today it loads the collection's `PoiCollectionItem`s, sets
each `OrderIndex` from `desired`, and SaveChanges under `writeLock`. It has BOTH the OLD order
(`item.OrderIndex` before mutation) and the NEW order (`desired`).

Add the mode-reset: **null `OutgoingTravelMode` ONLY for stops whose SUCCESSOR changed.**
- Compute, over the PLACEABLE stops, each stop's successor PoiId under the OLD order and under the
  NEW order. "Successor" = the placeable stop at the next OrderIndex in sequence.
- The **last stop's** successor is roundtrip-dependent: roundtrip (`FinishPoiId is null` or Finish ==
  Start) → successor = the FIRST stop (the closing leg From=last,To=first); open path (a distinct
  Finish pinned at N) → the last stop has NO successor (no closing leg). Read `StartPoiId`/
  `FinishPoiId` from the collection to decide (mirror how `BuildLegs`/the timeline decide roundtrip:
  a distinct Finish that is a real placeable stop other than the first ⇒ open path).
- For each placeable stop, if `oldSuccessor != newSuccessor` (including gaining or losing a
  successor, e.g. a stop that became the last stop on an open path, or a brand-new appended stop
  with no old successor), set its `OutgoingTravelMode = null` (≡ AnyAir → the leg reads "—" until a
  mode is set). A stop whose successor is UNCHANGED keeps its `OutgoingTravelMode` (and thus its
  cached time, since the `(From,To,Mode)` cache row is preserved — TRIP-CACHE-01).
- Do this in the SAME `SetOrderAsync` transaction (same `db`, same `writeLock` save), so OrderIndex
  and the mode reset commit atomically. `OutgoingTravelMode` is mutated ONLY here (AC3).
- Edge: if `desired` produces no OrderIndex change AND no successor change, keep the existing
  early-return (no write). If only modes need nulling (rare), still save.

### Part B — TSP cost matrix is mode-invariant (`DistanceMatrixService`)

`DistanceMatrixService.BuildAsync` today reads the collection's persisted `TravelMode` and filters
cached `RouteSegment` rows by it (`WHERE r.TravelMode == travelMode`), haversine-filling uncached
pairs. That trip-wide mode is now inert (Story 3.2) and per-leg modes are mostly AnyAir (no rows),
so the matrix must NOT depend on per-leg modes (RD3). Make it **mode-invariant**:
- Build the cost matrix from a **single mode-invariant basis: straight-line / haversine distance**
  for every pair (the simplest truly mode-invariant basis; under `Mock`, time = distance × a
  monotone speed scalar, so the optimal order is identical regardless of mode). I.e. drop the
  per-collection-`TravelMode` cache filter and build the matrix from haversine distance directly
  (you may keep using `EstimatedTravelTime`'s haversine helper). Do NOT feed per-leg
  `OutgoingTravelMode` into the matrix. (Equivalently a fixed nominal ground mode — haversine is
  preferred and matches RD3's wording.)
- The NN+2-opt algorithm (`TspSolver`) is UNCHANGED. The never-worse guard, pins (Start@1,
  Finish@N), and the single-OrderIndex write path are unchanged. After TSP writes the new order via
  `SetOrderAsync`, Part A nulls the modes of the legs whose successor changed (most/all of them on a
  real re-sort) — newly-appeared legs become Any/Air per the reset rule (AC2).

## Constraints (NFRs)

- Sole-writer: `OrderIndex` and `OutgoingTravelMode` mutated only in `TripOrderingService` under
  `SqliteWriteLock` (AC3).
- NFR3 — `RouteSegment` cache shape/key unchanged; mode-keyed rows of UNCHANGED legs are preserved.
- TRIP-LEGMODE-01 — null ≡ AnyAir; reset sets null (no separate sentinel).
- No new ctor dependency (NFR10); if added, both `AddTripServices` overloads + Trip integration.

## Testing

- `TripOrderingServiceTests`: after a reorder, a stop whose successor is UNCHANGED keeps its
  `OutgoingTravelMode`; a stop whose successor CHANGED has it nulled; a newly-appended stop's leg is
  null; roundtrip last-stop closing-leg successor handled; open-path last stop (distinct Finish) has
  no successor (not reset spuriously). Assert OrderIndex + mode reset commit together.
- `DistanceMatrixService`/TSP tests: the matrix is mode-invariant — building it for a collection
  whose per-leg/trip modes differ yields the SAME matrix (it ignores modes); TSP order does not
  depend on `OutgoingTravelMode`; the never-worse guard + pins still hold. Re-express any existing
  DistanceMatrix test that assumed the `PoiCollection.TravelMode` cache filter, faithfully.
- Run the Trip integration filter (ordering/VM change). Mobile green.

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`

## Dev Notes

The "Any — set mode" pill is Story 3.4 (UI); this story produces the null≡AnyAir state on reorder.
TSP mode-invariance resolves the chicken-and-egg (ordering needs costs before per-leg modes exist).

## Dev Agent Record

### Part A — successor-changed mode reset (`SetOrderAsync`)
`SetOrderAsync` (the sole OrderIndex writer) now also nulls `OutgoingTravelMode` in
the SAME tracked-context SaveChanges as the OrderIndex change. It loads the items
with `.Include(Poi)` (for the placeability predicate), reads the pins via
`ReadPinsAsync`, and computes each placeable Stop's successor under the OLD
(`item.OrderIndex`) and NEW (`desired` ?? current) order via a local `SuccessorMap`.
Successor = next Stop in the OrderIndex-sorted placeable sequence; the LAST stop's
successor mirrors `BuildLegs` exactly — open path (a distinct Finish that is a real
placeable Stop other than the first) ⇒ no successor; otherwise roundtrip ⇒ first
stop. A placeable Stop is reset only when it ALREADY had a leg (old OrderIndex > 0)
and its successor differs (incl. losing it on an open path). Stops newly entering
the sequence (old OrderIndex 0 — seed/append) are skipped, since they have no stale
leg (an appended membership's mode is null by default anyway). The early-return is
preserved when nothing changed; a mode-only reset still saves (`changed = true`).
Commit is atomic under `SqliteWriteLock` (verified by a 1-SaveChanges test).

### Part B — mode-invariant matrix (`DistanceMatrixService.BuildAsync`)
Dropped the `PoiCollection.TravelMode` read and the `RouteSegment` cache filter
entirely. Every cost cell is now `GeoUtils.HaversineDistance` between the two stops'
coordinates — a single mode-invariant basis. No per-leg `OutgoingTravelMode` or
trip-wide mode is consulted; `FromCache` is always all-false. `TspSolver`, the
never-worse guard, and the pins are untouched, so TSP order is identical for any
mode. `_options` is retained only for ctor-shape compatibility (no new ctor dep).

### Existing tests re-expressed (faithfully)
- `DistanceMatrixServiceTests.Build_ReusesCachedPair_Directionally` →
  `Build_IgnoresCachedRouteSegments_UsesHaversineDistance`: its premise (matrix
  reuses a cached duration filtered by `PoiCollection.TravelMode`) was deliberately
  removed by RD3. Re-expressed to assert the matrix ignores the cached row and uses
  symmetric haversine distance instead.
- `Build_FillsMissingPairs_WithHaversineEstimate_AndDoesNotWriteCache` →
  `Build_FillsEveryPair_WithHaversineDistance_AndDoesNotWriteCache`: now every pair
  (not just "missing" ones) is a haversine distance; no-cache-write guard preserved.

### Notes / edge cases
- A pure rotation of a roundtrip cycle changes NO stop's successor (the leg set is
  identical), so all modes survive — covered by a dedicated test.
- Start pin does not affect successor computation (only the Finish/roundtrip shape
  does); documented inline.
- No new ctor dependency added.

## File List

- LucidCartographer/Services/Trip/TripOrderingService.cs (Part A)
- LucidCartographer/Services/Trip/DistanceMatrixService.cs (Part B)
- LucidCartographer.Tests/Services/TripOrderingServiceTests.cs (Part A tests)
- LucidCartographer.Tests/Services/DistanceMatrixServiceTests.cs (Part B tests + re-expressed)
