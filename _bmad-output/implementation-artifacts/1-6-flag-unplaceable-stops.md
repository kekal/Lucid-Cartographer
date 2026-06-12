---
baseline_commit: 979180b38b60d38ab0052b1afe25a15c12239406
---

# Story 1.6: Flag unplaceable stops

Status: review

## Story

As a trip planner with an incompletely-enriched collection,
I want POIs without coordinates kept in the trip but clearly excluded from the route,
So that nothing is silently dropped and my loop stays honest.

This story makes Trip View honest about POIs the system cannot place on the map. A POI whose
`Latitude` or `Longitude` is null (an "unplaceable" POI — see `Data/Entities/Poi.cs`, both
coordinates are `double?`) must remain a member of the collection and a visible row in the stop
list, labelled **"Not placeable"**, but must be excluded from everything spatial: it is not drawn
as a map marker, never appears in a drawn leg, and is excluded from any all-pairs routing
computation (the Distance Matrix that Epic 3 will build). Crucially, the Stop Order numbering of
the *placeable* stops must read cleanly (1..M contiguous to the user) and must not be visually
broken by the unplaceable ones interleaved among them.

Scope is **detect + flag + exclude only**. This story defines and applies the `IsPlaceable`
exclusion contract; it does **not** implement any routing/travel-time computation (Epic 2/3), the
reorder UI (Story 1.5), or Start/Finish designation (Story 1.7).

## Acceptance Criteria

1. **(FR-4, UX-DR10, UX-DR11)** Given a stop whose POI has a null `Latitude` **or** a null
   `Longitude`, when Trip View is on, then that stop is labelled **"Not placeable"** in the stop
   list (copy sourced from `UiStrings`, never hardcoded), and the POI **remains a member of the
   collection** (no membership/`PoiCollectionItem` mutation, no removal, no reordering of
   membership).

2. **(FR-4, AR-7, D6)** Given an unplaceable stop, when the map renders, then it is **not drawn as
   a marker** and is **not included in any leg** (it is neither an endpoint of a drawn leg nor a
   skipped-over gap that severs the loop — legs connect consecutive *placeable* stops only).

3. **(FR-4, D11, AR-4)** Given an unplaceable stop, when any **all-pairs routing computation**
   (the on-demand N×N Distance Matrix, Epic 3 / `DistanceMatrixService`) is built, then the
   unplaceable stop is **excluded** from the candidate set — the matrix is built over the placeable
   subset only. This story does not compute the matrix; it defines and applies the `IsPlaceable`
   filter that the matrix builder and leg-drawing path consume.

4. **(FR-4, UX-DR10)** Given unplaceable stops exist **among** placeable ones, when the order is
   displayed, then the Stop Order numbering presented to the user for the remaining placeable stops
   is **not broken** by the unplaceable ones — placeable stops read as a clean contiguous sequence,
   and an unplaceable row does not consume or display a routed Stop Order badge number that would
   create a visible gap (e.g. the user never sees placeable badges `1, 3, 4` with `2` silently
   missing because an unplaceable stop sat at position 2).

5. **(UX-DR11 — voice & tone)** Given the "Not placeable" treatment renders, then the microcopy is
   honest, factual, and a complete sentence routed through `UiStrings` — the provenance-aware copy
   reads in the spirit of **"Not placeable — no coordinates. Kept in the collection, excluded from
   the route."** (per EXPERIENCE.md Voice and Tone) — no hype, no exclamation, no false precision,
   and never a silent drop.

6. **(Dual-surface — UX-DR12, project-context UI Conventions)** Given the "Not placeable" treatment
   and the exclusion behavior, when the trip renders, then both AC1–AC5 hold identically on the
   **desktop** render path (`TripStopList` / `TripPanel` beside the map) **and** the **mobile**
   render path (`MobileTripPanel` / `Mobile*Screen` bottom panel). Neither surface is a degraded
   view.

7. **(Build discipline — project-context, AR-11)** Given the implementation, then the build passes
   with `TreatWarningsAsErrors=true` and `Nullable=enable`, introduces **no group-B analyzer
   violation** (MA0002, MA0015, MA0046, MA0047, MA0074, VSTHRD200), adds **no `ConfigureAwait(false)`**,
   and tags the new design decisions with searchable **`TRIP-*`** comment codes.

## Tasks / Subtasks

- [x] **Define the `IsPlaceable` exclusion contract** (AC1, AC2, AC3) — `[TRIP-PLACE-01]`
  - [x] Add a single canonical placeable predicate that matches the **existing codebase convention**
        `Poi.Latitude != null && Poi.Longitude != null` (used today in `Services/PoiService.cs`
        lines ~42/65/360/372, `Services/StartupCleanupService.cs` ~148,
        `Services/Enrichment/PoiEnrichmentBackgroundService.cs` ~569). Do **not** invent a different
        rule (e.g. `(0,0)` sentinel) — a stop is placeable iff **both** coordinates are non-null.
  - [x] Expose it where the Trip layer reads it. Preferred: an `internal static bool
        IsPlaceable(this Poi poi)` helper (or a `Stop`/membership-level equivalent) on the
        `Services/Trip/` slice, so leg-drawing, the Distance Matrix, and the ViewModel all call the
        **same** predicate (single source of truth). `InternalsVisibleTo("LucidCartographer.Tests")`
        is already set — keep it internal and test it directly.
  - [x] Add `[TRIP-PLACE-01]` comment on the predicate documenting that null lat **or** null lon ⇒
        unplaceable, excluded from map/legs/matrix, kept in collection.

- [x] **Apply exclusion to leg drawing / map markers** (AC2, AC6) — `[TRIP-PLACE-02]`
  - [x] In the trip leg/marker rendering path (the Trip extension to `Components/Shared/LeafletMap.razor`
        + `Services/LeafletMapService.cs` + `leafletInterop.js`, introduced by Story 1.3), build the
        ordered list of stops to draw from the **placeable subset only** so that legs connect
        consecutive *placeable* stops and the loop is not severed by an interleaved unplaceable stop.
  - [x] Ensure an unplaceable stop produces **no marker** and is **not** an endpoint of any leg
        (including the closing roundtrip leg). Do not pass null-coordinate POIs into the marker/leg
        interop.
  - [x] Confirm both desktop and `Mobile*Screen` map paths route through this same filtered list.

- [x] **Apply exclusion to the all-pairs routing candidate set** (AC3) — `[TRIP-PLACE-03]`
  - [x] Where Trip 1.2/1.3 expose the ordered stop set consumed by routing (and where Epic 3's
        `DistanceMatrixService` / `TripOrderingService` will read candidates), filter to the
        placeable subset using the **same** `IsPlaceable` predicate before any all-pairs work.
  - [x] If `TripOrderingService` (from Story 1.2) already exposes a stops accessor, add/confirm a
        placeable-only accessor (e.g. `GetPlaceableStops(...)`) that callers use for routing; leave
        the full-membership accessor intact for the stop **list**. Do **not** implement matrix/TSP
        math here — only the filter the future matrix consumes.

- [x] **Preserve numbering integrity over the placeable subset** (AC4) — `[TRIP-ORDER-UNPLACE-01]`
  - [x] Establish and document (per epics.md Story 1.6 AC2 and AR-11 "`OrderIndex` 1-based,
        contiguous, gap-free") that **`OrderIndex` is stored over the full membership by Stories
        1.2/1.5**, but the **user-facing routed Stop Order** the badge displays must read contiguous
        across placeable stops. Implement the display rule in the ViewModel / row component:
        placeable stops are numbered `1..M` in their relative order; unplaceable rows do **not**
        display a routed Stop Order badge number (they show the "Not placeable" treatment instead),
        so the user never sees a gap in placeable badge numbers.
  - [x] Verify that toggling a POI placeable↔unplaceable (e.g. enrichment fills coordinates) does
        not corrupt the stored `OrderIndex` and that placeable numbering stays contiguous to the
        user across the change.
  - [x] Add `[TRIP-ORDER-UNPLACE-01]` comment explaining the relationship: stored `OrderIndex`
        (full set, owned by 1.2/1.5) vs. presented routed number (placeable subset, this story).

- [x] **"Not placeable" stop-list row treatment** (AC1, AC5, AC6) — `[TRIP-PLACE-04]`
  - [x] In the stop-list row component (the `TripStopList` / `StopListRow` from Story 1.3) add the
        "Not placeable" visual treatment per UX-DR10: the row replaces its order badge with the
        not-placeable marker, shows the not-placeable copy, and uses `on-surface-muted` /
        de-emphasized styling (per DESIGN.md "explicit empty markers are first-class"). Keep the row
        present — never hide or drop it.
  - [x] Add `aria-label` describing the not-placeable state for the row (the badge slot otherwise
        carries a meaningless number to a screen reader — see Accessibility Floor).
  - [x] Mirror the treatment on the mobile row path (`MobileTripPanel`).

- [x] **`UiStrings` additions** (AC1, AC5, AC7) — `[TRIP-PLACE-05]`
  - [x] Add to `Services/UiStrings.cs` (no hardcoded text anywhere in markup):
        - `TripStopNotPlaceable = "Not placeable"` (short row label)
        - `TripStopNotPlaceableDetail = "Not placeable — no coordinates. Kept in the collection, excluded from the route."` (full provenance-aware sentence, UX-DR11)
        - `TripStopNotPlaceableAria = "Not placeable: this stop has no coordinates and is excluded from the route, but kept in the collection."` (screen-reader label)
  - [x] Use the `Trip*`-prefixed naming consistent with other `UiStrings` groupings; place under a
        `// Trip View` section.

- [x] **Tests** (AC1–AC4, AC6, AC7)
  - [x] **Unit — `IsPlaceable` predicate** (`LucidCartographer.Tests/Services/TripPlaceableTests.cs`):
        null `Latitude` ⇒ unplaceable; null `Longitude` ⇒ unplaceable; null both ⇒ unplaceable;
        both present (incl. `0,0`) ⇒ placeable. Assert it matches the existing `Latitude != null &&
        Longitude != null` convention. (AC1)
  - [x] **Unit — exclusion from the routing candidate set** (Distance-Matrix / ordering candidate
        accessor): given a mixed set, the placeable-only accessor returns only placeable stops; an
        all-pairs candidate list built from it contains no unplaceable POI. (AC3)
  - [x] **Unit — exclusion from legs**: given an interleaved unplaceable stop, the ordered
        leg-drawing list connects only consecutive placeable stops (no leg has an unplaceable
        endpoint; the loop is not severed). (AC2)
  - [x] **Unit — numbering integrity**: given placeable stops with unplaceable stops interleaved,
        the presented routed Stop Order for placeable stops is contiguous `1..M` with no gap, and
        unplaceable stops carry no routed number. (AC4)
  - [x] **bUnit — "Not placeable" label**: rendering the stop list with an unplaceable stop shows
        the `UiStrings.TripStopNotPlaceable` copy, no routed order badge number on that row, the
        not-placeable `aria-label`, and the row is still present (not dropped). (AC1, AC5)
  - [x] **bUnit / dual-surface**: assert the not-placeable treatment renders on **both** the desktop
        stop-list and the mobile panel paths (mirror existing `Mobile*Tests` pattern). (AC6)
  - [x] Ensure `dotnet build` and `dotnet test` pass with warnings-as-errors and no group-B
        analyzer violations. (AC7)

## Dev Notes

### Patterns & constraints (must follow)

- **Placeable predicate = existing convention.** The codebase already treats a POI as placeable iff
  `Latitude != null && Longitude != null` — see `Services/PoiService.cs` (e.g. the visible-POI and
  bounds queries), `Services/StartupCleanupService.cs`, and the enrichment services. **Reuse this
  exact rule**; do not introduce a `(0,0)` sentinel or a "missing address" notion. Unplaceable is a
  pure coordinate-nullability fact (`Poi.Latitude`/`Poi.Longitude` are `double?` in
  `Data/Entities/Poi.cs`). `[TRIP-PLACE-01]`
- **Single source of truth.** Map legs (AC2), the Distance Matrix candidate set (AC3), and the
  ViewModel/row numbering (AC4) must all call the **same** `IsPlaceable` helper — do not duplicate
  the null-check inline in three places. This mirrors AR-11's "one ordering write-path" discipline.
- **Layering (strict):** Component → ViewModel → Service → Data. The `IsPlaceable` predicate belongs
  in the Service/Data layer (`Services/Trip/`), the filtered candidate accessors in the Trip
  services / `TripViewModel`, and the row treatment in the component. Components hold markup only;
  the not-placeable decision (which rows are unplaceable, what number a placeable row shows) is
  computed in `TripViewModel`, exposed as state, and the row just renders it.
- **No hardcoded UI text** — every string via `UiStrings` (`@UiStrings.*`). `[TRIP-PLACE-05]`
- **Honest microcopy (UX-DR11):** the not-placeable copy is a complete, factual sentence with
  provenance ("no coordinates"), explicitly stating the POI is *kept in the collection* and
  *excluded from the route*. This is one of the canonical Voice-and-Tone examples in EXPERIENCE.md.
  No exclamation, no hype, never a silent drop ("Banned: silently dropping unplaceable POIs").
- **Dual render paths:** implement the row treatment and exclusion on **both** the desktop Trip
  panel and the `Mobile*Screen` / `MobileTripPanel` path (project-context UI Conventions, UX-DR12).
- **Build discipline:** warnings-as-errors, `Nullable=enable`, no group-B analyzer violations
  (MA0002/MA0015/MA0046/MA0047/MA0074/VSTHRD200), no `ConfigureAwait(false)`. New decisions carry
  `TRIP-*` codes (`[TRIP-PLACE-01..05]`, `[TRIP-ORDER-UNPLACE-01]`).

### Dependency on Stories 1.2 and 1.3 (consume, do not re-build)

- **Story 1.2 (Toggle Trip View + seed Stop Order; `TripViewModel` + `TripOrderingService`):**
  provides the `TripViewModel` (sealed, Transient, `StateChanged`), the seeded/persisted 1-based
  `OrderIndex` on `PoiCollectionItem`, and `ITripOrderingService` (the sole `OrderIndex` writer).
  This story **reads** the ordered membership the ViewModel exposes, adds the `IsPlaceable` filter
  to the routing-candidate accessor, and computes the presented placeable numbering. It does **not**
  change how `OrderIndex` is seeded or written.
- **Story 1.3 (Render ordered stops, legs, stop panel; `TripStopList` + leg rendering):** provides
  `Components/Shared/Trip/TripStopList.razor` (+ `StopListRow`) and the leg/marker drawing path
  (`LeafletMap.razor` Trip extension → `LeafletMapService` → `leafletInterop.js`). This story
  **adds** the "Not placeable" row treatment to that list and the placeable-only filter to the leg
  drawing — it does not re-author the list or the leg renderer.
- If `TripStopList.razor` / `TripViewModel` / `TripOrderingService` are not yet present on disk when
  this story is implemented, treat their interfaces (per architecture.md "Project Structure &
  Boundaries" and the 1.2/1.3 ACs in epics.md) as the contract and integrate against them; do not
  fork a parallel stop-list or ordering path.

### Source tree — NEW / UPDATE (real paths under `C:\backup\maps_editor\LucidCartographer\`)

- **UPDATE** `Components/Shared/Trip/TripStopList.razor` (and/or `StopListRow.razor`) — add the
  "Not placeable" row treatment per UX-DR10 (no order badge number, not-placeable marker + copy,
  `aria-label`, de-emphasized styling). Row stays present.
  - *Current behavior (from Story 1.3):* renders each stop row with order badge · POI name · dwell
    placeholder · timeline-value placeholder. *Preserve:* row structure, `@key`, virtualization, and
    the placeable rows' existing badge/name rendering — only branch the unplaceable case.
- **UPDATE** `Components/Shared/Trip/MobileTripPanel.razor` — mirror the not-placeable treatment on
  the mobile path. *Preserve* the mobile bottom-panel/sheet layout.
- **UPDATE** `Components/Shared/LeafletMap.razor` + `Services/LeafletMapService.cs` +
  `wwwroot/js/leafletInterop.js` (the Trip leg/marker extension added by 1.3) — draw markers/legs
  from the **placeable subset only**; never pass a null-coordinate POI to the marker/leg interop.
  - *Current behavior (existing file):* `LeafletMap.razor` exposes `ShowCollectionAsync(collectionId,
    List<Poi> pois, color)`, `FocusOnPoiAsync(lat, lon, …)`, `HighlightMarkerAsync(poiId)` via
    `IMapService`; marker clicks flow through `MapService.OnMarkerClicked`. The base collection view
    already only plots placeable POIs (`PoiService` filters `Latitude/Longitude != null` before
    handing POIs to the map). *Preserve:* the existing collection-show/marker-click/bounds interop
    untouched; the Trip-leg path must layer on top without regressing popups/markers (FR-7 / 1.4).
- **UPDATE** `Services/Trip/TripOrderingService.cs` (+ `ITripOrderingService.cs`) — add/confirm a
  **placeable-only candidate accessor** used by routing (legs + future Distance Matrix); keep the
  full-membership accessor for the stop list. *Do not* implement matrix/TSP math here.
  - *Current behavior (from Story 1.2):* sole writer of 1-based contiguous `OrderIndex`. *Preserve:*
    the single-writer ordering contract; this story only **reads** + **filters**, never writes order.
- **UPDATE** `Components/Shared/Trip/TripViewModel.cs` — expose: the ordered full stop set (for the
  list), an `IsPlaceable`-derived per-stop flag, and the presented routed number for placeable
  stops. State via `private set` + `StateChanged`/`Notify()` per the ViewModel pattern.
- **NEW (small)** `IsPlaceable` helper in `Services/Trip/` (e.g. `Services/Trip/StopPlaceability.cs`
  or an extension on the existing trip stop projection) — `internal static`, the single placeable
  predicate. `[TRIP-PLACE-01]`
- **UPDATE** `Services/UiStrings.cs` — add `TripStopNotPlaceable`, `TripStopNotPlaceableDetail`,
  `TripStopNotPlaceableAria` under a `// Trip View` section.
- **NEW (tests)** `LucidCartographer.Tests/Services/TripPlaceableTests.cs` (predicate + exclusion +
  numbering) and bUnit coverage in the Trip component tests (label + dual surface).

### OrderIndex relationship (cite the epic)

epics.md **Story 1.6 AC2** requires: *"the Stop Order numbering of the remaining placeable stops is
not broken by the unplaceable ones."* AR-11 fixes `OrderIndex` as **1-based, contiguous, gap-free**
over the trip, seeded `AddedDate` ascending (D1). Reconciliation for this story: the **stored**
`OrderIndex` (owned/written by Stories 1.2/1.5, over the full membership) stays contiguous in
storage; the **presented routed Stop Order** the badge shows is computed over the **placeable
subset** so the user sees `1..M` with no visible gap where an unplaceable stop sits. Unplaceable
rows render the "Not placeable" treatment **instead of** a routed badge number. This story does not
change the stored `OrderIndex` semantics — it defines the placeable-subset *presentation* and the
placeable *candidate set* for routing. `[TRIP-ORDER-UNPLACE-01]`

### Testing summary

- **Unit (xUnit + FluentAssertions, internals visible):** `IsPlaceable` truth table (null lat / null
  lon / both null / both present incl. `0,0`); routing-candidate accessor excludes unplaceable; leg
  list connects only consecutive placeable stops; presented numbering contiguous over placeable
  subset with unplaceable interleaved.
- **Component (bUnit):** "Not placeable" copy + `aria-label` present, no routed badge on the
  unplaceable row, row not dropped; desktop **and** mobile paths (mirror `Mobile*Tests`).
- Build must pass warnings-as-errors with no group-B analyzer violations.

### Project Structure Notes

- New code stays within the `Services/Trip/` slice and `Components/Shared/Trip/` per architecture.md
  "Structure Patterns" — no unrelated slice is touched. The map interop extension reuses the single
  existing `leafletInterop.js` module (no second JS module — anti-pattern per architecture.md).
- `IsPlaceable` is intentionally tiny and `internal` so all three consumers (legs, matrix,
  ViewModel) share one predicate; `InternalsVisibleTo("LucidCartographer.Tests")` covers it.
- No schema change: unplaceability is derived from existing `Poi.Latitude`/`Poi.Longitude`
  nullability (added in Story 1.1's migration scope only insofar as the Trip fields; coordinates
  already exist). No migration in this story.

### References

- [Source: _bmad-output/planning-artifacts/epics.md#story-16-flag-unplaceable-stops] — ACs (FR-4, UX-DR10, UX-DR11), AC2 numbering-integrity statement.
- [Source: _bmad-output/planning-artifacts/epics.md#requirements-inventory] — FR-4 (identify Stops without usable coordinates, label Unplaceable, exclude from map/Legs/Distance Matrix, keep without breaking Stop Order); FR-5 (legs between consecutive Stops).
- [Source: _bmad-output/planning-artifacts/architecture.md#d11--distance-matrix] — "N×N over **placeable** Stops"; exclusion contract for all-pairs routing.
- [Source: _bmad-output/planning-artifacts/architecture.md#data-architecture] — `OrderIndex` 1-based contiguous gap-free; `Poi` coordinates nullable.
- [Source: _bmad-output/planning-artifacts/architecture.md#implementation-patterns--consistency-rules] — `TRIP-*` comment codes, no group-B violations, no `ConfigureAwait(false)`, dual-surface + `UiStrings`.
- [Source: _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-11/EXPERIENCE.md#voice-and-tone] — "Not placeable — no coordinates. Kept in the collection, excluded from the route."
- [Source: _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-11/EXPERIENCE.md#state-patterns] — Unplaceable POI: flagged "Not placeable", excluded from routing, never silently dropped (UX-DR10).
- [Source: _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-11/EXPERIENCE.md#interaction-primitives] — Banned: silently dropping unplaceable POIs.
- [Source: _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-11/DESIGN.md#brand--style] — "explicit empty markers are first-class visual citizens" (honest signaling).
- [Source: _bmad-output/project-context.md#ui-conventions] — no hardcoded UI text (`UiStrings`); distinct desktop/mobile render paths; `<Virtualize>` + `@key`.
- [Source: LucidCartographer/Data/Entities/Poi.cs] — `Latitude`/`Longitude` are `double?`; null = unplaceable.
- [Source: LucidCartographer/Services/PoiService.cs#L42,L65,L360,L372] — existing `Latitude != null && Longitude != null` placeable convention to reuse.
- [Source: LucidCartographer/Components/Shared/LeafletMap.razor] — existing map interop surface (`IMapService`: `ShowCollectionAsync`, `FocusOnPoiAsync`, `HighlightMarkerAsync`, `OnMarkerClicked`) that the Trip-leg path extends without regression.
- [Source: LucidCartographer/Services/UiStrings.cs] — target for new `TripStopNotPlaceable*` constants.

## Dev Agent Record

### Agent Model Used

claude-fable-5

### Debug Log References

- `dotnet build`: 0 warnings / 0 errors (TreatWarningsAsErrors=true).
- `dotnet test` (full suite): 647 passed, 0 failed, 0 skipped (the known
  ScrapeProgress_ShowsIndicator flake passed in-run; no isolation rerun needed).
- One bUnit iteration: clicking the unplaceable row throws bUnit's
  `MissingEventHandlerException` (the row has no handler at all) — the test now
  asserts that exception plus the VM-level guard, which is stronger proof of
  non-selectability than a no-op click.

### Completion Notes List

- `[TRIP-PLACE-01]` Canonical predicate lives in NEW `Services/Trip/StopPlaceability.cs`
  (`internal static`, entity + raw-value overloads, `(0,0)` placeable). The
  TripOrderingService row reader and the TripViewModel projection both route
  through it; the two EF `Where` clauses that must stay SQL-translatable
  (`HasOrderAsync`/`GetStopOrderAsync`) carry a lockstep comment.
- `[TRIP-PLACE-03]` NEW `ITripOrderingService.GetPlaceableStopsAsync` +
  `PlaceableStop` record — the ordered placeable-only routing candidate set for
  the Epic 3 Distance Matrix. Read-only; no matrix/TSP math implemented. The
  ordering write-path (seed/append/compact/reconcile, `SetOrderAsync`) is
  untouched.
- `[TRIP-PLACE-02]` No functional map change was needed: markers (PoiService
  filter + `StopOrders`) and legs (`OrderedLegs`) already flow exclusively from
  the ViewModel's placeable projection, now via the canonical predicate; legs
  connect consecutive placeable stops so the loop is never severed. Contract
  documented at the `LeafletMapService` interop boundary. Both desktop and
  Mobile*Screen map paths share this single MapPage→LeafletMap path.
- `[TRIP-ORDER-UNPLACE-01]` Reconciliation with the on-disk 1.2 reality: the
  stored `OrderIndex` covers the placeable membership (unplaceable items hold
  0 = "not a stop", per TripOrderingService) and is never written by this
  story. The presented routed number is recomputed contiguously 1..M over the
  placeable subset in `ReadStopsAndRowsAsync`, so badges can never show a gap.
- `[TRIP-PLACE-04]` NEW `TripStopRow` projection (full membership) +
  `TripViewModel.StopRows`; unplaceable rows trail the routed stops in
  AddedDate/PoiId order, are de-emphasized, non-selectable (no button
  semantics; `SelectStop` also guards), carry `UiStrings.TripStopNotPlaceableAria`
  and the detail sentence as `title`, and show the not-placeable marker instead
  of an order badge. Identical treatment on `TripStopList` and
  `MobileTripPanel` (tokens only on mobile).
- `[TRIP-PLACE-05]` Three `TripStopNotPlaceable*` constants added under the
  Trip View section of `UiStrings`; no hardcoded markup text.
- Behavior change to note: previously unplaceable members were absent from the
  stop list entirely (silent drop); they are now visible with the honest
  treatment. The empty-state check switched from `OrderedStops` to `StopRows`
  so an all-unplaceable membership still shows its rows.

### File List

- NEW `LucidCartographer/Services/Trip/StopPlaceability.cs`
- MOD `LucidCartographer/Services/Trip/ITripOrderingService.cs` (PlaceableStop record + GetPlaceableStopsAsync)
- MOD `LucidCartographer/Services/Trip/TripOrderingService.cs` (accessor impl; ReadAsync routed through predicate; lockstep comment)
- MOD `LucidCartographer/Components/Shared/Trip/TripProjections.cs` (TripStopRow record)
- MOD `LucidCartographer/Components/Shared/Trip/TripViewModel.cs` (StopRows projection, ReadStopsAndRowsAsync, SelectStop guard)
- MOD `LucidCartographer/Components/Shared/Trip/TripStopList.razor` (Not placeable row treatment, StopRows iteration)
- MOD `LucidCartographer/Components/Shared/Trip/MobileTripPanel.razor` (mirrored treatment)
- MOD `LucidCartographer/Services/LeafletMapService.cs` (TRIP-PLACE-02 contract comment only)
- MOD `LucidCartographer/Services/UiStrings.cs` (TripStopNotPlaceable / Detail / Aria)
- NEW `LucidCartographer.Tests/Services/TripPlaceableTests.cs`
- MOD `LucidCartographer.Tests/Components/Trip/TripStopListTests.cs` (Story 1.6 dual-surface bUnit coverage)
- MOD `_bmad-output/implementation-artifacts/1-6-flag-unplaceable-stops.md` (this record)

### Change Log

- 2026-06-12: Story 1.6 implemented — IsPlaceable contract (StopPlaceability),
  placeable-only routing candidate accessor, full-membership StopRows with
  contiguous presented numbering, "Not placeable" row treatment on desktop +
  mobile, UiStrings additions, unit + bUnit coverage. Full suite 647/647 green;
  build clean under warnings-as-errors. Status → review.
