# Research ↔ PRD Reconciliation — Travel-Time / Distance Estimation

Source: `research/technical-travel-time-distance-estimation-research-2026-06-23.md`
Reconciled against: `prds/prd-maps_editor-2026-06-23/prd.md` + `addendum.md`
Date: 2026-06-23

This is a list of places where the research contains material the PRD/addendum dropped or
distorted. Intentional, recorded decisions (e.g. "remove OSRM" vs research's "keep as legacy if
desired") are NOT flagged as gaps.

---

## Gaps (research material missing or weakened in PRD/addendum)

### 1. "Fidelity ladder" collapsed from THREE rungs to TWO — the research's core framing
- **Research says:** The decision's headline payoff is a *three-rung* ladder: "**estimate → good
  estimate → measured**" (raw straight-line → smart-haversine → Valhalla), "with NFR7 preserved at
  every rung." The research treats the smart-haversine as a distinct, nameable middle rung ("good
  estimate").
- **PRD coverage:** The PRD repeatedly compresses this to "**estimate → measured**" (Summary §1,
  Goals §2) and FR-17 explicitly decides a *two-badge* model where smart-haversine "improves the
  accuracy *behind* the Estimated rung rather than adding a third visible tier." OQ-6 flags this as
  `[confirm]` leaning two-badge.
- **Note:** The two-badge UI is a defensible product decision, BUT the PRD's prose ("estimate →
  measured") silently drops the research's three-rung conceptual model even in narrative sections,
  which understates what the feature delivers (the raw straight-line rung still exists as the bottom
  of the ladder). The distortion is the *conflation of the conceptual ladder with the badge count*.
- **Severity: MED**

### 2. Valhalla's lower-RAM rationale tied to *tile-based on-demand loading* vs OSRM full-graph mapping
- **Research says:** Valhalla is "**Tile-based, on-demand loading → lower RAM (~4–8 GB) than OSRM
  mapping the whole graph.**" This is given as one of the two architectural traits that make Valhalla
  win (the other being one-engine-all-modes).
- **PRD coverage:** Captured — NFR Performance (§7) states "tile-based on-demand loading targets lower
  RAM (~4–8 GB) than OSRM's full-graph mapping." Number and rationale preserved.
- **Severity: LOW (covered)**

### 3. Valhalla provenance / engine identity (C++, ex-MapQuest)
- **Research says:** "C++ engine (originally MapQuest)."
- **PRD coverage:** Not in PRD; not in addendum. Minor color/provenance, no functional impact.
- **Severity: LOW**

### 4. `server_threads=2` env var in the compose snippet
- **Research says:** The sample compose includes `server_threads=2` alongside `tile_urls`.
- **PRD coverage:** PRD FR-11/FR-12 say "one env var (`tile_urls`)". Addendum's compose block DOES
  retain `server_threads=2`. So preserved in addendum, but the PRD's "exactly one env var" framing
  glosses over the second tuning var. Minor.
- **Severity: LOW**

### 5. Auto-rebuild on `.pbf` change
- **Research says:** docker-valhalla "auto-rebuild on `.pbf` change."
- **PRD coverage:** Captured in FR-11 ("auto-rebuilds when the `.pbf` changes") and NFR Performance.
- **Severity: LOW (covered)**

### 6. Itinero maturity specifics — version 1.5.1, early-2024 last update, Itinero 2 "years without release"
- **Research says:** Detailed maturity evidence: stable line **1.5.1**, main repo last substantive
  update **early 2024**, Itinero 2 "in development for years without release," "low-activity
  single-maintainer project," plus concrete risk list (turn restrictions/one-ways less battle-tested;
  in-process OOM/crash lands in app process; must verify clean build under strict analyzers).
- **PRD coverage:** PRD §6 Scope and §11 reduce Itinero to "fallback only; maturity risk." The
  *addendum* preserves the specifics (1.5.1, early 2024, Itinero 2 unreleased, OOM-in-app, analyzer
  build check). So preserved in addendum, thinly in PRD. Acceptable since it's a rejected option.
- **Severity: LOW**

### 7. Turnkey-OSRM (Option A) rationale — "treats the symptom, not the cause"
- **Research says:** Option A (one-shot `osrm-prep` init service) is rejected because OSRM's
  one-profile-per-backend model is inherent: "three containers and three preprocessing passes remain
  inherent, and changing a profile means a full graph rebuild."
- **PRD coverage:** Addendum "Rejected / deferred options" preserves this verbatim-ish. PRD itself
  only references OSRM's friction, not Option A's specific rejection. Fine — provenance lives in
  addendum.
- **Severity: LOW**

### 8. "Changing a profile means a full graph rebuild" — OSRM pain point
- **Research says:** Stated as a concrete OSRM limitation (and a reason Valhalla's per-request costing
  wins).
- **PRD coverage:** PRD §3 Background captures it ("Changing a profile means a full graph rebuild").
  Covered.
- **Severity: LOW (covered)**

### 9. External SaaS — "strictly-consented opt-in" admissibility and the specific vendor list
- **Research says:** External routing (Google Routes / Mapbox / OpenRouteService / GraphHopper
  Directions) is "**admissible only as a strictly-consented opt-in**," not categorically banned.
- **PRD coverage:** PRD §4 out-of-scope and §11 follow-ons preserve "strictly-consented opt-in."
  Vendor list preserved in addendum. Covered.
- **Severity: LOW (covered)**

### 10. Open question: "Valhalla routing accuracy vs OSRM on representative regions"
- **Research says:** Listed as an explicit implementation-phase open question.
- **PRD coverage:** Carried as OQ-2. Covered.
- **Severity: LOW (covered)**

### 11. Open question: exact per-mode correction factors "sourced OR empirically tuned"
- **Research says:** Two acquisition paths — *sourced* (from literature) or *empirically tuned*.
- **PRD coverage:** FR-2 / OQ-1 say "sourced/empirically tuned." Covered. The specific example factor
  in research ("drive ≈ ×1.3") is expanded in FR-2 to a full set (Drive 1.3 / Cycle 1.2 / Walk 1.15)
  — note these Cycle/Walk numbers are PRD-introduced assumptions, NOT in the research (research only
  gave drive ≈ ×1.3). Reasonable, but flag that they originate in the PRD, not the source.
- **Severity: LOW**

### 12. The phrase / positioning "an afternoon of ops, not a product feature"
- **Research says:** Central motivating quote characterizing the OSRM path.
- **PRD coverage:** Reused in §1 and §3. Covered.
- **Severity: LOW (covered)**

---

## Contradictions (PRD vs research)

### C-1. OSRM removal — INTENTIONAL DECISION, not a gap (excluded per instructions)
- Research left open: "keep OSRM as legacy if desired" (Decision item 3; Open Q "Whether OSRM is
  removed outright or retained as a legacy provider").
- PRD deliberately decides **full removal** (Summary, FR-14, §8 Migration), and records it as a
  conscious breaking-change decision.
- **Per task instructions, this is an intentional recorded decision and is NOT flagged as a gap.**

No other PRD↔research contradictions found. All canonical constraints (NFR7 privacy hard constraint,
NFR8 ODbL attribution, seconds/meters units, off-circuit degrade `[TRIP-DEGRADE-01]`, never-downgrade
Manual/Measured) are faithfully carried.

---

## Summary of severity
- **HIGH:** none.
- **MED:** Gap 1 (three-rung "estimate → good estimate → measured" ladder collapsed to two in PRD
  prose).
- **LOW:** everything else — mostly preserved in the addendum or PRD; remainder is minor provenance/
  color.
