# Story 1.4: Canonical Stop Order shared across both views

Status: done

Adversarial review: 0 CRIT / 0 HIGH / 0 MED / 3 LOW → SHIP. Sole-writer invariant
independently confirmed (only the private `SetOrderAsync` assigns `OrderIndex`). OFF-path
population, multi-collection/no-order empties, OrderBy stability, and toggle persistence all
verified. LOW#1 addressed with a clarifying comment on the OFF-membership-change path.

## Story

As a trip planner,
I want the order I set in Trip View to be the one order for the collection, reflected in the
plain list too,
So that the trip and the plain list never disagree about stop sequence.

## Acceptance Criteria

1. **Given** I reorder stops in Trip View (drag, ▲▼, TSP-Sort, or via MCP), **When** the reorder is committed, **Then** it writes the shared `PoiCollectionItem.OrderIndex` through `TripOrderingService.SetOrderAsync` (the sole writer, under `SqliteWriteLock`); **And** no other code path writes `OrderIndex`.
2. **Given** a collection has an explicit Stop Order, **When** I view the plain Filtered Results list (Trip View off), **Then** the plain list renders in that same Stop Order.
3. **Given** a collection that has never been put into Trip View / has no explicit order, **When** I view the plain list, **Then** it keeps its normal default sort (no forced ordering).
4. **Given** I set an order in one view and switch to the other, **When** the other view renders, **Then** the order persists between the two views with no divergence (FR-4); **And** stop-order badges and selection sync remain correct (NFR9).

## Architecture & Code Context (RD8/FR-4, single-collection scope)

**AC1 is largely already true** — `TripOrderingService.SetOrderAsync` is the sole `OrderIndex`
writer (under `SqliteWriteLock`); drag/▲▼/TSP-Sort/MCP all funnel through it. This story's job is
mostly to (a) make the PLAIN list render in that order when Trip View is OFF, and (b) assert AC1
holds with a test. Do NOT add a second `OrderIndex` writer.

**The new work — plain Filtered Results list follows the canonical order (AC2/AC3):**

Today (`MapPage.razor`, after Story 1.1) the OFF-state branch renders
`<PoiTable Pois="Vm.FilteredPois" ... />` in the bottom region. `Vm.FilteredPois`
(`MapPageViewModel`) is the viewport-filtered POI list in default order. When a single collection
is in scope and it has an explicit Stop Order, the plain list must render in that order.

**Design (keep ordering logic in the VM layer, NFR1):**

1. In `TripViewModel` add a cached `IReadOnlyDictionary<int,int> CanonicalStopOrder` (PoiId →
   OrderIndex). Populate it from `ordering.GetStopOrderAsync(ActiveCollectionId)` whenever a
   single collection is scoped — i.e. wherever `LoadAsync` / `RefreshProjectionsAsync` /
   reorder-refresh already run — **regardless of `IsTripViewEnabled`** (the order exists on the
   entity whether or not Trip View is toggled on). Empty when there is no single active collection
   (`ActiveCollectionId is null`) or the collection `HasOrder` is false. (`GetStopOrderAsync` and
   `HasOrderAsync` already exist on `ITripOrderingService`; they read placeable items' OrderIndex.)
   This naturally satisfies AC3 (multi-collection → `ActiveCollectionId` null → empty → no forced
   order) and the single-collection scope (OQ resolved).
2. In `TripViewModel` add a **pure, in-memory** method, e.g.
   `IReadOnlyList<Poi> ApplyCanonicalOrder(IReadOnlyList<Poi> pois)`: if `CanonicalStopOrder` is
   empty, return `pois` unchanged; otherwise return `pois` ordered by their `OrderIndex` (POIs
   present in the order map first, ascending by index; POIs not in the map — unplaceable /
   unordered — kept after, preserving their incoming relative order, e.g. a stable order-by). No
   DB access here — it sorts the already-built `FilteredPois` against the cached map.
3. In `MapPage.razor`, the OFF-state branch passes
   `Pois="TripVm.ApplyCanonicalOrder(Vm.FilteredPois)"` to `PoiTable` (calling a VM method, not
   inlining logic). The ON-state branch (TripStopList takeover) is unchanged — it already renders
   in order.

**Why this seam:** `MapPageViewModel.FilteredPois` re-computes on every viewport move, so the
order map must be CACHED (refreshed only on collection/order change), and the per-render apply must
be a cheap pure sort — never an async/DB call in the render path. `TripViewModel` already owns the
single-collection scoping (`ActiveCollectionId`) and the ordering service dependency, so no new
constructor dependency is added to either VM (NFR10 — the `AddTripServices` overloads stay
untouched; if you must add one, register in BOTH and run the Trip integration filter).

**Persistence across toggle (AC4):** because `CanonicalStopOrder` reads the persisted entity order
(not the toggle-gated `StopOrders`), the plain list and the Trip list show the same sequence; a
reorder in either view refreshes the cache. Keep selection sync and stop-order badges working
(NFR9 — the Trip list badges are unchanged; the plain list is ordering-only, no badges required).

## Constraints (NFRs)

- NFR1 — ordering logic in the VM (`ApplyCanonicalOrder` is a pure sort; the cache read is in
  existing async refresh paths); `MapPage.razor` only calls the VM method.
- NFR9 — no regression to stop-order badges (Trip list), selection sync, or toggle persistence.
- NFR10 — no new VM/service ctor dependency expected; if added, both `AddTripServices` overloads +
  Trip integration filter.
- Sole-writer invariant: `OrderIndex` mutated only via `TripOrderingService.SetOrderAsync`.

## Testing

- VM unit test: `ApplyCanonicalOrder` orders a POI list by canonical OrderIndex with unordered
  POIs kept stably after; returns the list unchanged when `CanonicalStopOrder` is empty;
  `CanonicalStopOrder` is populated for a single in-scope collection with an order even when Trip
  View is OFF, and empty for multi-collection / no-order.
- A test asserting AC1's sole-writer invariant (reorder/TSP/MCP route through SetOrderAsync) — if
  one does not already exist, add a focused one; otherwise reference the existing coverage.
- bUnit/integration as practical: plain list (Trip View off) renders rows in stop order when an
  order exists; default order when none; order persists across a toggle.
- Trip integration filter green; mobile trip tests green.

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast tests: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`

## Dev Notes

This closes Epic 1. After this, the desktop Trip View is a full-width readable table, the plain
list follows the single canonical order, and per-leg info lives on the connector.

## Dev Agent Record

- Added `CanonicalStopOrder` (PoiId→OrderIndex) cache + pure `ApplyCanonicalOrder`
  to `TripViewModel`; populated from `ordering.GetStopOrderAsync` in `LoadAsync`
  (both ON/OFF branches; cleared when scope null) and `RefreshProjectionsAsync`
  (reorder/toggle/membership/TSP/designation). No new ctor dependency (NFR10) —
  `ordering` was already injected; `AddTripServices` overloads untouched.
- `MapPage.razor` OFF-state branch now passes
  `TripVm.ApplyCanonicalOrder(Vm.FilteredPois)` to `PoiTable`; ON-state unchanged.
- AC1 sole-writer: `SetOrderAsync` stays the only OrderIndex writer; added a
  focused test proving `SetDwellMinutesAsync` leaves OrderIndex untouched.
- Build clean (0 warn/0 err). Fast tests 794 passed; Trip integration 20 passed.

## File List

- LucidCartographer/Components/Shared/Trip/TripViewModel.cs (CanonicalStopOrder, ApplyCanonicalOrder, RefreshCanonicalStopOrderAsync, LoadAsync + RefreshProjectionsAsync wiring)
- LucidCartographer/Components/Pages/MapPage.razor (OFF-state PoiTable Pois)
- LucidCartographer.Tests/ViewModels/TripViewModelCanonicalOrderTests.cs (new)
- LucidCartographer.Tests/Services/TripOrderingServiceTests.cs (sole-writer test)
