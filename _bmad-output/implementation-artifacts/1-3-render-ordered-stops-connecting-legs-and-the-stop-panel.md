---
baseline_commit: 03d2765b0bc04755fa4329929211366e3c2bcbfe
---

# Story 1.3: Render ordered stops, connecting legs and the stop panel

Status: done

## Story

As a trip planner,
I want my stops drawn in order on the map with connecting legs and a side stop list,
So that I can see the shape of the loop at a glance.

This story is the **rendering** slice of Epic 1. It consumes the schema from Story 1.1
(`OrderIndex`, `RouteSegment`, `StartPoiId`/`FinishPoiId` on `PoiCollection`) and the
Trip-View toggle + seeded Stop Order from Story 1.2 (`TripViewModel`,
`TripOrderingService`, order badges). It adds three things and nothing more:

1. **Connecting legs on the map** — a straight polyline between each consecutive pair of
   placeable stops in Stop Order, plus the closing leg back to Start for the default
   Roundtrip (N legs) — or N−1 legs and no closing leg when a distinct Finish is already
   set. All Phase-1 legs are **non-Measured → dashed + muted** (only Measured legs are
   solid, and none exist yet in Phase 1).
2. **Numbered markers** — each stop marker shows its Stop Order number.
3. **The stop-list panel** — a scaffold rendered on **both** surfaces (desktop panel
   beside the map; mobile bottom panel/sheet). Each row shows the order badge, POI name,
   a **dwell-time field placeholder**, and a **timeline-value placeholder**.

Redraw on any Stop-Order change is **incremental** (update polylines + marker numbers
only), never a full page reload.

### Out of scope (do NOT implement here — owned by later stories)

- Travel-time / distance / fidelity **computation** and the provider contract — Epic 2.
  The dwell field and the timeline value in this story are **inert placeholders**.
- **Drag** and **keyboard** reorder controls — Story 1.5 (the row scaffold leaves room
  for the drag handle / move-up-down controls but does not wire them).
- **List ↔ map two-way selection sync** (pan/highlight on select, marker-click scrolls
  the row) — Story 1.4.
- **Unplaceable** handling UI/labelling — Story 1.6. This story simply *excludes*
  coordinate-less stops from the polylines/markers (consistent with the existing
  `LeafletMapService` null-coord filter) without adding the "Not placeable" row label.
- **Start/Finish designation logic / controls** — Story 1.7. This story **reads** the
  existing `StartPoiId`/`FinishPoiId` state to decide closing-leg vs open-path and
  draws accordingly; it does not add controls to change them.

## Acceptance Criteria

### AC1 — Ordered straight legs incl. roundtrip close _(FR-5, UX-DR14)_
**Given** Trip View is on for a Roundtrip (no distinct Finish set) with N placeable stops
**When** the map renders
**Then** a straight connecting leg is drawn between each consecutive pair in Stop Order,
**including** the closing leg from Stop N back to the Start — **N legs total**
**And** a Start≠Finish open path (a distinct `FinishPoiId` is set) draws **N−1 legs** with
**no** closing leg
**And** every stop marker displays its Stop Order number.

### AC2 — Stop-list panel on both surfaces with placeholder fields _(FR-5, UX-DR3, UX-DR12)_
**Given** Trip View is on
**When** the page renders
**Then** **desktop** shows the stop list **beside the map** and **mobile** shows it in the
**bottom panel/sheet** — **both render paths implemented**, not one degraded into the other
**And** each row shows: the **order badge**, the **POI name**, a **dwell-time field
placeholder**, and a **timeline-value placeholder** (the placeholders render an em-dash
"—" / disabled affordance via `UiStrings`; no real value is computed in this story).

### AC3 — Non-Measured legs dashed + muted, incremental redraw _(FR-5, UX-DR4, NFR1)_
**Given** non-Measured legs (which is **all** legs in Phase 1)
**When** they are drawn
**Then** they render **dashed and muted** per the line-solidity = geometric-fidelity rule
(only **Measured** legs render solid/full-weight/`primary`; none are Measured in Phase 1)
**And** the redraw on any Stop-Order change is **incremental** (polylines + marker numbers
are updated/replaced in place) and **never a full page reload**.

### AC4 — No regression to existing map behaviour _(brownfield-preserve)_
**Given** the existing collection markers, popups (desktop), tooltips/labels, marker-click
selection, bounds tracking, and mobile-mode popup suppression
**When** Trip View is toggled on and back off
**Then** none of those behaviours regress: plain-collection markers, the desktop popup,
the label toggle, marker-click → `OnMarkerSelected`, and `setMobileMode` all behave
exactly as before, and toggling Trip View off removes all trip legs/numbered markers and
restores the plain collection render.

## Tasks / Subtasks

- [x] **Task 1 — Leg geometry on the ViewModel (AC1)**
  - [x] Extend `TripViewModel` (Story 1.2, `Components/Shared/Trip/TripViewModel.cs`) to
        expose an **ordered, placeable-only** leg list, e.g.
        `IReadOnlyList<TripLeg> OrderedLegs` where `TripLeg` is a small immutable record
        `(int FromPoiId, int ToPoiId, double FromLat, double FromLon, double ToLat,
        double ToLon, bool IsMeasured)` — `IsMeasured` is **always false** in Phase 1
        (tag `// TRIP-LEG-01: Phase 1 — all legs straight + non-Measured`).
  - [x] Build the list from the seeded Stop Order: take placeable stops (lat & lon
        non-null) in ascending `OrderIndex`, pair consecutively (k → k+1). When **no
        distinct `FinishPoiId`** is set (Roundtrip default), append the **closing leg**
        from the last stop back to the Start (Order 1) → N legs. When `FinishPoiId` is a
        distinct stop (open path), emit **N−1** legs, **no** closing leg. (`// TRIP-LEG-02`)
  - [x] Exclude coordinate-less stops from `OrderedLegs` and from numbered markers without
        renumbering — reuse the null-coord filter precedent in `LeafletMapService`
        (`p is { Latitude: not null, Longitude: not null }`). Do **not** add the
        "Not placeable" label (Story 1.6).
  - [x] Recompute `OrderedLegs` and raise `StateChanged` whenever Stop Order, Start/Finish,
        or membership changes (consume Story 1.2's existing change signals).
  - [x] Expose an ordered, placeable-only stop projection for the panel + numbered markers
        (e.g. `IReadOnlyList<TripStop> OrderedStops` carrying `OrderIndex`, `PoiId`,
        `Name`, lat/lon, `IsStart`, `IsFinish`).

- [x] **Task 2 — Leaflet JS interop: draw legs + numbered markers + incremental redraw (AC1, AC3)**
  - [x] In `wwwroot/js/leafletInterop.js` add to the `state` object a dedicated trip layer
        (e.g. `state.tripLegLayer`, `state.tripMarkers = {}`) kept **separate** from
        `state.layerGroups` / `state.markers` so trip overlays never collide with the
        plain-collection markers. (`// TRIP-MAP-01`)
  - [x] Add `drawTripLegs(legs)` — clears the prior trip-leg layer and draws one
        `L.polyline([[fromLat,fromLon],[toLat,toLon]])` per leg. **Non-Measured →
        dashed + muted**: `dashArray` set (e.g. `'6 6'`), reduced weight/opacity using the
        muted token color (mirror `on-surface-muted`; pass the color from .NET so the
        token palette stays the single source — do not hardcode a new hex in JS). Reserve
        a `measured ? solid` branch for Phase 2 but only the dashed path is exercised now.
        (`// TRIP-MAP-02`)
  - [x] Add `drawStopNumbers(stops)` (or fold numbering into the existing
        `addCollectionMarkers` divIcon when Trip View is on) — render each stop marker as a
        numbered badge: `L.divIcon` whose `html` contains the `OrderIndex`, `primary`
        fill, `on-primary` numeral, `text-xs`/700 weight (mirror UX-DR2 / the
        `.custom-marker` precedent). Numbered markers live in `state.tripMarkers`.
  - [x] Add `clearTripLegs()` — removes the trip-leg layer **and** numbered markers and
        restores plain markers (used on Trip-View-off and on collection hide).
  - [x] **Incremental redraw:** `drawTripLegs` / `drawStopNumbers` must remove only the
        prior trip layers and re-add the new ones (no `initMap`, no full
        `addCollectionMarkers` rebuild of unrelated collections). (`// TRIP-MAP-03`,
        satisfies NFR1 / AC3 "incremental, not full reload".)
  - [x] Keep all new logic inside the **existing** `leafletInterop` IIFE — do **not** add a
        second JS module duplicating polyline logic (architecture anti-pattern).

- [x] **Task 3 — `LeafletMapService` + `LeafletMap.razor` interop surface (AC1, AC3, AC4)**
  - [x] In `IMapService` / `LeafletMapService` add `DrawTripLegsAsync`,
        `DrawStopNumbersAsync` (or a single `ShowTripAsync(legs, stops, color)`), and
        `ClearTripAsync`, routed through the existing `InvokeJsVoidAsync` helper (inherits
        its `IsCircuitGone` guard + disposed check). Pass leg/stop DTOs as named records
        (mirror `MarkerDto`) — e.g. `TripLegDto(double FromLat, double FromLon, double
        ToLat, double ToLon, bool IsMeasured)` and `TripStopDto(int PoiId, int OrderIndex,
        double Lat, double Lon, bool IsStart, bool IsFinish)`. Pass the muted leg color +
        primary badge color from .NET (token palette stays authoritative).
  - [x] In `LeafletMap.razor` add thin public methods (`DrawTripAsync` / `ClearTripAsync`)
        guarded on `IsInitialized` exactly like the existing `ShowCollectionAsync` etc.,
        delegating to `MapService`. Preserve every existing method/behaviour (AC4) — do
        not touch `OnAfterRenderAsync`, marker-click wiring, bounds tracking, or
        `DisposeAsync`.

- [x] **Task 4 — Desktop stop-list panel (AC2)**
  - [x] Create `Components/Shared/Trip/TripStopList.razor` — a thin component (markup +
        bindings only; no logic in `@code` beyond a ~12-line VM bridge). Render the
        ordered, placeable stops as rows echoing `PoiTable`'s row rhythm but trip-scoped:
        **[order badge] · [POI name] · [dwell-time field placeholder] · [timeline-value
        placeholder]**. Leave horizontal space at the row's leading edge for the
        Story-1.5 drag handle / move controls (render nothing there now).
  - [x] Order badge: numbered circle, `primary` fill, `on-primary` numeral, `text-xs`
        weight 700 (UX-DR2); carry an `aria-label` (e.g. "Stop 3 of 7").
  - [x] Dwell-field placeholder: a disabled/readonly affordance showing "—" via
        `UiStrings.TripDwellPlaceholder` with an `aria-label` (`UiStrings.TripDwellAria`).
        Timeline-value placeholder: "—" via `UiStrings.TripTimelinePlaceholder` /
        `…Aria`. No computation, no binding to a real value (Epic 2).
  - [x] Host the panel on the **desktop** path of `MapPage.razor` **beside the map** when
        Trip View is on (a right/left column or within the existing center column — keep
        the existing `PoiTable` + map intact; the trip panel is shown additively when
        `TripViewModel` reports Trip View on). Use `surface-*` / `on-surface-*` / `primary`
        tokens and Tailwind utilities only.
  - [x] Status/region uses `aria-live="polite"` for the stop-count; the list has an
        `aria-label`.

- [x] **Task 5 — Mobile stop-list panel (AC2, UX-DR12)**
  - [x] Create `Components/Shared/Trip/MobileTripPanel.razor` (the `MobileTrip*` split) —
        same rows/placeholders as Task 4 but in the **mobile bottom-panel/sheet** idiom
        (`.m-bottom-panel` / `.list` / `.row` classes, `data-poi-id` on each row,
        ≥44px touch targets, safe-area insets honored).
  - [x] Wire it into the **mobile** branch of `MapPage.razor` so that when Trip View is on
        the bottom panel renders the trip stop list (additively/alongside the existing
        POI list + collections drawer states — follow the existing
        `SelectedPoi`/`_drawerOpen` content-swap pattern; add a trip state without
        breaking the current ones). Map stays at the existing ~46% top.
  - [x] Confirm dark mode renders first-class (tokens, not raw hex).

- [x] **Task 6 — Wire MapPage ↔ TripViewModel redraw (AC1, AC3, AC4)**
  - [x] In `MapPage.razor` subscribe to the `TripViewModel.StateChanged` redraw signal (in
        addition to the existing `MapPageViewModel` bridge) and, when Trip View is on, call
        `LeafletMap.DrawTripAsync(OrderedLegs, OrderedStops, color)`; when Trip View is off
        call `ClearTripAsync` and let the plain `ShowCollectionAsync` render stand.
  - [x] Ensure the trip draw happens **after** map init (gate on
        `_leafletMap is { IsInitialized: true }`, mirroring the existing
        `PendingSearchMapUpdate` pattern) and is re-issued on viewport flip (the
        `_wiredMap` re-wire path), since the JS map is rebuilt on flip.
  - [x] Verify toggling Trip View off and re-rendering the plain collection does not leave
        orphaned trip polylines/numbered markers (AC4).

- [x] **Task 7 — UiStrings additions (NFR5)**
  - [x] Add to `Services/UiStrings.cs` (new "Trip View" region): `TripStopList`
        (panel/section label), `TripStopListAria`, `TripDwellPlaceholder` (= "—"),
        `TripDwellAria` (e.g. "Dwell time (set in a later step)"),
        `TripTimelinePlaceholder` (= "—"), `TripTimelineAria` (e.g. "Arrival time
        (computed in a later step)"), `TripStopBadgeAria` (format, e.g. "Stop {0} of {1}"),
        and any panel header/empty-state copy. No hardcoded UI text anywhere in the new
        components.

- [x] **Task 8 — Tests: bUnit component + integration, both surfaces**
  - [x] **Unit (`LucidCartographer.Tests/ViewModels/TripViewModelTests.cs`)** — assert
        `OrderedLegs`: N stops Roundtrip ⇒ N legs incl. closing leg (last→Start); distinct
        Finish ⇒ N−1 legs, no closing leg; coordinate-less stops excluded from legs without
        breaking numbering; every leg `IsMeasured == false` in Phase 1.
  - [x] **Component (bUnit) `Components/TripStopListTests.cs`** — renders one row per
        placeable stop in order; each row shows the order badge number, name, and the two
        em-dash placeholders with their `aria-label`s; uses `UiStrings`.
  - [x] **Mobile component** test (or `MobileTestBase`-derived) for `MobileTripPanel`
        rendering rows + placeholders in the mobile idiom.
  - [x] **Integration (`IntegrationTestBase`, Playwright)** — desktop: Trip View on draws
        the expected number of polylines + numbered markers and shows the stop panel beside
        the map; legs carry a dashed style (assert `stroke-dasharray` on the leg paths);
        toggling off clears legs/numbers and restores plain markers (AC4); reorder/seed
        change redraws **without** a full navigation (incremental).
  - [x] **Integration (Mobile, `MobileTestBase`-derived)** — Trip View on shows the stop
        list in the bottom panel and legs on the ~46% map; popups stay suppressed
        (`setMobileMode` unaffected).
  - [x] Build must pass with `TreatWarningsAsErrors=true`, `Nullable=enable`, and **no
        group-B analyzer violations** (MA0002/0015/0046/0047/0074, VSTHRD200); **no**
        `ConfigureAwait(false)`.

### Review Findings

_Code review 2026-06-12 (bmad-code-review — Blind Hunter + Edge Case Hunter +
Acceptance Auditor, 3 layers, none failed). Triage: 0 decision-needed, 4 patch,
3 deferred, 8 dismissed as noise (incl. a false-positive `marker._poiId`
"unset" — verified set at leafletInterop.js:352)._

- [x] [Review][Patch] Leg stroke colour uses an **undefined** `--outline` CSS var → it always falls to the literal `#727785` (a stranded hardcoded hex in CSS, the very anti-pattern the constraint targets) and never adapts to dark mode. Define a real `:root` token (+ `html[data-theme="dark"]` override) and reference it. [LucidCartographer/wwwroot/css/base.css]
- [x] [Review][Patch] `StubMapService.LastTripLegs` is dead code / false coverage — the Playwright integration tests run cross-process and never read it. Remove the field + recording; restore pure no-op Draw/Clear and fix the comment. [LucidCartographer.Tests/Integration/StubMapService.cs]
- [x] [Review][Patch] `BuildLegs` doesn't resolve `FinishPoiId` against the actual placeable stops — a Finish pointing at a non-placeable/absent POI yields an open path with no closing leg and an unrendered Finish. Treat an unresolvable Finish as Roundtrip (close the loop) + add a unit test. [LucidCartographer/Components/Shared/Trip/TripViewModel.cs]
- [x] [Review][Patch] The stop-count `aria-live` region carries an `aria-label` that overrides its text content → inconsistent screen-reader announcement on a live region. Move the localized phrase into an `sr-only` child, keep the number visible + `aria-hidden`. [LucidCartographer/Components/Shared/Trip/TripStopList.razor]
- [x] [Review][Defer] Closing leg targets the Order-1 stop (`stops[0]`), not the designated `StartPoiId` [TripViewModel.cs BuildLegs] — deferred, latent until Story 1.7 adds Start/Finish designation (Phase-1 Start is always Order 1).
- [x] [Review][Defer] Placeable count dropping below 2 while Trip View is on hides the toggle but leaves `IsTripViewEnabled` true (no control to turn it off) [TripViewModel.cs RefreshAfterMembershipChangeAsync] — deferred, pre-existing Story 1.2 gate logic, not changed by 1.3.
- [x] [Review][Defer] `OnVmChanged` fire-and-forget async DB work on every notify + single-slot assignable `MembershipChanged` callback (no re-entrancy guard) [MapPage.razor] — deferred, pre-existing Story 1.2 wiring, mitigated by Blazor's single-threaded circuit.

## Dev Notes

### Dependencies (consume, do not re-specify)
- **Story 1.1** (ready-for-dev) supplies the schema this story reads: `PoiCollectionItem.OrderIndex`
  (int, **1-based**), `PoiCollection.StartPoiId` / `FinishPoiId` (nullable FKs), and the
  `RouteSegment` cache entity (unused here — leg geometry is computed on the fly as straight
  connectors in Phase 1, **not** persisted). [Source: epics.md#Story-1.1; architecture.md#D1]
- **Story 1.2** (ready-for-dev) supplies `TripViewModel` (sealed, Transient, `StateChanged`,
  `IAsyncDisposable`), `TripOrderingService`, the seeded contiguous 1..N Stop Order, the
  Trip-View on/off state (persisted `TripViewEnabled`), and the `primary` order badges in
  the list + on markers. **This story extends `TripViewModel`** with `OrderedLegs` /
  `OrderedStops` projections and adds the rendering — it does not redefine the toggle, the
  seed, or the badge. [Source: epics.md#Story-1.2]

### Phase-1 leg fidelity (critical)
The custom-LRM `IRouter` + road geometry is **Epic 2/4**. In **this** story, legs are
**straight connectors only**, and **all** of them render **dashed + muted** (the
line-solidity = geometric-fidelity rule: only **Measured** legs are solid, and nothing is
Measured in Phase 1). Do not call any routing engine; do not call `ITravelTimeProvider`
(it does not exist yet). [Source: epics.md#Story-1.3; architecture.md#D6/AR-7; DESIGN.md (line-solidity)]
The architecture's D6 names Leaflet Routing Machine, but the LRM `IRouter` is a later
phase — for Phase 1 the simplest faithful implementation is **direct `L.polyline`** draws
inside the existing `leafletInterop` module (the architecture explicitly keeps "the
`IRouter` seam thin so a later swap to a custom `L.polyline` interop layer requires no
data-layer change"). Do not introduce LRM in this story. [Source: architecture.md#D6 (lines 257-270)]

### Project patterns & constraints (must follow)
- **Layering:** Component(.razor) → ViewModel → Service → Data. `TripStopList.razor` /
  `MobileTripPanel.razor` hold markup + bindings only; all leg/stop projection logic lives
  in `TripViewModel`; all JS interop goes through `LeafletMapService`. Never let a
  component call `IJSRuntime` for trip drawing directly. [Source: project-context.md#Architecture-Layering]
- **ViewModel rules:** `TripViewModel` stays `sealed`, **Transient** (registered in
  `Configuration/ViewModelExtensions.cs` — Story 1.2 owns the registration), exposes
  `event Action? StateChanged` + private `Notify()`, state with `private set`. The
  component `@code` block is a ~12-line bridge (subscribe in `OnInitializedAsync`,
  `InvokeAsync(StateHasChanged)`, unsubscribe/dispose in `DisposeAsync`).
  [Source: project-context.md#Architecture-Layering; architecture.md#AR-12]
- **UI text:** all strings via `UiStrings.*` — no hardcoded text. Status regions use
  `aria-live`; badges/fields carry `aria-label`. [Source: project-context.md#UI-Conventions; NFR5]
- **Dual-surface:** desktop and mobile are **distinct render paths**
  (`Viewport.IsMobile` → `Mobile*` ). Trip UI lives in **new** `Components/Shared/Trip/`
  with a **desktop + `MobileTrip*` split** — implement **both**. Mobile is the on-the-road
  scenario, not a degraded view; honor ≥44px touch targets + safe-area insets; dark mode
  first-class. [Source: project-context.md#UI-Conventions; UX-DR12; architecture.md#AR-12]
- **Tokens:** Tailwind utilities with the `surface-*` / `on-surface-*` / `primary` /
  `on-surface-muted` palette. The muted leg color must derive from the token palette
  passed from .NET — do **not** hardcode a new hex inside `leafletInterop.js` (anti-pattern:
  "a second JS module duplicating leafletInterop polyline logic"). [Source: DESIGN.md#Color; architecture.md#Anti-patterns]
- **Build discipline:** `TreatWarningsAsErrors=true`, `Nullable=enable`; no group-B
  analyzer violations (MA0002, MA0015, MA0046, MA0047, MA0074, VSTHRD200); no
  `ConfigureAwait(false)` (the Blazor circuit needs the sync context). A warning is a
  build break. [Source: project-context.md#Build-Language-Discipline]
- **Comment codes:** tag every new trip rendering decision with a searchable `TRIP-*`
  code (e.g. `TRIP-LEG-01`, `TRIP-MAP-01..03`). [Source: project-context.md#Conventions-Agents-Miss; AR-11]
- **Units convention (for forward-compat, even though unused here):** durations in
  seconds, distances in meters, dwell/budget in minutes, `OrderIndex` 1-based — convert at
  the UI edge only. [Source: architecture.md#AR-11]

### Source tree — NEW / UPDATE
**NEW**
- `LucidCartographer/Components/Shared/Trip/TripStopList.razor` — desktop stop-list panel.
- `LucidCartographer/Components/Shared/Trip/MobileTripPanel.razor` — mobile bottom-panel/sheet stop list.
- `LucidCartographer.Tests/ViewModels/TripViewModelTests.cs` — leg-geometry unit tests (add to existing if Story 1.2 created it).
- `LucidCartographer.Tests/Components/TripStopListTests.cs` — bUnit row/placeholder tests.
- `LucidCartographer.Tests/Integration/TripRenderTests.cs` (+ a `MobileTestBase`-derived mobile variant) — both-surface flows.

**UPDATE**
- `LucidCartographer/Components/Shared/Trip/TripViewModel.cs` — add `OrderedLegs` /
  `OrderedStops` projections + redraw `StateChanged`. (Created by Story 1.2.)
- `LucidCartographer/Components/Shared/LeafletMap.razor` — add `DrawTripAsync` /
  `ClearTripAsync` thin guarded methods.
- `LucidCartographer/Services/IMapService.cs` + `Services/LeafletMapService.cs` — add trip
  draw/clear interop methods + `TripLegDto` / `TripStopDto` records.
- `LucidCartographer/wwwroot/js/leafletInterop.js` — add `drawTripLegs`, `drawStopNumbers`,
  `clearTripLegs`, trip layer state; reset trip state in `initMap`/`destroyMap`.
- `LucidCartographer/Components/Pages/MapPage.razor` — host `TripStopList` (desktop, beside
  map) + `MobileTripPanel` (mobile bottom panel); subscribe to `TripViewModel` and drive
  draw/clear after map init and on viewport flip.
- `LucidCartographer/Services/UiStrings.cs` — Trip View string constants (Task 7).

### UPDATE files — current behaviour to PRESERVE (do not regress — AC4)
- **`LeafletMap.razor`** — thin wrapper over `IMapService`; every public method guards on
  `IsInitialized`; `OnAfterRenderAsync` wires `MapService.OnMarkerClicked` /
  `OnBoundsChanged`, calls `InitMapAsync` + `EnableBoundsTrackingAsync`, and signals
  `_initTcs`. `DisposeAsync` flips `IsInitialized` first, then `DestroyMapAsync` (swallows
  `JSDisconnectedException` / `ObjectDisposedException` / `InvalidOperationException`).
  Add new methods in the same guarded style; **do not** alter init/dispose/marker-click.
- **`LeafletMapService.cs`** — scoped (per-circuit, not per-component); `InvokeJsVoidAsync`
  short-circuits on `_disposed` and swallows circuit-gone exceptions; `ShowCollectionAsync`
  filters null-coord POIs and passes a `MarkerDto[]`. Reuse `InvokeJsVoidAsync` for the new
  calls; reuse the null-coord filter; **do not** dispose the service from the component.
- **`leafletInterop.js`** — single IIFE; one global `window.leafletInterop`. `state` holds
  `layerGroups` (per-collection) + `markers` (per-POI) + `mobileMode`. `addCollectionMarkers`
  builds `.custom-marker` divIcons and **skips `bindPopup` when `state.mobileMode`**;
  marker click invokes `OnMarkerClickedJs`; `setLabelsVisible` (re)binds tooltips;
  `setMobileMode` closes popups on mobile; `initMap`/`destroyMap` reset all `state`. **Keep
  the trip layers/markers in their OWN state keys** so they never collide with these;
  reset them in `initMap`/`destroyMap`; do **not** change `addCollectionMarkers`'
  popup/tooltip/click behaviour (Story 1.4 owns selection sync). The `LucidCartographer.*`
  helpers (`downloadFile`, `scrollListToPoi`, `scrollMobileRowIntoView`) and the splitter
  initializers are unrelated — leave them.
- **`MapPage.razor`** — `@rendermode InteractiveServer`; branches on `Viewport.IsMobile`
  (mobile: `.m-app` shell, ~46% map over `.m-bottom-panel`, `MobileTabBar`; desktop:
  sidebar + map + `PoiTable` + detail pane). `OnAfterRenderAsync` re-wires whichever
  `LeafletMap` instance is realized (`_wiredMap` ref identity — the instance changes on
  viewport flip), pushes `setMobileMode`, inits the desktop splitters, and calls
  `Vm.OnMapInitializedAsync()`. The `PendingSearchMapUpdate` block shows the
  "draw-after-init" pattern to mirror. **Preserve** the existing desktop/mobile branches,
  the `_wiredMap` re-wire, `setMobileMode`, splitter init, and the mobile detail/drawer
  content-swap; add the trip panel + trip-draw **additively**.

### Incremental-redraw note (AC3 / NFR1)
"Incremental, not a full page reload" means: on a Stop-Order/Start/Finish change,
`TripViewModel` recomputes `OrderedLegs`/`OrderedStops` and raises `StateChanged`; the
page calls `DrawTripAsync`, which (JS side) removes only the prior trip-leg layer +
numbered markers and re-adds the new ones. No `NavigationManager` navigation, no
`initMap`, no teardown of unrelated collection layers. The existing `RefreshLayoutAsync`
(→ `invalidateSize`) is the only map-wide call and is **not** needed for a leg redraw.

### Testing summary
Three layers per project convention. **Unit:** `TripViewModel.OrderedLegs` arithmetic
(roundtrip N vs open N−1, closing leg, coordinate-less exclusion, `IsMeasured==false`).
**Component (bUnit):** `TripStopList` / `MobileTripPanel` render rows, badges, and the two
em-dash placeholders with `aria-label`s, all via `UiStrings`. **Integration
(`IntegrationTestBase` + Playwright):** desktop draws the right leg count + numbered
markers, legs are dashed (`stroke-dasharray`), panel sits beside the map, toggle-off
clears trip overlays + restores plain markers, reorder redraws without navigation;
**Mobile (`MobileTestBase`):** trip list in the bottom panel, legs on the ~46% map,
popups still suppressed. `InternalsVisibleTo("LucidCartographer.Tests")` is set — test VM
internals directly. [Source: project-context.md#Testing-Rules]

### Project Structure Notes
New files land under the prescribed `Components/Shared/Trip/` slice (`TripStopList.razor`
desktop + `MobileTripPanel.razor` mobile split), matching architecture.md's NEW tree
(lines 483-491). No files moved or renamed; the change is purely additive. The architecture
tree names additional Trip components (`TripPanel`, `StopListRow`, `ItineraryTimeline`,
`FidelityBadge`, etc.) — those belong to Story 1.2 (panel host) and Epic 2 (timeline,
fidelity). This story adds only `TripStopList`/`MobileTripPanel` and the leg/marker render
path; it must not pre-build the Epic-2 components. The JS extension stays **inside** the
existing `leafletInterop.js` IIFE (no second module — architecture anti-pattern). No
conflicts with the documented structure.

### References
- [Source: _bmad-output/planning-artifacts/epics.md#Story-1.3] — ACs (FR-5, UX-DR3, UX-DR12, UX-DR14).
- [Source: _bmad-output/planning-artifacts/epics.md#Epic-1] — Phase-1 straight connectors, dual-surface scope.
- [Source: _bmad-output/planning-artifacts/epics.md#Story-1.1] — schema (OrderIndex, StartPoiId/FinishPoiId, RouteSegment).
- [Source: _bmad-output/planning-artifacts/epics.md#Story-1.2] — TripViewModel, TripOrderingService, seed, badges (consumed).
- [Source: _bmad-output/planning-artifacts/architecture.md#D6/AR-7 (lines 257-270, 300)] — leg rendering, straight Phase 1, dashed non-Measured, thin IRouter seam → L.polyline.
- [Source: _bmad-output/planning-artifacts/architecture.md#Project-Structure (lines 433-503)] — Trip slice NEW/MOD tree.
- [Source: _bmad-output/planning-artifacts/architecture.md#Anti-patterns (lines 428-431)] — no second JS polyline module; 1-based OrderIndex.
- [Source: _bmad-output/planning-artifacts/ux-designs/.../DESIGN.md (lines 168-184)] — stop-order badge, stop-list row, line-solidity = geometric fidelity (only Measured solid; rest dashed+muted).
- [Source: _bmad-output/planning-artifacts/ux-designs/.../DESIGN.md (line 127)] — mobile ~46% map over bottom panel, safe-area insets.
- [Source: _bmad-output/project-context.md] — build discipline, layering, ViewModel rules, UI conventions, dual-surface, testing rules.
- [Source: LucidCartographer/Components/Shared/LeafletMap.razor] — wrapper methods + init/dispose to preserve.
- [Source: LucidCartographer/Services/LeafletMapService.cs] — `InvokeJsVoidAsync`, `MarkerDto`, null-coord filter.
- [Source: LucidCartographer/wwwroot/js/leafletInterop.js] — `state`, `addCollectionMarkers`, divIcon, `setMobileMode`, layer/marker state to keep separate.
- [Source: LucidCartographer/Components/Pages/MapPage.razor] — desktop/mobile branches, `_wiredMap` re-wire, `PendingSearchMapUpdate` draw-after-init pattern.
- [Source: LucidCartographer/Components/Shared/PoiTable.razor] — row-table precedent for the stop-list rows.
- [Source: LucidCartographer/Services/UiStrings.cs] — string-constant convention.

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Dev Story workflow)

### Debug Log References

- App + test builds green with `TreatWarningsAsErrors=true`, `Nullable=enable` —
  no group-B analyzer violations, no `ConfigureAwait(false)`.
- Full regression suite: **611/612 passing**. The single failure
  (`ScraperIntegrationTests.ScrapeProgress_ShowsIndicator`) is a **pre-existing
  flaky** Playwright strict-mode locator race (`text=Scraping` matches both the
  "Scraping..." button and the "Scraping Google Maps list..." status when both
  are momentarily present). It is unrelated to this story (no scraper code/text
  was touched) and **passes in isolation**. All 11 Trip tests added/extended here
  pass, plus the existing Story 1.2 Trip tests.

### Completion Notes List

Implemented the rendering slice of Epic 1 — connecting legs, numbered markers,
and the dual-surface stop-list panel — strictly on top of Story 1.1/1.2.

- **Leg geometry (Task 1):** Added immutable `TripLeg` / `TripStop` records
  (`Components/Shared/Trip/TripProjections.cs`) and `OrderedLegs` / `OrderedStops`
  projections on `TripViewModel`. A single DB read (`RefreshProjectionsAsync`)
  rebuilds `StopOrders` + `OrderedStops` + `OrderedLegs` consistently whenever the
  order, Start/Finish, or membership changes; all three clear when Trip View is
  off. Roundtrip (null/Start-equal Finish) ⇒ N legs incl. the closing leg back to
  Order 1; a distinct Finish ⇒ N−1 open legs. Coordinate-less stops are excluded
  (same null-coord filter as the ordering service) without renumbering. Every leg
  is `IsMeasured == false` in Phase 1 (TRIP-LEG-01/02).
- **Numbered markers (AC1):** reuse the **existing** Story-1.2 `setStopOrders` +
  `buildMarkerIcon` badge path (already wired via `PushStopOrdersAsync`); no
  duplicate `drawStopNumbers` was added — Task 2's new JS work is the leg layer
  only.
- **JS interop (Task 2):** added `state.tripLegLayer` + `drawTripLegs(legs)` /
  `clearTripLegs()` inside the existing `leafletInterop` IIFE (no second module).
  Incremental redraw replaces only the prior leg layer (TRIP-MAP-01..03). Legs are
  `interactive:false` so they never steal marker clicks (preserves AC4 selection).
- **Dashed + muted style (AC3):** the leg stroke colour comes from a token-driven
  CSS class `.trip-leg-line` (`stroke: var(--outline)`) in `base.css`, mirroring
  the existing `.stop-order-marker` precedent — keeping the token palette the
  single source and **no hex hardcoded in JS**. A reserved `.trip-leg-measured`
  (solid + `primary`) covers the Phase-2 Measured branch. *Design note:* the story
  text suggested passing the colour from .NET; I used the established CSS-token
  class instead because (a) it matches the in-repo badge precedent, (b) Leaflet's
  `color` option writes an SVG presentation attribute that a `var()` cannot
  resolve into, and (c) it keeps theming authoritative in CSS. Outcome (token-driven,
  no JS hex) is identical to the story's stated intent.
- **Service surface (Task 3):** `IMapService`/`LeafletMapService` gained
  `DrawTripLegsAsync(IReadOnlyList<TripLegDto>)` + `ClearTripAsync()` (routed through
  the existing `InvokeJsVoidAsync` guard) and a `TripLegDto` record mirroring
  `MarkerDto`. `LeafletMap.razor` exposes thin `IsInitialized`-guarded wrappers;
  init/dispose/marker-click untouched.
- **Panels (Tasks 4/5):** `TripStopList.razor` (desktop, beside the map) and
  `MobileTripPanel.razor` (mobile bottom-panel list body). Both are markup + a
  ~12-line VM bridge. Mobile reuses `StopOrderBadge` in the `.row/.avatar` idiom and
  is rendered **under the existing results header** so the Trip toggle stays
  reachable for turning Trip View off. All copy via `UiStrings`; dark mode via
  tokens only.
- **Wiring (Task 6):** `MapPage` hosts both panels additively and pushes legs via a
  new `PushTripLegsAsync` (de-duped by record value equality), combined with the
  badge push in `PushTripAsync`. Both dedup caches reset on the viewport-flip
  re-wire so the freshly-rebuilt JS map is re-drawn.
- **Strings (Task 7):** added the Trip-View stop-list `UiStrings` region
  (panel/list labels, count, empty-state, `Stop {0} of {1}` badge aria, dwell +
  timeline placeholders/aria).
- **Tests (Task 8):** unit (`TripViewModelTests` — N vs N−1 legs, closing leg,
  coordinate-less exclusion, name/coord projection, `IsMeasured==false`, clear on
  off), bUnit (`TripStopListTests` — desktop + mobile rows/badges/placeholders/
  empty-state), integration (desktop panel-beside-map + clear-on-off; mobile
  bottom-panel list). `StubMapService` extended to satisfy the new interface
  members. *Coverage note:* the leaflet stub means real polyline `stroke-dasharray`
  cannot be asserted cross-process at the integration layer — leg geometry is
  covered by unit tests and the dashed style by the `.trip-leg-line` CSS rule.

### File List

**NEW**
- `LucidCartographer/Components/Shared/Trip/TripProjections.cs`
- `LucidCartographer/Components/Shared/Trip/TripStopList.razor`
- `LucidCartographer/Components/Shared/Trip/MobileTripPanel.razor`
- `LucidCartographer.Tests/Components/Trip/TripStopListTests.cs`

**UPDATED**
- `LucidCartographer/Components/Shared/Trip/TripViewModel.cs`
- `LucidCartographer/Components/Shared/LeafletMap.razor`
- `LucidCartographer/Components/Pages/MapPage.razor`
- `LucidCartographer/Services/IMapService.cs`
- `LucidCartographer/Services/LeafletMapService.cs`
- `LucidCartographer/Services/UiStrings.cs`
- `LucidCartographer/wwwroot/js/leafletInterop.js`
- `LucidCartographer/wwwroot/css/base.css`
- `LucidCartographer.Tests/ViewModels/TripViewModelTests.cs`
- `LucidCartographer.Tests/Integration/TripViewIntegrationTests.cs`
- `LucidCartographer.Tests/Integration/MobileTripViewTests.cs`
- `LucidCartographer.Tests/Integration/StubMapService.cs`

## Change Log

| Date       | Change                                                                                  |
|------------|-----------------------------------------------------------------------------------------|
| 2026-06-12 | Story 1.3 implemented: ordered straight legs (dashed + muted, Roundtrip close), numbered markers (reuse 1.2 badge path), desktop + mobile stop-list panels with inert dwell/timeline placeholders, incremental leg redraw. 611/612 tests green (1 pre-existing flaky scraper test, passes in isolation). Status → review. |
| 2026-06-12 | Code review (3 adversarial layers) — 4 patches applied: real `--trip-leg-muted` token (was an undefined `--outline` → stranded hex / no dark-mode), removed dead `StubMapService.LastTripLegs`, hardened `BuildLegs` against an unresolvable Finish (+ unit test), fixed the stop-count `aria-live` region. 3 findings deferred, 8 dismissed (incl. a false-positive `marker._poiId`). 26/26 Trip tests green. Status → done. |
