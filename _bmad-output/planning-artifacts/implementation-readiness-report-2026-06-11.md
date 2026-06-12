---
stepsCompleted: [1, 2, 3, 4, 5, 6]
date: '2026-06-11'
project_name: 'maps_editor'
user_name: 'Yurik'
documentsAssessed:
  - prds/prd-maps_editor-2026-06-11/prd.md
  - prds/prd-maps_editor-2026-06-11/addendum.md
  - architecture.md
  - epics.md
  - ux-designs/ux-maps_editor-2026-06-11/DESIGN.md
  - ux-designs/ux-maps_editor-2026-06-11/EXPERIENCE.md
  - _bmad-output/project-context.md
---

# Implementation Readiness Assessment Report

**Date:** 2026-06-11
**Project:** maps_editor (Trip Planning for Collections)

## Document Inventory

| Type | Document | Format | Status |
|---|---|---|---|
| PRD | prds/prd-maps_editor-2026-06-11/prd.md | whole | ✅ final |
| PRD addendum | prds/prd-maps_editor-2026-06-11/addendum.md | whole | ✅ technical depth |
| Architecture | architecture.md | whole | ✅ complete (D1–D11) |
| Epics & Stories | epics.md | whole | ✅ 4 epics / 17 stories |
| UX — Design | ux-designs/ux-maps_editor-2026-06-11/DESIGN.md | whole | ✅ final |
| UX — Experience | ux-designs/ux-maps_editor-2026-06-11/EXPERIENCE.md | whole | ✅ final |
| Project context | _bmad-output/project-context.md | whole | ✅ brownfield rules |

**Excluded from assessment (QA scaffolding, not requirement sources):** `review-*.md`, `review-rubric.md`, `.decision-log.md` under the PRD and UX folders.

**Duplicates:** None — no whole+sharded conflicts.
**Missing required documents:** None — PRD, Architecture, Epics, and UX all present.

## PRD Analysis

### Functional Requirements

- **FR-1** — Toggle Trip View on/off for any Collection (off = plain collection; on = Stop Order badges + trip panel; state restored on reopen; no membership change).
- **FR-2** — Assign and persist Stop Order (1..N), contiguous/gap-free/unique; deterministic seed (added-date asc) on first open; add appends, remove re-compacts.
- **FR-3** — Manual reorder by drag; updates Order, Legs, Travel Times, timeline; overrides prior TSP-Sort; pinned Start/Finish keep slots (interior-only reorder).
- **FR-4** — Flag Unplaceable Stops (null lat/lon); exclude from map/Legs/Distance Matrix; keep in Trip; don't break numbering.
- **FR-5** — Draw ordered connecting Legs between consecutive Stops incl. roundtrip closing Leg (N legs roundtrip / N−1 open); redraw on reorder without full reload; numbered markers.
- **FR-6** — Road-shaped geometry for Drive/Walk/Cycle when provider returns Measured geometry; else straight connector; non-Measured visually distinct (line style).
- **FR-7** — Map and list stay in sync (select list ↔ highlight marker, click marker ↔ scroll row); reuse marker-click interop without regressions.
- **FR-8** — Select Travel Mode per Trip (Any/Air, Drive, Walk, Cycle); mode change invalidates+recomputes; Any/Air = Placeholder absent Manual; per-Leg Manual time for Any/Air overrides.
- **FR-9** — Obtain per-Leg Travel Time + distance from configured provider under the Trip's Mode, each carrying Fidelity; Measured for D/W/C when configured; total = Σ legs.
- **FR-10** — Graceful degradation with honest Fidelity: provider unreachable/no-route/out-of-coverage or Any/Air → Estimated haversine fallback; never blank/error; later upgrade Estimated→Measured.
- **FR-11** — Cache per `(FromStop, ToStop, Mode, Provider)`; reused until input changes; no-op reorder = no recompute; invalidate on coords/mode/provider/assumed-speed; explicit Estimated→Measured upgrade; off-thread compute with pending state.
- **FR-12** — Set Dwell Time per Stop on the Collection–POI membership; no dwell = zero; overnight = large dwell.
- **FR-13** — Compute Itinerary Timeline (arrival per stop + finish): Start dwell once; roundtrip return arrival; clock vs relative offsets; Unplaceable dwell counts; Placeholder propagation; optional soft budget overrun flag.
- **FR-14** — Designate Start (pinned Order 1) and optional Finish (pinned Order N); Finish unset = Roundtrip; distinct Finish = open path; no Stop holds two Order values.
- **FR-15** — "Sort in Traveling Salesman order" button (NN+2-opt over Distance Matrix); on-demand only; pins Start/Finish; result total ≤ pre-sort; interactive for N≤30; drag-overridable.
- **FR-16** — Assign Stop Order via MCP (read stops+legs, assign order, set Start/Finish, set Dwell); honors existing `/mcp` auth; persists like a drag; remains drag-editable.
- **FR-17** — Discoverable Trip View toggle in the filtered-results region (not a menu); present/enabled at ≥2 placeable POIs; both desktop and mobile.

**Total FRs: 17**

### Non-Functional Requirements

- **NFR1 (Performance)** — Distance Matrix + TSP-Sort for N≤30 within p95 ≤ 3 s warm (SM-5); incremental (not full-page) map redraw on reorder.
- **NFR2 (Reliability/degradation)** — Provider outage/out-of-coverage must never break Trip View; haversine Estimated fallback keeps every Leg populated + badged.
- **NFR3 (UI responsiveness/layering)** — ViewModel-driven (Component→ViewModel→Service→Data); long compute off-circuit via background job + `StateChanged`.
- **NFR4 (Accessibility)** — `aria-label`s on badges/legs/timeline; `aria-live` on computing; keyboard-accessible reorder (not drag-only); both desktop + `Mobile*Screen`; ≥44px touch targets + safe-area insets.
- **NFR5 (i18n)** — All new UI text via `UiStrings`; no hardcoded strings.
- **NFR6 (Observability)** — Log travel-time computations + provider failures, distinguishing Measured vs Estimated/Placeholder/Manual (feeds SM-3).
- **NFR7 (Privacy/data residency)** — Self-hosted providers keep coordinates in-deployment; any out-calling provider must surface egress to the operator before first out-call (firm guard).
- **NFR8 (Licensing)** — OSM-based provider's data = ODbL → UI must show OSM attribution on the map (both surfaces).
- **NFR9 (Cost)** — Mock + self-hosted OSRM incur no per-request cost; hosted BYO-key is the only metered option, opt-in, never default.
- **NFR10 (Counter-metrics)** — Don't force plain Collections to become Trips (SM-C1); keep recomputation rare via cache (SM-C2).

**Total NFRs: 10**

### Additional Requirements

- **Constraints/Guardrails** — Cost, privacy/data-residency, and licensing guardrails (§11); default experience fully self-hosted; out-calling providers strictly opt-in with egress surfaced.
- **Integration/Deployment** (§12) — Provider abstraction; optional OSRM docker-compose sidecar (not a launch dependency); EF Core schema migration (OrderIndex, Dwell, RouteSegment cache, trip fields); Leaflet interop extension; background job mirroring `PoiEnrichmentBackgroundService`; MCP trip tools; coverage-boundary degradation; manual OSM refresh in v1.
- **Assumptions** (§9) — Load-bearing: Dwell-on-membership; FR-11 cache/upgrade behavior; FR-13 timeline edge rules. Others are downstream-owned defaults (now settled by Architecture/UX).
- **Open Questions** (§8) — OQ1 (default Measured provider), OQ2 (per-Leg mode override, deferred), OQ3/4/6/8 (UX defaults), OQ5/7 (architecture). All have working defaults; none block.

### PRD Completeness Assessment

The PRD is **final and exceptionally complete** for a launch-grade brownfield feature. It anchors a strict Glossary, nests globally-numbered FRs under features, tags every assumption inline and indexes them, separates capability-level requirements from technical depth (addendum), and routes each Open Question to an owning downstream workflow with a documented working default — so no OQ blocks implementation. Each FR carries explicit "Consequences (testable)" that translate cleanly into acceptance criteria. Success metrics (SM-1–5) and counter-metrics (SM-C1/C2) are defined and FR-linked. No requirement gaps or ambiguities that would block epic coverage.

## Epic Coverage Validation

### Coverage Matrix

| FR | PRD Requirement (short) | Epic / Story | Status |
|---|---|---|---|
| FR-1 | Toggle Trip View on a Collection | Epic 1 / Story 1.2 | ✅ Covered |
| FR-2 | Establish & persist Stop Order (seed) | Epic 1 / Story 1.2 | ✅ Covered |
| FR-3 | Manual reorder by drag | Epic 1 / Story 1.5 | ✅ Covered |
| FR-4 | Flag Unplaceable Stops | Epic 1 / Story 1.6 | ✅ Covered |
| FR-5 | Draw ordered connecting Legs | Epic 1 / Story 1.3 | ✅ Covered |
| FR-6 | Road geometry when Measured | Epic 4 / Stories 4.1, 4.2 | ✅ Covered |
| FR-7 | Map ↔ list sync | Epic 1 / Story 1.4 | ✅ Covered |
| FR-8 | Select Travel Mode + Any/Air Manual | Epic 2 / Story 2.2 | ✅ Covered |
| FR-9 | Per-Leg Travel Time from provider | Epic 2 / Story 2.1 | ✅ Covered |
| FR-10 | Graceful degradation + Fidelity | Epic 2 / Story 2.3 | ✅ Covered |
| FR-11 | Cache + invalidate + upgrade | Epic 2 / Story 2.4 | ✅ Covered |
| FR-12 | Set Dwell Time per Stop | Epic 2 / Story 2.5 | ✅ Covered |
| FR-13 | Compute Itinerary Timeline | Epic 2 / Story 2.6 | ✅ Covered |
| FR-14 | Designate Start / Finish / Roundtrip | Epic 1 / Story 1.7 | ✅ Covered |
| FR-15 | "Sort in TSP order" button | Epic 3 / Story 3.1 | ✅ Covered |
| FR-16 | Assign Stop Order via MCP | Epic 3 / Story 3.2 | ✅ Covered |
| FR-17 | Discoverable Trip View toggle | Epic 1 / Story 1.2 | ✅ Covered |

### Missing Requirements

**None.** Every PRD FR has a traceable implementation path in a story. No story claims an FR that is absent from the PRD (no orphan coverage). The epics document's own FR Coverage Map matches this independent re-derivation exactly.

**Note on FR-6:** Covered in Epic 4 (optional Phase 2). The PRD §6.1 lists "road geometry when a Measured provider supplies it" as in-scope, while §6.2/Architecture D2a mark OSRM itself as *not a launch dependency*. This is an intentional, documented phasing — the line-solidity/dashed-vs-solid rendering baseline ships in Epic 1, and actual road geometry activates only when a Measured provider (OSRM) is enabled. Not a gap, but flagged here for the readiness decision (see final report): a v1 launch on the Mock provider ships FR-6's fallback behavior but not its Measured behavior until Epic 4 is built.

### Coverage Statistics

- Total PRD FRs: **17**
- FRs covered in epics: **17**
- Coverage percentage: **100%**
- Orphan coverage (in epics, not in PRD): **0**

## UX Alignment Assessment

### UX Document Status

**Found** — two whole documents: `DESIGN.md` (visual spine: tokens, palette, Trip View component specs) and `EXPERIENCE.md` (behavior, states, interactions, accessibility, key flows). EXPERIENCE.md explicitly cites the PRD + addendum as sources.

### UX ↔ PRD Alignment

- **User journeys map 1:1.** EXPERIENCE Flows 1/2/3 correspond exactly to PRD UJ-1 (Yurik desktop loop), UJ-2 (Mara AI ordering), UJ-3 (Priya mobile feasibility), including the same edge cases (unplaceable POI, provider down, overrun flag).
- **Honesty model fully reflected.** PRD's Fidelity contract (Measured/Estimated/Placeholder/Manual) and "never dress a guess as a fact" are realized as the DESIGN line-solidity rule, fidelity badges, and the aggregate-honesty rule.
- **Open Questions resolved consistently.** UX settles OQ4 (em-dash for unmeasured, "quieter") and OQ8 (Trip View persists per-collection) — both consistent with the PRD's stated assumptions; no contradiction.
- **No UX-only requirements that contradict the PRD.** Everything in the UX traces back to a PRD FR/NFR or a PRD assumption.

### UX ↔ Architecture Alignment

- **Architecture supports every UX affordance.** Dual-surface (desktop + `Mobile*Screen`), `StateChanged`-driven incremental redraw, off-circuit compute (NFR3), and `UiStrings` are all pinned in Architecture's Frontend/Pattern sections.
- **DESIGN line-solidity rule → D6.** "Only Measured legs render solid" is encoded in the Architecture's map-rendering decision (LRM custom `IRouter`, dashed great-circle for Air/non-Measured) and Format Patterns ("Fidelity authoritative on every leg").
- **Aggregate-honesty rule → Architecture format pattern.** "Totals inherit the lowest fidelity among summed legs" appears verbatim in both DESIGN.md and Architecture's Format Patterns.
- **UX build-blocker resolved at architecture time.** EXPERIENCE flagged the keyboard-reorder mechanism as `[ASSUMPTION]` to be specified; Architecture **D8** resolves it concretely (focusable move-up/move-down controls, `aria-live`, both surfaces). Gap closed before stories — and Story 1.5 carries it.

### Alignment Issues

**None blocking.** UX, PRD, and Architecture are mutually consistent on the load-bearing decisions (fidelity model, persistence granularity, dual-surface, keyboard a11y).

### Warnings

- **Egress-consent placement (deferred, non-blocking):** EXPERIENCE marks the out-calling-provider consent notice as "firm rule, placement TBD." Architecture enforces the guard in the provider-selection path; exact UI copy/placement is a build-time detail. **No out-calling provider ships in v1** (Mock default; OSRM is self-hosted/in-deployment), so this is latent, not active — flagged for whenever a scrape/BYO-key provider is added.
- **`TripStartTime` timezone handling** (UTC vs local display) is a noted nice-to-have detail left to story time — non-blocking.

## Epic Quality Review

Reviewed all 4 epics / 17 stories against create-epics-and-stories standards: user value, epic independence, forward dependencies, story sizing, AC quality, DB-creation timing, and FR traceability.

### Per-Epic Compliance Checklist

| Check | Epic 1 | Epic 2 | Epic 3 | Epic 4 |
|---|---|---|---|---|
| Delivers user value (not a technical milestone) | ✅ | ✅ | ✅ | ✅ |
| Functions independently (no forward epic dep) | ✅ | ✅ (needs E1) | ✅ (needs E1+E2) | ✅ (optional; enriches E2) |
| Stories appropriately sized | ✅ | ⚠️ see 2.1 | ✅ | ✅ |
| No forward (later-story) dependencies | ✅ | ✅ | ✅ | ✅ |
| DB tables created when needed | ⚠️ see 1.1 | ✅ | ✅ | ✅ |
| Clear, testable Given/When/Then ACs | ✅ | ✅ | ✅ | ✅ |
| Traceability to FRs maintained | ✅ | ✅ | ✅ | ✅ |

### Epic Independence

- **Epic 1** stands fully alone — an ordered, mapped, persisted loop with no travel-time dependency.
- **Epic 2** builds only on Epic 1 (backward dependency — allowed).
- **Epic 3** builds on Epic 1 + Epic 2 (its Distance Matrix reads the cache that Epic 2's provider/compute populate). Backward only.
- **Epic 4** is optional and enriches Epic 2's provider seam; nothing earlier requires it.
- **No epic requires a later epic.** No circular dependencies. ✅

### 🔴 Critical Violations

**None.** No technical-milestone epics; no forward dependencies breaking independence; no epic-sized unimplementable stories.

### 🟠 Major Issues

**None.**

### 🟡 Minor Concerns (non-blocking — recommendations)

1. **Story 1.1 creates all trip schema upfront (incl. `RouteSegment`, first used in Epic 2).** This deviates from the "create tables only when the story needs them" guideline. **Verdict: accept.** It is mandated by Architecture **D1** — SQLite's limited `ALTER` plus the single-`AddTripPlanning`-migration-via-`MigrateAsync` convention make one consolidated migration safer than several incremental ones, and the Architecture explicitly names this as the first story. Deliberate and justified, not accidental churn. *No action required; rationale is documented.*

2. **Story 2.1 is the heaviest story** — it introduces the provider contract + haversine Mock + the background compute service + the `RouteSegment` write + the per-leg display/badge in one story. **Recommendation:** if it exceeds a single dev-agent session in practice, split into **2.1a** (provider contract + Mock impl) and **2.1b** (background compute service + cached display/badge). Flagged as a sizing watch-point, not a defect — the ACs are cleanly separable along that seam.

3. **Story 2.4's Estimated→Measured upgrade path cannot be fully exercised in a Mock-only v1** (no Measured provider exists until Epic 4). The mechanism is still independently *testable* (e.g. via a stub Measured provider in unit/integration tests) and is not a forward dependency — the code is complete and correct without Epic 4. **Recommendation:** the story's tests should use a fake Measured provider so the upgrade path is verified before Epic 4 ships.

4. **Story 1.3 renders dwell-field and timeline-value "placeholders"** that are populated by Epic 2 (Stories 2.5/2.6). This is **not** a forward dependency — 1.3 renders empty/inert slots and is fully completable alone; Epic 2 fills them later. Noted only to confirm it was checked and is compliant.

### Brownfield Indicators (correct for this project)

- No starter-template story (Architecture explicitly: none — brownfield). ✅ Correct that Story 1.1 is the EF migration, not a project-init.
- Integration/compatibility is handled in-place: migration via existing startup `MigrateAsync`, MCP tools on the existing `/mcp` slice, Leaflet interop extension, background service mirroring `PoiEnrichmentBackgroundService`. ✅

### Story-Quality Verdict

All 17 stories use proper Given/When/Then BDD structure, each AC is independently testable, edge/error cases are present (unplaceable, provider-down, out-of-coverage, mixed-fidelity totals, budget overrun, coverage-boundary degradation), and every story references the specific FR(s)/AR(s)/UX-DR(s) it implements. No vague or non-measurable criteria found.

## Summary and Recommendations

### Overall Readiness Status

**✅ READY FOR IMPLEMENTATION**

The planning set (PRD + addendum, Architecture, UX Design+Experience, Epics+Stories) is complete, mutually consistent, and traceable end to end. FR coverage is 100% (17/17), UX/PRD/Architecture are aligned on every load-bearing decision, and the epic/story structure passes best-practice review with zero critical and zero major violations.

### Issue Tally

| Severity | Count | Blocking? |
|---|---|---|
| 🔴 Critical | 0 | — |
| 🟠 Major | 0 | — |
| 🟡 Minor | 4 | No |

### Critical Issues Requiring Immediate Action

**None.** Nothing blocks the start of implementation.

### Minor Items to Keep in View (not blockers)

1. **Story 2.1 sizing** — split into provider-contract+Mock and background-compute+display if it overflows a single dev session.
2. **Story 2.4 upgrade test** — verify the Estimated→Measured path with a stub Measured provider, since no real one ships until Epic 4.
3. **FR-6 phasing** — a v1 launch on the Mock provider ships FR-6's straight-line fallback but not Measured road geometry until Epic 4 (optional) is built. Confirm that matches launch intent.
4. **Egress-consent guard** — latent (no out-calling provider in v1); wire the consent surface when/if a scrape or BYO-key provider is added.

### Recommended Next Steps

1. **Proceed to sprint planning** — run `bmad-sprint-planning` to turn the 4 epics / 17 stories into a tracked sprint status.
2. **Create the first story** — run `bmad-create-story` for **Story 1.1** (the `AddTripPlanning` EF migration), the foundation that unblocks everything else.
3. **Confirm the v1 launch boundary** — decide explicitly whether v1 ships on the Mock provider (Epics 1–3) with Epic 4 (OSRM) as a fast-follow, and record that decision.
4. **Carry the four minor items** into story execution as watch-points (sizing of 2.1, stub-Measured test for 2.4, FR-6 phasing, egress guard).

### Final Note

This assessment identified **4 minor issues** across **3 categories** (story sizing, test coverage, and phasing/guard placement) and **zero critical or major issues**. None block implementation; all are documented with rationale and recommendations. The artifacts may be used as-is to begin Phase 4 implementation.

---

**Assessment date:** 2026-06-11
**Assessor:** Implementation Readiness workflow (acting PM) · for Yurik
**Documents assessed:** PRD + addendum, Architecture, Epics+Stories, UX Design + Experience, project-context
