# Story 1.2: Wide trip-scoped table with trip-only columns

Status: done

## Dev Agent Record

TripStopList reshaped into a wide CSS-grid trip table (one shared `GridTemplate` per `<li>`,
7 columns) — kept `<li data-poi-id>` rows (no `<table>`) so the Story 1.1 takeover test holds.
Columns: reorder gutter (drag + ▲▼) · Stop# badge (Start/Finish ring+glyph) · Name (full,
break-words, address sub-line, enrichment icon mirroring PoiTable) · Dwell · Arrival · ○/⚑ ·
Actions (Focus + Open-in-Google-Maps only). Per-leg time/distance/FidelityBadge/manual-minutes
moved OUT of the row into an interim single-line connector between rows (Story 1.3 makes it the
real `LegConnector`). `TripStopRow` projection extended (Address/IsEnriched/
EnrichmentNeedsManualUrl/GoogleMapsUrl) — resolved at the VM edge via `PoiUrlHelper`, single
query, no N+1, VM ctor unchanged (integration host safe). Focus-on-map via new
`EventCallback<int> OnFocusClicked` wired in MapPage to `Vm.HandleFocusPoiAsync` (parity with
PoiTable). New `Trip*` UiStrings; no hardcoded text. MobileTripPanel untouched (additive
projection).

Adversarial review: 0 CRIT / 0 HIGH / 0 MED / 2 LOW → SHIP. LOW#1 (interim connector is a
`<div>` child of `<ul>` — invalid nesting) accepted, removed by Story 1.3's `LegConnector`.
LOW#2 harmless. Build clean; 777 fast + 20 Trip integration green.

## File List

- LucidCartographer/Components/Shared/Trip/TripStopList.razor (MOD)
- LucidCartographer/Components/Shared/Trip/TripViewModel.cs (MOD — projection only)
- LucidCartographer/Components/Shared/Trip/TripProjections.cs (MOD — TripStopRow fields)
- LucidCartographer/Components/Pages/MapPage.razor (MOD — OnFocusClicked wiring)
- LucidCartographer/Services/UiStrings.cs (MOD — Trip* strings)
- LucidCartographer.Tests/Components/Trip/TripStopListTests.cs (MOD — 8 new tests)

## Story

As a trip planner on desktop,
I want the trip list shown as a wide table with full POI names and only trip-relevant
columns,
So that I can read each stop clearly without the collection-management clutter that no
longer applies in a trip.

## Acceptance Criteria

1. **Given** Trip View is on, **When** the trip stop table renders, **Then** each row shows, left→right: a reorder gutter (drag handle **and** ▲▼ move buttons), the Stop # badge (with Start/Finish glyph + ring), the **full POI name** with address sub-line and enrichment-state icon, a Dwell input, an Arrival cell (relative offset always), Start/Finish (○/⚑) controls, and Actions (**Focus on map** + **Open in Google Maps** only); **And** the POI name is not truncated to an unreadable width (UX-DR1).
2. **Given** the trip stop table renders, **When** I compare it to the plain PoiTable, **Then** the Select checkbox, Coordinates, Collection chips, Added date, and per-row Move/Copy/Delete actions are absent; **And** the batch-action toolbar is absent; **And** the list header carries only trip-relevant controls (stop count, TSP-Sort, Recompute, total travel time, start, time limit); Fit All / Labels stay on the map.
3. **Given** a row is clicked (not on a dwell/action control), **When** the click is handled, **Then** the stop is selected (list→map), and dwell/action clicks `stopPropagation` so they do not also select the row (UX-DR1).
4. **Given** varied stop-row states (placeable vs. unplaceable, Start/Finish pinned, dwell set vs. empty, long vs. short names), **When** the table renders any combination, **Then** the columns stay orderly and aligned — not a ragged cluster (FR-11, FR-12); **And** a selected row shows the `{colors.primary}/10` tint + inset `{colors.primary}` ring (UX-DR1).

## Architecture & Code Context (RD8, FR-2/11/12, UX-DR1)

**File:** `LucidCartographer/Components/Shared/Trip/TripStopList.razor` (now owns the WIDE
desktop region after Story 1.1 — the old `w-64` width constraints are gone). It currently
renders a cramped vertical `<ul>` (each `<li>` is a row) sized for a 256px column, with
per-leg travel time/distance/fidelity/manual-input crammed INSIDE each stop row.

**Required reshape — a wide, full-width, aligned trip-scoped table:**

1. **Use an aligned column structure** (CSS grid, e.g. `grid` with named/sized template
   columns shared by every row, OR a real `<table>`). The point of FR-11/12 is that columns
   line up across all row states — pick grid or table so the reorder gutter, badge, name,
   dwell, arrival, start/finish, actions occupy the SAME columns on every row (placeable,
   unplaceable, pinned, long/short name). Avoid per-row flex clusters that drift.
2. **Columns L→R (FR-2):**
   - **Reorder gutter:** drag handle (`⠿`, existing) **and** the ▲▼ move buttons (existing
     `MoveStopUp/Down`). Keep their existing aria-labels/disabled/pin logic and
     `stopPropagation`.
   - **Stop # badge:** existing primary-filled numbered circle with the Start/Finish ring +
     glyph (`trip_origin`/`sports_score`) and `BadgeAria`. Unplaceable rows keep the muted
     "—" marker in the badge slot.
   - **Name:** the **full POI name** (do NOT truncate to a tiny width — it can wrap or use
     the wide space; the whole point of the takeover), with an **address sub-line**
     (`Poi.Address`, muted `text-xs`, only when present) and an **enrichment-state icon**.
     Mirror PoiTable's enrichment icon mapping exactly (`PoiTable.razor:100-107`):
     `EnrichmentNeedsManualUrl` → `error`/red; `IsEnriched` → `location_on`/muted;
     else → `hourglass_empty`/amber, with the same `title` text.
   - **Dwell:** existing dwell number input (keep `type="number"` here — the HH:MM picker is
     Story 4.4), `stopPropagation`, existing aria-label.
   - **Arrival:** the per-stop honest arrival (existing `ArrivalFor`/`ArrivalText`/
     `ArrivalCompactText` from the timeline). This STAYS a row column. In the wide region it
     can show the fuller `ArrivalText` (no longer starved for width).
   - **Start/Finish controls:** existing ○ (`trip_origin`) / ⚑ (`sports_score`) buttons with
     `aria-pressed`, disabled-on-opposite-pin, `stopPropagation`.
   - **Actions:** **Focus on map** and **Open in Google Maps** ONLY (mirror PoiTable:133-148).
     - *Open in Google Maps:* `<a href="@PoiUrlHelper.GetGoogleMapsUrl(poi)" target="_blank"
       rel="noopener" @onclick:stopPropagation>` with `open_in_new` icon, title "Open in
       Google Maps".
     - *Focus on map:* `my_location` icon button, title "Focus on map", `stopPropagation`.
       Wire it to focus/pan the map. **Recommended:** add an `EventCallback<int> OnFocusClicked`
       parameter to `TripStopList` and wire it in `MapPage.razor` to the same map-focus handler
       PoiTable uses (`Vm.HandleFocusPoiAsync` on the *MapPageViewModel*), giving parity with the
       plain table. (If wiring the MapPageViewModel handler is awkward, falling back to
       `Vm.SelectStop(poiId, TripSelectionSource.List)` — which already pans via MapPage's
       selection sync — is acceptable; document the choice.)
3. **Move per-leg travel time / distance / fidelity / manual-minutes OUT of the stop row
   (FR-3).** Per-leg travel info is NOT a row column. For THIS story, render the departing
   leg's time + distance + `FidelityBadge` (and, under Any/Air, the existing manual-minutes
   input) as a **simple single-line element BETWEEN consecutive rows** (an interim
   proto-connector). **Story 1.3 replaces this interim line with the proper `LegConnector`
   component** (inset under the name column, `↓` glyph, reset affordance, closing leg after the
   last row). Keep the existing `LegFrom`, manual-minutes handlers, and "—"/computing behavior
   — just relocate them out of the row's columns. The Arrival cell stays in the row.
4. **Header:** already trip-only (stop count, TravelModeSelector, total, TSP-Sort, Recompute,
   start time, budget). Leave it; just confirm AC2 (no batch toolbar, no Fit All/Labels — those
   are on the map in MapPage). The `TravelModeSelector` stays for now (removed in Epic 3 Story 3.4).
5. **Selection:** the row stays `role="button" tabindex="0"`, click/Enter/Space → `SelectStop`;
   selected row keeps `bg-primary/10 ring-1 ring-inset ring-primary` + `aria-current`. Dwell,
   actions, move, start/finish controls all `stopPropagation` (already do).

**VM projection change (data only, NFR1 — no new ctor dependency):** `TripStopRow` currently
carries only PoiId/Name/IsPlaceable/DisplayOrder/DwellMinutes. Extend it (and
`TripViewModel.ReadStopsAndRowsAsync`) to also carry the fields the Name column + Actions need:
`Address` (string?), enrichment flags (`IsEnriched`, `EnrichmentNeedsManualUrl`), and either the
`GoogleMapsUrl`/coordinates needed by `PoiUrlHelper.GetGoogleMapsUrl(...)` (read these from the
`Poi` in the existing membership read). This is pure projection — do NOT add a service/VM
constructor dependency. If you nonetheless must, register in BOTH `AddTripServices()` overloads
and run the Trip integration filter.

## Constraints (NFRs)

- NFR1 strict layering — markup + thin projection only; no arithmetic/ordering/timeline logic in
  the component.
- NFR6 — Tailwind `surface-*`/`on-surface-*`/`primary` tokens only; all text via `UiStrings`
  (reuse existing keys; add new ones with the `Trip*` prefix only if a genuinely new string is
  needed, e.g. action labels — prefer reusing PoiTable's intent but route through `UiStrings`,
  do NOT hardcode literals).
- NFR7 a11y — keep aria-labels, `aria-pressed`, `aria-current`, the live regions, keyboard
  reorder, and list↔map sync intact.
- NFR9 — no regression to stop-order badges, selection sync, dwell/start/finish behavior.

## Testing

- bUnit (`LucidCartographer.Tests/Components/Trip/`): assert the wide table renders the FR-2
  columns; full name present (not truncated away); address sub-line + enrichment icon shown;
  Actions = Focus + Open-in-Google-Maps only (no move/copy/delete/select/coords/collection/added-
  date); per-leg time/distance is NOT inside a stop row; row click selects; dwell/action clicks
  don't select. Cover varied states (unplaceable, pinned Start/Finish, long name) for alignment.
- Run the Trip integration filter (projection change touches VM):
  `dotnet test ... --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`
- Keep mobile trip tests green (do NOT change `MobileTripPanel` — shared VM projection additions
  are additive; mobile keeps reading what it read).

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast tests: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`

## Dev Notes

The mobile `MobileTripPanel` is NOT in scope (deferred mirror phase) — but it shares the VM, so
keep VM/projection changes additive and mobile tests green. Story 1.3 will extract the interim
inter-row leg line into the proper `LegConnector` component.

## Dev Agent Record (detailed)

- Reshaped `TripStopList.razor` into a wide, column-aligned trip-scoped table using a
  single shared CSS-grid template (`GridTemplate` constant) applied inline on every
  `<li>` row so the seven columns line up across all states (placeable, unplaceable,
  pinned Start/Finish, dwell set/empty, long/short names). Did NOT use a real `<table>`
  because `TripDesktopTakeoverTests` asserts no `<table>` renders when Trip View is on;
  rows remain `<li data-poi-id>`.
- Columns L→R: reorder gutter (⠿ drag handle + ▲▼) · Stop # badge (Start/Finish ring +
  glyph) · Name (full name, no truncate; address sub-line when present; enrichment icon
  mirroring PoiTable) · Dwell input · Arrival cell (compact value carries the honest "~"
  marker; full `ArrivalText` in title/aria) · Start/Finish (○/⚑) · Actions (Focus on map
  + Open in Google Maps only).
- Per-leg travel time/distance/FidelityBadge/manual-minutes moved OUT of the row into an
  interim single-line `<div class="trip-leg-connector">` rendered BETWEEN consecutive
  rows (a `<div>`, not an `<li>`, so the one-`<li>`-per-stop count holds). Arrival stays
  a row column. "—"/computing/manual behavior preserved.
- VM projection (data only, no new ctor dep): extended `TripStopRow` with `Address`,
  `IsEnriched`, `EnrichmentNeedsManualUrl`, `GoogleMapsUrl`; `ReadStopsAndRowsAsync` now
  reads those Poi fields and resolves the maps URL via `PoiUrlHelper.GetGoogleMapsUrl` at
  the projection edge for both placeable and unplaceable rows.
- Focus-on-map wired via a new `EventCallback<int> OnFocusClicked` parameter, bound in
  `MapPage.razor` to `Vm.HandleFocusPoiAsync` (same handler PoiTable uses → parity). When
  unwired, it falls back to `Vm.SelectStop(..., List)`.
- New UiStrings (Trip*-prefixed): `TripFocusOnMap(/Aria)`, `TripOpenInGoogleMaps(/Aria)`,
  `TripEnrichmentFailed/Enriched/Waiting`.

## File List

- `LucidCartographer/Components/Shared/Trip/TripStopList.razor` (reshaped; OnFocusClicked param)
- `LucidCartographer/Components/Shared/Trip/TripProjections.cs` (TripStopRow fields)
- `LucidCartographer/Components/Shared/Trip/TripViewModel.cs` (projection read + maps URL)
- `LucidCartographer/Components/Pages/MapPage.razor` (OnFocusClicked wiring)
- `LucidCartographer/Services/UiStrings.cs` (new Trip* action/icon strings)
- `LucidCartographer.Tests/Components/Trip/TripStopListTests.cs` (8 new Story 1.2 tests)
