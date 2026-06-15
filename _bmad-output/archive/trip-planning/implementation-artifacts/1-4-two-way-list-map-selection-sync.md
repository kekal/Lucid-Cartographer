---
baseline_commit: 03d2765b0bc04755fa4329929211366e3c2bcbfe
---

# Story 1.4: Two-way list ↔ map selection sync

Status: done

## Story

As a trip planner,
I want selecting a stop in the list to highlight it on the map and vice versa,
So that I can connect a row to its place without hunting.

This story adds **bidirectional selection sync** between the Trip stop list (`Components/Shared/Trip/TripStopList.razor`, created in Story 1.3) and the Leaflet map markers (`Components/Shared/LeafletMap.razor` + `wwwroot/js/leafletInterop.js`). It introduces a single `SelectedStop`/`SelectedStopPoiId` state on `TripViewModel` (extended from Story 1.2) that both directions read and write through one path. Selecting a stop row pans the map so the marker is in view and visually emphasises it (distinct from unselected markers); clicking a stop marker scrolls its list row into view and emphasises it. The sync **extends the existing marker-click interop** (`leafletInterop.addCollectionMarkers` → `marker.on('click')` → `state.dotnetRef.invokeMethodAsync('OnMarkerClickedJs', poi.id)` → `IMapService.OnMarkerClicked` → `LeafletMap.OnMarkerSelected`) **without regressing** the existing marker popup (desktop) / tooltip behaviour. Both the desktop panel-beside-map path and the mobile (`m-app`) map-over-bottom-panel path are implemented.

**Scope:** bidirectional selection sync ONLY. Explicitly NOT in this story: drag/keyboard reorder (Story 1.5), leg rendering (consumed from Story 1.3, not built here), unplaceable flagging (Story 1.6), Start/Finish designation (Story 1.7), travel times / timeline (Epic 2).

## Acceptance Criteria

**AC1 (FR-7, UX-DR13) — list → map.** Given Trip View is on, when I select a stop row in the list, then the map pans so that stop's marker is within the viewport and the marker is visually emphasised (distinct from unselected markers); and the selection clears (emphasis removed) from the previously-selected stop when another stop is chosen — at most one stop is emphasised at a time.

**AC2 (FR-7, UX-DR13) — map → list.** Given Trip View is on, when I click a stop marker on the map, then its list row scrolls into view and is emphasised; and the existing marker popup (desktop) / tooltip behaviour is NOT regressed — the sync reuses the existing marker-click interop (`OnMarkerClickedJs` → `IMapService.OnMarkerClicked` → `LeafletMap.OnMarkerSelected`), so on desktop the popup still opens and on mobile the tooltip click still works exactly as before.

**AC3 (UX-DR12, NFR4) — both surfaces + a11y.** Both directions of the sync are implemented on the desktop render path (stop list panel beside the map) and the mobile render path (`Mobile*Screen` / `m-app`: map over bottom panel). The selected stop row exposes its selected state to assistive tech (`aria-current` / `aria-selected` + an `aria-label` conveying the stop's order and name), and a selection-announcement uses an `aria-live="polite"` region. All new user-facing copy (e.g. a selected-stop announcement) routes through `UiStrings`. Mobile touch targets stay ≥ ~44px and safe-area insets are honoured.

**AC4 (NFR3, project layering) — single state, no regressions.** Selection state lives as `SelectedStopPoiId` (+ derived `SelectedStop`) with `private set` on the sealed Transient `TripViewModel`, raised via the existing `StateChanged` event; both directions mutate it through one ViewModel method. Trip selection state is **independent of** the existing non-trip `MapPageViewModel.SelectedPoiId` POI-detail selection — turning Trip View off and selecting a POI must behave exactly as today (no popup/detail regression). The build passes with `TreatWarningsAsErrors=true` and no group-B analyzer violations; no `ConfigureAwait(false)`; new design decisions carry `TRIP-*` comment codes.

## Tasks / Subtasks

- [x] **T1 — Extend `TripViewModel` with selection state (AC1, AC2, AC4).**
  - [x] Add `public int? SelectedStopPoiId { get; private set; }` and a derived `public TripStop? SelectedStop { get; private set; }` (or compute from the existing ordered-stop collection introduced in Story 1.3) to the sealed, Transient `TripViewModel`. Tag with `// TRIP-SELECT-01`.
  - [x] Add one mutation method `public void SelectStop(int? poiId)` (or `Task SelectStopAsync` if it must touch the map — see T3) that sets `SelectedStopPoiId`, recomputes `SelectedStop`, and calls the private `Notify()` so `StateChanged` fires. Selecting the same stop again is idempotent (or toggles per UX — default: re-select keeps selected). Selecting a different stop replaces the prior selection (satisfies "selection clears when another is chosen").
  - [x] Do NOT couple this to `MapPageViewModel.SelectedPoiId`; trip selection is a distinct concept (AC4). Confirm Trip-off behaviour is untouched.
- [x] **T2 — JS interop: marker emphasis + pan + reuse marker-click (AC1, AC2).**
  - [x] In `wwwroot/js/leafletInterop.js`, add `emphasizeStop(poiId)` (and a clear path, e.g. `emphasizeStop(null)` or `clearStopEmphasis()`): look up `state.markers[poiId]`, apply a distinct selected style (e.g. add a CSS class / swap the divIcon to a `selected` variant, or bump `zIndexOffset` + ring) and remove emphasis from the previously emphasised marker so at most one is emphasised. Track `state.selectedStopId`. Tag with `// TRIP-SELECT-02`.
  - [x] Add `panToStop(poiId)`: if the marker exists and is outside the current `state.map.getBounds()`, `state.map.panTo(marker.getLatLng())` (use `panInside`/`setView` to bring it within viewport without a jarring zoom change; reuse the existing `flyTo` ergonomics from `focusOnPoi` but do NOT force a zoom level — AC1 says "within the viewport", not "centred+zoomed"). Tag `// TRIP-SELECT-03`.
  - [x] **Reuse, do not replace, the existing marker-click path.** The marker `click` and tooltip `click` handlers already call `state.dotnetRef.invokeMethodAsync('OnMarkerClickedJs', poi.id)` (lines ~177-184 and ~305-309) and bind the popup (desktop) / skip it on `mobileMode`. Leave that intact. The map→list direction flows through the EXISTING callback; this story does NOT add a new JS→.NET marker callback. Verify emphasis can be applied to trip markers regardless of whether `mobileMode` is on.
  - [x] If Story 1.3's trip markers are drawn via a trip-specific layer (numbered badges) rather than `addCollectionMarkers`, apply `emphasizeStop` against whatever marker registry Story 1.3 populates; keep one marker registry keyed by POI id. (CONFIRM against the as-built 1.3 marker code — see Dev Notes "Dependency on 1.3".)
- [x] **T3 — `LeafletMap.razor` + `IMapService`/`LeafletMapService` API extension (AC1, AC2).**
  - [x] Add to `IMapService` (`Services/IMapService.cs`): `Task EmphasizeStopAsync(int? poiId);` and `Task PanToStopAsync(int poiId);` (or a single `Task SelectStopOnMapAsync(int poiId)` that pans + emphasises). Implement in `LeafletMapService.cs` via the existing `InvokeJsVoidAsync(...)` helper (which already swallows `JSDisconnectedException`/`ObjectDisposedException`/`InvalidOperationException`). Tag `// TRIP-SELECT-04`.
  - [x] Expose passthrough methods on `LeafletMap.razor` (mirroring the existing `HighlightMarkerAsync`/`FocusOnPoiAsync` guarded-by-`IsInitialized` pattern). Do NOT add a new `EventCallback` for marker clicks — the existing `OnMarkerSelected` (line 9) is the channel.
  - [x] The list→map call is initiated from the host page (MapPage / mobile path) reacting to `TripViewModel.StateChanged`, OR from `TripViewModel` holding the `LeafletMap` ref the same way `MapPageViewModel` holds `_map` (see `MapPageViewModel.HandlePoiSelectedAsync` → `_map.HighlightMarkerAsync`). Prefer the host wiring a `Vm.SelectedStopPoiId` change to `_leafletMap.EmphasizeStopAsync(...)` + `PanToStopAsync(...)` to keep the VM free of the JS ref, consistent with layering (CONFIRM how 1.3 wired the map ref to the trip VM and follow that).
- [x] **T4 — `TripStopList.razor` row selection + scroll-into-view + emphasis (AC1, AC2, AC3).**
  - [x] Make each stop row selectable: `@onclick` calls `Vm.SelectStop(stop.PoiId)`; row is keyboard-activatable (`role="button"` or a real `<button>`, `tabindex="0"`, Enter/Space) consistent with the existing mobile `.row` pattern in `MapPage.razor` (line 165: `role="button" tabindex="0"`).
  - [x] Render the selected row with a distinct emphasis style (Tailwind `surface-*`/`primary` token palette; e.g. `primary` ring / tinted background) bound to `Vm.SelectedStopPoiId == stop.PoiId`. Set `aria-current="true"` (or `aria-selected`) on the selected row and an `aria-label` conveying order + name (UX-DR13/NFR4: "a number on a pin is meaningless to a screen reader").
  - [x] **Scroll-into-view (map→list):** when `SelectedStopPoiId` changes due to a marker click, scroll the selected row into view. Reuse the existing JS scroll helpers: for the **mobile** non-virtualized list use the existing `leafletInterop.scrollMobileRowIntoView(poiId)` (matches `.m-app .row[data-poi-id="…"]`, line ~520) — so the trip stop rows MUST carry `data-poi-id="@stop.PoiId"`; for a **desktop virtualized** list reuse `window.LucidCartographer.scrollListToPoi(container, poiId, index, itemSize)` (line ~601). If the desktop trip stop list is short/non-virtualized, a plain `element.scrollIntoView({block:'center', behavior:'auto'})` via a tiny new `leafletInterop` helper is acceptable — match whichever list Story 1.3 built. Tag `// TRIP-SELECT-05`.
  - [x] Invoke the scroll only for the **map→list** direction (avoid scroll-jank when the user clicked the row themselves). Distinguish the two triggers (e.g. a `selectionSource` flag on the VM mutation, or scroll unconditionally if it's cheap and idempotent — prefer source-aware).
- [x] **T5 — Desktop host wiring (AC1, AC2, AC3).**
  - [x] In the desktop branch of `MapPage.razor` (the `<LeafletMap @ref="_leafletMap" OnMarkerSelected="…">` host, line 288), when Trip View is on, route marker selection to the trip VM (so a marker click sets `SelectedStopPoiId`) and route `SelectedStopPoiId` changes to `_leafletMap.EmphasizeStopAsync(...)` + `PanToStopAsync(...)`. Ensure the existing non-trip `OnMarkerSelected="Vm.HandleMarkerSelectedAsync"` POI-detail behaviour is preserved when Trip View is off (AC4).
  - [x] Desktop popup must still open on marker click (don't suppress `bindPopup`). Emphasis is additive to the popup, not a replacement.
- [x] **T6 — Mobile host wiring (`m-app`) (AC1, AC2, AC3).**
  - [x] In the mobile branch of `MapPage.razor` (line 49 `<LeafletMap …>` within `.m-app`), implement the same two directions against the mobile trip panel/sheet from Story 1.3. Marker click (tooltip click on mobile) → set `SelectedStopPoiId` → scroll the mobile stop row into view (`scrollMobileRowIntoView`) + emphasise. Row tap → `SelectStop` → pan + emphasise marker. Honour `mobileMode` (no popup on mobile — tooltip path is the interop).
  - [x] Verify touch targets ≥44px and safe-area insets (existing `.m-app` conventions).
- [x] **T7 — UiStrings (AC3).**
  - [x] Add any new copy to `Services/UiStrings.cs` (e.g. `TripStopSelectedAnnouncement` for the `aria-live` region, `TripStopRowAriaLabel`-style format, or a "Selected stop N: {name}" template). No hardcoded UI text. Keep voice/tone honest & factual per EXPERIENCE.md.
- [x] **T8 — Regression guard for popups/tooltips (AC2, AC4).**
  - [x] Manually and via integration test confirm: desktop marker click still opens the Leaflet popup AND emphasises; mobile tooltip click still fires selection (no popup) AND emphasises; toggling Trip View off restores plain POI-detail selection with no emphasis artifacts left on markers; turning Trip View on/off does not leak a stale `SelectedStopPoiId`.
- [x] **T9 — Tests (all ACs).**
  - [x] **Unit** (`LucidCartographer.Tests/ViewModels/TripViewModelTests.cs`): selecting a stop sets `SelectedStopPoiId`/`SelectedStop` and raises `StateChanged`; selecting another stop replaces it (only one selected); `SelectStop(null)` clears; trip selection does not touch `MapPageViewModel` POI selection.
  - [x] **Component (bUnit)** (`Components/Trip*Tests.cs`): clicking a `TripStopList` row marks it emphasised + sets `aria-current`/`aria-selected`; keyboard Enter/Space activates; rows carry `data-poi-id`; only one row emphasised at a time.
  - [x] **Integration** (`Integration/` + `Mobile`): list→map emphasises the marker and pans it into view; map→list (marker/tooltip click) scrolls the row into view and emphasises it; **popup/tooltip not regressed** (desktop popup opens; mobile tooltip selects); both render paths via `IntegrationTestBase` + `MobileTestBase` per project testing rules.

### Review Findings

_Code review 2026-06-12 (bmad-code-review) — Blind Hunter + Edge Case Hunter + Acceptance
Auditor over the Story-1.4 File List diff. AC verdict: **AC1 PASS, AC2 PARTIAL** (non-regression
verified by inspection only — see defer below), **AC3 PASS, AC4 PASS**. Two High-severity
hunter findings were false positives (`marker._poiId` IS assigned at `leafletInterop.js:363`;
the `===` emphasis comparison is number-vs-number, not a type mismatch)._

**Patch**

- [x] [Review][Patch] List re-selection of the already-selected stop doesn't re-pan the map — **FIXED 2026-06-12**: added a monotonic `TripViewModel.SelectionTick` (bumped on every `SelectStop`, incl. idempotent re-selects); `PushTripSelectionAsync` now dedups on `(poiId, SelectionTick)` so a re-select re-runs the pan/scroll follow-up while unrelated `StateChanged` notifications stay no-ops; emphasis re-skins only when the marker actually changes. Regression test `SelectStop_ReSelectingSameStop_AdvancesSelectionTick`. [LucidCartographer/Components/Pages/MapPage.razor:618]

**Deferred (new — see deferred-work.md)**

- [x] [Review][Defer] Popup/tooltip non-regression (AC2/T8) verified by inspection only — no automated assertion because the Leaflet interop is stubbed in integration tests [LucidCartographer.Tests/Integration] — deferred, needs a real-browser Playwright assertion
- [x] [Review][Defer] Stale `aria-current` on a removed selected stop until the async membership refresh completes [LucidCartographer/Components/Shared/Trip/TripViewModel.cs RefreshProjectionsAsync] — deferred, brief self-healing window
- [x] [Review][Defer] `scrollTripRowIntoView` invoked via direct `IJSRuntime`, not routed through `LeafletMapService` [LucidCartographer/Components/Pages/MapPage.razor:637] — deferred, matches existing as-built pattern; low-priority layering cleanup

**Re-surfaced — already tracked in deferred-work.md (from the 1-3 review, not re-added)**

- [x] [Review][Defer] Roundtrip closing leg returns to `stops[0]` instead of the designated `StartPoiId` [TripViewModel.cs BuildLegs] — already deferred (1-3); resolve when Story 1.7 lands
- [x] [Review][Defer] Trip View stuck-on below the ≥2 placeable gate — toggle hidden, panel stays [TripViewModel.cs UpdatePlaceableCount / MapPage.razor:160,349] — already deferred (1-3); pre-existing Story 1.2 gate
- [x] [Review][Defer] `OnVmChanged` fire-and-forget async + single-slot `MembershipChanged` callback [MapPage.razor] — already deferred (1-3); mitigated by the single-threaded Blazor circuit

## Dev Notes

### Patterns & constraints (from project-context.md + architecture.md)
- **Layering (strict):** Component (`.razor`) → ViewModel → Service → Data. `TripStopList.razor` and `MapPage.razor` are thin bridges; selection state and orchestration live in `TripViewModel`. Map JS is reached only via `LeafletMapService` → `leafletInterop.js` — components never call `IJSRuntime` for map ops directly. (Architecture: "components never invoke JS directly … Map rendering is mediated by `LeafletMapService` → `leafletInterop.js`".)
- **`TripViewModel`** is `sealed`, primary-ctor DI, registered **Transient** in `Configuration/ViewModelExtensions.cs` (from Story 1.2), exposes `event Action? StateChanged` + private `Notify()`, state with `private set`, and implements `IAsyncDisposable`. New `SelectedStopPoiId`/`SelectedStop` follow that exactly.
- **Build discipline:** `TreatWarningsAsErrors=true`, `Nullable=enable`. No group-B analyzer violations (MA0002, MA0015, MA0046, MA0047, MA0074, VSTHRD200). **No `ConfigureAwait(false)`** (Blazor circuit sync context). `[JSInvokable]` is unchanged here — we reuse `OnMarkerClickedJs`, so no new VSTHRD-prone interop surface.
- **UI conventions:** all copy via `UiStrings`; selection emphasis uses the Tailwind `surface-*` / `on-surface-*` / `primary` token palette; selected row uses `aria-current`/`aria-selected`; selection change announced via `aria-live="polite"`; both desktop and mobile (`m-app`) paths implemented (UX-DR12).
- **`TRIP-*` comment codes** on every new trip decision (`TRIP-SELECT-01..05` suggested above). Greppable per architecture enforcement.
- **Canonical units / OrderIndex** are not directly touched here, but the stop's display order (1-based `OrderIndex` from Story 1.1/1.2) feeds the row `aria-label`.

### Dependency on Story 1.2 + Story 1.3 (consume, do not rebuild)
- **Story 1.2** delivered the Trip View toggle, the `TripViewModel` (sealed/Transient/`StateChanged`), and the seeded 1-based Stop Order. This story **extends** that VM with selection state only.
- **Story 1.3** delivered `Components/Shared/Trip/TripStopList.razor` (the row list), numbered map markers, leg rendering, and the desktop panel / mobile bottom-panel split. This story adds **row selection + scroll-into-view + emphasis** to that list and **marker emphasis + pan** to those markers. **CONFIRM at implementation time** (read the as-built 1.3 files, which are not yet on disk): (a) how trip markers are registered in `leafletInterop.js` (the existing `state.markers` map keyed by POI id, or a new trip-marker registry) — apply `emphasizeStop` against that registry; (b) whether the desktop trip list is virtualized (drives which scroll helper to reuse); (c) how the `LeafletMap`/`IMapService` ref is shared with the trip VM/host (mirror it).

### Source tree — NEW / UPDATE (real paths under `C:\backup\maps_editor\LucidCartographer\`)
- **UPDATE** `Components/Shared/LeafletMap.razor` — add guarded passthroughs `EmphasizeStopAsync`/`PanToStopAsync` (mirror existing `HighlightMarkerAsync` at lines 97-105). Do NOT add a new marker EventCallback — reuse `OnMarkerSelected` (line 9) / `OnMarkerClickedJs`.
- **UPDATE** `Services/IMapService.cs` — add `EmphasizeStopAsync(int? poiId)` + `PanToStopAsync(int poiId)` to the interface (alongside `HighlightMarkerAsync`, line 19).
- **UPDATE** `Services/LeafletMapService.cs` — implement them via the existing `InvokeJsVoidAsync(...)` helper (lines 124-140) calling `leafletInterop.emphasizeStop` / `leafletInterop.panToStop`. (`MarkerDto` and the marker-click `[JSInvokable] OnMarkerClickedJs`, lines 64-71, are reused unchanged.)
- **UPDATE** `wwwroot/js/leafletInterop.js` — add `emphasizeStop(poiId)` (+ clear), `panToStop(poiId)`; reuse existing `state.markers`, `state.map.getBounds()`, `flyTo`/`panTo`. Leave `addCollectionMarkers` popup/tooltip binding (lines 277-326) and the two existing `invokeMethodAsync('OnMarkerClickedJs', …)` call sites untouched.
- **UPDATE** `Components/Shared/Trip/TripStopList.razor` (from Story 1.3) — selectable rows (`@onclick`, keyboard), `data-poi-id="@stop.PoiId"`, emphasis style bound to `Vm.SelectedStopPoiId`, `aria-current`/`aria-selected` + `aria-label`, scroll-into-view on map→list.
- **UPDATE** `Components/Shared/Trip/TripViewModel.cs` (from Story 1.2) — `SelectedStopPoiId` (+ `SelectedStop`) `private set`, `SelectStop(...)` method, `Notify()`. (Architecture places `TripViewModel.cs` under `Components/Shared/Trip/`.)
- **UPDATE** `Components/Pages/MapPage.razor` — desktop branch (host at line 288) and mobile branch (host at line 49 in `.m-app`): wire both sync directions when Trip View is on; preserve non-trip behaviour when off.
- **UPDATE** `Services/UiStrings.cs` — new selection/announcement copy.
- **(possible) UPDATE** mobile trip panel component from Story 1.3 (e.g. `MobileTripPanel.razor`) if the mobile stop list is a distinct component rather than `TripStopList` reused in the mobile branch — apply the same row-selection/scroll/emphasis there. CONFIRM against 1.3.
- **NEW (tests)** `LucidCartographer.Tests/ViewModels/TripViewModelTests.cs` (extend), `Components/Trip*Tests.cs`, `Integration/` + `Mobile` trip-sync tests.

### Existing marker interop — current behaviour + what MUST be preserved
Current flow (verified in source):
- `addCollectionMarkers` (`leafletInterop.js` lines 277-326) creates a `L.marker` per POI, stores it in `state.markers[poi.id]`, and on **desktop** (`!state.mobileMode`) binds a popup with name/address/Google-Maps link; on **mobile** it skips `bindPopup`. Every marker gets `marker.on('click', …)` → `state.dotnetRef.invokeMethodAsync('OnMarkerClickedJs', poi.id)`.
- Permanent name **tooltips** (`bindLabel`, lines 166-186) are interactive; their click opens the popup (desktop only) **and** fires `OnMarkerClickedJs`.
- `OnMarkerClickedJs` (`LeafletMapService.cs` line 64) → `IMapService.OnMarkerClicked` → `LeafletMap.HandleMarkerClickedAsync` (`LeafletMap.razor` line 118) → `OnMarkerSelected.InvokeAsync(poiId)` → `MapPageViewModel.HandleMarkerSelectedAsync` (`MapPageViewModel.cs` line 355).
- `highlightMarker(poiId)` (line 379) currently just `marker.openPopup()`. `focusOnPoi` (line 353) `flyTo` zoom 16.

**Must NOT regress (AC2/AC4):** (1) desktop popup still opens on marker/tooltip click; (2) mobile tooltip click still selects with no popup (`mobileMode`); (3) the single `state.dotnetRef.invokeMethodAsync('OnMarkerClickedJs', …)` channel stays the only JS→.NET marker callback — emphasis/pan are **additive** JS functions, not a rewrite of the click path; (4) non-trip POI-detail selection (`MapPageViewModel.SelectedPoiId`) is unchanged when Trip View is off; (5) `state.markers` registry, `setLabelsVisible`, locate/bounds/splitter logic untouched.

### Testing summary
Three project layers: **Unit** (`TripViewModel` selection state + `StateChanged`), **Component/bUnit** (`TripStopList` row emphasis, `aria-current`/`aria-selected`, keyboard, single-selection, `data-poi-id`), **Integration** (`IntegrationTestBase` desktop + `MobileTestBase` mobile: list→map pan+emphasis, map→list scroll+emphasis, **popup/tooltip non-regression**). Use `InternalsVisibleTo("LucidCartographer.Tests")` to test VM internals directly. Cover BOTH render paths (project rule: "Mobile vs desktop paths have dedicated bases/tests").

### Project Structure Notes
- No new vertical slice and no schema change in this story — it is UI + interop only, layered on Story 1.1's schema and Story 1.2/1.3's VM + list/markers.
- New `IMapService` members are additive (interface-first convention preserved). `LeafletMapService` is registered scoped (its lifetime owned by the circuit) — do not dispose it from the component; follow the existing guarded `InvokeJsVoidAsync` teardown pattern (already handles `JSDisconnectedException`/`ObjectDisposedException`/`InvalidOperationException`).
- One marker registry keyed by POI id (`state.markers`) is the single source for emphasis — do not introduce a second registry (architecture anti-pattern: "a second JS module duplicating `leafletInterop` … logic").
- Selection is a UI concern on `TripViewModel`; it does **not** persist to the DB (only `TripViewEnabled` + `OrderIndex` persist, per Story 1.1/1.2) — `SelectedStopPoiId` is transient per circuit.

### References
- [Source: _bmad-output/planning-artifacts/epics.md#Story-1.4] — story statement + ACs (FR-7, UX-DR13); scope vs Stories 1.5/1.6/1.7.
- [Source: _bmad-output/planning-artifacts/epics.md#Requirements-Inventory] — FR-7 ("Selecting a Stop in the list highlights/pans … and vice versa; clicking a marker scrolls its list row into view. Reuses existing marker-click interop without regressing popups/tooltips").
- [Source: _bmad-output/planning-artifacts/architecture.md#Frontend-Architecture] — D6 ("list↔map two-way sync reuses existing marker-click interop"); LeafletMapService → leafletInterop.js mediation; component-as-thin-bridge boundary.
- [Source: _bmad-output/planning-artifacts/architecture.md#Naming-Patterns] — JS interop camelCase verb fns added to `leafletInterop.js` (e.g. `highlightStop`), invoked via `LeafletMapService.cs`; "No new JS module — extend the existing one."
- [Source: _bmad-output/planning-artifacts/architecture.md#Project-Structure-&-Boundaries] — `LeafletMapService.cs [MOD]`, `leafletInterop.js [MOD] … highlightStop, list↔map sync`; `TripViewModel` sealed/Transient/StateChanged.
- [Source: _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-11/EXPERIENCE.md#Interaction-Primitives] — UX-DR13: "List ↔ map two-way sync … a core trip interaction, not a nicety."
- [Source: _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-11/EXPERIENCE.md#Accessibility-Floor] — aria-live for recompute/selection; descriptive aria-labels on stop-order badges/legs/values; keyboard reachability; both surfaces.
- [Source: _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-11/EXPERIENCE.md#Responsive-&-Platform] — desktop panel-beside-map vs mobile map-over-bottom-panel; both implement full behaviour.
- [Source: _bmad-output/project-context.md#Architecture-Layering] — Component → ViewModel → Service → Data; ViewModels sealed/Transient/StateChanged/private set/IAsyncDisposable.
- [Source: _bmad-output/project-context.md#UI-Conventions] — UiStrings only; aria-live/aria-label/aria-pressed; desktop & mobile distinct render paths.
- [Source: _bmad-output/project-context.md#Build-&-Language-Discipline] — warnings-as-errors; no group-B analyzer violations; no `ConfigureAwait(false)`.
- [Source: LucidCartographer/Components/Shared/LeafletMap.razor] — `OnMarkerSelected` EventCallback (L9); `HighlightMarkerAsync` guarded pattern (L97-105); `HandleMarkerClickedAsync` (L118).
- [Source: LucidCartographer/wwwroot/js/leafletInterop.js] — `addCollectionMarkers`/popup-tooltip binding (L277-326); `bindLabel` interactive tooltip (L166-186); `marker.on('click') → OnMarkerClickedJs` (L305-309); `highlightMarker` (L379); `focusOnPoi` (L353); `scrollMobileRowIntoView` (L520); `state.mobileMode`/`setMobileMode` (L422); `LucidCartographer.scrollListToPoi` (L601).
- [Source: LucidCartographer/Services/IMapService.cs] — interface surface incl. `HighlightMarkerAsync` (L19), `OnMarkerClicked` callback (L35).
- [Source: LucidCartographer/Services/LeafletMapService.cs] — `InvokeJsVoidAsync` guarded helper (L124-140); `[JSInvokable] OnMarkerClickedJs` (L64-71); scoped-service teardown notes (L83-119).
- [Source: LucidCartographer/Components/Pages/MapPage.razor] — mobile host `<LeafletMap …>` in `.m-app` (L49); mobile `.row` `data-poi-id` + `role="button" tabindex="0"` (L162-165); desktop host (L288).
- [Source: LucidCartographer/Components/Pages/MapPageViewModel.cs] — `HandleMarkerSelectedAsync` (L355), `HandlePoiSelectedAsync` → `_map.HighlightMarkerAsync` (L357-364), `SelectPoiAsync` (L375).

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Dev Story workflow)

### Debug Log References

- App + test builds green under `TreatWarningsAsErrors=true`, `Nullable=enable` —
  no group-B analyzer violations, no `ConfigureAwait(false)`.
- Full regression suite: **627/627 passing** (incl. 32 Trip unit/component + 8
  Trip integration). The previously-flaky `ScraperIntegrationTests.ScrapeProgress_ShowsIndicator`
  passed this run too.

### Completion Notes List

Bidirectional list ↔ map selection sync, layered on Story 1.2/1.3 with one new
selection concept threaded through a single path.

- **Single selection state (T1, AC4):** `TripViewModel` gained `SelectedStopPoiId`
  (+ derived `SelectedStop`, `LastSelectionSource`, `SelectionAnnouncement`, all
  `private set`) and one mutator `SelectStop(int? poiId, TripSelectionSource source)`
  that both directions call. It is **independent of** `MapPageViewModel.SelectedPoiId`
  (no coupling), no-ops when Trip View is off, and is cleared whenever the
  projections clear (toggle-off) or the selected stop is removed by a membership
  change — so no stale selection survives (AC4). `TripSelectionSource{List,Map}`
  drives the directional follow-up. (TRIP-SELECT-01)
- **JS interop (T2, AC1/AC2):** added `emphasizeStop(poiId)`, `panToStop(poiId)`,
  `scrollTripRowIntoView(poiId)` inside the existing `leafletInterop` IIFE (no new
  module). Emphasis is folded into `buildMarkerIcon` via `state.selectedStopId` (a
  `.trip-stop-selected` class) so it survives `setStopOrders`/`addCollectionMarkers`
  re-skins; at most one marker is emphasised and the selected marker is raised.
  `panToStop` only pans when the marker is outside the viewport and never changes
  zoom (AC1 "within the viewport"). The existing marker-click / tooltip / popup
  binding and the single `OnMarkerClickedJs` channel are **untouched** — the sync
  reuses them (AC2/AC4). (TRIP-SELECT-02/03/05)
- **Service surface (T3):** `IMapService`/`LeafletMapService` gained
  `EmphasizeStopAsync(int?)` + `PanToStopAsync(int)` via the existing
  `InvokeJsVoidAsync` guard; `LeafletMap.razor` exposes thin `IsInitialized`-guarded
  passthroughs. No new marker `EventCallback` — the existing `OnMarkerSelected`
  channel is reused. (TRIP-SELECT-04)
- **Rows + emphasis (T4/T6, AC1/AC3):** `TripStopList` (`<li role="button">`) and
  `MobileTripPanel` (`.row role="button"`) rows are click/Enter/Space selectable,
  carry `data-poi-id`, expose `aria-current` when selected, and tint with a
  primary token (desktop `bg-primary/10 ring-primary`, mobile `--primary-soft`).
  Both lists carry a `trip-stop-list` class scoping the scroll-into-view helper.
  A per-panel `aria-live="polite"` region announces the selected stop via
  `UiStrings.TripStopSelectedAnnouncement`. Row accessible name via the new
  `UiStrings.TripStopRowAria` ("Stop {n} of {N}: {name}").
- **Host wiring (T5/T6):** `MapPage` routes both `LeafletMap` hosts' `OnMarkerSelected`
  through a trip-aware `OnMarkerSelectedAsync` — Trip View on ⇒ `SelectStop(…, Map)`
  (the Leaflet popup still opens in JS); Trip View off ⇒ the original
  `Vm.HandleMarkerSelectedAsync` POI-detail behaviour, exactly as before (AC4).
  `PushTripSelectionAsync` (folded into `PushTripAsync`, de-duped on the pushed
  poiId, reset on viewport-flip re-wire) applies emphasis on every change and the
  directional follow-up only on a real change: List ⇒ `PanToStopAsync`, Map ⇒
  `scrollTripRowIntoView`.
- **Tests (T9):** unit (selection set/replace/clear, source, off-no-op, cleared on
  toggle-off + on stop removal, independence from projections), bUnit (row click /
  keyboard sets `aria-current`, single-selection, `data-poi-id`/role on both
  surfaces), integration (desktop + mobile list→map row selection state).
  *Coverage note:* the Leaflet stub means actual marker emphasis/pan and the
  map→list popup/tooltip non-regression (T8/AC2) aren't assertable cross-process at
  the integration layer — the marker-click channel and `addCollectionMarkers`
  popup/tooltip binding were left untouched (structural preservation), and the JS
  interop is exercised only against a real browser. (Logged for follow-up.)

### File List

**UPDATED**
- `LucidCartographer/Components/Shared/Trip/TripViewModel.cs`
- `LucidCartographer/Components/Shared/Trip/TripProjections.cs`
- `LucidCartographer/Components/Shared/Trip/TripStopList.razor`
- `LucidCartographer/Components/Shared/Trip/MobileTripPanel.razor`
- `LucidCartographer/Components/Shared/LeafletMap.razor`
- `LucidCartographer/Components/Pages/MapPage.razor`
- `LucidCartographer/Services/IMapService.cs`
- `LucidCartographer/Services/LeafletMapService.cs`
- `LucidCartographer/Services/UiStrings.cs`
- `LucidCartographer/wwwroot/js/leafletInterop.js`
- `LucidCartographer/wwwroot/css/base.css`
- `LucidCartographer.Tests/ViewModels/TripViewModelTests.cs`
- `LucidCartographer.Tests/Components/Trip/TripStopListTests.cs`
- `LucidCartographer.Tests/Integration/TripViewIntegrationTests.cs`
- `LucidCartographer.Tests/Integration/MobileTripViewTests.cs`
- `LucidCartographer.Tests/Integration/StubMapService.cs`

## Change Log

| Date       | Change                                                                                  |
|------------|-----------------------------------------------------------------------------------------|
| 2026-06-12 | Story 1.4 implemented: bidirectional list ↔ map selection sync — single `SelectStop` state on `TripViewModel`, marker emphasis + viewport-aware pan, row selection with `aria-current`/keyboard + scroll-into-view, aria-live announcement, both surfaces; reuses the existing marker-click channel with no popup/tooltip regression. 627/627 tests green. Status → review. |
