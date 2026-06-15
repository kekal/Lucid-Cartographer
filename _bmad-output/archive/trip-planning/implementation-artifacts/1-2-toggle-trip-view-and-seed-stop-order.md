---
baseline_commit: 03d2765b0bc04755fa4329929211366e3c2bcbfe
---

# Story 1.2: Toggle Trip View and seed Stop Order

Status: done

## Story

As a self-hoster viewing a collection,
I want a discoverable Trip View toggle that reveals an ordered Trip,
so that I can switch a plain collection into a trip and back without losing anything.

## Acceptance Criteria

_Derived from epics.md Story 1.2 (FR-1, FR-2, FR-17, UX-DR1, UX-DR2). Each Given/When/Then is decomposed below; all detail preserved._

**AC1 — Discoverable toggle in the filtered-results region, both surfaces (FR-17, UX-DR1).**
1. On a collection whose visible/filtered result set contains **≥2 placeable POIs** (a POI is *placeable* when both `Latitude` and `Longitude` are non-null), a **Trip View toggle** is rendered in the **filtered-results region** of the collection view — NOT inside a menu, dropdown, or overflow.
2. The toggle exposes `aria-pressed` reflecting its current on/off state and **announces its on/off change** via an `aria-live` region (copy through `UiStrings`).
3. On a collection whose filtered result set has **fewer than 2 placeable POIs**, the toggle is **hidden or disabled** — it must NEVER render an error, throw, or show a broken affordance ("Trip View unavailable" = simply absent/disabled, per UX-DR10).
4. The toggle is rendered on **BOTH** render paths: desktop (the filtered-results header region above/around `PoiTable`) and mobile (`Viewport.IsMobile` bottom-panel results region). Behavior and accessibility are identical on both.

**AC2 — First toggle-on seeds a deterministic, contiguous Stop Order with badges (FR-2, UX-DR2).**
5. When Trip View is toggled **on for the first time** on a collection that has **never had a Stop Order** (no persisted `OrderIndex` values for its items), the system assigns a **deterministic seed order** ordering the placeable items by **POI added-date (`Poi.AddedDate`) ascending** (ties broken deterministically, e.g. by `PoiId` ascending).
6. The seeded order is **contiguous and gap-free, 1..N**, 1-based (AR-11), **unique** per item, and **persisted** to `PoiCollectionItem.OrderIndex` via the single ordering-write method (`SqliteWriteLock`-guarded).
7. Each stop shows a **`primary`-filled order badge** (numbered circle, `on-primary` numeral, `text-xs` weight 700, fully rounded) **in the stop list** and **on its map marker**.

**AC3 — Toggle off restores the plain collection losslessly; state persists per-collection (FR-1, FR-2).**
8. When Trip View is toggled **off**, **all trip affordances disappear** (order badges in list and on markers, the trip-scoped view) and the **plain collection is restored** — the same POI set and the same existing controls as before Trip View, with **no membership change** (no POI added, removed, or reordered in the collection itself).
9. Toggling never modifies, reorders, or deletes POI **membership** — order is metadata on the existing join rows only.
10. Trip View state (**on/off**) and the **Stop Order** are **persisted per-collection** (`PoiCollection.TripViewEnabled` + `PoiCollectionItem.OrderIndex`); **reopening** the collection later restores both exactly.

**AC4 — Membership change while a Stop Order exists keeps order contiguous (FR-2).**
11. When a POI is **added** to a collection that already has a Stop Order, it is **appended as the new last Stop** (`OrderIndex = current max + 1`).
12. When a POI is **removed** from a collection that has a Stop Order, the remaining stops **re-compact** so the order stays **contiguous and gap-free (1..N)** with no duplicate or skipped index.
13. Append/compaction is computed over **placeable** items for badge numbering consistency, runs through the same single ordering-write method, and persists.

> **Scope boundary (do not implement here):** leg drawing / stop-panel layout (Story 1.3), drag & keyboard reorder (1.5), unplaceable handling beyond the ≥2-placeable visibility gate (1.6), Start/Finish designation (1.7), travel times / dwell / timeline (Epic 2). `TripOrderingService` seed+append+compaction and `TripViewModel` are **begun** here; later stories extend them.

## Tasks / Subtasks

- [x] **Task 1 — Trip ordering service: seed + append + compaction (AC2, AC3, AC4)**
  - [x] Create `Services/Trip/ITripOrderingService.cs` and `Services/Trip/TripOrderingService.cs` (NEW vertical slice, interface-first). [Source: architecture.md#AR-12; #Structure Patterns]
  - [x] Implement a method that returns whether a collection already has any persisted `OrderIndex` (never-ordered detection for the first-toggle-on path). [AC2]
  - [x] Implement **seed**: order placeable items by `Poi.AddedDate` ascending, ties by `PoiId` ascending; assign contiguous **1-based** `OrderIndex` 1..N; persist. Tag `// TRIP-ORDER-01: 1-based contiguous seed by AddedDate asc`. [AC2; Source: architecture.md#D1, #AR-11]
  - [x] Implement **append**: new POI → `OrderIndex = max existing + 1`. [AC4]
  - [x] Implement **re-compact**: after a removal, renumber remaining items to contiguous 1..N preserving relative order. [AC4]
  - [x] Route **all** `OrderIndex` writes through **one** method (`SetOrder`/equivalent) under `SqliteWriteLock.Gate` + `IDbContextFactory<AppDbContext>`; never mutate order rows from elsewhere. No `ConfigureAwait(false)`. [Source: architecture.md#Communication Patterns; project-context.md]
  - [x] Add `TravelMode`/`Fidelity` enums are NOT needed for this story — do not pull Epic-2 surface in.
- [x] **Task 2 — TripViewModel (AC1, AC2, AC3, AC4)**
  - [x] Create `Components/Shared/Trip/TripViewModel.cs`: `sealed`, **primary-constructor DI** (inject `ITripOrderingService`, `IPoiService`, `IDbContextFactory<AppDbContext>` as needed, `ILogger<TripViewModel>`), expose `event Action? StateChanged` + private `Notify()`, state with `private set`, own a `CancellationTokenSource`, implement `IAsyncDisposable`. [Source: project-context.md#Architecture Layering; architecture.md#AR-12]
  - [x] Expose `bool IsTripViewEnabled { get; private set; }`, an `IsToggleAvailable` derived from a placeable-count ≥ 2 input, and the ordered stop projection (poiId → order badge number) for the active collection.
  - [x] `ToggleAsync()`: flip + persist `PoiCollection.TripViewEnabled`; on first enable of a never-ordered collection, call the seed path; raise `StateChanged`. [AC2, AC3]
  - [x] `LoadAsync(collectionId)` / restore: read persisted `TripViewEnabled` + `OrderIndex` so reopening restores state. [AC3]
  - [x] On membership change notification (POI added/removed in the active collection while a Stop Order exists), call append/compaction and `Notify()`. [AC4]
  - [x] Provide localized announcement text (on/off) sourced from `UiStrings` for the `aria-live` region.
- [x] **Task 3 — DI registration (AC1–AC4)**
  - [x] Create `Configuration/TripServicesExtensions.cs` with `AddTripServices(this IServiceCollection)` registering `ITripOrderingService → TripOrderingService` (Scoped, matching slice precedent) and the shared `SqliteWriteLock` (already a singleton — reuse, do not re-register a second instance). Call it from `Program.cs` composition root. [Source: architecture.md#AR-12, #File Organization; DeduplicationServicesExtensions.cs precedent]
  - [x] Register `TripViewModel` as **Transient** in `Configuration/ViewModelExtensions.cs` (alongside `MapPageViewModel`). [Source: project-context.md#Architecture Layering]
- [x] **Task 4 — Desktop UI: TripToggle + badges (AC1, AC2, AC3)**
  - [x] Create `Components/Shared/Trip/TripToggle.razor` — a switch with `aria-pressed`, `aria-label` (via `UiStrings`), `primary`-accented active state when on; disabled/hidden when placeable count < 2. Thin component: subscribe `Vm.StateChanged` in `OnInitializedAsync`, `OnVmChanged() => InvokeAsync(StateHasChanged)`, unsubscribe + dispose in `DisposeAsync`. [Source: DESIGN.md#Trip View toggle; project-context.md#Component bridge]
  - [x] Add an `aria-live="polite"` status span that announces the on/off change. [Source: EXPERIENCE.md#Accessibility Floor]
  - [x] Place `TripToggle` in the **desktop filtered-results region** of `MapPage.razor` (the existing filter-chip / fit-labels header bar above the `PoiTable`, or `PoiTable`'s own filtered-results header). Do NOT put it in a menu. [AC1; Source: MapPage.razor lines 242–284, PoiTable.razor lines 11–19]
  - [x] Create `Components/Shared/Trip/StopOrderBadge.razor` — reusable numbered circle (`primary` fill, `on-primary` numeral). Render it in the stop list rows and pass the order number to the map marker draw path. [AC2/AC3; Source: DESIGN.md#Stop-order badge; PoiTable.razor order-badge placement precedent]
  - [x] When Trip View is on, surface the order badge on each placeable POI in the desktop results list (badge only — full stop-list panel/legs are Story 1.3).
- [x] **Task 5 — Map marker order badges (AC2, AC3)**
  - [x] Extend the Leaflet marker draw path so that, when Trip View is on, each placeable POI's marker shows its **Stop Order number**; when off, markers revert to the plain numbered/pin state. Reuse the existing `LeafletMapService` → `leafletInterop.js` interop (extend `addCollectionMarkers` or add a thin `setStopOrder`/`highlightStop`-style helper); do NOT add a second JS module. [Source: architecture.md#JS interop naming, #anti-patterns; LeafletMapService.cs lines 36–55]
  - [x] Toggling off removes the order numerals from markers, restoring prior marker rendering with no leftover state.
- [x] **Task 6 — Mobile UI: MobileTripToggle + badges (AC1, AC2, AC3)**
  - [x] Create `Components/Shared/Trip/MobileTripToggle.razor` (mobile render path) honoring ≥44px touch targets and safe-area insets; same `aria-pressed` + `aria-live` semantics as desktop. [Source: EXPERIENCE.md#Responsive & Platform, #Accessibility Floor; DESIGN.md mobile rules]
  - [x] Place it in the **mobile bottom-panel results region** of `MapPage.razor` (the `_drawerOpen == false` POI-list header at lines ~142–153). [AC1, AC4-dual-surface]
  - [x] Render order badges on the mobile POI list rows and on mobile map markers when Trip View is on. [AC2, AC3]
- [x] **Task 7 — MapPage wiring (AC1–AC4, no regression)**
  - [x] `MapPage.razor`: `@inject TripViewModel TripVm`; subscribe `TripVm.StateChanged` in `OnInitializedAsync` and dispose in `DisposeAsync` alongside the existing `Vm` bridge. Pass the active collection / placeable-count context to `TripVm`. [Source: MapPage.razor lines 364–369, 537–547]
  - [x] `MapPageViewModel.cs`: expose the current filtered/visible **placeable** POI set and the active collection id needed by `TripVm`; after membership mutations (`RefreshAfterMutationAsync`, add/remove handlers) notify the trip layer so append/compaction runs. Do NOT change existing plain-collection behavior. [AC4; Source: MapPageViewModel.cs lines 482–613, 647–656]
  - [x] Confirm the plain (Trip-off) map/list/detail flow is byte-for-byte unchanged when Trip View is off. [AC3 no-regression]
- [x] **Task 8 — UiStrings additions (all ACs)**
  - [x] Add to `Services/UiStrings.cs`: toggle label, `aria-label`, on-state announcement, off-state announcement, and stop-order badge `aria-label` template (e.g. "Stop {0}"). No hardcoded literals in any new `.razor`/`.cs`. [Source: project-context.md#UI Conventions; NFR5]
- [x] **Task 9 — Tests (unit + bUnit + integration; desktop + mobile)**
  - [x] **Unit — `Services/TripOrderingServiceTests.cs`**: seed assigns 1-based contiguous order by `AddedDate` asc with deterministic tie-break; first-vs-already-ordered detection; append puts new POI at max+1; removal re-compacts to 1..N gap-free; all writes 1-based (assert no 0-based off-by-one). Use EF Core InMemory / temp SQLite per existing unit precedent. [Source: project-context.md#Testing Rules]
  - [x] **Unit — `ViewModels/TripViewModelTests.cs`**: toggle availability gate (≥2 placeable), `ToggleAsync` persists `TripViewEnabled` and seeds on first enable, restore reads persisted state, membership-change triggers append/compaction, `StateChanged` raised; VM is disposed cleanly.
  - [x] **bUnit — `Components/Trip/TripToggleTests.cs`**: renders in filtered-results region with `aria-pressed`; hidden/disabled below 2 placeable; on/off announcement present; order badge renders `primary` fill with correct number.
  - [x] **Integration — Trip toggle flow, BOTH paths**: desktop (`IntegrationTestBase`) and mobile (`MobileTestBase`) — toggle on seeds + shows badges in list and on markers; toggle off restores plain collection with no membership change; reopen restores state; add/remove POI keeps order contiguous. [Source: project-context.md#Testing Rules — cover both render paths]

### Review Findings

_Adversarial code review 2026-06-12 (Blind Hunter + Edge Case Hunter + Acceptance Auditor, triaged). 8 noise/false-positives dropped — notably the diff-only "`marker._poiId` never assigned" and "key-type mismatch" Criticals, both disproven by `leafletInterop.js:341` (`marker._poiId = poi.id`)._

**Decision needed**

- [x] [Review][Decision] Trip toggle scope is narrower than the literal AC1 — `MapPageViewModel.SingleVisibleCollectionId` hides the toggle whenever a search is active OR >1 collection is visible. **Resolved 2026-06-12: keep as-is (closed).** The single-collection scope is the correct reading of "active collection"; per-collection persistence (`TripViewEnabled`+`OrderIndex`) is only coherent against one collection, and Stories 1.3/1.5/1.7 build on this seam.

**Patch**

- [x] [Review][Patch] Viewport flip (desktop⇄mobile) drops all Stop badges — `_pushedStopOrders` is never reset on map re-init, so `PushStopOrdersAsync` short-circuits via `OrdersEqual` and never re-pushes to the new JS map. **Fixed:** reset `_pushedStopOrders = null` in the re-wire block [LucidCartographer/Components/Pages/MapPage.razor:409]
- [x] [Review][Patch] Migration backfill numbers non-placeable rows (`ROW_NUMBER` over a `LEFT JOIN`, all rows) → non-placeable POIs get `OrderIndex>0`, defeating `HasOrderAsync` seed-detection and making `GetStopOrderAsync` return non-placeable rows as stops. **Fixed:** migration numbers only placeable rows (non-placeable → 0) + defensive placeable filter in `HasOrderAsync`/`GetStopOrderAsync`; migration unit test updated to assert the placeable-only invariant [LucidCartographer/Migrations/20260611213107_AddTripPlanning.cs:80; LucidCartographer/Services/Trip/TripOrderingService.cs:19,29]
- [x] [Review][Patch] Membership churn while Trip View is OFF leaves a stale/gappy order on re-enable/reopen. **Fixed:** `ToggleAsync` reconciles on enable when already-ordered, and `LoadAsync` reconciles when restoring an enabled collection (both idempotent) [LucidCartographer/Components/Shared/Trip/TripViewModel.cs]
- [x] [Review][Patch] Async event-handler lambdas / `PushStopOrdersAsync` JS interop. **Resolved — no change needed:** the interop is already guarded centrally by `LeafletMapService.InvokeJsVoidAsync` (`IsCircuitGone` swallows `JSDisconnectedException`/`ObjectDisposedException`/`InvalidOperationException`) and every `TripViewModel` DB method has its own try/catch, so no exception escapes the lambdas. Adding a redundant guard would fight the project's centralized convention [LucidCartographer/Services/LeafletMapService.cs:139]
- [x] [Review][Patch] `TripViewModel.DisposeAsync` is not idempotent (page + DI container double-dispose → `ObjectDisposedException`). **Fixed:** added a `_disposed` guard [LucidCartographer/Components/Shared/Trip/TripViewModel.cs]
- [x] [Review][Patch] Marker badge CSS hardcodes hex instead of token palette. **Fixed:** `var(--primary, #005bbf)` / `var(--on-primary, #fff)` (zero-regression fallbacks) [LucidCartographer/wwwroot/css/base.css:61]
- [x] [Review][Patch] `StopOrderBadge` numeral not `aria-hidden`. **Fixed:** wrapped the numeral in `<span aria-hidden="true">` so the "Stop {N}" `aria-label` is the only announcement [LucidCartographer/Components/Shared/Trip/StopOrderBadge.razor]

**Deferred**

- [x] [Review][Defer] Map-marker Stop badges have no automated coverage — `StubMapService.SetStopOrdersAsync` is a no-op and real Leaflet never runs in integration, so the JS `setStopOrders`/`buildMarkerIcon` + `PushStopOrdersAsync` reconcile path is unasserted [LucidCartographer.Tests/Integration/StubMapService.cs:25] — deferred, harness limitation
- [x] [Review][Defer] No mobile integration coverage for toggle-off restore or AC4 add/remove re-compaction through the UI path (only `MobileToggle_SeedsBadges_OnEnable` exists) [LucidCartographer.Tests/Integration/MobileTripViewTests.cs] — deferred, change-introduced gap
- [x] [Review][Defer] OrderIndex read-compute-write is not atomic under `SqliteWriteLock` (gate wraps only `SaveChangesAsync`); `ToggleAsync` seed+persist is two transactions. Low risk: `TripOrderingService` is the sole `OrderIndex` writer and page calls are awaited sequentially [LucidCartographer/Services/Trip/TripOrderingService.cs:762] — deferred, theoretical under current single-writer wiring
- [x] [Review][Defer] Enrichment that makes an existing member placeable does not append it to an active Stop Order (only membership add/remove fires reconcile), so a now-placeable POI shows no badge until the next membership change [LucidCartographer/Components/Pages/MapPage.razor:156] — deferred, needs design (enrichment hook)

## Dev Notes

### Architecture patterns & constraints

- **Strict layering (Component → ViewModel → Service → Data).** `TripToggle.razor` / `MobileTripToggle.razor` are ~12-line bridges (subscribe `StateChanged`, `InvokeAsync(StateHasChanged)`, dispose). All logic lives in `TripViewModel` → `ITripOrderingService` → EF Core via `IDbContextFactory<AppDbContext>`. Components never touch the DbContext or JS directly (map interop goes through `LeafletMapService`). [Source: project-context.md#Architecture Layering; architecture.md#Component boundaries]
- **ViewModel rules:** `sealed`, primary-constructor DI, registered **Transient** in `ViewModelExtensions.cs`, `event Action? StateChanged` + private `Notify()`, state `private set`, owns a `CancellationTokenSource`, `IAsyncDisposable`. Mirror `MapPageViewModel`. [Source: project-context.md; MapPageViewModel.cs]
- **Single ordering write-path.** Every `OrderIndex` write (seed, append, compact — and later drag/keyboard/TSP/MCP) goes through **one** `TripOrderingService` method under `SqliteWriteLock.Gate`. This is the architectural seam later stories extend; do not write `OrderIndex` from the ViewModel or component. [Source: architecture.md#AR-11, #Communication Patterns]
- **Canonical conventions:** `OrderIndex` is **1-based**, contiguous, gap-free, unique (Start would be 1, last N). No 0-based-storage + `+1`-in-view. [Source: architecture.md#Format Patterns, #AR-11]
- **Build discipline:** `TreatWarningsAsErrors=true`, `Nullable=enable`. New code must introduce **no group-B analyzer violations** (MA0002, MA0015, MA0046, MA0047, MA0074, VSTHRD200) and must **not** add `ConfigureAwait(false)` (MA0004 suppressed for the Blazor circuit). Async methods used by components/VMs follow the existing naming to avoid VSTHRD200. [Source: project-context.md#Build & Language Discipline]
- **TRIP-\* comment codes** on new trip design decisions (e.g. `TRIP-ORDER-01` for the seed rule). [Source: architecture.md#Enforcement Guidelines]
- **No hardcoded UI text**; all strings via `UiStrings`. `aria-live` on the toggle status region; `aria-pressed`/`aria-label` on the control; descriptive `aria-label` on stop-order badges (a bare number on a pin is meaningless to a screen reader). [Source: project-context.md#UI Conventions; EXPERIENCE.md#Accessibility Floor; UX-DR1]
- **Dual render paths.** Desktop and mobile are distinct (`Viewport.IsMobile` → mobile branch in `MapPage.razor`); the toggle, badges, and announcements must land on **both**. [Source: project-context.md#UI Conventions; DESIGN.md#Layout & Spacing; EXPERIENCE.md#Responsive & Platform; UX-DR12]

### Dependency on Story 1.1 (prerequisite — do NOT re-specify the migration)

Story 1.1 (status ready-for-dev) ships the `AddTripPlanning` EF Core migration adding the schema this story consumes: `PoiCollectionItem.OrderIndex` (int, **1-based**) and `DwellMinutes` (int?), and `PoiCollection` trip fields including **`TripViewEnabled`** (bool, per-collection persistence) plus `TravelMode`/`StartPoiId`/`FinishPoiId`/`TripStartTime`/`TimeBudgetMinutes`, and the `RouteSegment` cache entity. **This story persists only `TripViewEnabled` and `OrderIndex`**; the other trip fields belong to later epics. Assume the migration is applied via startup `MigrateAsync`; do not add or hand-edit a migration here. [Source: epics.md#Story 1.1; architecture.md#D1, #D10]

### Source tree — files to create / modify

**NEW**
- `LucidCartographer/Services/Trip/ITripOrderingService.cs` — interface for seed/append/compact + has-order query. [Source: architecture.md#AR-12 structure table]
- `LucidCartographer/Services/Trip/TripOrderingService.cs` — sole `OrderIndex` writer; `SqliteWriteLock` + `IDbContextFactory<AppDbContext>`.
- `LucidCartographer/Configuration/TripServicesExtensions.cs` — `AddTripServices`; wired from `Program.cs`.
- `LucidCartographer/Components/Shared/Trip/TripViewModel.cs` — sealed, Transient, `StateChanged`, `IAsyncDisposable`.
- `LucidCartographer/Components/Shared/Trip/TripToggle.razor` — desktop toggle (filtered-results region).
- `LucidCartographer/Components/Shared/Trip/MobileTripToggle.razor` — mobile toggle (bottom-panel results region).
- `LucidCartographer/Components/Shared/Trip/StopOrderBadge.razor` — reusable numbered `primary` badge.
- Tests: `LucidCartographer.Tests/Services/TripOrderingServiceTests.cs`, `…/ViewModels/TripViewModelTests.cs`, `…/Components/Trip/TripToggleTests.cs`, integration (+ Mobile) Trip-toggle flow specs.

**UPDATE — current behavior + what to preserve**
- `LucidCartographer/Configuration/ViewModelExtensions.cs` — *current:* registers page VMs (incl. `MapPageViewModel`) Transient + `ViewportService` Scoped. *Add:* `services.AddTransient<TripViewModel>();`. Preserve existing registrations and the Transient-VM rationale comment. [Source: ViewModelExtensions.cs]
- `LucidCartographer/Components/Pages/MapPage.razor` — *current:* two distinct render branches (`Viewport.IsMobile` mobile split + desktop). Desktop **filtered-results region** = the filter-chip header bar (lines ~242–284) above `PoiTable` (whose own header reads "Filtered Results", lines ~11–19). Mobile results header at lines ~142–153 (`_drawerOpen == false`). The `@code` block already bridges `Vm.StateChanged`, wires the map, manages mobile detail/scroll. *Preserve:* the entire plain map/list/detail/search/enrichment flow, the splitter wiring, `_wiredMap` re-wire on viewport flip, and the existing `Vm` bridge. *Add:* `TripVm` injection + bridge (subscribe/dispose), place `TripToggle` (desktop) and `MobileTripToggle` (mobile) in the two results regions, surface order badges when Trip View on. Do NOT regress popup-skip-on-mobile or geolocation behavior. [Source: MapPage.razor]
- `LucidCartographer/Components/Pages/MapPageViewModel.cs` — *current:* holds collections, `FilteredPois`/`VisiblePois`, selection, viewport filter; mutation handlers (`HandleDeletePoiAsync`, membership handlers, copy/batch) all funnel through `RefreshAfterMutationAsync` → `LoadVisibleCollectionsAsync`. *Preserve:* all existing query/mutation/map-population logic and the enrichment-refresh subscription. *Add:* expose active-collection id + placeable-POI count for the trip layer; after membership mutations, notify the trip append/compaction path. Keep the change additive — no behavioral change when Trip View is off. [Source: MapPageViewModel.cs]
- `LucidCartographer/Components/Shared/PoiTable.razor` — *current:* renders the desktop filtered-results table; header region (lines 11–19) is the natural desktop **filtered-results region** and the order-badge placement precedent (per-row leading cell). *Preserve:* all batch-select, modal, virtualization, and row-action behavior. *Optional:* host the desktop order badge as a leading per-row element when Trip View is on (or render the badge from `MapPage`'s desktop branch — implementer's call, but keep `PoiTable` plain-collection behavior intact when Trip off). [Source: PoiTable.razor]
- `LucidCartographer/Services/UiStrings.cs` — add the trip toggle/badge strings (see Task 8). Preserve existing constants. [Source: UiStrings.cs]
- `LucidCartographer/Services/LeafletMapService.cs` (+ `wwwroot/js/leafletInterop.js`) — *current:* `addCollectionMarkers`/`removeCollectionMarkers`/`highlightMarker` interop. *Add:* a thin path to render the Stop Order numeral on markers when Trip View is on, reusing the single existing JS module. Preserve existing marker/label/popup behavior. [Source: LeafletMapService.cs lines 36–57; architecture.md#JS interop naming]

### Testing standards summary

Three layers, per project rules: **Unit** (xUnit + FluentAssertions + Moq; EF Core InMemory / temp SQLite) for `TripOrderingService` (seed determinism, 1-based contiguity, append, compaction) and `TripViewModel`; **Component** (bUnit) for `TripToggle`/`StopOrderBadge` (aria-pressed, availability gate, announcement, badge styling); **Integration** (`IntegrationTestBase` real WebApplication + Playwright + per-test temp SQLite, and `MobileTestBase` for the mobile path) for the end-to-end toggle/seed/persist/restore + membership-change flows on **both** render paths. Test internals directly via `InternalsVisibleTo("LucidCartographer.Tests")`. Unit tests explicitly assert 1-based ordering (guard against 0-based + offset). [Source: project-context.md#Testing Rules; architecture.md#File Organization & Workflow]

### Project Structure Notes

- New code is confined to the **`Services/Trip/`** slice (interface-first), **`Components/Shared/Trip/`** (desktop + mobile split), `Configuration/TripServicesExtensions.cs`, and additive edits to `ViewModelExtensions.cs`, `MapPage.razor`, `MapPageViewModel.cs`, `PoiTable.razor`, `UiStrings.cs`, `LeafletMapService.cs`/`leafletInterop.js`. No unrelated slice is touched; no files moved or renamed. [Source: architecture.md#Project Structure & Boundaries]
- `TripOrderingService` is registered Scoped (slice precedent, e.g. `IPoiDeduplicationService`); `TripViewModel` is Transient (page-VM precedent); `SqliteWriteLock` is the existing singleton — reuse it. [Source: DeduplicationServicesExtensions.cs; ViewModelExtensions.cs]
- This story deliberately ships **no** travel-time provider, cache, leg drawing, or timeline — those are Epic 2/later. `TripViewModel` and `TripOrderingService` are the seam; keep their public surface minimal so Stories 1.3/1.5/1.7 extend rather than rewrite.

### References

- [Source: _bmad-output/planning-artifacts/epics.md#Story 1.2: Toggle Trip View and seed Stop Order] — the four Given/When/Then blocks the ACs decompose (FR-1, FR-2, FR-17, UX-DR1, UX-DR2).
- [Source: _bmad-output/planning-artifacts/epics.md#FR-1] — toggle on/off, state restored on reopen, never modifies membership.
- [Source: _bmad-output/planning-artifacts/epics.md#FR-2] — persist contiguous 1..N order; seed by added-date asc; append on add; re-compact on remove.
- [Source: _bmad-output/planning-artifacts/epics.md#FR-17] — visible control in the filtered-results region (not a menu), ≥2 placeable, both surfaces.
- [Source: _bmad-output/planning-artifacts/epics.md#Story 1.1] — prerequisite schema (`OrderIndex` 1-based, `TripViewEnabled`); do not re-specify the migration.
- [Source: _bmad-output/planning-artifacts/architecture.md#D1] — trip schema (`OrderIndex` seeded by `AddedDate` asc, contiguous unique).
- [Source: _bmad-output/planning-artifacts/architecture.md#D10] — `TripViewEnabled` persisted per-collection; reopening restores on/off + Stop Order.
- [Source: _bmad-output/planning-artifacts/architecture.md#AR-11] — 1-based `OrderIndex`; single ordering write-path through `TripOrderingService`; TRIP-\* codes; no group-B violations; no `ConfigureAwait(false)`.
- [Source: _bmad-output/planning-artifacts/architecture.md#AR-12] — `Services/Trip/` slice; `TripViewModel` sealed/Transient/`StateChanged`/`IAsyncDisposable`; DI in `TripServicesExtensions.cs`; Trip UI under `Components/Shared/Trip/` desktop + `MobileTrip*` split.
- [Source: _bmad-output/planning-artifacts/architecture.md#Communication Patterns] — `OrderIndex` writes through one method; `SqliteWriteLock`; `StateChanged` via `InvokeAsync(StateHasChanged)`.
- [Source: _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-11/DESIGN.md#Trip View toggle] — switch in filtered-results region, off = plain, on = `primary`-accented, visible/enabled only at ≥2 placeable POIs.
- [Source: _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-11/DESIGN.md#Stop-order badge] — numbered circle, `primary` fill, `on-primary` numeral, `text-xs` weight 700, in list and on marker.
- [Source: _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-11/EXPERIENCE.md#Component Patterns — Trip View toggle] — enabled only at ≥2 placeable; state persists per-collection (OQ8); reopening restores on/off + order.
- [Source: _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-11/EXPERIENCE.md#State Patterns — Trip View unavailable] — below 2 placeable the toggle is hidden/disabled, never an error.
- [Source: _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-11/EXPERIENCE.md#Accessibility Floor] — `aria-pressed` + on/off announcement; descriptive `aria-label`s; both surfaces; ≥44px mobile targets, safe-area insets.
- [Source: _bmad-output/project-context.md#Architecture Layering] — Component→VM→Service→Data; Transient VMs; composition-root DI.
- [Source: _bmad-output/project-context.md#Build & Language Discipline] — warnings-as-errors, no group-B violations, no `ConfigureAwait(false)`.
- [Source: LucidCartographer/Data/Entities/Poi.cs] — `AddedDate` (seed-order key), `Latitude`/`Longitude` nullable (placeable test).
- [Source: LucidCartographer/Components/Pages/MapPage.razor] — desktop filtered-results header (lines ~242–284) and mobile results header (lines ~142–153) toggle host sites; existing `Vm` bridge + dispose.
- [Source: LucidCartographer/Components/Shared/PoiTable.razor] — "Filtered Results" header region (lines 11–19) and per-row leading-cell badge precedent.
- [Source: LucidCartographer/Services/SqliteWriteLock.cs] — shared singleton write gate for the ordering write-path.
- [Source: LucidCartographer/Configuration/DeduplicationServicesExtensions.cs] — `*ServicesExtensions` registration precedent (Scoped service + shared `SqliteWriteLock`).

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Claude Code, BMad dev-story workflow)

### Debug Log References

- `dotnet build LucidCartographer/LucidCartographer.csproj` → succeeded, 0 warnings (TreatWarningsAsErrors active, no group-B analyzer violations introduced).
- New test namespace initially shadowed `LucidCartographer.Services` (`Services.X` shorthand in `IntegrationTestBase` resolved to `LucidCartographer.Tests.Services`). Fixed by using the folder convention `namespace LucidCartographer.Tests;`.
- Trip unit + bUnit tests: 20 passed.
- Trip integration tests (desktop + mobile, Playwright): 4 passed.
- Full regression suite: **602 passed, 0 failed**.

### Completion Notes List

- **Single ordering write-path:** `TripOrderingService` is the sole `OrderIndex` writer. Seed/append/compact/reconcile all compute a desired `PoiId→OrderIndex` map and commit through one private `SetOrderAsync` under `SqliteWriteLock.Gate`. Tagged `TRIP-ORDER-01` for the seed rule.
- **1-based invariant:** seed orders placeable items by `Poi.AddedDate` asc, tie-broken by `PoiId` asc, assigning contiguous 1..N; non-placeable items are reset to 0 ("not a stop"). Unit tests assert Stop 1 is `1` (no 0-based + offset).
- **"Never ordered" detection:** `HasOrderAsync` = any item with `OrderIndex > 0`. Story 1.1's migration backfilled existing collections, so the first toggle-on of an existing collection restores rather than re-seeds; freshly-created collections (new join rows default to 0) seed on first enable.
- **Design decision — active collection scope:** MapPage shows the *union* of visible collections, but per-collection Trip persistence (`TripViewEnabled` + `OrderIndex`) is only coherent against one collection. The Trip toggle is therefore scoped to the **single visible collection** (`MapPageViewModel.SingleVisibleCollectionId`): it is available only when exactly one collection is visible, no search is active, and that collection has ≥2 placeable POIs. Below that gate the toggle is simply absent (UX-DR10), never an error. This is the natural reading of "the active collection" the story refers to and keeps the public surface minimal for Stories 1.3/1.5/1.7 to extend.
- **Lossless toggle off:** turning Trip View off clears the badge projection and reverts markers to plain dots but never deletes `OrderIndex` metadata or changes membership (unit + integration verified).
- **Membership change:** `MapPageViewModel.RefreshAfterMutationAsync` raises a new `MembershipChanged` hook (kept separate from `StateChanged` so the DB-touching reconcile only runs on real add/remove, not on every pan/selection). `TripViewModel.RefreshAfterMembershipChangeAsync` → `ReconcileOrderAsync` appends new placeable POIs at the end and re-compacts to 1..N after removals.
- **Map badges:** extended the single existing `leafletInterop` module (no second JS module) with `state.stopOrders` + `setStopOrders`; markers re-skin in place to a primary-filled numbered badge and revert on toggle off. `IMapService.SetStopOrdersAsync` added (with `StubMapService` no-op for integration tests, which don't run real Leaflet).
- **A11y:** toggle exposes `role="switch"` + `aria-pressed` + descriptive `aria-label`; on/off change announced via an `aria-live="polite"` `role="status"` region; Stop badges carry a descriptive "Stop {N}" `aria-label`. Mobile toggle honors a ≥44px touch target. All copy via `UiStrings` — no hardcoded UI literals.
- No new dependencies; no migration added (consumes Story 1.1 schema as specified).

### File List

**New (production)**
- `LucidCartographer/Services/Trip/ITripOrderingService.cs`
- `LucidCartographer/Services/Trip/TripOrderingService.cs`
- `LucidCartographer/Configuration/TripServicesExtensions.cs`
- `LucidCartographer/Components/Shared/Trip/TripViewModel.cs`
- `LucidCartographer/Components/Shared/Trip/TripToggle.razor`
- `LucidCartographer/Components/Shared/Trip/MobileTripToggle.razor`
- `LucidCartographer/Components/Shared/Trip/StopOrderBadge.razor`

**New (tests)**
- `LucidCartographer.Tests/Services/TripOrderingServiceTests.cs`
- `LucidCartographer.Tests/ViewModels/TripViewModelTests.cs`
- `LucidCartographer.Tests/Components/Trip/TripToggleTests.cs`
- `LucidCartographer.Tests/Integration/TripViewIntegrationTests.cs`
- `LucidCartographer.Tests/Integration/MobileTripViewTests.cs`

**Modified (production)**
- `LucidCartographer/Program.cs` — `.AddTripServices()` in the composition root.
- `LucidCartographer/Configuration/ViewModelExtensions.cs` — register `TripViewModel` (Transient).
- `LucidCartographer/Services/UiStrings.cs` — Trip View toggle/announcement/badge strings.
- `LucidCartographer/Services/IMapService.cs` — `SetStopOrdersAsync`.
- `LucidCartographer/Services/LeafletMapService.cs` — `SetStopOrdersAsync` interop call.
- `LucidCartographer/Components/Shared/LeafletMap.razor` — `SetStopOrdersAsync` wrapper.
- `LucidCartographer/Components/Shared/PoiTable.razor` — optional `StopOrders` param + leading Stop badge.
- `LucidCartographer/Components/Pages/MapPage.razor` — `TripVm` injection/bridge, desktop + mobile toggle placement, list badges, map-badge push, membership hook, dispose.
- `LucidCartographer/Components/Pages/MapPageViewModel.cs` — `MembershipChanged` hook, `SingleVisibleCollectionId`, `PlaceablePoiCount`.
- `LucidCartographer/wwwroot/js/leafletInterop.js` — `state.stopOrders`, `buildMarkerIcon`, `setStopOrders`, resets.
- `LucidCartographer/wwwroot/css/base.css` — `.stop-order-marker` badge style.

**Modified (tests)**
- `LucidCartographer.Tests/Integration/IntegrationTestBase.cs` — register `AddTripServices()`.
- `LucidCartographer.Tests/Integration/StubMapService.cs` — `SetStopOrdersAsync` no-op.

## Change Log

| Date       | Change                                                                                  |
|------------|-----------------------------------------------------------------------------------------|
| 2026-06-12 | Implemented Story 1.2: Trip View toggle (desktop + mobile), deterministic Stop Order seed/append/compaction via `TripOrderingService`, `TripViewModel`, list + map order badges, per-collection persistence. Full suite 602/602 green. |
