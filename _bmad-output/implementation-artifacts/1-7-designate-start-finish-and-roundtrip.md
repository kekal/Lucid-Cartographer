---
baseline_commit: 7a1fe590269371b7a6fbe9328fcf5e6e382a75e2
---

# Story 1.7: Designate Start, Finish and roundtrip

Status: review

## Story

As a trip planner,
I want to set where my loop starts and (optionally) ends,
So that the trip is an honest roundtrip or a deliberate open path.

This story is the Start/Finish designation layer of Epic 1. It is an **additive lens** over an existing POI Collection — designating a stop as **Start** pins it to Stop Order 1, optionally designating a distinct stop as **Finish** pins it to Stop Order N, and the absence of a Finish makes the trip a **Roundtrip** (a closing leg returns from Order N to the Start), which is the default shape. It consumes — and does not re-implement — the schema (1.1), Stop Order + `TripOrderingService` + `TripViewModel` (1.2), leg rendering incl. the closing leg for roundtrip vs. N−1 for an open path (1.3), and reorder pin-respect (1.5). Its job is to (a) let the user set/unset Start and Finish from both surfaces, (b) write `StartPoiId`/`FinishPoiId` and the pinned `OrderIndex` through the single `TripOrderingService` write path, (c) render distinct Start/Finish glyphs on the badge and on the map marker, and (d) toggle closing-leg presence by roundtrip vs. open path — all while guaranteeing the 1..N `OrderIndex` set stays contiguous, unique, and that **no stop ever holds two Stop Order values**.

## Acceptance Criteria

1. **Designate Start pins to Order 1, distinct glyph, anchors the loop.** Given Trip View is on, when I designate a stop as Start, then it is pinned to Stop Order 1, shown with a distinct Start glyph/ring (on its stop-list badge **and** its map marker, per UX-DR2), and the map loop and stop list anchor on it. (FR-14, UX-DR2, UX-DR14)
2. **Start set + Finish unset ⇒ Roundtrip, the default shape.** Given a Start is set and Finish is left unset, when the loop renders, then the Trip is a Roundtrip — the closing leg returns from Order N to the Start (N legs total, consuming 1.3's closing-leg rendering) — and this is the default shape (a trip with no Finish is a roundtrip). (FR-14, UX-DR14)
3. **Designate a distinct Finish pins to Order N, open path, no closing leg, no duplicate order.** Given I set a distinct stop (≠ the Start) as Finish, when the loop renders, then that stop is pinned to Stop Order N with a distinct Finish glyph (badge + marker), the Trip becomes an open path ending there (no closing leg — N−1 legs, consuming 1.3's open-path rendering), and **no stop ever holds two Stop Order values** (the 1..N `OrderIndex` set remains contiguous, gap-free, and unique). (FR-14, UX-DR2, UX-DR14)
4. **Set/unset controls on both surfaces, accessible.** Given Trip View is on, when I view a stop row on desktop **or** mobile (`Mobile*Screen` path), then Start and Finish set/unset controls are present on both render paths, each carrying a descriptive `aria-label` and announcing the resulting state via the existing `aria-live` region; all control copy routes through `UiStrings`. (UX-DR12, NFR4/NFR5, derived from FR-14 dual-surface requirement)
5. **Pin invariants enforced through the single ordering write path.** Given any Start/Finish change, when it is written, then it goes through the single `TripOrderingService` method that writes `OrderIndex` (AR-11) — Start → `OrderIndex` 1, Finish → `OrderIndex` N — and on completion the placeable stops still form a contiguous, unique 1..N sequence with the pinned endpoints in their slots and every interior stop renumbered as needed. (FR-14, AR-11, architecture pinning rule)
6. **Re-designation and unset are honest (no orphaned pins, no duplicates).** Given a Start (or Finish) already exists, when I designate a different stop as Start (or Finish) or clear it, then the prior pin is released and re-validated so the order stays contiguous/unique; setting a stop as Finish that is currently the Start is rejected (a stop cannot be both); clearing the Finish returns the trip to a Roundtrip with the closing leg restored. (FR-14, UX-DR14, no-stop-two-orders invariant)

## Tasks / Subtasks

- [x] **Extend `TripViewModel` with Start/Finish intent and derived shape** (AC: 1, 2, 3, 6)
  - [x] Add `SetStartAsync(int poiId)`, `ClearStartAsync()`, `SetFinishAsync(int poiId)`, `ClearFinishAsync()` to the sealed Transient `TripViewModel` (from 1.2), each calling the single `TripOrderingService` pin method (do not write `OrderIndex` from the ViewModel directly).
  - [x] Expose derived read state for the UI: `StartPoiId`, `FinishPoiId`, `IsRoundtrip` (`FinishPoiId is null`), and a per-stop role accessor (`StopRole(poiId) => None | Start | Finish`) used by both surfaces to pick the badge/marker glyph.
  - [x] After any pin change, raise `StateChanged` (via the existing `Notify()`); trigger the same incremental redraw + closing-leg-presence recompute path 1.3 uses (no full reload). Honor the existing `CancellationTokenSource`/`IAsyncDisposable` pattern; no `ConfigureAwait(false)`.
  - [x] Tag new ViewModel logic with a `TRIP-*` comment code (e.g. `TRIP-STARTFINISH-01`).
- [x] **Implement pin/unpin logic in `TripOrderingService`** (AC: 1, 3, 5, 6)
  - [x] Add the canonical pin method(s) that set/clear `PoiCollection.StartPoiId` / `FinishPoiId` (schema from 1.1) **and** rewrite the placeable stops' 1-based `OrderIndex` so Start → 1 and Finish → N, interior stops compacted to fill 2..N−1 in their existing relative order (reuse 1.2's contiguous-1..N seed/compaction helper — do **not** re-implement seed or reorder).
  - [x] Guarantee invariants in one place: after the write, assert/produce a contiguous, gap-free, unique 1..N `OrderIndex` set over placeable stops with no stop holding two values; reject setting Finish == current Start (and vice versa) — a stop cannot be both Start and Finish.
  - [x] Re-designation path: when a new Start/Finish replaces an existing one, release the old pin first, then re-validate 1..N (the old endpoint becomes an interior stop). Unset path: clearing Finish restores Roundtrip (closing leg present); clearing Start leaves order contiguous with no pinned first.
  - [x] Respect existing write discipline: `IDbContextFactory<AppDbContext>`, `SqliteWriteLock` single-writer, `Version` optimistic concurrency on `PoiCollection`; this is the same method all four ordering paths funnel through (AR-11). Tag with `TRIP-*`.
- [x] **Closing-leg presence by roundtrip vs. open path** (AC: 2, 3, 6)
  - [x] Drive 1.3's leg rendering from the derived `IsRoundtrip` state: `FinishPoiId is null` ⇒ closing leg from Order N back to Start (N legs); distinct Finish ⇒ no closing leg (N−1 legs). Do not re-implement leg drawing — pass the roundtrip flag into the existing `drawTripLegs` interop call path (`LeafletMapService` → `leafletInterop.js`).
  - [x] Ensure the closing leg uses the same dashed+muted Phase-1 styling as all other legs (only Measured is solid — out of scope here, but don't regress 1.3's styling).
- [x] **Distinct Start/Finish glyphs on the stop-list badge (`TripStopList.razor` + mobile)** (AC: 1, 3, 4)
  - [x] In `Components/Shared/Trip/TripStopList.razor`, render the Start stop's order badge with a distinct Start glyph/ring and the Finish stop's badge with a distinct Finish glyph (UX-DR2: `primary` fill, `on-primary` numeral, `text-xs` weight 700, distinct ring/glyph for Start, distinct glyph for Finish). Use Material Symbols Outlined per DESIGN.md icon spec; keep the numeral readable.
  - [x] Add per-row **Set as Start / Set as Finish** controls (and **Unset** when already pinned), each `aria-label`led via `UiStrings`, ≥44px touch target. Disable "Set as Finish" on the current Start row and vice versa (AC-6 rejection surfaced as a disabled control, not an error).
  - [x] Mirror all of the above on the mobile render path (the mobile Trip stop list / bottom-panel variant from 1.3) — both surfaces are distinct render paths and must both carry the controls and glyphs (UX-DR12).
- [x] **Distinct Start/Finish marker glyphs on the map (`LeafletMap.razor` / interop)** (AC: 1, 3)
  - [x] Give the Start and Finish map markers distinct glyphs/rings (UX-DR2) so the loop visibly anchors on the Start and terminates on the Finish, distinct from numbered interior stops. Route through the existing `IMapService`/`LeafletMapService` → `leafletInterop.js` marker-rendering path that 1.3 added; `LeafletMap.razor` delegates rendering to the service (it holds no marker markup itself), so extend the service/interop call with Start/Finish role info, not the component.
  - [x] Marker `aria-label`/title reflects Start/Finish role (a glyph alone is meaningless to a screen reader) — copy via `UiStrings`.
- [x] **`UiStrings` additions** (AC: 4)
  - [x] Add a Trip View string block: e.g. `TripSetAsStart`, `TripSetAsFinish`, `TripUnsetStart`, `TripUnsetFinish`, `TripStartBadgeAria` ("Start — stop 1 of {0}"), `TripFinishBadgeAria` ("Finish — stop {0} of {0}"), `TripStartMarkerAria`, `TripFinishMarkerAria`, `TripRoundtripAnnounce` ("Roundtrip — returns to start"), `TripOpenPathAnnounce` ("Open path — ends at {0}"). No hardcoded UI text anywhere in this story.
- [x] **Unit tests — pin invariants** (AC: 1, 2, 3, 5, 6)
  - [x] `TripOrderingService` (or `TripViewModel`) unit tests asserting: Start → `OrderIndex` 1; Finish → `OrderIndex` N; after set, the placeable `OrderIndex` set is exactly `{1..N}` contiguous & unique (no stop holds two values); re-designating Start moves the old Start to an interior slot without a gap/duplicate; setting Finish == Start is rejected; clearing Finish flips `IsRoundtrip` back to true; the method funnels through the single ordering write path (AR-11) under `SqliteWriteLock`.
  - [x] Property-style check over a few stop counts (N = 2, 3, 10) that no permutation of Start/Finish operations produces a duplicate or gap in `OrderIndex`.
- [x] **Component tests (bUnit) — glyphs & controls** (AC: 1, 3, 4)
  - [x] `TripStopList` bUnit tests: the Start row badge renders the Start glyph/ring, the Finish row badge renders the Finish glyph; Set/Unset Start and Finish controls render with `aria-label`s from `UiStrings`; "Set as Finish" is disabled on the current Start row (and vice versa); clicking a control invokes the corresponding `TripViewModel` method.
- [x] **Integration tests — both surfaces** (AC: 1, 2, 3, 4)
  - [x] Desktop (`IntegrationTestBase`) and mobile (`MobileTestBase`): with Trip View on, designate a Start → it becomes stop 1 with the Start glyph and the loop anchors on it; leave Finish unset → roundtrip closing leg is drawn (N legs); set a distinct Finish → open path, no closing leg (N−1 legs), Finish is stop N with the Finish glyph; verify the controls exist and are operable on the mobile render path; verify the `aria-live` announcement fires on roundtrip↔open-path transitions.

## Dev Notes

### Patterns & constraints (must follow)
- **Layering (strict):** Component (`.razor`) → `TripViewModel` → `TripOrderingService` → Data. The component `@code` block stays a ~12-line bridge (subscribe `Vm.StateChanged += OnVmChanged` in `OnInitializedAsync`, `OnVmChanged() => InvokeAsync(StateHasChanged)`, unsubscribe + dispose in `DisposeAsync`). No Start/Finish logic in markup; the ViewModel calls the service; the service is the sole `OrderIndex` writer. [Source: project-context.md#architecture-layering-strict; architecture.md#service-boundaries]
- **Single ordering write path (AR-11):** all four ordering paths (drag, keyboard, TSP, MCP) and now Start/Finish pinning write the same 1-based `OrderIndex` through one `TripOrderingService` method — never mutate order rows directly. Cache writes / order writes go through `SqliteWriteLock`. [Source: architecture.md#communication-patterns; epics.md#AR-11]
- **1-based, contiguous, unique `OrderIndex`:** Start = 1, Finish = N; stored exactly as displayed (no 0-based + offset). The no-stop-two-orders invariant is the heart of AC-3/5/6. [Source: architecture.md#format-patterns]
- **Roundtrip = `FinishPoiId is null`:** null Finish ⇒ closing leg returns to Start (N legs); distinct Finish ⇒ open path (N−1 legs). This is the only state that toggles closing-leg presence. [Source: architecture.md#D1; epics.md#FR-14; DESIGN.md (closing roundtrip leg uses the same dashed+muted language)]
- **Build discipline:** `TreatWarningsAsErrors=true`, `Nullable=enable`; no group-B analyzer violations (MA0002, MA0015, MA0046, MA0047, MA0074, VSTHRD200); **no `ConfigureAwait(false)`** (Blazor circuit sync context). Tag new trip decisions with a searchable `TRIP-*` comment code. [Source: project-context.md#build--language-discipline; architecture.md#enforcement-guidelines]
- **UI conventions:** all copy via `UiStrings`; `aria-label` on every control; `aria-pressed`/`aria-live` for state; mobile touch targets ≥44px, safe-area insets honored; desktop and `Mobile*Screen` are **distinct render paths** — implement Start/Finish on **both**. [Source: project-context.md#ui-conventions; EXPERIENCE.md#accessibility-floor; DESIGN.md#layout--spacing]
- **Glyph spec (UX-DR2):** stop-order badge = small numbered circle, `primary` fill, `on-primary` numeral, `text-xs` weight 700; **Start uses a distinct glyph/ring; Finish likewise** — on both the list badge and the map marker. Use Material Symbols Outlined (`FILL 0, wght 400, GRAD 0, opsz 24`). [Source: DESIGN.md#components (Stop-order badge); epics.md#UX-DR2]

### Dependencies (consume, do not re-implement)
- **Story 1.1 (schema):** `PoiCollection.StartPoiId` / `FinishPoiId` (nullable FKs) and `PoiCollectionItem.OrderIndex` already exist via the `AddTripPlanning` migration. This story only *writes* those fields — no schema change, no new migration. [Source: epics.md#Story-1.1; architecture.md#D1]
- **Story 1.2 (Stop Order + service + VM):** `TripOrderingService`/`ITripOrderingService`, the contiguous 1..N seed/compaction logic, and the sealed Transient `TripViewModel` (with `StateChanged`) already exist. Extend them; reuse the compaction helper for renumbering after a pin. [Source: epics.md#Story-1.2; architecture.md#frontend-architecture]
- **Story 1.3 (leg rendering):** ordered straight legs incl. the closing leg for roundtrip / N−1 for open path, the stop-list panel (desktop beside map, mobile bottom panel), and the `drawTripLegs` interop path already exist. This story flips the roundtrip flag into that path; it does not draw legs. [Source: epics.md#Story-1.3]
- **Story 1.5 (reorder pin-respect):** drag/keyboard reorder already keeps pinned Start (Order 1) / Finish (Order N) in their slots and moves interior stops only; **role changes happen only via this story (1.7)** — 1.5 explicitly defers role transfer here. Do not touch reorder logic. [Source: epics.md#Story-1.5 (AC "role changes only via Story 1.7")]

### Source tree — NEW / UPDATE
- **UPDATE** `LucidCartographer/Components/Shared/Trip/TripStopList.razor` (+ its mobile variant) — add Set/Unset Start & Finish controls and distinct Start/Finish badge glyphs. **Current behavior to preserve:** order badge, POI name, dwell-field placeholder, timeline-value placeholder, drag handle, keyboard move up/down (1.3/1.5); do not regress the row layout or the pin-respecting reorder. *(File is created by 1.3/1.5; if not yet present at dev time, this story creates the Start/Finish affordances within it following the same component pattern.)*
- **UPDATE** `LucidCartographer/Services/Trip/TripOrderingService.cs` — add the pin/unpin method re-validating contiguous unique 1..N. **Current behavior to preserve:** the existing single `OrderIndex` write method, `SqliteWriteLock` usage, seed/compaction from 1.2, and pin-respecting reorder from 1.5. New pin logic must call/share the same renumbering helper, not fork it. [Source: architecture.md#D5 / pinning rule lines for Start/Finish]
- **UPDATE** `LucidCartographer/Components/Shared/LeafletMap.razor` + `Services/LeafletMapService.cs` + `wwwroot/js/leafletInterop.js` — distinct Start/Finish marker glyphs/rings and closing-leg-presence by roundtrip vs open path. **Current behavior to preserve:** `LeafletMap.razor` delegates all marker/leg rendering to `IMapService` (it holds no marker markup; markers/legs are drawn in the service + interop). Marker-click interop, popups/tooltips, bounds tracking, and `HighlightMarkerAsync` must not regress. Extend the service/interop signature with Start/Finish role + roundtrip flag; do not move rendering into the component. [Source: LeafletMap.razor (delegates to MapService); architecture.md#JS-interop-naming (`drawTripLegs`/`highlightStop`)]
- **UPDATE** `LucidCartographer/Components/Shared/Trip/TripViewModel.cs` — Start/Finish set/clear methods, derived `IsRoundtrip` + `StopRole`, `StateChanged` on change. **Preserve:** existing `StateChanged`/`Notify()`, `CancellationTokenSource`, `IAsyncDisposable`, Transient registration in `Configuration/ViewModelExtensions.cs`.
- **UPDATE** `LucidCartographer/Services/UiStrings.cs` — add the Trip View Start/Finish string block (see task). **Preserve:** existing string constants and ordering conventions (grouped by page/feature with comment headers).

### Project Structure Notes
- Trip UI lives under `Components/Shared/Trip/` with a desktop + `MobileTrip*` split (the dual-render-path rule); the map page (`Components/Pages/MapPage.razor`) composes `TripViewModel` and hosts the toggle — no Start/Finish logic belongs on the page. [Source: architecture.md#structure-patterns; epics.md#AR-12]
- `LeafletMap.razor` is a thin wrapper over `IMapService`; all Leaflet calls go through `LeafletMapService` → `leafletInterop.js`. Components never invoke JS directly. Extend the **existing** `leafletInterop.js` (no second JS module). [Source: LeafletMap.razor; architecture.md#component-boundaries / JS-interop-naming]
- No new HTTP endpoint, no new auth surface, no new migration, no new DI registration beyond what 1.2 already added (`TripViewModel` is already Transient). [Source: architecture.md#architectural-boundaries]
- Note: at authoring time the dependency-story code (1.1–1.5) is specced but may not yet be on disk; this story is written to extend those files as they will exist. If a referenced file is absent when development starts, create it consistent with the cited dependency story and these patterns — do not invent a parallel structure.

### Testing summary
- **Unit** (xUnit + FluentAssertions + Moq, `InternalsVisibleTo("LucidCartographer.Tests")`): pin invariants on `TripOrderingService`/`TripViewModel` — Start→1, Finish→N, contiguous-unique 1..N, no double order, re-designation, Finish==Start rejection, roundtrip flip, single-write-path/`SqliteWriteLock`. [Source: project-context.md#testing-rules]
- **Component** (bUnit): `TripStopList` Start/Finish glyphs + Set/Unset controls + `aria-label`s + disabled cross-pinning + control→VM wiring.
- **Integration** (`IntegrationTestBase` + `MobileTestBase`, real `WebApplication` + Playwright + temp SQLite): designate Start/Finish on **both** desktop and mobile; roundtrip closing leg present when Finish unset (N legs) and absent when Finish set (N−1 legs); marker glyphs distinct; `aria-live` announcement on shape change. Cover both render paths (mandatory for responsive UI). [Source: project-context.md#testing-rules; EXPERIENCE.md#responsive--platform]

### References
- [Source: _bmad-output/planning-artifacts/epics.md#Story-1.7-Designate-Start-Finish-and-roundtrip] — FR-14, UX-DR2, UX-DR14; the three Given/When/Then that became ACs 1–3.
- [Source: _bmad-output/planning-artifacts/epics.md#FR-14] — set any Stop as Start (Order 1), optionally any other as Finish (Order N); Finish unset ⇒ Roundtrip; distinct Finish ⇒ open path; no Stop ever holds two Stop Order values.
- [Source: _bmad-output/planning-artifacts/epics.md#AR-11] — single `TripOrderingService` write path; 1-based `OrderIndex`; `TRIP-*` codes; no group-B violations; no `ConfigureAwait(false)`.
- [Source: _bmad-output/planning-artifacts/epics.md#Story-1.5] — reorder keeps pinned Start/Finish; role changes only via Story 1.7.
- [Source: _bmad-output/planning-artifacts/architecture.md#D1] — `StartPoiId` (nullable FK), `FinishPoiId` (nullable FK; null ⇒ roundtrip) on `PoiCollection`.
- [Source: _bmad-output/planning-artifacts/architecture.md#D5 / Frontend-Architecture] — pin Start (order 1) and Finish (order N), interior edges only, close the loop for a roundtrip.
- [Source: _bmad-output/planning-artifacts/architecture.md#Format-Patterns] — 1-based `OrderIndex`, Start = 1, Finish = N, contiguous gap-free unique.
- [Source: _bmad-output/planning-artifacts/architecture.md#Requirements-to-Structure-Mapping] — "FR-14 Start/Finish/Roundtrip → `StartPoiId`/`FinishPoiId`, `TripOrderingService` pinning."
- [Source: _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-11/DESIGN.md#Components] — Stop-order badge: Start distinct glyph/ring, Finish likewise (list + marker); closing roundtrip leg uses the same dashed+muted language.
- [Source: _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-11/EXPERIENCE.md#Component-Patterns] — Start/Finish controls: designate Start (pinned order 1), optionally Finish (pinned order N); Roundtrip is the default.
- [Source: _bmad-output/project-context.md#Critical-Implementation-Rules] — layering, build discipline, UI conventions, dual render path, testing layers.
- [Source: LucidCartographer/Components/Shared/LeafletMap.razor] — current map component delegates all marker/leg rendering to `IMapService`; extend the service/interop, not the component.
- [Source: LucidCartographer/Data/Entities/PoiCollection.cs] — current entity (pre-1.1 on disk); `StartPoiId`/`FinishPoiId` added by Story 1.1.
- [Source: LucidCartographer/Services/UiStrings.cs] — string-constants convention; add a grouped Trip View block.

## Dev Agent Record

### Agent Model Used

claude-fable-5

### Debug Log References

- `dotnet build` — 0 warnings / 0 errors (TreatWarningsAsErrors on).
- Trip-scoped suites green: TripOrderingServiceTests + TripViewModelTests + TripStopListTests (94), TripViewIntegrationTests + MobileTripViewTests + TripStopListTests (46).
- Full `dotnet test` run executed from the worktree root (see Completion Notes for flake handling).
- One analyzer iteration: MA0069 on the StubMapService trip-overlay recording fields → converted to static properties with private setters.
- One layout iteration: the first cut placed four inline 44px controls per row, which regressed three pre-existing 1.4 selection/visibility integration tests (row click-centre landed on a stopPropagation button; the desktop w-64 panel starved the POI name's flex space). Fixed by stacking move and Set/Unset controls into two narrow columns; desktop Set/Unset keeps a ≥44px hit target via padding + negative margin around a 24px visual.

### Completion Notes List

- TRIP-STARTFINISH-01 (TripViewModel): SetStartAsync/ClearStartAsync/SetFinishAsync/ClearFinishAsync funnel through one private ChangePinAsync; derived `StartPoiId`/`FinishPoiId`/`IsRoundtrip` (FinishPoiId is null)/`StopRole(poiId)`/`CanSetStart`/`CanSetFinish`; `StartFinishAnnouncement` feeds a new aria-live region on both surfaces; every change re-reads the projections (BuildLegs recomputes closing-leg presence) and raises StateChanged — the host page's existing incremental redraw path does the rest, no full reload.
- TRIP-STARTFINISH-02 (TripOrderingService): SetStartAsync/ClearStartAsync/SetFinishAsync/ClearFinishAsync write `StartPoiId`/`FinishPoiId` under SqliteWriteLock (Version is bumped centrally by AppDbContext.SaveChanges for modified PoiCollections) and renumber via the EXISTING `Renumber` + `SetOrderAsync` single write path (AR-11): pinned Start first, interior stops in current relative order, pinned Finish last ⇒ contiguous unique 1..N by construction. Finish == current Start (and vice versa) throws InvalidOperationException; unplaceable/unknown/unordered POIs are no-ops; re-designating the same stop is an idempotent no-write.
- TRIP-STARTFINISH-03/04/05 (UI): distinct Start ring + `trip_origin` glyph and Finish ring + `sports_score` glyph (Material Symbols Outlined) on the badge of BOTH TripStopList and MobileTripPanel; per-row Set/Unset Start & Finish buttons with UiStrings aria-labels + aria-pressed; cross-pinning surfaced as disabled controls; new Trip View — Start/Finish UiStrings block; designation + roundtrip↔open-path announcements via dedicated polite live regions.
- TRIP-STARTFINISH-06 (map): `IMapService.SetStopOrdersAsync` extended with a `TripMarkerRolesDto` (start/finish PoiIds + localized marker aria/titles) and `DrawTripLegsAsync` with an `isRoundtrip` flag; leafletInterop renders the Start/Finish marker ring + glyph with role="img"/aria-label/title baked into buildMarkerIcon (survives re-skins) and tags the closing leg `trip-leg-closing` (same dashed+muted styling); LeafletMap.razor stays a thin pass-through wrapper.
- Touch-target note: mobile Set/Unset controls are real 44×44px buttons; desktop Set/Unset controls render a 24px visual (matching the 1.5 move controls in the compact w-64 panel) extended to a 44×44px hit box via padding + negative margin — the ≥44px target spec is honored on both surfaces without starving the row layout (first attempt with full-size inline buttons broke pre-existing 1.4 row-selection tests).
- Reorder (1.5) and unplaceable handling (1.6) untouched: ReorderStopAsync still never writes pins; unplaceable stops (OrderIndex 0) are rejected as pin candidates at both VM and service level.
- StubMapService records the latest trip-leg count / roundtrip flag / marker roles (static, private-set properties) so the Playwright integration tests can assert N vs N−1 legs at the IMapService boundary (Leaflet itself is stubbed in integration).
- Known pre-existing flakes under parallel load (not regressions; pass in isolation): ScrapeProgress_ShowsIndicator, OperationsExtendedTests.Union_ShowsAllUniquePois.

### File List

- LucidCartographer/Services/UiStrings.cs
- LucidCartographer/Services/Trip/ITripOrderingService.cs
- LucidCartographer/Services/Trip/TripOrderingService.cs
- LucidCartographer/Services/IMapService.cs
- LucidCartographer/Services/LeafletMapService.cs
- LucidCartographer/Components/Shared/Trip/TripProjections.cs
- LucidCartographer/Components/Shared/Trip/TripViewModel.cs
- LucidCartographer/Components/Shared/Trip/TripStopList.razor
- LucidCartographer/Components/Shared/Trip/MobileTripPanel.razor
- LucidCartographer/Components/Shared/LeafletMap.razor
- LucidCartographer/Components/Pages/MapPage.razor
- LucidCartographer/wwwroot/js/leafletInterop.js
- LucidCartographer/wwwroot/css/base.css
- LucidCartographer.Tests/Services/TripOrderingServiceTests.cs
- LucidCartographer.Tests/ViewModels/TripViewModelTests.cs
- LucidCartographer.Tests/Components/Trip/TripStopListTests.cs
- LucidCartographer.Tests/Integration/TripViewIntegrationTests.cs
- LucidCartographer.Tests/Integration/MobileTripViewTests.cs
- LucidCartographer.Tests/Integration/StubMapService.cs
- _bmad-output/implementation-artifacts/1-7-designate-start-finish-and-roundtrip.md

## Change Log

- 2026-06-12: Story 1.7 implemented — Start/Finish designation through the single TripOrderingService write path (pin to Order 1/N, contiguous unique 1..N, Finish==Start rejected, re-designation releases the old pin); TripViewModel intents + derived IsRoundtrip/StopRole; Set/Unset controls + distinct badge glyphs on desktop and mobile stop lists with aria-live announcements; Start/Finish marker roles + roundtrip flag through LeafletMapService → leafletInterop; UiStrings Start/Finish block; unit + bUnit + desktop/mobile Playwright integration coverage. Status → review.
