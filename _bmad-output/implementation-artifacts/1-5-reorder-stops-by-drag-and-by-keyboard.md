# Story 1.5: Reorder stops by drag and by keyboard

Status: ready-for-dev

## Story

As a trip planner,
I want to reorder stops by dragging or by keyboard controls,
So that I can arrange the loop the way I want, accessibly.

This story adds the two user-driven reorder paths — pointer drag and keyboard move-up/move-down — to the Trip View stop list that Story 1.3 renders. Both paths write the 1-based `OrderIndex` through the **same single `TripOrderingService` method** introduced in Story 1.2 (AR-11), and both respect a pinned Start (Order 1) / Finish (Order N) by moving only interior stops (designation itself is Story 1.7; this story only enforces the pins during reorder). After any reorder the dependent views (map legs, stop list, timeline placeholders) redraw incrementally without a full page reload. The keyboard path is the accessibility build-blocker (AR-9 / NFR4): move controls carry descriptive `aria-label`s, every move is announced via an `aria-live` region, and the path is implemented identically on desktop and `Mobile*Screen`.

## Acceptance Criteria

1. **Drag reorder renumbers, persists, and redraws (FR-3).** Given Trip View is on, when I drag a stop to a new position and drop it, then the affected range of stops is renumbered to a contiguous, gap-free 1..N `OrderIndex`, the new order is persisted, and the legs plus dependent views (stop list order badges, timeline placeholders, map polylines) update immediately without a full page reload.

2. **Keyboard move-up / move-down, announced and labelled (NFR4 / AR-9, UX-DR3).** Given keyboard-only operation, when I focus a stop row and activate its move-up or move-down control, then the stop moves exactly one position in `OrderIndex`, the change is announced through an `aria-live` region (e.g. "<name> moved to stop 3 of 7"), and each move control carries a descriptive `aria-label` (e.g. "Move <name> up", "Move <name> down"). The move controls are reachable by Tab and operable by Enter/Space — the keyboard path is NOT drag-only.

3. **Keyboard path identical on desktop and `Mobile*Screen` (NFR4 / AR-9, UX-DR12).** Given the same trip, when it is rendered on the desktop stop list (`TripStopList.razor`) and on the mobile path (`MobileTripPanel` / mobile `TripStopList` surface), then the move-up/move-down controls, their `aria-label`s, the `aria-live` announcement behavior, and the resulting `OrderIndex` writes are functionally identical on both surfaces. Mobile touch targets for the move controls are ≥ ~44px.

4. **Pinned Start / Finish stay at 1 / N during reorder (FR-3).** Given a pinned Start (Order 1) and/or a pinned Finish (Order N), when I reorder by drag or keyboard, then only interior stops (Order 2..N-1) move; a drag that drops a stop into the first or last slot does NOT transfer the Start/Finish role to it — the pinned stop remains at Order 1 / Order N (role changes happen only via Story 1.7). Move-up on the first interior stop and move-down on the last interior stop are no-ops (control disabled or guarded), as is any move of the pinned Start/Finish itself.

5. **Single OrderIndex writer; manual reorder overrides assisted ordering (AR-11, FR-3).** Given the four ordering paths (drag, keyboard, TSP-Sort, MCP), when this story's drag and keyboard paths write order, then they call the **same** `TripOrderingService` reorder/renumber method that Story 1.2 established — no path mutates `OrderIndex` rows directly. A manual reorder persists and thereby overrides any prior assisted (TSP/MCP) ordering for that trip.

6. **No-op reorder is harmless; result stays contiguous (FR-2 invariant).** Given a drag that drops a stop back into its own position, or a guarded keyboard no-op, when the operation completes, then `OrderIndex` remains a contiguous gap-free 1..N sequence with no duplicate values and no orphaned write, and the redraw is a no-op or cheap re-render (no full reload).

## Tasks / Subtasks

- [ ] **Extend the single `TripOrderingService` ordering method (AC: 1, 4, 5, 6)**
  - [ ] In `Services/Trip/TripOrderingService.cs` (from Story 1.2), add a public reorder method — e.g. `Task ReorderStopAsync(int collectionId, int poiId, int targetOrderIndex, CancellationToken ct)` (move a single stop to a target slot, covers both drag-to-position and one-step keyboard moves) — that delegates to the SAME private renumber/compaction routine Story 1.2 uses for seed/compaction. Do NOT fork a second renumbering implementation (AR-11). [Source: architecture.md#Communication Patterns; architecture.md#Frontend Architecture (D8)]
  - [ ] Enforce 1-based contiguous gap-free `OrderIndex` after the move; reject/clamp out-of-range targets. [Source: architecture.md#Format Patterns]
  - [ ] Pin enforcement: if `PoiCollection.StartPoiId` is set, the Start is fixed at Order 1; if `FinishPoiId` is set, the Finish is fixed at Order N. Compute the movable interior window (`[2 .. N-1]` when both pinned; `[2 .. N]` Start-only; `[1 .. N-1]` Finish-only; `[1 .. N]` neither) and clamp the target into it; never let a reorder relocate the Start/Finish or push an interior stop into a pinned slot. Reordering must NOT change `StartPoiId`/`FinishPoiId` (that is Story 1.7). [Source: epics.md#Story 1.5; architecture.md#Frontend Architecture (D5 pin precedent)]
  - [ ] Load/save through `IDbContextFactory<AppDbContext>`; serialize the `SaveChangesAsync` under `SqliteWriteLock.Gate` (`await Gate.WaitAsync(ct)` … `Gate.Release()` in `finally`) exactly like enrichment/dedup. No `ConfigureAwait(false)`. [Source: project-context.md#Critical Implementation Rules; Services/SqliteWriteLock.cs]
  - [ ] Detect the no-op case (target == current order, or a clamped no-op) and short-circuit before writing so no redundant `SaveChangesAsync` runs (protects the "no-op reorder" invariant and SM-C2 intent). [Source: epics.md#Story 1.5 AC; architecture.md#Process Patterns]
  - [ ] Tag the new decision with a `TRIP-*` comment code (e.g. `TRIP-ORDER-02`) referencing AR-11 single-writer. [Source: architecture.md#Enforcement Guidelines]

- [ ] **Surface reorder on `TripViewModel` (AC: 1, 2, 5)**
  - [ ] Add VM methods the rows bind to — e.g. `Task MoveStopUpAsync(int poiId)`, `Task MoveStopDownAsync(int poiId)`, and `Task MoveStopToAsync(int poiId, int targetOrderIndex)` for drag — each calling the `TripOrderingService` reorder method, then refreshing the VM's ordered-stop state and raising `StateChanged` (mirror the existing `Notify()` pattern). VM stays sealed/Transient with `private set` state. [Source: project-context.md#Architecture Layering; Components/Pages/MapPageViewModel.cs]
  - [ ] Expose a `LastReorderAnnouncement` (string, `private set`) the row/panel binds into the `aria-live` region; set it on every successful move using `UiStrings` formatted with the stop name + new position + total. [Source: architecture.md#Frontend Architecture (D8)]
  - [ ] After a successful reorder, trigger the existing dependent-view redraw used by Story 1.3 (leg re-render via `LeafletMapService`/`leafletInterop.js`) — incremental, not a full reload. Do NOT add travel-time recompute here (that is Epic 2); only redraw. [Source: epics.md#Story 1.5 AC; architecture.md#Frontend Architecture (D6)]

- [ ] **Keyboard move controls in `TripStopList.razor` (AC: 2, 3, 4)**
  - [ ] Add per-row move-up and move-down `<button>`s (real buttons, keyboard-focusable, Enter/Space activatable) bound to the VM move methods. Each carries an `aria-label` from `UiStrings` (formatted with the POI name). [Source: epics.md#Story 1.5; architecture.md#Frontend Architecture (D8)]
  - [ ] Disable (or guard) move-up on the topmost movable stop and move-down on the bottommost movable stop, and disable both on a pinned Start/Finish row, so AC-4 holds without throwing. Disabled buttons get `aria-disabled`/`disabled`. [Source: epics.md#Story 1.5 AC]
  - [ ] Add a single visually-hidden `aria-live="polite"` region per stop-list surface bound to `Vm.LastReorderAnnouncement`, following the existing announce pattern (see `EnrichmentStatus.razor` aria/status precedent). [Source: Components/Shared/EnrichmentStatus.razor; architecture.md#Frontend Architecture (D8)]
  - [ ] Keep the component a thin bridge: `@code` only subscribes `Vm.StateChanged`, calls VM methods, no ordering logic in markup. [Source: project-context.md#Architecture Layering]

- [ ] **Drag interop (AC: 1, 4, 6)**
  - [ ] Add a drag handle to each stop row (UX-DR3: drag handle · order badge · name · dwell field · timeline value · keyboard move up/down). Prefer native HTML5 drag-and-drop via Blazor (`draggable="true"`, `@ondragstart`/`@ondragover:preventDefault`/`@ondrop`) to avoid a new JS module; if a small JS helper is required for reliable drop-position calculation, add it to the existing `wwwroot/js` (extend, do not create a parallel module — `leafletInterop.js` precedent for "extend the existing one"). [Source: architecture.md#Gap Analysis (drag mechanism non-blocking); architecture.md#Naming Patterns (JS interop)]
  - [ ] On drop, compute the target `OrderIndex`, clamp into the movable interior window (reuse the same pin logic — do not duplicate it client-side; the service is authoritative), and call `Vm.MoveStopToAsync`. A drop onto the first/last (pinned) slot clamps to the nearest interior slot and never reassigns Start/Finish. [Source: epics.md#Story 1.5 AC]
  - [ ] Ensure drop on own position is a no-op (AC-6).

- [ ] **Mirror everything on the mobile path (AC: 3)**
  - [ ] Apply the same drag handle + move-up/down buttons + `aria-live` region to the mobile stop-list surface (`MobileTripPanel.razor` / mobile `TripStopList` per the `Mobile*` split). Touch targets ≥ ~44px. Both surfaces share the same `TripViewModel`, so behavior is identical by construction. [Source: architecture.md#Structure Patterns; project-context.md#UI Conventions (dual render path)]

- [ ] **UiStrings (AC: 2, 3)**
  - [ ] Add to `Services/UiStrings.cs`: `TripMoveStopUp` / `TripMoveStopDown` aria-label templates (e.g. `"Move {0} up"`, `"Move {0} down"`), `TripStopMovedAnnouncement` (e.g. `"{0} moved to stop {1} of {2}"`), and a drag-handle `aria-label` (e.g. `TripDragHandle = "Drag to reorder {0}"`). No hardcoded UI text in markup. [Source: project-context.md#UI Conventions; epics.md#NFR5]

- [ ] **Tests (AC: 1–6)**
  - [ ] **Unit** (`LucidCartographer.Tests/Services/TripOrderingServiceTests.cs`): renumber-on-move correctness; contiguity/gap-free/uniqueness invariant after move; move within interior window; **pinned-Start-only**, **pinned-Finish-only**, **both-pinned** cases (interior-only, pin stays at 1/N, drop-into-pinned-slot clamps); no-op short-circuits without writing; manual reorder overrides a prior order. Assert 1-based. [Source: architecture.md#Pattern Enforcement; epics.md#Story 1.5]
  - [ ] **Component / bUnit** (`LucidCartographer.Tests/Components/TripStopListTests.cs`): move buttons present with correct `aria-label`s; activating move-up/down invokes the VM and the move buttons are keyboard-operable; `aria-live` region receives the announcement text; move-up disabled on first movable row, move-down on last, both disabled on pinned rows. [Source: architecture.md#Implementation Readiness (bUnit layer)]
  - [ ] **Integration, both surfaces** (`LucidCartographer.Tests/Integration/…` desktop via `IntegrationTestBase`, mobile via `MobileTestBase`): drag a stop and assert persisted order + incremental redraw (no full reload); keyboard move on both surfaces produces identical `OrderIndex` and announcement; pinned Start/Finish unaffected. [Source: project-context.md#Testing Rules; architecture.md#File Organization]

## Dev Notes

### Patterns & constraints

- **AR-11 single OrderIndex writer (hard rule).** Drag and keyboard MUST write through the one `TripOrderingService` method established in Story 1.2 — the same path TSP-Sort (Story 3.1) and MCP (Story 3.2) will use. Four triggers, one write-path. Do NOT add a second renumbering routine; extend/reuse the private compaction Story 1.2 already wrote. [Source: architecture.md#Communication Patterns; architecture.md#Decision Impact Analysis]
- **AR-9 keyboard reorder is a build-blocker (NFR4).** The story is not "done" without: keyboard-focusable move-up/move-down controls, `aria-label` on each, an `aria-live` announcement on every move, and identical behavior on desktop AND `Mobile*Screen`. Drag is the *pointer* path only — it never substitutes for the keyboard path. [Source: architecture.md#Frontend Architecture (D8); epics.md#Story 1.5]
- **1-based, contiguous, gap-free `OrderIndex`.** Stored exactly as displayed; never 0-based with a +1 in the view. Start = 1, Finish = N. [Source: architecture.md#Format Patterns]
- **`SqliteWriteLock` on every write.** Wrap `SaveChangesAsync` in `Gate.WaitAsync(ct)` / `Gate.Release()` in `finally`, matching `PoiEnrichmentBackgroundService` / `PoiDeduplicationService`. [Source: Services/SqliteWriteLock.cs]
- **Build discipline.** `TreatWarningsAsErrors=true`, `Nullable=enable`. No group-B analyzer violations (MA0002, MA0015, MA0046, MA0047, MA0074, VSTHRD200). No `ConfigureAwait(false)`. New decisions carry `TRIP-*` comment codes. [Source: project-context.md#Build & Language Discipline]
- **No travel-time recompute here.** Epic 2 owns recompute/cache; this story only triggers the existing Story 1.3 dependent-view redraw (legs/badges/timeline placeholders). A reorder that introduces no new pair would not recompute anyway — but that logic is Epic 2's concern, not this story's. [Source: epics.md#Story 1.5 scope; epics.md#Story 2.4]
- **Aggregate-honesty / Fidelity, Start/Finish designation, Unplaceable handling are out of scope** (Stories 1.6, 1.7, Epic 2). This story only moves placeable stops within the order; designation and unplaceable flagging are separate stories.

### Dependency on 1.2 + 1.3

- **Story 1.2** delivered `TripOrderingService` (seed + compaction, the single OrderIndex writer) and `TripViewModel` (sealed, Transient, `StateChanged`). This story adds a public reorder method to that service and move methods to that VM. **Reuse, do not fork.**
- **Story 1.3** delivered `TripStopList.razor` rows (order badge, POI name, dwell-field placeholder, timeline-value placeholder), the desktop-beside-map / mobile-bottom-panel split, and incremental leg redraw on order change. This story adds the drag handle + move controls to those existing rows and reuses 1.3's redraw path.

### Source tree — NEW / UPDATE (real paths under `C:\backup\maps_editor\LucidCartographer\`)

- **UPDATE** `Services/Trip/TripOrderingService.cs` — add public reorder/renumber method delegating to the existing private compaction; pin-aware interior clamp; `SqliteWriteLock`. (and its interface `Services/Trip/ITripOrderingService.cs`)
- **UPDATE** `Components/Shared/Trip/TripViewModel.cs` — `MoveStopUpAsync` / `MoveStopDownAsync` / `MoveStopToAsync`, `LastReorderAnnouncement`, redraw trigger, `StateChanged`.
- **UPDATE** `Components/Shared/Trip/TripStopList.razor` — drag handle, move-up/move-down buttons (`aria-label`), visually-hidden `aria-live` region, disabled-edge/pinned guards.
- **UPDATE** mobile stop-list surface (`Components/Shared/Trip/MobileTripPanel.razor` and/or the mobile `TripStopList` rendering) — same controls, ≥44px touch targets.
- **UPDATE** `Services/UiStrings.cs` — move-control aria-labels, announcement template, drag-handle label.
- **UPDATE (only if needed)** a file under `wwwroot/js` — small drag-position helper, extending an existing module rather than adding a parallel one. Prefer pure Blazor HTML5 DnD with no JS.
- **NEW** tests: `LucidCartographer.Tests/Services/TripOrderingServiceTests.cs` (or extend if 1.2 created it), `LucidCartographer.Tests/Components/TripStopListTests.cs`, integration desktop + mobile reorder tests.

> Note: the `Services/Trip/` and `Components/Shared/Trip/` files are introduced by Stories 1.2/1.3 and are not yet on disk at authoring time. Confirm the exact method/property names those stories landed and bind to them; the names above follow the architecture's naming patterns.

### UPDATE — current behavior to preserve

- **`TripStopList.razor` (1.3):** row layout is drag handle · order badge · POI name · dwell field · timeline value · move up/down (UX-DR3). Preserve the existing badge/name/dwell/timeline cells and the `@key`/`Virtualize` row strategy used elsewhere (see `PoiTable.razor`, `ItemSize="44"`); only ADD the drag handle and move controls and the `aria-live` region.
- **`TripOrderingService` (1.2):** the seed/compaction routine is the single OrderIndex writer and already enforces 1-based contiguity. Preserve it; the reorder method must funnel through the same renumber so all four ordering paths stay consistent.
- **Leg redraw (1.3):** order changes already redraw legs incrementally via `LeafletMapService` → `leafletInterop.js`. Preserve and reuse — do not add a full-page reload.
- **`aria-live` precedent:** `Components/Shared/EnrichmentStatus.razor` shows the status-region + `InvokeAsync(StateHasChanged)` pattern; follow it for the announcement region (use `aria-live="polite"`, visually hidden).
- **`TripViewModel` (1.2):** sealed, Transient, `StateChanged` + `Notify()`, `private set` state; preserve and extend without changing its lifetime or contract.

### Testing summary

Three layers per project convention. **Unit** carries the renumber/pin logic (including the three pinned permutations and the no-op short-circuit, asserting 1-based contiguity) — this is the load-bearing coverage. **bUnit** verifies the a11y surface (aria-labels, aria-live text, keyboard activation, disabled edges). **Integration** proves drag + keyboard on BOTH desktop (`IntegrationTestBase`) and mobile (`MobileTestBase`) with persistence and incremental (non-reload) redraw. `InternalsVisibleTo("LucidCartographer.Tests")` lets tests hit service internals directly.

### Project Structure Notes

- New logic stays inside the `Services/Trip/` and `Components/Shared/Trip/` slices; no unrelated slice is touched. Layering Component → ViewModel → Service → Data is preserved: the `.razor` rows only bind to `TripViewModel`, which calls `TripOrderingService`, which uses `IDbContextFactory<AppDbContext>` + `SqliteWriteLock`. DI for the Trip slice already lives in `Configuration/TripServicesExtensions.cs` and the VM in `Configuration/ViewModelExtensions.cs` (from 1.2) — no new registrations expected unless a JS-helper service is introduced.
- Dual render path is mandatory: desktop `TripStopList` and the mobile panel both get the controls; they share one VM so they cannot diverge.

### References

- [Source: epics.md#Story 1.5: Reorder stops by drag and by keyboard] — ACs, FR-3, NFR4/AR-9, UX-DR3
- [Source: epics.md#FR-3] — drag reorder, renumber, persist, immediate update; manual overrides TSP; pinned Start/Finish keep slots, interior-only
- [Source: epics.md#NFR4] — keyboard-accessible reorder, aria-labels, aria-live, both surfaces, ≥44px touch targets
- [Source: epics.md#AR-9 (D8 keyboard reorder — a11y build-blocker)] — move-up/down controls, aria-labelled, aria-live, identical desktop + Mobile*Screen; drag is the pointer path
- [Source: epics.md#AR-11 (Pattern enforcement)] — 1-based OrderIndex; all four ordering paths write the same OrderIndex through one TripOrderingService method; TRIP-* comment codes; no group-B violations; no ConfigureAwait(false)
- [Source: architecture.md#Frontend Architecture (D8)] — keyboard-accessible stop reorder build-blocker
- [Source: architecture.md#Communication Patterns] — four ordering paths, one TripOrderingService method; SqliteWriteLock single-writer; StateChanged → InvokeAsync(StateHasChanged)
- [Source: architecture.md#Format Patterns] — 1-based contiguous gap-free OrderIndex, Start=1/Finish=N
- [Source: architecture.md#Process Patterns] — no-op reorder triggers no recompute
- [Source: architecture.md#Gap Analysis Results] — drag-reorder interop mechanism left to story time, keyboard path is the a11y-safe baseline
- [Source: architecture.md#Structure Patterns] — Trip UI under Components/Shared/Trip/ with desktop + MobileTrip* split
- [Source: ux-designs/ux-maps_editor-2026-06-11/DESIGN.md + EXPERIENCE.md#UX-DR3] — stop list row: drag handle · order badge · name · dwell field · timeline value · keyboard move up/down; reorderable, trip-scoped echo of the POI table row
- [Source: project-context.md#Critical Implementation Rules] — warnings-as-errors, Nullable, group-B analyzers, no ConfigureAwait(false), layering, UiStrings, dual render path
- [Source: Services/SqliteWriteLock.cs] — Gate.WaitAsync/Release write-serialization pattern
- [Source: Components/Shared/EnrichmentStatus.razor] — aria/status region + InvokeAsync(StateHasChanged) precedent
- [Source: Components/Shared/PoiTable.razor] — Virtualize/@key row precedent (ItemSize="44") the trip stop row echoes

## Dev Agent Record

### Agent Model Used

### Debug Log References

### Completion Notes List

### File List
