---
baseline_commit: 6e22753a527d07c4643723f7acc7850f64e346c6
---

# Story 2.4: Cache invalidation, recompute & Estimated→Measured upgrade

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a self-hoster,
I want computed times cached and only recomputed when something really changed,
so that the app stays responsive and never hammers the provider.

## Acceptance Criteria

_(Source: epics.md → Epic 2 → Story 2.4; FR-11, AR-4, NFR1, NFR10/SM-C2, UX-DR9, AR-11)_

1. **No-op reorder ⇒ no recompute (SM-C2).** When a Stop Order change introduces no new `(From, To, Mode)` pair (all consecutive pairs already cached), no recomputation is triggered — only the displayed legs change. (The VM already gates the compute trigger on `IsAnyLegComputing`; this AC is a guarantee to preserve + prove, not new behavior.)

2. **Invalidation on coordinate change.** When a Stop's POI coordinates change, the cached `RouteSegment` rows for every leg touching that POI (as `FromPoiId` **or** `ToPoiId`, across modes) are invalidated (deleted) so they recompute on the next trigger. This is wired at the real coordinate-mutation paths (`PoiService` edit + the enrichment coordinate write). A **Manual** row is exempt (user-entered, not derived from coordinates).

3. **Invalidation on mode / provider / assumed-speed change.** Mode change already selects a different cache key (Story 2.2) so prior-mode rows are naturally unused — keep that. A provider change or assumed-speed change is a deployment/config event handled by the **explicit Recompute** action (below): there is no silent runtime mutation of those settings, so a user forces a refresh after changing them. (Document this; do not build a runtime config-watcher.)

4. **Explicit "Recompute travel times" action (UX-DR9).** A user-initiated control (both surfaces) re-requests leg times for the active trip: it invalidates the **eligible** cached rows — `Estimated`, `Placeholder`, and `EstimatedFallback`, but **never** `Manual` (user-entered) and **never** downgrades `Measured` — then signals the background compute. The action is on-demand only (never automatic).

5. **Estimated→Measured upgrade, never silent (UX-DR9).** When a recompute (or a provider-available signal) causes an eligible leg to be served by a higher-fidelity provider, the leg **upgrades** Estimated→Measured: its line goes solid, its badge updates, the total recomputes — and the change lands via the VM's `StateChanged` (never silently mutated on a stale screen). In Epic 2 the Mock yields no Measured result, so the upgrade is exercised by a test with a stub Measured provider; the mechanism (recompute → invalidate eligible → provider → upgraded row → `StateChanged`) must be real.

6. **Responsiveness / off-thread (NFR1, NFR3).** Recompute runs off the circuit thread through the existing `TravelTimeComputationBackgroundService` + `TravelTimeTrigger`; the UI shows the pending/computing state and updates via `StateChanged`. Invalidation writes go under the shared `SqliteWriteLock`.

7. **Scope & convention gates.** No OSRM/road geometry (Epic 4); no dwell (2.5); no timeline (2.6). No new migration (the cache key stays `(FromPoiId, ToPoiId, TravelMode)`; Provider remains the `Source` column, single active provider in Epic 2). Build warnings-as-errors, no group-B analyzer violations, no `ConfigureAwait(false)`; all UI text via `UiStrings`; new decisions tagged `TRIP-INVALIDATE-01` / `TRIP-RECOMPUTE-01`; both surfaces updated; canonical units.

## Tasks / Subtasks

- [x] **Task 1 — Invalidation service (AC: 2, 4)**
  - [x] Add `IRouteSegmentInvalidationService` + impl in `Services/Trip/` (interface-first), registered in `Configuration/TripServicesExtensions.cs` (the parameterless overload — it's VM/edit-path facing, no Polly/hosted dependency; keeps the integration host booting). Methods:
    - `Task InvalidateForPoiAsync(int poiId, CancellationToken ct)` — delete `RouteSegment` rows where `FromPoiId == poiId || ToPoiId == poiId` AND `Fidelity != Manual`, under `SqliteWriteLock`.
    - `Task<int> InvalidateRecomputableForCollectionAsync(int collectionId, CancellationToken ct)` — delete the rows backing the collection's current legs whose `Fidelity` is `Estimated`/`Placeholder`/`EstimatedFallback` (NOT `Manual`, NOT `Measured`); return the count. (Used by Recompute.)
  - [x] Per-worker `IDbContextFactory<AppDbContext>`; write under the shared `SqliteWriteLock` (reuse the singleton). Tag `// TRIP-INVALIDATE-01`.

- [x] **Task 2 — Wire coordinate-change invalidation (AC: 2)**
  - [x] In `PoiService` (the canonical edit path, ~`:121-122` Update and ~`:526-527` coord-clear): when a POI's `Latitude`/`Longitude` actually change (compare old vs new — don't invalidate on an unchanged save), call `InvalidateForPoiAsync(poiId)`. Inject the service.
  - [x] In `PoiEnrichmentBackgroundService` (~`:450-451`, where enrichment sets `poi.Latitude/Longitude`): after the enrichment write, if coordinates changed, invalidate that POI's segments. Keep it minimal and off the hot path; reuse the same service (it's registered in the parameterless overload, available to the hosted worker). Tag `// TRIP-INVALIDATE-01`.
  - [x] **Deferred (note, do not implement):** the MCP `PoiWriteTools` coordinate write (`Services/Mcp/PoiWriteTools.cs:160-161`) — leave a `// TODO TRIP-INVALIDATE-01` referencing this story so a follow-up can route MCP coord edits through the same invalidation. (Agent-driven coordinate edits are rare and the user can Recompute.)

- [x] **Task 3 — Recompute action in the ViewModel (AC: 4, 5, 6)**
  - [x] Add `public async Task RecomputeTravelTimesAsync()` to `TripViewModel`: guard on active collection + Trip View on; call `InvalidateRecomputableForCollectionAsync(collectionId)`; `await RefreshProjectionsAsync(collectionId)` (the now-missing rows ⇒ `IsAnyLegComputing` ⇒ it already `Signal()`s the trigger); `Notify()`. Inject `IRouteSegmentInvalidationService`. Tag `// TRIP-RECOMPUTE-01`.
  - [x] No-op-reorder guarantee (AC1): confirm/keep that `RefreshProjectionsAsync` only `Signal()`s when `IsAnyLegComputing` — a reorder over a fully-cached trip must not invalidate or recompute. Do not add an unconditional signal to any reorder path.
  - [x] Upgrade path (AC5): rely on the background service writing the higher-fidelity row + the existing progress→`RefreshLegsFromCacheAsync` path raising `StateChanged`. The `Measured` row makes `TripLeg.IsMeasured` true ⇒ the existing solid-line/`secondary`-badge treatment applies. No silent screen mutation (it lands via the subscription, not a manual refresh).

- [x] **Task 4 — Recompute UI, both surfaces (AC: 4, 6, 7)**
  - [x] Add a "Recompute travel times" button to the trip header area on `TripStopList.razor` (desktop) and `MobileTripPanel.razor` (mobile) — NOT inside the clickable stop rows (2.2 lesson: elements over the row center break Playwright row-selection). `UiStrings` label + `aria-label`. Wire to `Vm.RecomputeTravelTimesAsync`. While computing, the existing `aria-live` computing state applies.
  - [x] No hardcoded strings; muted/`primary` token treatment consistent with the palette.

- [x] **Task 5 — Tests (AC: all)**
  - [x] **Unit (invalidation):** `InvalidateForPoiAsync` deletes rows where the POI is From or To (both directions, all modes) and **keeps** a `Manual` row; `InvalidateRecomputableForCollectionAsync` deletes Estimated/Placeholder/EstimatedFallback but keeps Manual and Measured; returns the count.
  - [x] **Unit (no-op reorder, AC1):** a fully-cached roundtrip, then a reorder that yields the same `(From,To,Mode)` pair set ⇒ no `RouteSegment` rows deleted/added and the trigger is not signalled (assert via row count + a provider call counter / a non-signalled `TravelTimeTrigger`).
  - [x] **Unit/VM (recompute + upgrade, AC4/5):** `RecomputeTravelTimesAsync` invalidates eligible rows and leaves Manual intact; after a background pass with a **stub Measured provider**, the leg's `RouteSegment` is `Measured`, `TripLeg.IsMeasured` is true, and the VM raised `StateChanged`. With the Mock, an Estimated leg recomputes to Estimated (no spurious upgrade).
  - [x] **Unit (coords hook):** updating a POI's coordinates via `PoiService` invalidates that POI's non-Manual segments; an unchanged save does not.
  - [x] **Component (bUnit), both surfaces:** the Recompute button renders, is `UiStrings`-labelled, and invokes the VM; clicking it does not break row selection (place it outside the rows).
  - [x] Full unit/component suite green; **Trip integration green** (host boot + selection); no new analyzer warnings.

## Dev Notes

### Scope guardrails
- **In scope:** an invalidation service (by-POI + recompute-eligible), coordinate-change invalidation wired at `PoiService` + enrichment, the explicit **Recompute** action (VM + button both surfaces), the Estimated→Measured **upgrade mechanism** (tested via a stub Measured provider), and preserving + proving the no-op-reorder = no-recompute guarantee.
- **OUT of scope:** OSRM / a real Measured provider (Epic 4); a runtime provider/assumed-speed config-watcher (handled by manual Recompute); MCP coord-write invalidation (leave a TODO); dwell (2.5); timeline (2.6); any schema/migration change (cache key unchanged).

### Already-true behavior to preserve (don't re-implement)
- `TripViewModel.RefreshProjectionsAsync` (`:659-694`) only calls `travelTimeTrigger.Signal()` when `IsAnyLegComputing` (a leg lacks a cache row). So a reorder over a fully-cached trip already triggers **no** recompute (AC1). Reorder paths (`MoveStopToAsync` `:258`, drag `OnDropAsync`, `:274`) end in `RefreshProjectionsAsync` — keep them as-is; do **not** add an unconditional `Signal()`.
- `TravelTimeComputationBackgroundService.LoadPendingLegsAsync` only queues `(From,To,Mode)` pairs lacking a row; `UpsertAsync` already guards Manual + Measured (Story 2.3) — those guards become **load-bearing** now that Recompute deletes-then-recomputes (a Manual row is never deleted by invalidation, so it's never re-queued; a Measured row is kept too). The fallback (2.3) re-attempts on the next trigger.

### Cache key / Provider dimension (reconcile FR-11 with AR-1)
FR-11 names the key `(FromStop, ToStop, Travel Mode, Provider)`, but the shipped `RouteSegment` key (AR-1, migration `20260611213107_AddTripPlanning`) is `(FromPoiId, ToPoiId, TravelMode)` with Provider as the `Source` column. Epic 2 has exactly one active provider (Mock), so the Provider dimension is moot — do **not** add it to the key (that would need a forbidden second migration). A provider change is a deployment event: the operator/user forces a full refresh via Recompute. Document this in the invalidation service XML doc.

### Built on 2.1–2.3 (reuse, don't reinvent)
- `RouteSegment` (`Data/Entities/RouteSegment.cs`), `Fidelity`/`TravelMode` constants, `TravelTimeSource` (`Mock`/`Manual`/`EstimatedFallback`, Story 2.3). Eligible-to-recompute = NOT `Manual` and NOT `Measured`.
- `SqliteWriteLock` (singleton, shared write gate) — invalidation deletes go under it.
- `TripViewModel`: `Set/ClearManualLegTimeAsync` (2.2) show the VM's write-under-lock + refresh + `Notify` pattern — mirror it for Recompute. `IsShowingApproximateEstimates`/`IsFallback` (2.3) — a successful recompute that replaces an `EstimatedFallback` row with a fresh `Mock`/`Measured` row should clear the note (happens naturally via the refresh).
- `TravelTimeComputationBackgroundService` + `TravelTimeTrigger` + the progress→`RefreshLegsFromCacheAsync` subscription — the recompute reuses all of it (invalidate → Signal → compute → StateChanged). No new background plumbing.
- `FidelityBadge.razor` + the dashed/solid line treatment already switch on `Measured` — an upgraded leg renders solid + `secondary` badge automatically.

### Coordinate-write sites (Task 2 targets)
- `Services/PoiService.cs:121-122` (Update) and `:526-527` (coord-clear) — canonical edit path; compare old vs new lat/lon, invalidate only on real change.
- `Services/Enrichment/PoiEnrichmentBackgroundService.cs:450-451` — enrichment sets coords; invalidate the POI's segments after the write when coords changed. This is the common "a previously-unplaceable stop got coordinates" / "pin moved" path. (A newly-placeable stop has no prior rows — invalidation is a no-op there; harmless.)
- `Services/Mcp/PoiWriteTools.cs:160-161` — **deferred**, leave a TODO only.

### Architecture & conventions (project-context.md)
- Layering Component→ViewModel→Service→Data; the invalidation service is a Service; the Recompute button binds to a VM method. `PoiService`/enrichment call the Service.
- Build discipline: warnings-as-errors, no group-B analyzer violations, no `ConfigureAwait(false)`, `CultureInfo` on formats. Tag `TRIP-INVALIDATE-01` / `TRIP-RECOMPUTE-01`.
- i18n: Recompute label + any text via `UiStrings`. a11y: button `aria-label`; both desktop and `Mobile*`. DI: invalidation service in the parameterless `AddTripServices` (no Polly/hosted dependency) so the integration host still boots; if the VM gains a ctor dependency, update the integration test host's VM construction and the unit-test VM builders.
- Units canonical; no UI-edge conversion changes here.

### Testing standards
- Unit (invalidation by-POI + recompute-eligible; no-op-reorder no-signal; coords hook via `PoiService`; VM recompute + stub-Measured upgrade), Component (bUnit Recompute button both surfaces via `MobileTestBase`). `InternalsVisibleTo` set — drive internals directly; inject a stub `ITravelTimeProvider` returning `Measured` for the upgrade test. EF InMemory / temp-SQLite for invalidation/upsert tests.

### Previous-story intelligence
- 2.1: keep the `AddTripServices` split — the integration test host calls the parameterless overload and must boot; register the invalidation service there.
- 2.2: a UI element over the stop-row center breaks Playwright row-selection — put the Recompute button in the header/panel area. Adding a VM ctor dependency means updating EVERY `new TripViewModel(...)` call site (unit + integration test builders) — grep for them.
- 2.2/2.3: tests must exercise the computed path, not seed shortcuts; assert the upgraded row's `Fidelity=Measured` + `IsMeasured` + that `StateChanged` fired, not just the VM flag.
- Reuse `GeoUtils.HaversineDistance`; never hand-roll.

### References
- [Source: _bmad-output/planning-artifacts/epics.md#Story 2.4] — FR-11, AR-4, NFR1, NFR10/SM-C2, UX-DR9
- [Source: _bmad-output/planning-artifacts/epics.md#Additional Requirements] — AR-4 (cache + centralized invalidation + matrix; explicit Estimated→Measured upgrade), AR-1 (cache key — do not change), AR-11 (directional key, units)
- [Source: _bmad-output/project-context.md]
- [Source: LucidCartographer/Components/Shared/Trip/TripViewModel.cs:258-694, 1034-1170] (reorder/refresh/signal, manual-entry pattern), [TravelTimeComputationBackgroundService.cs] (LoadPendingLegs/UpsertAsync guards), [TravelTimeSource.cs]
- [Source: LucidCartographer/Services/PoiService.cs:121-122,526-527], [Services/Enrichment/PoiEnrichmentBackgroundService.cs:450-451], [Services/Mcp/PoiWriteTools.cs:160-161]
- [Source: LucidCartographer/Configuration/TripServicesExtensions.cs], [Data/Entities/RouteSegment.cs], [Fidelity.cs], [Components/Shared/Trip/FidelityBadge.razor], [TripStopList.razor], [MobileTripPanel.razor], [Services/UiStrings.cs]

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (BMAD dev-story; delegated dev subagent + orchestrator verification + fresh-context review)

### Debug Log References

- Build: 0 warnings / 0 errors.
- Unit/component (`!~Integration`): **641 passed**.
- Trip integration (`Integration&Trip`): **19 passed** (host boot + VM/PoiService ctor change verified).

### Completion Notes List

- ✅ AC1 — no unconditional `Signal()` added; the only trigger stays gated on `IsAnyLegComputing` in `RefreshProjectionsAsync`. Test `NoOpReorder_OverFullyCachedTrip_AddsNoRows_AndDoesNotSignal` (drained trigger + provider-call counter + row count).
- ✅ AC2 — `RouteSegmentInvalidationService.InvalidateForPoiAsync` deletes both-direction/all-mode rows, keeps `Manual`; wired at `PoiService` (Update + coord-clear, old-vs-new compare, null↔value safe) and the enrichment coord write (only on real change). MCP coord write left as a tracked TODO.
- ✅ AC3 — documented (Provider = `Source` column, single Epic-2 provider; provider/assumed-speed change handled by the explicit Recompute, no runtime config-watcher).
- ✅ AC4 — `RecomputeTravelTimesAsync` invalidates eligible (Estimated/Placeholder/EstimatedFallback), keeps Manual + Measured, re-queues missing rows; Recompute button on both surfaces, header area, `UiStrings`-labelled.
- ✅ AC5 — upgrade mechanism real: a background pass with a **stub Measured provider** writes a `Measured` row → `IsMeasured` true, solid line + `secondary` badge, total recomputes, `StateChanged` fired (test `Recompute_ThenBackgroundPass_WithMeasuredProvider_UpgradesLeg_AndFiresStateChanged`); with the Mock, Estimated→Estimated (no spurious upgrade).
- ✅ AC6 — recompute reuses the off-circuit `TravelTimeComputationBackgroundService`/`TravelTimeTrigger`; invalidation deletes under `SqliteWriteLock`.
- ✅ AC7 — no migration, clean build, both surfaces, tags applied.
- Review (fresh context): **0 CRITICAL / 0 HIGH / 0 MEDIUM / 2 LOW**, both accepted (below). Two highest-risk seams explicitly cleared: **no enrichment write-lock self-deadlock** (invalidation runs after the enrichment write releases the gate, in a disposed `IServiceScopeFactory` scope) and the cross-collection shared-row behavior is harmless.

**Accepted LOW findings (with Epic-4 follow-ups):**
- [ ] [LOW, accepted] `InvalidateRecomputableForCollectionAsync` deletes by collection-member POI cross-product; since `RouteSegment` is keyed by POI pair (no collection id), a pair shared by two collections (`copy_poi`) shares one row, so Recompute on one trip also invalidates the other's Estimated leg. Harmless in Epic 2 — only Estimated/Placeholder/EstimatedFallback are deleted (Manual/Measured survive) and they recompute deterministically to the same value via the single provider; worst case is a redundant recompute. **Epic-4 follow-up:** with a real Measured/OSRM provider this could cause a redundant out-call — scope deletion to the active collection's ordered consecutive pairs then, or add a collection dimension (needs a migration).
- [ ] [LOW, accepted] The AC1 no-op-reorder test uses a same-position move on a 2-stop trip (the only honest no-op for N=2 — any swap flips the pair set and should recompute). It genuinely exercises the gated `Signal()` path (drained trigger + counters), per the reviewer; not a tautology.

**Deviations / decisions:**
- MA0026 (forbids the literal `TODO` token under warnings-as-errors): the required MCP-invalidation TODO is wrapped in a tightly-scoped `#pragma warning disable/restore MA0026` so the tracked marker survives and the build stays green. Reviewer judged this acceptable.
- Enrichment (hosted singleton) injects `IServiceScopeFactory` to resolve the Scoped invalidation service per coord-write from a fresh `await using` scope (correctly disposed; no lock re-entrancy).
- Page-test DI containers (`DataSourcesPageTests`/`OperationsPageTests`) register the invalidation service + `SqliteWriteLock` since they build `PoiService` via DI.
- Boundary TODOs: dwell (2.5), timeline (2.6) untouched; no migration.

### File List

**New (5):**
- `LucidCartographer/Services/Trip/IRouteSegmentInvalidationService.cs`
- `LucidCartographer/Services/Trip/RouteSegmentInvalidationService.cs`
- `LucidCartographer.Tests/Services/RouteSegmentInvalidationTests.cs`
- `LucidCartographer.Tests/Services/PoiServiceCoordInvalidationTests.cs`
- `LucidCartographer.Tests/ViewModels/TripViewModelRecomputeTests.cs`

**Modified (production):**
- `LucidCartographer/Configuration/TripServicesExtensions.cs` (register invalidation service in the parameterless overload)
- `LucidCartographer/Services/PoiService.cs` (ctor + coord-change invalidation, Update + coord-clear)
- `LucidCartographer/Services/Enrichment/PoiEnrichmentBackgroundService.cs` (`IServiceScopeFactory` + invalidate-on-coord-change)
- `LucidCartographer/Services/Mcp/PoiWriteTools.cs` (deferred TODO, pragma-scoped)
- `LucidCartographer/Components/Shared/Trip/TripViewModel.cs` (ctor + `RecomputeTravelTimesAsync`)
- `LucidCartographer/Components/Shared/Trip/TripStopList.razor` + `MobileTripPanel.razor` (Recompute button)
- `LucidCartographer/Services/UiStrings.cs` (`TripRecomputeLabel`/`TripRecomputeAria`)

**Modified (tests):** `TestDbHelper.cs` (+`CreateInvalidationService`), all `new TripViewModel(...)` + `new PoiService(...)` builders, `DataSourcesPageTests.cs`, `OperationsPageTests.cs`.

## Change Log

| Date | Change |
|------|--------|
| 2026-06-14 | Story 2.4 implemented: `RouteSegmentInvalidationService` (by-POI + recompute-eligible), coord-change invalidation wired at `PoiService` + enrichment, explicit Recompute action (VM + button both surfaces), Estimated→Measured upgrade mechanism (stub-Measured-provider test), no-op-reorder no-recompute preserved. No migration. Build clean; 641 unit/component + 19 Trip integration green. Status → review. |
| 2026-06-14 | Adversarial review (fresh context): 0 CRITICAL/0 HIGH/0 MEDIUM/2 LOW (both accepted); enrichment write-lock re-entrancy and cross-collection shared-row seams explicitly cleared. Status → done. |

## Senior Developer Review (AI)

**Outcome:** Approve (done) — 0 CRITICAL / 0 HIGH / 0 MEDIUM; 2 LOW accepted.
**Reviewer:** Fresh-context adversarial reviewer (separate agent from the implementer).
**Date:** 2026-06-14

**Findings:**
- [ ] [LOW, accepted, Epic-4 follow-up] Cross-collection shared `RouteSegment` row → Recompute on one trip invalidates another's Estimated leg (redundant recompute, never stale/wrong data in Epic 2).
- [ ] [LOW, accepted] AC1 no-op-reorder test uses a same-position N=2 move (the only honest no-op for N=2); genuinely exercises the gated signal path.

**Verified safe (priority seams):** enrichment write-lock — NO self-deadlock (invalidate runs outside the enrichment's gate, in a disposed scope); cross-collection rows — by-design, harmless. All 7 ACs hold. DI: all 17 `new TripViewModel(`/`new PoiService(` call sites updated; integration host boots. No migration; no scope leakage into 2.5/2.6/Epic 4 (Measured provider is a test stub only).
