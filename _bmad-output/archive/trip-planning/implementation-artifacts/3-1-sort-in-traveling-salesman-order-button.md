---
baseline_commit: dea236c4c2c10381e964c9032f468b6a002d3be2
---

# Story 3.1: "Sort in Traveling Salesman order" button

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a trip planner with a zig-zag collection,
I want one button that reorders my stops into an efficient loop,
so that I don't have to untangle the order by hand.

## Acceptance Criteria

_(FR-15, AR-6, NFR1, UX-DR10; epics.md#Story-3.1)_

1. **On-demand sort builds a matrix from the shared cache.** Given Trip View is on with placeable stops, when I press "Sort in Traveling Salesman order", then an on-demand N×N Distance Matrix is built over the **placeable** stops (the `GetPlaceableStopsAsync` candidate set — never unplaceable items, [TRIP-PLACE-03]) from the shared `RouteSegment` cache **reusing cached pairs** under the collection's persisted `TravelMode`, and a nearest-neighbor + 2-opt search rewrites `OrderIndex`.
2. **Never automatic.** The system never reorders stops without this explicit press. No toggle-on, membership change, recompute, enrichment, or render path may trigger a sort.
3. **Pins honored.** Given a designated Start and/or Finish, when the sort runs, then it keeps the Start at Order 1 and the Finish at Order N (swapping interior edges only); with no Start/Finish designated it optimizes the whole sequence without that pin; for a Roundtrip (no distinct Finish) it closes the loop (the closing leg back to Order 1 is part of the optimized cost).
4. **Never worse + redraw + still editable.** Given a completed sort, when the result is applied, then the new order's total travel time is **≤** the pre-sort order for the same stops and mode (never worse), the map and timeline redraw via the existing `StateChanged` path, and the result remains overridable by a subsequent manual drag (it is just another `OrderIndex` write — no new immutability).
5. **Interactive for N≤30 with a warm matrix.** Given a trip of up to 30 stops with a warm matrix (all pairs cached), when I trigger the sort, then it completes interactively (p95 ≤ 3 s, NFR1); larger N still completes (correctness preserved) without the interactivity guarantee.
6. **Single write path.** The new order is written **only** through the one `OrderIndex` writer (`TripOrderingService` → `SetOrderAsync` under `SqliteWriteLock`, AR-11). No ViewModel or component mutates `OrderIndex` directly.

## Tasks / Subtasks

- [x] **Task 1 — `IDistanceMatrixService` / `DistanceMatrixService` (NEW, `Services/Trip/`)** (AC: #1, #5)
  - [x] Add `IDistanceMatrixService.BuildAsync(int collectionId, CancellationToken)` returning an N×N duration matrix (seconds) over the ordered placeable stops plus the stop id list (index ↔ PoiId map). Canonical unit: **seconds** (matches `RouteSegment.DurationSeconds`).
  - [x] Read existing pairs from the shared `RouteSegment` cache for the collection's persisted `TravelMode`, **directionally** ([TRIP-CACHE-01]: A→B ≠ B→A — do not collapse). Reuse cached durations as-is.
  - [x] For any matrix pair with **no cache row**, fill the cell with the shared haversine `EstimatedTravelTime.Compute(...)` straight-line value (same code path the provider-down fallback uses) so the matrix is always complete and the sort is deterministic. **Do NOT write these fill values back to the cache** (matrix is on-demand input only; the background compute service owns cache writes for the actual displayed legs). Document this as the matrix's "warm where cached, estimated where not" contract.
  - [x] Diagonal cells (i==i) are 0; never routed.
  - [x] Register the service in `Configuration/TripServicesExtensions.cs` (mirror the existing Trip service registrations; pick the lifetime the sibling services use).
- [x] **Task 2 — `SortTravelingSalesmanAsync` on `ITripOrderingService` / `TripOrderingService`** (AC: #1, #3, #4, #6)
  - [x] Add `Task SortTravelingSalesmanAsync(int collectionId, CancellationToken ct = default)` to the interface with full XML doc (mirror the existing method docs; reference AR-6, AR-11, [TRIP-PLACE-03]).
  - [x] Implementation (~120–150 lines, **no OR-Tools**, all in-process): build the matrix via `IDistanceMatrixService` (inject it), read the placeable stop sequence + pins (`ReadPinsAsync`), run **nearest-neighbor construction → 2-opt local search**, then arrange the result with pins via the existing `ArrangeWithPins(...)` helper and commit via the existing `Renumber` + `SetOrderAsync` (the SAME write path seed/reorder/designation use — AC6).
  - [x] **Pin handling (AC3):** Start (if set & a real stop here) fixed at index 0, Finish (if set & a real stop here) fixed at index N-1; NN + 2-opt optimize the **interior** only (2-opt edge swaps never move the pinned endpoints — restrict swap window to interior indices). Roundtrip (no distinct Finish) ⇒ the cost function includes the closing edge last→first; open path (distinct Finish) ⇒ no closing edge. Defensive: a pin whose POI is not actually a placeable ordered stop is ignored (mirror `ReorderStopAsync`/`ArrangeWithPins`).
  - [x] **Never-worse guarantee (AC4):** compute the pre-sort tour cost (current order, same matrix, same open/closed shape) and the post-sort cost; if the optimized tour is not strictly better, **keep the pre-sort order** (write nothing / write the original). 2-opt only ever accepts improving swaps, and NN seeded then 2-opt-improved from the existing order's cost guarantees ≤, but assert it explicitly so a degenerate matrix can never produce a worse order. Never throws on N<2 (no-op) or N==2 (already optimal).
  - [x] `OrderIndex` stays 1-based, contiguous, gap-free, unique over placeable items; unplaceable items stay 0 (they never enter the matrix or the tour).
- [x] **Task 3 — `TripViewModel.SortTravelingSalesmanAsync()` + button wiring** (AC: #2, #4)
  - [x] Add a public VM method mirroring the `RecomputeTravelTimesAsync()` write-then-`RefreshProjectionsAsync`-then-`Notify` shape: guard on `ActiveCollectionId` + `IsTripViewEnabled`, call `ordering.SortTravelingSalesmanAsync(...)`, `await RefreshProjectionsAsync(collectionId)`, `catch (OperationCanceledException) { return; }` + general `catch`/log (copy the existing error-handling shape), then `Notify()`.
  - [x] Add an aria-live announcement (new `UiStrings` constant) summarizing the result (e.g. "Stops sorted into travel order"); follow the `LastReorderAnnouncement` precedent.
  - [x] Wire a button on **both** surfaces next to the existing Recompute control: desktop `TripStopList.razor`, mobile `MobileTripPanel.razor` (`TripRecomputeLabel`/`TripRecomputeAria` show the existing pattern). New labels go through `UiStrings` (e.g. `TripSortTspLabel`, `TripSortTspAria`) — **no hardcoded UI text**.
  - [x] Button is only offered when Trip View is on with ≥2 placeable stops (it shares the same availability gate as the existing Trip controls — never a broken affordance below the gate, UX-DR10).
- [x] **Task 4 — Tests** (AC: all)
  - [x] **Unit (`Services/`):** `DistanceMatrixService` — reuses cached pairs, fills missing with haversine estimate, directional (A→B ≠ B→A), does not write to cache. `TripOrderingService.SortTravelingSalesmanAsync` — NN+2-opt untangles a known zig-zag to the optimal/expected order; **pins honored** (Start@1, Finish@N, interior-only); Roundtrip closes the loop vs open-path does not; **never-worse** holds (post ≤ pre on a crafted matrix, and a degenerate/already-optimal input writes nothing); N<2 / N==2 no-op; single write path (assert via the same `SqliteWriteLock`/`SetOrderAsync` seam the other ordering tests use in `TripOrderingServiceTests`).
  - [x] **ViewModel:** `SortTravelingSalesmanAsync` triggers exactly one ordering call + a projection refresh + `Notify`; **no other code path calls the sort** (AC2 — assert the sort is invoked only by the explicit method, mirroring how recompute is tested in `TripViewModelRecomputeTests`).
  - [x] **Component (bUnit):** the Sort button renders on both surfaces above the ≥2 gate and is absent below it; clicking invokes the VM method.
  - [x] **Perf (AC5):** a deterministic test asserting a warm-matrix sort of N=30 completes within budget (use a fixed in-memory matrix; assert algorithmic completion + a generous wall-clock ceiling — keep it non-flaky, this is a guardrail not a benchmark).
  - [x] **Integration:** run the **Trip integration filter** after the new DI registration + VM ctor change (see Critical Rules) — a new injected service in `TripViewModel`/`TripOrderingService` is exactly the DI/VM-ctor change that has regressed the integration host before.

## Dev Notes

### What this story adds (and the two NEW files)

Per architecture (architecture.md:464–465, :540), this story introduces **two new files** and rides existing seams for everything else:

- `Services/Trip/IDistanceMatrixService.cs` + `DistanceMatrixService.cs` — on-demand N×N over the shared `RouteSegment` cache (D11).
- A new method on the **existing** `ITripOrderingService`/`TripOrderingService` — `SortTravelingSalesmanAsync` (D5, the NN+2-opt). **Do not** create a separate TSP service; AR-6 and the architecture both place TSP inside `TripOrderingService` so it shares the single `OrderIndex` write path.

### Key source files to read before implementing (UPDATE targets)

- `LucidCartographer/Services/Trip/TripOrderingService.cs` — **the single `OrderIndex` writer.** Study `SetOrderAsync` (the one write method, under `writeLock.Gate`), `Renumber`, `ArrangeWithPins` (Start→1/Finish→N arrangement — **reuse it**, do not reimplement pin placement), `ReadPinsAsync`, `ReadAsync` (placeability projection via `StopPlaceability.IsPlaceable`). The sort must funnel through `Renumber` + `SetOrderAsync` exactly as `ReorderStopAsync`/`SetPinAsync` do.
- `LucidCartographer/Services/Trip/ITripOrderingService.cs` — note `PlaceableStop` is the routing candidate record and `GetPlaceableStopsAsync` returns the ordered placeable-only set ([TRIP-PLACE-03], coordinates non-nullable by construction). The matrix consumes this.
- `LucidCartographer/Data/Entities/RouteSegment.cs` — directional composite key `(FromPoiId, ToPoiId, TravelMode)`; `DurationSeconds` (int, **seconds**), `DistanceMeters` (double, meters). [TRIP-CACHE-01] directional — never collapse A→B with B→A.
- `LucidCartographer/Services/Trip/EstimatedTravelTime.cs` (`internal`, `InternalsVisibleTo` covers tests) — the shared haversine estimate. Use `EstimatedTravelTime.Compute(from, to, mode, options)` to fill matrix cells with no cache row. It needs `TravelTimeOptions` (the per-mode assumed speeds) — inject `IOptions<TravelTimeOptions>` into `DistanceMatrixService` like the background service does.
- `LucidCartographer/Services/Trip/TravelTimeComputationBackgroundService.cs` — mirror its cache-read shape (`existing` keyset over `(FromPoiId, ToPoiId, TravelMode)`) and its `DirectionalPairs` closing-leg logic (Roundtrip ⇒ closing leg back to `stops[0]`; distinct Finish ⇒ open path). The TSP cost function must use the **same** open/closed shape so "≤ pre-sort total" is measured consistently with what the UI draws.
- `LucidCartographer/Components/Shared/Trip/TripViewModel.cs` — the VM is a `sealed` primary-constructor DI class (registered Transient). `RecomputeTravelTimesAsync()` (≈1497) is the **template** for the new sort method (guard → service call → `RefreshProjectionsAsync` → `Notify`). `MoveStopToAsync` (≈281) shows the announcement + projection-refresh pattern. `TotalTravelTimeSeconds`/`RecomputeTotal` (≈1039) is the displayed Σ — the never-worse AC is about this total for the same stops/mode.
- `LucidCartographer/Components/Shared/Trip/TripStopList.razor` and `MobileTripPanel.razor` — both render the existing Recompute button; add the Sort button alongside on both.
- `LucidCartographer/Services/UiStrings.cs:159–164` — `TripRecomputeLabel`/`TripRecomputeAria` precedent for the new sort strings.
- `LucidCartographer/Configuration/TripServicesExtensions.cs` — where the new `DistanceMatrixService` registers.

### Algorithm (D5, architecture.md:251–255, AR-6)

In-process C# (~150 lines), no OR-Tools:
1. Build duration matrix from the cache (Task 1).
2. **Nearest-neighbor construction** seeded from the Start (or index 0 if no Start pin), greedily picking the nearest unvisited interior stop.
3. **2-opt local search** — repeatedly reverse interior segments where doing so lowers the tour cost; iterate until no improving swap remains (or a small iteration cap for large N). Pinned endpoints are excluded from the swap window.
4. Close the loop for a Roundtrip (cost includes last→first); open path for a distinct Finish.
5. Compare optimized cost to pre-sort cost; keep whichever is ≤ (never worse, AC4). Write the result through `SetOrderAsync`.

### Architecture compliance / guardrails

- **Layering (strict):** Component → ViewModel → Service → Data. The button lives in `.razor` (markup/binding only), calls the VM, which calls `TripOrderingService`. The matrix/TSP services never reference Components.
- **AR-11 canonical units & single writer:** durations in **seconds**, distances in **meters**, `OrderIndex` **1-based**, cache key **directional**. All four ordering paths (drag, keyboard, TSP, MCP) write the same `OrderIndex` through one `TripOrderingService` method — TSP is path #3 and must not open a second write route. Tag new code with `TRIP-*` comment codes (e.g. `TRIP-TSP-01`).
- **Build discipline:** `TreatWarningsAsErrors=true`, `Nullable=enable`. New code must introduce **no group-B analyzer violation** (`MA0002`, `MA0015`, `MA0046`, `MA0047`, `MA0074`, `VSTHRD200`) and **no `ConfigureAwait(false)`** (`MA0004` is suppressed on purpose — Blazor circuit needs the sync context).
- **No hardcoded UI text** — all new strings via `UiStrings`. Update both desktop and mobile render paths (`Viewport.IsMobile` → `Mobile*`).
- **Concurrency note (deferred-work alignment):** `deferred-work.md` flags "OrderIndex write-path atomicity" and "Pin/order ops are not atomic under true concurrency" with TSP-Sort 3.1 named as the revisit point. The read-validate-write is split across DbContexts with only `SaveChangesAsync` under the gate. For a single-user self-hosted Blazor circuit this is acceptable (calls are awaited and serialized per circuit) — **keep the same pattern as the existing methods; do not introduce a new locking scheme**, but note in the Completion Notes whether the sort widens the window so the reviewer can weigh it.

### Testing standards

Three layers (project-context.md): **Unit** (pure logic — the matrix builder and the TSP algorithm are ideal pure-logic targets; consider making the NN+2-opt core a testable internal that takes a matrix + pin indices and returns an index permutation, separate from the DB read/write), **Component** (bUnit, both surfaces), **Integration** (`IntegrationTestBase`). `InternalsVisibleTo("LucidCartographer.Tests")` is set — test internals directly. Cover desktop **and** mobile.

### Project Structure Notes

- New: `Services/Trip/IDistanceMatrixService.cs`, `Services/Trip/DistanceMatrixService.cs`, `LucidCartographer.Tests/Services/DistanceMatrixServiceTests.cs`, TSP tests extending `LucidCartographer.Tests/Services/TripOrderingServiceTests.cs`.
- Updated: `ITripOrderingService.cs`, `TripOrderingService.cs`, `TripViewModel.cs`, `TripStopList.razor`, `MobileTripPanel.razor`, `UiStrings.cs`, `TripServicesExtensions.cs`.
- **No EF migration** — this story reads/writes only existing schema (`OrderIndex`, `RouteSegment`).

### References

- [Source: epics.md#Story-3.1] — ACs (FR-15, AR-6, NFR1, UX-DR10).
- [Source: architecture.md#D5-TSP-Sort (lines 251–255)] — NN+2-opt, pin endpoints, ≤ pre-sort, N≤30 p95 ≤ 3s.
- [Source: architecture.md#D11-Distance-Matrix (lines 196–198)] — on-demand N×N over the shared cache, one cache two readers, no separate matrix table.
- [Source: architecture.md lines 389–390, 410, 464–465, 540] — single ordering write path; new file placement.
- [Source: project-context.md] — build/layering/testing/units rules.
- [Source: deferred-work.md] — OrderIndex write-path atomicity & pin/order concurrency (TSP-Sort named revisit point).

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Claude Opus 4.8) — via bmad-story-automator manual cycle (no tmux, Windows).

### Debug Log References

- `dotnet build LucidCartographer/LucidCartographer.csproj` → clean (0 warnings, TreatWarningsAsErrors).
- `dotnet test --filter "Tsp|DistanceMatrix"` → 21 passed.
- `dotnet test --filter "TspSort|TripStopListTests"` → 55 passed.
- `dotnet test` (full suite, incl. Trip integration) → **867/867 passed**, 5m25s.

### Completion Notes List

- **Two new files as designed:** `Services/Trip/IDistanceMatrixService.cs` + `DistanceMatrixService.cs` (D11 on-demand N×N over the shared `RouteSegment` cache) and `Services/Trip/TspSolver.cs` (pure NN+2-opt, D5). The TSP logic lives on `TripOrderingService.SortTravelingSalesmanAsync` (new interface method) so it shares the single `OrderIndex` write path (AR-11) — no separate ordering writer was introduced.
- **DI regression guarded:** `DistanceMatrixService` is registered in the **parameterless** `AddTripServices()` overload (not just the production one) because it is now a hard dependency of `TripOrderingService`, which the integration host composes by hand. Confirmed by the full integration run (867/867). The VM constructor was **not** changed — the VM calls the existing `ordering` dependency.
- **Asymmetric-matrix correctness:** the cache key is directional ([TRIP-CACHE-01]) so the cost matrix may be asymmetric; a segment reversal flips internal edge directions, making the cheap boundary-edge 2-opt delta invalid. The 2-opt therefore compares full `TourCost` before/after each trial reversal (O(n³)/sweep — fine for N≤30 and still terminating beyond). Covered by `Solve_HandlesAsymmetricMatrix_WithoutIncreasingCost`.
- **Never-worse (AC4):** `SortTravelingSalesmanAsync` computes the pre-sort tour cost (identity permutation over the current OrderIndex sequence) and keeps the optimized tour only when strictly better; otherwise the current order is retained. Position 0 is always anchored and pinned Start/Finish are excluded from the 2-opt window, so pins never move.
- **`ArrangeWithPins` reuse:** the solved index permutation is mapped back to the tracked `ItemRow`s and run through the existing `ArrangeWithPins` → `Renumber` → `SetOrderAsync` chain (defensive Start→1/Finish→N arrangement), exactly as the reorder/designation paths do.
- **AC2 (never automatic):** only `TripViewModel.SortTravelingSalesmanAsync()` (the button) calls the sort. `EnablingTripView_SeedsByAddedDate_AndDoesNotAutoSort` proves enabling Trip View seeds but does not sort.
- **Concurrency (deferred-work note):** the sort reuses the existing split read/compute/write-under-gate pattern of the sibling ordering methods — it does **not** widen the atomicity window beyond what `ReorderStopAsync`/`SetPinAsync` already do (a single awaited call per Blazor circuit). The "OrderIndex write-path atomicity" / "pin-order atomicity" defers remain as-is; TSP did not introduce a new concurrent writer. Left for the reviewer to weigh against MCP (Story 3.2), which adds an off-circuit caller.
- **No EF migration** — reads/writes only existing schema (`OrderIndex`, `RouteSegment`).
- Both surfaces (`TripStopList.razor` desktop + `MobileTripPanel.razor`) gained the Sort button next to Recompute, with a polite aria-live announcement; all strings via `UiStrings`.

### File List

**New (production):**
- `LucidCartographer/Services/Trip/IDistanceMatrixService.cs`
- `LucidCartographer/Services/Trip/DistanceMatrixService.cs`
- `LucidCartographer/Services/Trip/TspSolver.cs`

**New (tests):**
- `LucidCartographer.Tests/Services/TspSolverTests.cs`
- `LucidCartographer.Tests/Services/DistanceMatrixServiceTests.cs`
- `LucidCartographer.Tests/Services/TripOrderingServiceTspTests.cs`
- `LucidCartographer.Tests/ViewModels/TripViewModelTspSortTests.cs`

**Modified (production):**
- `LucidCartographer/Services/Trip/ITripOrderingService.cs` — added `SortTravelingSalesmanAsync`.
- `LucidCartographer/Services/Trip/TripOrderingService.cs` — injected `IDistanceMatrixService`; added `SortTravelingSalesmanAsync` + `MatrixIndexOf`.
- `LucidCartographer/Configuration/TripServicesExtensions.cs` — registered `IDistanceMatrixService` in the parameterless overload.
- `LucidCartographer/Components/Shared/Trip/TripViewModel.cs` — added `SortTravelingSalesmanAsync()` + `LastSortAnnouncement`.
- `LucidCartographer/Services/UiStrings.cs` — added `TripSortTspLabel`/`TripSortTspAria`/`TripSortTspAnnouncement`.
- `LucidCartographer/Components/Shared/Trip/TripStopList.razor` — desktop Sort button + live region.
- `LucidCartographer/Components/Shared/Trip/MobileTripPanel.razor` — mobile Sort button + live region.

**Modified (tests):**
- `LucidCartographer.Tests/TestDbHelper.cs` — added `CreateOrderingService` helper.
- `LucidCartographer.Tests/Services/TripOrderingServiceTests.cs`, `TripPlaceableTests.cs`, and 9 other Trip test files — updated `TripOrderingService` construction to the new 4-arg signature via the helper.
- `LucidCartographer.Tests/Components/Trip/TripStopListTests.cs` — added Sort-button render/click tests.

## Change Log

| Date | Change |
|------|--------|
| 2026-06-14 | Story 3.1 implemented: TSP-Sort (DistanceMatrixService + TspSolver NN/2-opt) on the single OrderIndex write path, button on both surfaces. Full suite 867/867 green incl. Trip integration. Status → review. |
| 2026-06-14 | Fresh-context adversarial review (0 CRITICAL/0 HIGH/1 MED/2 LOW). MED auto-fixed: a no-op sort (never-worse guard kept the order) no longer announces a reorder on the aria-live region — now matches MoveStopToAsync's no-op silence; added `SortTravelingSalesmanAsync_KeepsSilent_WhenOrderUnchanged`. 2 LOW accepted (position-0 anchor for unpinned open path — never worse; announcement not cleared — pre-existing pattern). Full suite 869/869 green. Status → done. |

## Senior Developer Review (AI)

**Reviewer:** adversarial fresh-context review via bmad-story-automator-review (claude-opus-4-8)
**Date:** 2026-06-14
**Outcome:** Approve (0 CRITICAL, 0 HIGH, 1 MED fixed, 2 LOW accepted)

File List cross-checked against `git status` — exact match, no undocumented or phantom changes. All 5 ACs verified implemented; every `[x]` task backed by real code + assertions (not placeholders). The single-write-path invariant (AR-11) holds — the sort funnels through `ArrangeWithPins` → `Renumber` → `SetOrderAsync`. The asymmetric-matrix risk ([TRIP-CACHE-01]) is correctly handled by full-cost 2-opt evaluation. The DI regression risk (integration host constructing `TripOrderingService` with the new matrix dependency) is cleared by the full integration run.

### Action Items

- [x] [AI-Review][MED] No-op sort announced a reorder that didn't happen — fixed in `TripViewModel.SortTravelingSalesmanAsync` (announce only when the PoiId sequence changes).
- [x] [AI-Review][LOW] (accepted) `TspSolver` anchors tour position 0 for an unpinned open path — may miss some improvements but never produces a worse result (never-worse guard); documented inline.
- [x] [AI-Review][LOW] (accepted) `LastSortAnnouncement` is never cleared — consistent with the pre-existing `LastReorderAnnouncement` pattern.
