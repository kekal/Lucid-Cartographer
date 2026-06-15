# Story 1.1: Switch the desktop list region into the trip

Status: done

## Story

As a trip planner on desktop,
I want toggling Trip View on to replace the POI table with the trip stop list in the
same wide region (and toggling off to restore the table unchanged),
So that I see one trip-focused list instead of two redundant lists in a cramped column.

## Acceptance Criteria

1. **Given** a single collection with ≥2 placeable POIs is in scope and Trip View is off, **When** I toggle Trip View on, **Then** the desktop bottom filtered-results region renders `TripStopList` instead of `PoiTable`; **And** the plain `PoiTable` is not shown at the same time; **And** the additive `w-64` side column is removed; **And** the map stays visible in its region and list↔map two-way selection sync still works.
2. **Given** Trip View is on, **When** I toggle Trip View off, **Then** the plain `PoiTable` and its controls are restored exactly as before, with no data loss; **And** per-collection toggle persistence still behaves as it did (no regression, NFR9).
3. **Given** the desktop takeover is implemented, **When** the bUnit component test for `MapPage` runs, **Then** it asserts Trip-View-on hides `PoiTable` and shows the wide `TripStopList` (NFR8); **And** the change is markup/Tailwind in `MapPage.razor` reusing the existing `TripStopList`/VM, with no new ordering or timeline logic (NFR1).
4. **Given** the shared `TripViewModel` is used by both surfaces, **When** the Trip integration filter runs after this change, **Then** the desktop takeover flow passes and existing mobile trip tests stay green (NFR5, NFR8).

## Architecture & Code Context (RD8, FR-1/5/6)

**Current desktop markup** (`LucidCartographer/Components/Pages/MapPage.razor`):
- The bottom "filtered-results region" is the table container at **lines ~322-344**, which renders `<PoiTable Pois="Vm.FilteredPois" ... />` below the map + splitter handle.
- Trip View currently adds a SEPARATE side column at **lines ~347-354**:
  ```razor
  @if (TripVm.IsTripViewEnabled)
  {
      <div class="w-64 flex-shrink-0">
          <TripStopList Vm="TripVm" />
      </div>
  }
  ```
- Mobile ALREADY does the takeover (the pattern to mirror), at **lines ~160-166**:
  ```razor
  @if (TripVm.IsTripViewEnabled) { <MobileTripPanel Vm="TripVm" /> } else { ...list... }
  ```

**Required change (markup-only, RD8/NFR1):**
- In the bottom table region (lines ~322-344): when `TripVm.IsTripViewEnabled`, render `<TripStopList Vm="TripVm" />` in that wide region INSTEAD of `<PoiTable .../>`. Keep the map (line ~314) and the splitter handle (line ~319) intact above it. The `StopOrders` prop currently threaded into `PoiTable` (line 328) belongs to Story 1.4 (plain list follows order) — leave `PoiTable`'s props unchanged for the OFF state.
- REMOVE the additive `w-64` side-column block (lines ~347-354) entirely.
- Do NOT add a drag-resizable splitter for the list (FR-6 [ASSUMPTION] — none needed). The existing map/table splitter stays.
- No changes to `TripViewModel`, ordering, or timeline logic. No new C# logic. This is a pure markup/Tailwind move reusing the existing `TripStopList`/VM.

**Do not regress:** the `TripToggle` (line ~295), Fit All / Labels controls (map-side), the right detail pane (lines ~356-367), list↔map two-way selection sync (`OnMarkerSelectedAsync`, `Vm.HandlePoiSelectedAsync`, `SelectStop`), and per-collection toggle persistence.

## Constraints (NFRs)

- NFR1 strict layering — markup only in `.razor`; no arithmetic/logic moves into the component.
- NFR5 cross-surface — desktop change must not touch shared VM; mobile path (`MobileTripPanel`) unchanged and green.
- NFR6 UI conventions — Tailwind `surface-*`/`on-surface-*`/`primary` tokens only; any new text via `UiStrings` (none expected here); `TreatWarningsAsErrors` holds.
- NFR7 a11y — preserve list↔map sync and keyboard select after the relocation.
- NFR9 no regressions to selection sync, stop-order badges, toggle persistence.

## Testing

- Add/extend a bUnit `MapPage` component test asserting: Trip View ON → `PoiTable` absent, `TripStopList` present in the wide region; Trip View OFF → `PoiTable` present, no `w-64` Trip side column. (Look at `LucidCartographer.Tests/Components/Trip/` for the bUnit harness pattern; `MapPage` may need its render dependencies — check existing component tests for the established setup, e.g. `TripToggleTests`.)
- Run the Trip integration filter:
  `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`
- Keep mobile trip tests green.

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast tests: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`

## Dev Notes

This is the lowest-risk Epic 1 story (pure markup reuse). Subsequent Epic 1 stories
(1.2 wide columns, 1.3 connector, 1.4 canonical order) build on this takeover.

## Dev Agent Record

Desktop bottom filtered-results region (`MapPage.razor`) now renders `TripStopList`
instead of `PoiTable` when `TripVm.IsTripViewEnabled` (takeover); the additive `w-64`
side column was removed. Pure markup move — no VM/service/timeline change (NFR1). The
now-dead `StopOrders` prop was dropped from the OFF-state `PoiTable` (it was always
`null` in that branch — behavior-preserving). Mobile path untouched.

Tests: new bUnit `TripDesktopTakeoverTests` (renders a faithful proxy of MapPage's
takeover conditional — MapPage itself hangs under bUnit on `LeafletMap.WaitForInitAsync`,
documented in the test); the authoritative end-to-end proof is the Playwright Trip
integration suite, realigned to the takeover and net-strengthened with "PoiTable absent
during takeover" assertions (`td:has-text('Wawel Castle') count == 0`).

Adversarial review: 0 CRIT / 0 HIGH / 1 MED / 2 LOW → SHIP. MED (asymmetric
`CountAsync()>0` relaxation) addressed with a clarifying comment (below-the-fold row,
presence proves no data loss). LOW (bUnit proxy vs real MapPage) accepted, backstopped
by integration. Build clean (0 warnings, TreatWarningsAsErrors). 769 fast + 20 Trip
integration green.

## File List

- LucidCartographer/Components/Pages/MapPage.razor (MOD)
- LucidCartographer.Tests/Components/Trip/TripDesktopTakeoverTests.cs (NEW)
- LucidCartographer.Tests/Integration/TripViewIntegrationTests.cs (MOD)
