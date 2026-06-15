# Story 3.4: Per-leg mode pill on the connector

Status: done

## Dev Agent Record

New `ITripOrderingService.SetOutgoingTravelModeAsync` (sole writer of `OutgoingTravelMode`, under
`SqliteWriteLock`, validates null|TravelMode.All, no-op unchanged, not via `SetOrderAsync`).
`TripViewModel.SetLegModeAsync` writes via it, refreshes, and signals compute ONLY for ground modes
(FR-21). New presentational `LegModePill` (set→primary; Any/Air→neutral outline "Any — set mode",
no error tone; menu Walk/Drive/Cycle/Any-Air with active checked → `SetLegModeAsync`; stopPropagation;
UiStrings labels). `LegConnector` renders the pill and gates the manual input on `Leg.Mode==AnyAir`.
Desktop `TravelModeSelector` removed (FR-23); mobile's left for the deferred mirror; VM TravelMode +
PoiCollection.TravelMode retained (RD1a dead-column fallback). No ctor dep.

Adversarial review: 0 CRIT / 0 HIGH / 0 MED / 2 LOW → SHIP. Sole-writer/lock, ground-only compute
trigger (Any/Air never gets a cache row so it stays "—"), FR-23, pill neutrality/a11y, and faithful
(strengthened) test re-expression all confirmed. LOWs (menu lacks outside-click/Escape close;
redundant aria-label on menu items) accepted — not AC-required. 874 fast + 20 Trip integration + 54
mobile green.

## File List

- LucidCartographer/Services/Trip/ITripOrderingService.cs (MOD — SetOutgoingTravelModeAsync)
- LucidCartographer/Services/Trip/TripOrderingService.cs (MOD — sole-writer impl)
- LucidCartographer/Components/Shared/Trip/TripViewModel.cs (MOD — SetLegModeAsync)
- LucidCartographer/Components/Shared/Trip/LegModePill.razor (NEW)
- LucidCartographer/Components/Shared/Trip/LegConnector.razor (MOD — pill + per-leg manual gate)
- LucidCartographer/Components/Shared/Trip/TripStopList.razor (MOD — removed desktop selector)
- LucidCartographer/Services/UiStrings.cs (MOD — pill strings)
- LucidCartographer.Tests/Components/Trip/LegModePillTests.cs (NEW)
- LucidCartographer.Tests/ViewModels/TripViewModelTravelModeTests.cs (MOD)
- LucidCartographer.Tests/Components/Trip/TripStopListTests.cs (MOD)

## Story

As a trip planner, I want a mode control on each leg's connector instead of one trip-wide selector,
so that I can set Walk / Drive / Cycle / Any-Air for each movement individually.

## Acceptance Criteria

1. **Given** a leg connector, **When** it renders, **Then** it shows a `LegModePill` displaying the leg's mode ("Drive") when set, or "Any — set mode" as a neutral outline pill (not an error colour) when undefined (FR-19, UX-DR3/DR11).
2. **Given** I click the mode pill, **When** the menu opens, **Then** it offers Walk / Drive / Cycle / Any-Air with the active mode checked; choosing a ground mode triggers compute for that leg, choosing Any/Air leaves it manual-only (FR-19, FR-21).
3. **Given** per-leg modes replace the trip-wide mode, **When** the trip panel renders, **Then** the trip-level mode selector is removed entirely (no dead duplicate) (FR-23); **And** the pill is presentational — it raises a VM command and never mutates state or calls services directly (NFR1); all labels come from `UiStrings` (NFR6).

## Architecture & Code Context (RD2/RD6 spine, FR-19/21/23, UX-DR3)

### New write-path for a single leg's mode (sole-writer discipline)

`OutgoingTravelMode` is mutated only inside `TripOrderingService` (the order + mode-reset writer,
TRIP-LEGMODE-01). Add a method to set ONE leg's mode there:
- `ITripOrderingService.SetOutgoingTravelModeAsync(int collectionId, int fromPoiId, string? mode, CancellationToken ct)`
  — validates `mode` is null or one of `TravelMode.All`; writes `PoiCollectionItem.OutgoingTravelMode`
  for the From-stop under `SqliteWriteLock`; no-op if unchanged. (This is also what the MCP
  `set_leg_travel_mode` tool will call in Story 3.6.) Do NOT route through `SetOrderAsync` (no order
  change); a small dedicated write under the same lock is correct.
- `TripViewModel.SetLegModeAsync(int fromPoiId, string mode)`: calls
  `ordering.SetOutgoingTravelModeAsync(...)`, then refreshes projections; if `mode` is a ground mode
  (Walk/Drive/Cycle), signal the background compute (`TravelTimeTrigger`) so the leg's time computes
  (mirror how the old `SetTravelModeAsync` triggered compute); Any/Air leaves it manual-only (no
  compute). Results surface via `StateChanged`. (Setting a mode does NOT change order, so no reset
  rule applies.)

### LegModePill component + connector

- **New `LucidCartographer/Components/Shared/Trip/LegModePill.razor`** — presentational
  (`[Parameter] TripLeg Leg`, `[Parameter] TripViewModel Vm`); rounded-full pill, `text-xs`
  (UX-DR3):
  - Set state (leg.Mode ∈ {Walk,Drive,Cycle}, or a non-Any mode): `{colors.primary}/10` fill +
    `{colors.primary}` text + a mode glyph + the mode label.
  - Undefined / Any (leg.Mode == AnyAir, incl. null normalized): OUTLINE ONLY, label
    "Any — set mode" + a neutral help glyph — NOT an error colour (UX-DR11).
  - Click opens a menu (a small Material-style list / popover) of the four modes
    Walk / Drive / Cycle / Any-Air with the ACTIVE one checked; selecting calls
    `Vm.SetLegModeAsync(Leg.FromPoiId, chosenMode)`. Use `stopPropagation` so the pill/menu never
    selects/reorders the row. All labels from `UiStrings` (`Trip*`-prefixed: mode names, "Any — set
    mode", menu aria). A `title` at parity for discoverability (FR-17 pattern).
- **`LegConnector.razor`**: render `<LegModePill Leg="Leg" Vm="Vm" />` on the connector line.
  Change the manual-minutes input gate from the trip-wide `Vm.TravelMode == AnyAir` to the LEG's own
  mode: `Leg.Mode == TravelMode.AnyAir` (per-leg). (Story 3.5 generalizes manual edit to any leg;
  here just fix the gate to the leg's mode so it's correct once the trip-wide mode is gone.)

### Remove the trip-wide selector (FR-23)

- Remove `<TravelModeSelector Vm="Vm" />` from the DESKTOP `TripStopList.razor` (it's replaced by
  per-leg pills). The trip-wide selector becomes a dead duplicate — remove it (no dead control).
- **Mobile is the deferred mirror phase:** leave `MobileTripPanel.razor`'s `TravelModeSelector` in
  place for now (its per-leg pill is deferred). It is inert (post-3.2 it no longer drives legs) but
  removing/replacing mobile controls is the mirror phase — out of scope here. (Note it; don't break
  mobile tests.) Keep `TripViewModel.TravelMode`/`SetTravelModeAsync` (still used by mobile's
  selector). `PoiCollection.TravelMode` stays as the RD1a dead-column fallback — do NOT drop it.
- The `TravelModeSelector.razor` component file may remain (still used by mobile); just remove its
  desktop usage.

## Constraints (NFRs)

- NFR1 — pill/connector are presentational; the mode-set logic is `TripViewModel` → `TripOrderingService`.
- Sole-writer — `OutgoingTravelMode` mutated only in `TripOrderingService` (new method) under `SqliteWriteLock`.
- NFR6 — all copy via `UiStrings`; Tailwind `primary`/`surface-*`/`on-surface-*` tokens; neutral
  (not error) for the undefined pill.
- NFR8/NFR10 — if `SetLegModeAsync` needs no new ctor dep (it uses existing `ordering` +
  `travelTimeTrigger`), the `AddTripServices` pair is untouched; run the Trip integration filter
  (VM/ordering change). If a dep is added, both overloads.

## Testing

- VM unit: `SetLegModeAsync(from, ground)` writes the From-stop's `OutgoingTravelMode` and triggers
  compute; `SetLegModeAsync(from, AnyAir)` sets Any/Air and does NOT trigger compute; the leg's
  projected `Mode` reflects it. `SetOutgoingTravelModeAsync` rejects invalid modes, is the sole
  writer, no-ops when unchanged.
- bUnit: `LegModePill` renders the mode label when set; "Any — set mode" neutral outline (no error
  colour) when AnyAir; the menu offers the four modes with the active checked; selecting raises
  `SetLegModeAsync`. `TripStopList` no longer renders `TravelModeSelector` (FR-23). Connector manual
  input gates on the LEG's mode.
- Trip integration filter green; mobile trip tests green (mobile selector untouched).

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`
- Mobile: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Mobile"`

## Dev Notes

Story 3.5 generalizes manual time edit + reset to any leg (on top of this pill). 3.6 exposes the
same `SetOutgoingTravelModeAsync` via the MCP `set_leg_travel_mode` tool. Keep the new sole-writer
method reusable by both.

## Dev Agent Record

(to be filled by dev)

## File List

(to be filled by dev)
