# Story 1.3: Inter-row leg connector

Status: done

## Story

As a trip planner,
I want each leg's travel time shown on the boundary between the two stops it connects,
So that travel information reads as "between these stops" rather than crowding a row column.

## Acceptance Criteria

1. **Given** the trip stop table renders consecutive stops, **When** a leg between two stops exists, **Then** a compact single-line `LegConnector` appears on the shared edge between the two rows, inset to align under the name column (UX-DR2); **And** it shows the `↓` glyph, travel time, `·` distance, and the fidelity badge — and a reset (↺) affordance hidden at rest, revealed on hover/focus; **And** per-leg travel time/distance is **not** rendered as a stop-row column (FR-3).
2. **Given** the closing leg of the trip (roundtrip return, or the leg to a designated Finish), **When** the table renders, **Then** the closing connector renders after the last row, ahead of the finish/return footer.
3. **Given** a leg has no computed time yet (uncomputed), **When** the connector renders, **Then** the travel time reads "—" and the connector is styled neutrally (not as an error, UX-DR11).
4. **Given** the connector is a presentational component under `Components/Shared/Trip/`, **When** it renders, **Then** it is driven by the VM's leg projection and raises no service calls or state mutations itself (NFR1); **And** the mode pill and generalized edit/reset are deferred to Epic 3 (this story builds the connector shell with time/distance/fidelity/reset).

## Architecture & Code Context (RD9, FR-3, UX-DR2/DR11)

**Baseline:** Story 1.2 already moved per-leg travel time/distance/`FidelityBadge`/manual-minutes
OUT of the stop row into an interim inline `<div class="trip-leg-connector">` rendered between
consecutive rows inside the `<ul>` in `LucidCartographer/Components/Shared/Trip/TripStopList.razor`
(look for `trip-leg-connector`, ~after each row, driven by `LegFrom(row.PoiId)`). This story
EXTRACTS that interim line into a proper presentational component and polishes it.

**Required:**

1. **Create `LucidCartographer/Components/Shared/Trip/LegConnector.razor`** — a small,
   presentational component:
   - **Parameter:** the `TripLeg` to render (`[Parameter, EditorRequired] public TripLeg Leg`).
     To keep it presentational while still letting the user clear a manual override, also pass the
     VM (`[Parameter, EditorRequired] public TripViewModel Vm`) OR explicit `EventCallback`s for
     manual edit/clear — your choice; calling `Vm` command methods (`SetManualLegTimeAsync`,
     `ClearManualLegTimeAsync`) counts as "raising a VM command" and is consistent with how
     `TripStopList` already uses `Vm`. The component must NOT touch services/DB/DbContext directly
     and must NOT mutate state itself (NFR1).
   - **Renders, single line:** a `↓` glyph (decorative, `aria-hidden`), the travel time
     (`TravelTimeFormatting.Duration(Leg.DurationSeconds)` — keep the existing formatter; the
     "min" unit change is Epic 2 Story 2.2, do NOT change it here), a `·` separator, the distance
     (`TravelTimeFormatting.Distance(Leg.DistanceMeters)`), and `<FidelityBadge Fidelity="@Leg.Fidelity" />`.
   - **Reset (↺) affordance:** a real focusable `<button>` with an `aria-label` (via `UiStrings`,
     new `Trip*`-prefixed key), **hidden at rest, revealed on hover/focus** (Tailwind
     `opacity-0 group-hover:opacity-100 focus:opacity-100` / `focus-within` pattern; make the
     connector a `group`). **Render the reset only when the leg actually has something to reset —
     i.e. `Leg.Fidelity == Fidelity.Manual`** (an existing manual override). Clicking it calls
     `Vm.ClearManualLegTimeAsync(Leg.FromPoiId, Leg.ToPoiId)` (this path already exists). For
     non-manual legs do NOT render a dead reset button. (Story 3.5 generalizes manual edit + reset
     to any leg; this story ships the shell + the manual-leg reset that already works.)
   - **Manual-minutes input (Any/Air):** keep the existing Any/Air manual-minutes input behavior
     that the interim connector carried (under `TravelMode == TravelMode.AnyAir`), moved into this
     component. Reuse `Vm.SetManualLegTimeAsync` / `Vm.ClearManualLegTimeAsync` and
     `stopPropagation` on the input. (This stays until Epic 3 reshapes per-leg mode.)
   - **Uncomputed leg:** when `Leg.DurationSeconds is null`, time reads "—"
     (`UiStrings.TripLegTimeUnknown`) and styling is neutral/muted — never an error color (UX-DR11).
   - **Inset:** left-pad the connector so its content aligns under the **name column** (the 3rd
     grid column — after the reorder gutter + badge). UX-DR2 marks this an [ASSUMPTION]; a left
     padding/indent approximating the name-column offset is fine. Token-styled: `surface-container`
     background, hairline divider feel; `text-xs`.
2. **Use it in `TripStopList.razor`:** replace the interim `trip-leg-connector` `<div>` with
   `<LegConnector Leg="leg" Vm="Vm" />`. Render it on the shared edge between consecutive rows
   (driven by `LegFrom(row.PoiId)`), and ensure the **closing leg** (the leg whose `FromPoiId` is
   the last stop — roundtrip return or leg to Finish) renders AFTER the last row and BEFORE the
   finish/return footer (AC2). Fix the Story-1.2 LOW: do not emit a bare `<div>` as a direct child
   of `<ul>` — wrap the connector in an `<li aria-hidden="true">` (or restructure) so list nesting
   is valid; keep the existing tests that count `li[data-poi-id]` rows passing (they filter on
   `data-poi-id`, which the connector `<li>` won't have).
3. **Do NOT** add the per-leg mode pill (Story 3.4) or generalize edit/reset to all legs (Story
   3.5). Connector shell only.

## Constraints (NFRs)

- NFR1 — presentational component; raises VM commands only; no service/DB calls, no self-mutation.
- NFR6 — Tailwind `surface-*`/`on-surface-*`/`primary` tokens only; all text via `UiStrings`
  (new keys `Trip*`-prefixed); no hardcoded literals; `TreatWarningsAsErrors`.
- NFR7 — reset (↺) is a real focusable button with an `aria-label`; keyboard reachable.
- NFR9 — no regression to leg time/distance/fidelity display, manual-entry, selection, reorder.

## Testing

- bUnit (`LucidCartographer.Tests/Components/Trip/`): `LegConnector` renders ↓ + time + distance +
  fidelity badge; uncomputed leg → "—" + neutral (no error color); reset (↺) present only for a
  Manual leg and absent otherwise; reset click invokes `ClearManualLegTimeAsync`. In
  `TripStopListTests`: connector renders between consecutive rows; closing connector renders after
  the last row; per-leg time/distance still NOT in a stop-row column; valid list nesting
  (connector is not a `data-poi-id` row).
- Trip integration filter green; mobile trip tests green (MobileTripPanel not in scope — do not
  change it).

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast tests: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`

## Dev Notes

Connector placement (left-indent under name) is a UX [ASSUMPTION] (OQ-A) — approximate is fine.
Epic 3 adds the mode pill (3.4) and generalizes manual edit + reset to any leg (3.5) on top of
this shell.

## Dev Agent Record

New presentational `LegConnector.razor` (params: `Leg`, `Vm`) renders a single inset line:
`↓` · time (`TravelTimeFormatting.Duration`) · `·` · distance · `FidelityBadge`. Uncomputed →
"—" neutral (no error color). Reset (↺) is a real focusable button, hidden-at-rest/revealed on
hover/focus, rendered ONLY for `Fidelity.Manual` legs → `Vm.ClearManualLegTimeAsync`. Any/Air
manual-minutes input moved into the component (stopPropagation). `TripStopList` renders
`<LegConnector>` between consecutive rows; closing leg (roundtrip return / leg-to-Finish) renders
after the last row, before the finish/return footer. Connector wrapped in a plain `<li>` (no
`data-poi-id`) — fixes the Story-1.2 `<ul> > <div>` nesting wart. NFR1: presentational, raises VM
commands only, no service/DB calls, VM ctor unchanged. MobileTripPanel untouched.

Adversarial review: 1 HIGH / 0 MED / 2 LOW. **HIGH fixed** — the connector `<li>` was
`aria-hidden="true"`, which trapped focusable controls + meaningful info outside the a11y tree
(NFR7 violation, traced to the story's own conflicting instruction). Removed `aria-hidden` from
the wrapper `<li>` and the inert `aria-hidden="false"` from the connector div; the four tests that
locked in the defect now assert the connector `<li>` is identified by absence of `data-poi-id`
(valid nesting) and is NOT aria-hidden. LOW#2 (fixed `pl-10` inset vs grid-derived) accepted per
UX [ASSUMPTION]. Build clean; 786 fast + 20 Trip integration green.

## File List

- LucidCartographer/Components/Shared/Trip/LegConnector.razor (NEW)
- LucidCartographer/Components/Shared/Trip/TripStopList.razor (MOD — use LegConnector; remove interim line + orphaned helpers)
- LucidCartographer/Services/UiStrings.cs (MOD — TripLegResetManualAria)
- LucidCartographer.Tests/Components/Trip/LegConnectorTests.cs (NEW)
- LucidCartographer.Tests/Components/Trip/TripStopListTests.cs (MOD — connector placement + a11y assertions)
