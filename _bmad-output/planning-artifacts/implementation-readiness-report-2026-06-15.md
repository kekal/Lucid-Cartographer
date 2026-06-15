---
stepsCompleted: ['step-01-document-discovery', 'step-02-prd-analysis', 'step-03-epic-coverage-validation', 'step-04-ux-alignment', 'step-05-epic-quality-review', 'step-06-final-assessment']
status: 'complete'
readinessStatus: 'READY (2 pre-implementation clarifications)'
documentsIncluded:
  - 'prds/prd-maps_editor-2026-06-15/prd.md'
  - 'architecture.md'
  - 'epics.md'
  - 'ux-designs/ux-maps_editor-2026-06-15/DESIGN.md'
  - 'ux-designs/ux-maps_editor-2026-06-15/EXPERIENCE.md'
date: '2026-06-15'
---

# Implementation Readiness Assessment Report

**Date:** 2026-06-15
**Project:** maps_editor

## 1. Document Inventory

| Type | File | Size | Modified |
|------|------|------|----------|
| PRD | prds/prd-maps_editor-2026-06-15/prd.md | 24 KB | 2026-06-15 |
| Architecture | architecture.md | 54 KB | 2026-06-15 |
| Epics & Stories | epics.md | 50 KB | 2026-06-15 |
| UX — Design | ux-designs/ux-maps_editor-2026-06-15/DESIGN.md | 4 KB | 2026-06-15 |
| UX — Experience | ux-designs/ux-maps_editor-2026-06-15/EXPERIENCE.md | 8 KB | 2026-06-15 |

**Issues:** No duplicate formats; no missing primary documents. Archive (`archive/trip-planning/`) excluded as out of scope.

## 2. PRD Analysis

**Title:** Trip View — Layout Realignment & Honest Schedule PRD (status: final, 2026-06-15)
**Nature:** Brownfield delta on shipped Trip Planning (Epics 1–4). Desktop UI built now; mobile UI surfacing deferred to a "mirror" phase; shared-layer fixes reach both surfaces.

### Functional Requirements (33 total, grouped by Feature)

**Feature A — Trip View switches desktop list region into the trip (Issue 1, root)**
- **FR-1** Toggling Trip View ON makes the desktop filtered-results region *become* the trip stop list; the plain PoiTable is not shown simultaneously. Toggling OFF restores the plain PoiTable + controls unchanged (no data loss).
- **FR-2** Trip stop list renders in the **wide list region** (not 256px side col) as a trip-scoped table: columns = Reorder gutter (drag + ▲▼), Stop # (badge + Start/Finish glyph), Name (full name + address sub-line + enrichment icon), Dwell (HH:MM picker, FR-30), Arrival (relative always; wall-clock + date when start set), Start/Finish controls (○/⚑), Actions (Focus on map + Open in Google Maps only). Drops: Select checkbox, Coordinates, Collection chips, Added date, per-row Move/Copy/Delete, batch-action toolbar. Header carries only trip-relevant controls.
- **FR-3** Per-leg travel time shown **between** the two stops it connects — a compact connector on the shared row edge (not a column, not a full row). Connector carries mode control (Feature F), travel time ("min", FR-16), distance, fidelity badge, edit/reset (FR-25); uncomputed/Any reads "—". Closing leg renders after last row, before finish/return footer.
- **FR-4** Stop Order is the **single canonical ordering** for the collection. Reorder (drag, ▲▼, TSP-Sort, MCP) writes shared `PoiCollectionItem.OrderIndex` via sole-writer `TripOrderingService.SetOrderAsync`; the plain Filtered Results list renders in that same order when one exists. Order persists between views.
- **FR-5** Map stays visible beside/above the trip list (two-region work area preserved); list↔map two-way selection sync keeps working.
- **FR-6** Desktop matches the pattern mobile already uses — replaces list content rather than adding a parallel list. *[ASSUMPTION] no drag-resizable splitter needed.*

**Feature B — Legible travel-time fidelity (Issue 2)**
- **FR-7** Each fidelity badge (Estimated/Measured/Manual) explains its meaning in plain language on hover + to AT, replacing circular "Provenance: Estimated."
- **FR-8** When deployment has no measured provider (default `Mock`, all legs Estimated), the panel makes this legible — distinct from the engine-unreachable fallback note.
- **FR-9** "Recompute travel times" must not imply it will upgrade fidelity when no measured provider is configured.
- **FR-10** The panel **recommends enabling OSRM** for measured times (explains Estimated + points to how, e.g. `docs/osrm.md`); does NOT stand up/configure OSRM itself.

**Feature C — Clean trip-row layout (Issue 1, residual)**
- **FR-11** In the wide trip list, row columns present as orderly aligned columns, not a ragged cluster.
- **FR-12** Row alignment holds across stop-row states: placeable vs unplaceable, Start/Finish pinned, dwell set vs empty, long vs short names.

**Feature D — Reconciled travel-time arithmetic & units (Issue 3)**
- **FR-13** Displayed trip **total** equals the sum of displayed **per-leg** times — no drift from independent rounding.
- **FR-14** Displayed arrivals produced by the existing `ItineraryTimeline` accumulation rule and reconcile with displayed per-leg/total figures (does not redefine accumulation, only removes rounding drift).
- **FR-15** Rounding applied once at the display edge from canonical seconds, consistently across legs/arrivals/total, preserving honesty qualifiers ("—", provenance, partial-trip em-dash).
- **FR-16** Minute unit renders as **"min"** not "m" (collides with distance meters); hours stay "h"; distance meters stay "m". Shared layer — both surfaces.

**Feature E — Discoverable button tooltips (Issue 4)**
- **FR-17** Every icon-only trip control shows a hover tooltip naming its action: move up/down, Set/Unset Start, Set/Unset Finish, TSP-Sort, Recompute.
- **FR-18** Tooltip text from `UiStrings`, reflects control state (e.g. "Set as Start" vs "Unset Start"; disabled edge/pinned read sensibly); sighted + AT parity.

**Feature F — Per-leg travel mode (Issue 5) — new capability**
- **FR-19** Travel mode is a property of each leg (consecutive pair + roundtrip closing leg), not the trip; each leg shows + lets user set: Walk/Drive/Cycle/Any-Air.
- **FR-20** A newly-appeared leg defaults to **Any/Air ("undefined")** — no auto time, reads "—" until user acts. Undefined and Any/Air are the same state.
- **FR-21** A ground mode (Walk/Drive/Cycle) yields an automatic time (Estimated, or Measured under OSRM); Any/Air legs are never auto-estimated (user-specified only).
- **FR-22** A leg unchanged across a reorder (same From→To, same mode) retains its mode + cached time; only newly-appeared pairs reset to Any/Air (uses directional mode-keyed cache TRIP-CACHE-01).
- **FR-23** The **trip-level mode selector is removed**; per-leg modes replace it. Per-leg mode persists per stop's outgoing leg (nullable `PoiCollectionItem.OutgoingTravelMode`); small EF migration adds it, constrained by `TravelMode.All` check pattern.
- **FR-24** Per-leg mode reachable from the `map_editor` MCP: `get_trip` reports each leg's mode, and a tool sets a leg's mode (alongside existing `assign_stop_order`/`set_dwell_time`).
- **FR-25** Per-leg travel time is user-editable (inline/popup) and resettable to auto. Editing sets a **Manual** override (never auto-overwritten, TRIP-MANUAL-01); Reset clears it → auto (Estimated/Measured ground; "—"/undefined Any/Air).

**Feature G — Multi-day schedule: start time & time limit (Issues 6–7) — new capability**
- **FR-26** Start specified as **date AND time** (date-time picker), persisted in existing `PoiCollection.TripStartTime` (no schema change); empty = relative offsets only. Replaces the `type="time"` input that hard-pairs `DateTime.Today`.
- **FR-27** Wall-clock arrivals reflect date and **roll across midnight/multiple days**; later-day arrivals show date. Date/time locale-driven (`CultureInfo.CurrentCulture`). Overnight modeling stays out of scope.
- **FR-28** The **time limit** (renamed from "Time budget") is a fixed goal; enterable as a **duration (HH:MM)** not only raw minutes. Persisted as `TimeBudgetMinutes` (no schema change). Shows **"Over limit"** indicator when exceeded.
- **FR-29** Time limit can alternatively be entered by picking a **finish-by deadline** (date+time): app computes limit once as `deadline − start`. Input convenience only — afterwards a fixed goal stored as minutes, does not recompute. Distinct from the Finish stop (Feature H).
- **FR-30** **Dwell entered with a duration picker (HH:MM)**, not raw-minutes box. Persisted as canonical `DwellMinutes`; empty clears. No schema change.

**Feature H — Finish designation & roundtrip readout (Issue 8)**
- **FR-31** A trip is **roundtrip by default**; with no Finish, end readout reads "Return to start" + return-to-Start arrival.
- **FR-32** Pressing **Finish** on a stop makes the trip an open path: that stop becomes Finish, pinned to list end; readout switches to "Finish" + its arrival (date-aware FR-27); never "Return to start" while a Finish is set.
- **FR-33** Finish designation is **revertable**: unsetting returns trip to roundtrip + "Return to start" readout, no data loss.

**Total FRs: 33** (FR-1 … FR-33)

### Non-Functional Requirements & Constraints (§5 — categorical, unnumbered in PRD)

- **NFR-Arch** Strict layering (markup-only `.razor` → `TripViewModel` → services). Feature A = markup/layout move in `MapPage.razor` reusing `TripStopList`/VM, no new ordering/timeline logic. Arithmetic (FR-13–15) in `ItineraryTimeline`/`TravelTimeFormatting`/`TripViewModel`, never component. Canonical units unchanged (sec/m/min). No change to `RouteSegment` cache semantics or default provider. Feature F: nullable outgoing-leg mode column via small EF migration, `TravelMode.All` constrained (TRIP-SCHEMA-01), reusing directional cache (TRIP-CACHE-01).
- **NFR-CrossSurface** Shared-layer changes (FR-16 units, Feature D arithmetic, Feature F data/VM, Feature G persistence) authored once → apply to both desktop + mobile; mobile must stay correct (data/strings/times) though its new-feature controls deferred. Don't break `MobileTripPanel`.
- **NFR-UIConventions** All new/changed text via `UiStrings`. Tailwind `surface-*`/`on-surface-*`/`primary` tokens only. No group-B analyzer violations; `TreatWarningsAsErrors` holds.
- **NFR-A11y** Preserve `aria-live`/`aria-label` parity; tooltips available to AT; list↔map sync + keyboard reorder/select intact after relocation.
- **NFR-Testing** Cover desktop component path (bUnit) + arithmetic invariant (unit). After any Trip VM/DI/schema change run the Trip integration filter. Add a test asserting Trip-View-on hides PoiTable + shows wide stop list. Keep mobile trip tests green.
- **NFR-NoRegressions** No regressions to map-side leg rendering, stop-order badges, selection sync, or per-collection toggle persistence.

### Additional Requirements / Constraints

- **Non-Goals (§6):** mobile UI surfacing (deferred); standing up OSRM / changing default `Mock`; changing default values / auto-fill / overnight modeling (input *affordances* DO change); drag-resizable splitter; any further new trip features (export, scheduling automation, optimization beyond TSP).
- **Resolved items (§7):** FR-4 single-collection-only scope; FR-10 recommend-not-configure OSRM; FR-20 first toggle-on seeds all legs Any/Air; FR-23 trip-level selector removed; FR-24 MCP extended with per-leg mode r/w.
- **Open assumptions (§7):** FR-1/2 hides PoiTable; FR-6 no splitter; FR-15 round-then-sum; FR-7 tooltip wording; FR-19/23 mode persists on outgoing leg.

## 3. Epic Coverage Validation

The epics document carries an explicit **FR Coverage Map** plus per-epic "FRs covered" lists. I verified each FR not only against the map but against an actual **story acceptance criterion** that lands it.

### Coverage Matrix (33 FRs)

| FR | Requirement (short) | Epic / Story | Status |
|----|--------------------|--------------|--------|
| FR-1 | Trip View ON switches list region; PoiTable hidden | Epic 1 / Story 1.1 | ✓ Covered |
| FR-2 | Wide trip-scoped table, trip-only columns | Epic 1 / Story 1.2 | ✓ Covered |
| FR-3 | Per-leg info on inter-row connector | Epic 1 / Story 1.3 | ✓ Covered |
| FR-4 | Single canonical Stop Order across both views | Epic 1 / Story 1.4 | ✓ Covered |
| FR-5 | Map stays visible; list↔map sync | Epic 1 / Story 1.1 | ✓ Covered |
| FR-6 | Desktop matches mobile switch; no splitter | Epic 1 / Story 1.1 | ✓ Covered |
| FR-7 | Self-explaining fidelity badges | Epic 2 / Story 2.3 | ✓ Covered |
| FR-8 | Legible all-Estimated (Mock) state | Epic 2 / Story 2.4 | ✓ Covered |
| FR-9 | Recompute copy doesn't imply fidelity upgrade | Epic 2 / Story 2.4 | ✓ Covered |
| FR-10 | Recommend enabling OSRM (link, not configure) | Epic 2 / Story 2.4 | ✓ Covered |
| FR-11 | Orderly aligned trip-row columns | Epic 1 / Story 1.2 | ✓ Covered |
| FR-12 | Alignment holds across stop-row states | Epic 1 / Story 1.2 | ✓ Covered |
| FR-13 | Total == Σ per-leg displayed | Epic 2 / Story 2.1 | ✓ Covered |
| FR-14 | Arrivals via ItineraryTimeline, reconciled | Epic 2 / Story 2.1 | ✓ Covered |
| FR-15 | Round once at display edge; qualifiers kept | Epic 2 / Story 2.1 | ✓ Covered |
| FR-16 | Minute unit "m" → "min" | Epic 2 / Story 2.2 | ✓ Covered |
| FR-17 | Hover tooltips on icon-only controls | Epic 2 / Story 2.5 | ✓ Covered |
| FR-18 | Tooltip text from UiStrings, state-reflecting | Epic 2 / Story 2.5 | ✓ Covered |
| FR-19 | Travel mode per-leg | Epic 3 / Stories 3.2, 3.4 | ✓ Covered |
| FR-20 | Newly-appeared legs default Any/Air "—" | Epic 3 / Story 3.3 | ✓ Covered |
| FR-21 | Ground auto-time; Any/Air never auto-estimated | Epic 3 / Stories 3.2, 3.4 | ✓ Covered |
| FR-22 | Unchanged legs retain mode + cached time | Epic 3 / Story 3.3 | ✓ Covered |
| FR-23 | Trip-level selector removed; OutgoingTravelMode col | Epic 3 / Stories 3.1, 3.4 | ✓ Covered |
| FR-24 | MCP get_trip per-leg + set_leg_travel_mode | Epic 3 / Story 3.6 | ✓ Covered |
| FR-25 | Per-leg time editable (Manual) + reset | Epic 3 / Story 3.5 | ✓ Covered |
| FR-26 | Start = date AND time | Epic 4 / Story 4.1 | ✓ Covered |
| FR-27 | Date-aware multi-day arrivals; locale-driven | Epic 4 / Story 4.2 | ✓ Covered |
| FR-28 | Time limit as HH:MM duration; "Over limit" | Epic 4 / Story 4.3 | ✓ Covered |
| FR-29 | Time limit via finish-by deadline, computed once | Epic 4 / Story 4.3 | ✓ Covered |
| FR-30 | Dwell HH:MM picker | Epic 4 / Story 4.4 | ✓ Covered |
| FR-31 | Roundtrip default → "Return to start" | Epic 4 / Story 4.5 | ✓ Covered |
| FR-32 | Finish pins to end; "Finish" + dated arrival | Epic 4 / Story 4.5 | ✓ Covered |
| FR-33 | Finish revertable to roundtrip | Epic 4 / Story 4.5 | ✓ Covered |

### Missing Requirements

- **None.** All 33 FRs trace to at least one story acceptance criterion.

### FRs in epics but NOT in PRD (reverse check)

- **None.** No invented scope; the epic FR set matches the PRD FR set exactly (FR-1…FR-33).

### NFR coverage (bonus check — full validation in later step)

- All 6 PRD NFR categories map to the epics' NFR1–10 (epics expanded them, adding **NFR4** schema discipline, **NFR10** DI-seam discipline — the latter directly addresses your project's recurring `AddTripServices()` parameterless-overload regression point). No NFR category dropped.

### Coverage Statistics

- **Total PRD FRs:** 33
- **FRs covered in epics:** 33
- **Coverage percentage:** **100%**
- **FRs orphaned (in epics, not PRD):** 0

## 4. UX Alignment Assessment

### UX Document Status

**Found.** Two complementary files form one coherent UX spec:
- `DESIGN.md` — visual delta (4 new components: trip-stop-row, leg-connector, leg-mode-pill, schedule-picker), inheriting canonical `ux-maps_editor-2026-06-11` tokens **unchanged**.
- `EXPERIENCE.md` — behavioral delta (component patterns, state patterns, interaction primitives, microcopy, a11y floor, a key flow).

Both correctly declared as **delta spines** ("this delta wins on conflict" for Trip View). Architecture's frontmatter confirms it consumed both UX files plus the archived base architecture — clean brownfield lineage.

### UX ↔ PRD Alignment

Every one of the 12 UX requirements (UX-DR1…UX-DR12, mirrored in EXPERIENCE.md's pattern tables) traces to a PRD FR:

| UX spec | PRD FR(s) | Aligned |
|---------|-----------|---------|
| UX-DR1 trip-stop-row | FR-2, FR-11, FR-12 | ✓ |
| UX-DR2 leg-connector | FR-3 | ✓ |
| UX-DR3 leg-mode-pill | FR-19, FR-20, FR-23 | ✓ |
| UX-DR4 schedule pickers | FR-26, FR-28, FR-29, FR-30 | ✓ (see gap) |
| UX-DR5 fidelity badge + Mock note | FR-7, FR-8, FR-9, FR-10 | ✓ |
| UX-DR6 leg-time inline edit + reset | FR-25 | ✓ |
| UX-DR7 Start/Finish footer | FR-31, FR-32, FR-33 | ✓ |
| UX-DR8 "Over limit" chip | FR-28 | ✓ |
| UX-DR9 microcopy/voice | UiStrings (NFR6) | ✓ |
| UX-DR10 a11y floor | FR-17, FR-18, NFR7 | ✓ |
| UX-DR11 undefined/Any leg | FR-20 | ✓ |
| UX-DR12 multi-day rollover | FR-27 | ✓ |

- **No orphan UX scope** — no UX requirement introduces capability absent from the PRD.
- **No unserved PRD UI requirement** — every FR with a UI surface (columns, tooltips, connector, pickers, footer) has a matching UX spec.
- **Key flow** ("Yurik plans a 4-day Wrocław run") exercises FR-1, FR-3, FR-19–21, FR-25, FR-26, FR-29, FR-32, FR-27 end-to-end — a good acceptance narrative.

### UX ↔ Architecture Alignment

Every UX component has an explicit home in the architecture's file tree and Requirements-to-Structure map:
- trip-stop-row → `TripStopList.razor` [MOD]; leg-connector → `LegConnector.razor` [NEW]; leg-mode-pill → `LegModePill.razor` [NEW]; schedule-picker → `TripScheduleControls.razor` [NEW or MOD]; fidelity badge → `FidelityBadge.razor` [MOD].
- `.razor` components kept presentational (raise VM commands, no service calls) — consistent with UX's interaction primitives and your project's layering rule.
- Tokens unchanged → no design-system/architecture drift.

### Alignment Issues / Warnings

- ⚠️ **[MEDIUM] Native `<input type="time">` cannot express a multi-day duration (UX-DR4 / FR-28).** DESIGN.md specifies the **Time limit duration** entry as a native `time` (HH:MM) input, and RD10 echoes "entered as an HH:MM duration." But a native HTML `time` input is a **time-of-day** control capped at **23:59** — it cannot represent a limit longer than 24h. Feature G's entire premise is **multi-day** trips (the worked example is 4 days ≈ 90h). So the *duration* entry path silently can't express the limits multi-day trips most need. The PRD's **finish-by deadline** alternative (FR-29, `datetime-local`) *does* cover multi-day and is likely the intended path for long limits — but the plan should either (a) explicitly scope the HH:MM duration input to ≤24h limits and steer multi-day users to the deadline path, or (b) replace the native `time` control for Time-limit with a duration control that exceeds 24h. **Recommendation:** confirm at Story 4.3 design time; add an acceptance criterion covering a >24h limit. *(Dwell via `time` HH:MM — UX-DR4 — is fine; dwell >24h is implausible.)*
- ℹ️ **[LOW — already tracked] Schedule UI host undecided.** `TripScheduleControls.razor` is tagged `[NEW or MOD]` pending confirmation of whether start/limit controls are inline in `TripStopList` today. Architecture already lists this as a story-time gap; UX is host-agnostic, so no UX conflict — just noting consistency.
- ℹ️ **[LOW] Connector left-indent placement** is an `[ASSUMPTION]` in both UX (UX-DR2) and architecture (OQ-A), to be finalized at mock review. Mutually acknowledged, not a conflict.

## 5. Epic Quality Review

Validated 4 epics / 19 stories against create-epics-and-stories standards.

### A. Epic structure — user value & independence

| Epic | User-centric goal? | Independent of later epics? | Verdict |
|------|-------------------|------------------------------|---------|
| 1 — Readable Trip View takeover | ✓ "one trip-focused list, full names" | ✓ stands alone (markup reuse of existing VM) | PASS |
| 2 — Trustworthy & legible trip times | ✓ "trust the numbers" | ✓ shared-layer; could even precede Epic 1 | PASS |
| 3 — Honest per-leg travel modes | ✓ "drive in, walk between" | ✓ uses Epic 1 (connector) — backward, allowed | PASS |
| 4 — Multi-day schedule & honest finish | ✓ "reads in real days" | ✓ uses Epics 1–2 — backward, allowed | PASS |

- **No technical-milestone epics.** Even Epic 3 — which carries the schema migration — is framed as user value ("honest per-leg modes"), not "set up the database." ✓
- **No forward (Epic N → N+1) dependencies.** All cross-epic dependencies point backward (3→1, 4→1, 4→2). No circular dependencies. ✓

### B. Story sizing & acceptance-criteria quality

- **Sizing:** every story is single-concern and appropriately sized; no epic-sized stories; no "set up everything" stories. ✓
- **AC format:** all 19 stories use proper **Given/When/Then** BDD, are testable, specific, and trace each AC to FR/NFR IDs. Most include an explicit testing AC (bUnit / unit invariant / Trip integration filter per NFR8). This is **above-average AC rigor.** ✓
- **DB-timing:** the `OutgoingTravelMode` column is created in **Epic 3 Story 3.1** — i.e. in the epic that first needs per-leg mode, **not** front-loaded into Epic 1. Correct "create-when-needed" pattern. ✓
- **Brownfield shape:** integration points with existing systems (reuses `TripViewModel`, MCP, `RouteSegment` cache, ordering writer) ✓; migration/compatibility stories present (3.1 migration, 3.6 MCP contract migration, 4.5 verify-and-fix) ✓. Starter-template story correctly **omitted** (architecture declares N/A for brownfield; first story is the migration). ✓

### Findings by severity

#### 🔴 Critical Violations
- **None.**

#### 🟠 Major Issues

1. **Story 3.1 is not independently completable as written — it drops a still-referenced column.** Story 3.1's ACs both *add* `PoiCollectionItem.OutgoingTravelMode` **and** *drop* `PoiCollection.TravelMode` (RD1a "recommended"). But the code that references the old column is removed only **later**: the trip-wide → per-leg **projection** is Story 3.2, and the **trip-level mode selector UI** is Story 3.4. If 3.1 runs first and drops the column, the build breaks (references to a dropped property — and with `TreatWarningsAsErrors`, that's a hard break). So 3.1 cannot be completed and shipped in isolation, violating story independence.
   - **Mitigation already half-present:** RD1a documents a "leave it as a dead column" fallback — but the *story* AC as worded takes the drop path up front.
   - **Recommendation:** make the sequencing explicit — Story 3.1 should **only ADD** the new column (additive, independently shippable), and a **later story** (after 3.2 projection + 3.4 selector removal) drops `PoiCollection.TravelMode` once nothing references it. Alternatively, fold the reference removals into 3.1. Add an AC to whichever story performs the drop asserting no remaining references + green build.

#### 🟡 Minor Concerns

1. **Story 1.3 connector "reset" affordance is ambiguous vs Story 3.5.** Story 1.3 says it "builds the connector shell with time/distance/fidelity/**reset**," yet also "the mode pill and generalized **edit/reset** are deferred to Epic 3." This risks shipping a visible reset (↺) button in Epic 1 that does nothing until Story 3.5. **Recommendation:** clarify whether the reset control is *rendered-but-inert*, *hidden until Epic 3*, or *omitted from 1.3* — and align the AC wording.
2. **Epic numbering ≠ build order; the connector→pill cross-epic dependency must be made explicit at sprint planning.** The architecture's recommended implementation sequence puts the **Epic 3 migration (3.1) first** and interleaves epics (migration → Epic 2 shared-layer → Epic 3 projection/MCP → Epic 1 takeover+connector → Epic 4). The epics are **value-grouped, not build-ordered.** A hard cross-epic edge exists: **Epic 1 Story 1.3 (connector shell) must precede Epic 3 Stories 3.4 (pill) and 3.5 (edit on connector).** **Recommendation:** capture the build order + this edge in sprint planning so nobody builds strictly 1→2→3→4 and strands the pill on a missing connector.
3. **Story 4.3 should carry a >24h acceptance criterion** for the Time-limit duration input (ties to the §4 MEDIUM native-`time` finding). Without it, the multi-day duration-entry ceiling stays implicit.

### Best-Practices Compliance Checklist

| Check | Result |
|-------|--------|
| Epic delivers user value | ✅ all 4 |
| Epic can function independently (backward deps only) | ✅ all 4 |
| Stories appropriately sized | ✅ |
| No forward (Epic N → N+1) dependencies | ✅ |
| DB tables/columns created when needed | ✅ (1 caveat: 3.1 drop-sequencing — Major #1) |
| Clear, testable acceptance criteria | ✅ (strong) |
| Traceability to FRs maintained | ✅ 33/33 |
| Story independence | ⚠️ one exception — Story 3.1 (Major #1) |

## 6. Summary and Recommendations

### Overall Readiness Status

**READY — with two pre-implementation clarifications.**

This is a tightly-traced, mature planning set. The PRD (33 numbered FRs, each tied to an observed issue + code anchor), the architecture (RD1–RD13 with its own coverage validation and a resolved coupling risk), and the UX delta (12 specs, tokens unchanged, every spec homed in a file) are mutually consistent. **FR coverage is 100% (33/33), with zero gaps and zero orphans.** Epic/story structure passes the best-practices bar with strong, testable Given/When/Then ACs and correct brownfield shape. No Critical defects exist. The two items below are **clarifications to settle at story kickoff**, not redesigns — hence "ready," not "needs work."

### Issue Tally

- 🔴 Critical: **0**
- 🟠 Major: **1** (Story 3.1 drop-column sequencing)
- 🟡 Minor / Medium: **4** (native-`time` 24h ceiling [Medium]; connector reset ambiguity; epic-vs-build-order; >24h AC) + 2 low/tracked (schedule-host, connector placement)

### Critical Issues Requiring Immediate Action

- **None blocking.** Implementation can begin on the lowest-risk, dependency-free work immediately — RD8 desktop takeover (Epic 1) and the shared-layer RD5 "min" / RD4 reconciliation (Epic 2) have no schema dependency.

### Recommended Next Steps

1. **Fix the Story 3.1 drop sequence (Major #1).** Re-word Story 3.1 to **only ADD** `OutgoingTravelMode`; move the `PoiCollection.TravelMode` **drop** to a later story (after the projection 3.2 and selector-removal 3.4 eliminate all references), with an AC asserting "no remaining references + green build." Or adopt RD1a's "dead column" fallback until references are gone. This restores Story 3.1's independence.
2. **Resolve the multi-day Time-limit input (Medium, §4).** Decide whether the HH:MM duration entry is scoped to ≤24h (steering multi-day users to the finish-by-deadline path) or needs a non-native >24h duration control. Add a **>24h acceptance criterion to Story 4.3** either way.
3. **Make the build order + cross-epic edge explicit at sprint planning (Minor).** Document that epics are value-grouped, not build-ordered; the architecture's sequence runs migration → shared-layer → projection/MCP → takeover/connector → schedule/finish, and **Story 1.3 (connector) must precede Stories 3.4/3.5 (pill/edit)**.
4. **Clarify Story 1.3's reset affordance (Minor)** — rendered-but-inert vs deferred — so no dead control ships in Epic 1.
5. **(Story-time, already tracked)** Confirm `TripScheduleControls` as `[NEW]` vs `[MOD]`; finalize connector left-indent at mock review; confirm RD1a table-rebuild against the live schema.

### Final Note

This assessment reviewed 5 primary documents and identified **1 Major + 4 Minor/Medium issues** across 5 categories (document set, FR coverage, UX alignment, epic structure, story quality). **No issue blocks implementation.** Address Major #1 (a one-story re-sequencing) and decide the Time-limit input scope before the affected stories (Epic 3 Story 3.1; Epic 4 Story 4.3); the remainder are story-kickoff clarifications. The planning is otherwise implementation-ready and unusually well-traced for a brownfield delta.

---
**Assessed by:** Implementation Readiness workflow (PM — requirements traceability) · **Assessor:** Yurik · **Date:** 2026-06-15

### PRD Completeness Assessment (initial)

- **Strengths:** Every FR is uniquely numbered (FR-1…33) and traced to an observed issue (1–8) and to concrete code anchors (file:line). Schema-change scope is explicit (Feature F migration). Non-Goals and resolved/open assumptions are well-separated. Cross-surface impact is called out per requirement.
- **Watch items for traceability:** (a) NFRs are **categorical/unnumbered** — epic coverage must be checked against categories, not NFR IDs; (b) several FRs are "verify-then-fix" (FR-33/Feature H, FR-9) — epics must include verification tasks, not just build tasks; (c) the **mirror-to-mobile** deferral must be tracked as explicit out-of-scope in epics so it isn't silently dropped or silently built; (d) MCP contract change (FR-24) spans Epic 3 territory.
