---
title: UX Spine Review — PRD Fidelity
reviewer-lens: PRD FIDELITY (coverage, faithfulness, anti-invention)
prd-source: prds/prd-maps_editor-2026-06-11/prd.md (+ addendum.md)
spines-reviewed:
  - ux-maps_editor-2026-06-11/DESIGN.md
  - ux-maps_editor-2026-06-11/EXPERIENCE.md
status: complete
created: 2026-06-11
---

# PRD Fidelity Review — Trip Planning UX Spines

**Verdict:** Spines are highly faithful. Coverage of FR-1..FR-17 is near-complete and the four resolved decisions (OQ6, OQ4, FR-6, OQ8) are correctly applied. No blockers. Findings are concentrated in two omitted PRD edge rules and a few minor traceability gaps. No material invention beyond the PRD.

**Severity counts:** Blocker 0 · Major 2 · Minor 6

Legend: each finding gives Severity · Location · PRD ref · Issue · Concrete fix.

---

## MAJOR

### M1 — FR-13 "Unplaceable dwell still counts toward the timeline" rule is dropped
- **Severity:** Major
- **Location:** EXPERIENCE.md — State Patterns "Unplaceable POI" row; Component Patterns "Stop list"; Flow 1 edge. DESIGN.md "Itinerary timeline".
- **PRD ref:** FR-13 consequence: *"Unplaceable Stops (FR-4) are skipped by the Leg walk; if such a Stop carries a Dwell Time it still contributes its Dwell to the running total at its ordered position, but adds no Travel Time."* (§9 lists this as a load-bearing assumption.)
- **Issue:** Both spines describe Unplaceable Stops only as "excluded from routing / never silently dropped." They never state that an Unplaceable Stop's Dwell Time still accrues into the timeline. This is a defined, testable timeline behavior with a direct UX surface (the timeline value shown next to an unplaceable stop row), and it is flagged load-bearing in the PRD. Leaving it unspecified risks the build excluding unplaceable dwell entirely — a silent contradiction of FR-13.
- **Fix:** Add to the EXPERIENCE.md "Unplaceable POI" state (and/or the Stop list row spec): "An Unplaceable Stop adds no Travel Time (no leg in/out), but if it carries a Dwell Time that Dwell still accrues to the running timeline at its ordered position (FR-13)." Mirror in DESIGN.md timeline note.

### M2 — FR-13 timeline computation rule "Start's Dwell counted once at the beginning" + Roundtrip closing-leg return arrival not carried
- **Severity:** Major
- **Location:** DESIGN.md "Itinerary timeline" component; EXPERIENCE.md Component Patterns / State Patterns (timeline).
- **PRD ref:** FR-13: *"arrival(1)=TripStart; departure(k)=arrival(k)+Dwell(k); arrival(k+1)=departure(k)+TravelTime(Leg k→k+1). For a Roundtrip, a final return arrival back at the Start uses the closing Leg's Travel Time; the Start's own Dwell is counted once, at the beginning."*
- **Issue:** The spines show the timeline's *outputs* (relative offset + wall-clock, finish/return time) faithfully, but never state the two non-obvious accumulation rules: (a) the Roundtrip produces a distinct **return arrival back at Start** computed from the closing leg, and (b) the Start's Dwell is counted **once, at the beginning** (not re-added on return). Flow 1's "back to hotel by 18:10" implies the return arrival but does not pin the rule. Without it, a builder could double-count the hotel dwell or omit the return-arrival readout.
- **Fix:** Add a one-line behavioral note to the timeline pattern: "Timeline accumulates Dwell then Travel along Stop Order; the Start's Dwell is counted once at the start; a Roundtrip appends a final return-to-Start arrival from the closing leg (FR-13)." This is behavior, not visual, so it belongs primarily in EXPERIENCE.md.

---

## MINOR

### m1 — Estimated→Measured upgrade / "recompute travel times" action has no UX surface
- **Severity:** Minor
- **Location:** EXPERIENCE.md State Patterns ("Routing provider down", "Leg computing"); Component Patterns ("Ordering actions" / no recompute control).
- **PRD ref:** FR-10 (*"When a Measured provider becomes available again, a recompute can upgrade Estimated values to Measured"*) and FR-11 (*"explicit 'recompute travel times' action"*; §9 load-bearing).
- **Issue:** The spines cover graceful degradation *down* to Estimated well, but never surface the *upgrade* path or the explicit "recompute travel times" action the PRD names. Users who configured a Measured provider after first compute have no described affordance to upgrade their badged-Estimated legs.
- **Fix:** Add a "Recompute travel times" action (note it as on-demand, like the other ordering actions) and a state note that Estimated legs can upgrade to Measured on the next compute trigger.

### m2 — FR-2 seed Stop Order on first toggle-on not mentioned
- **Severity:** Minor
- **Location:** EXPERIENCE.md "Trip View toggle" pattern; Flow 1 step 1.
- **PRD ref:** FR-1/FR-2: a one-time Stop Order seed (added-date ascending) is written on first toggle-on; FR-2 also: adding a POI appends as last Stop, removing re-compacts.
- **Issue:** The spines say "stop order persists" but never describe what order a never-ordered collection shows on first Trip-View open, nor the append-on-add / compact-on-remove behavior. These have a visible UX result (the initial numbering, and how membership edits reflow stops). Note: the *exact* seed key (added-date) is a backend detail, so only the *user-visible* consequence needs carrying.
- **Fix:** Add to the Trip View toggle / Stop list behavior: "First enable seeds a deterministic stop order; later collection edits append a new POI as the last stop and re-compact order on removal (FR-2)."

### m3 — FR-3 "drag reorders interior only; first/last drop does NOT transfer Start/Finish role" not stated
- **Severity:** Minor
- **Location:** EXPERIENCE.md "Stop list" + "Start/Finish controls"; Interaction Primitives ("drag to reorder").
- **PRD ref:** FR-3 / FR-14: pinned Start (1) and Finish (N) keep their slots; dragging a stop to first/last does not transfer the role — role changes only via FR-14.
- **Issue:** Spines correctly say Start/Finish are pinned and that TSP/drag reorder "interior" stops (Ordering actions row references it for TSP), but the **drag** pattern itself doesn't state that dropping at position 1/N won't steal the Start/Finish role. This is a real interaction-design decision a builder needs.
- **Fix:** Note on the Stop list / Start-Finish pattern: "Drag reorders interior stops only; dropping at the first/last slot does not reassign Start/Finish — that changes only via the Start/Finish control (FR-3, FR-14)."

### m4 — FR-15 "result is never worse than pre-sort" / no-Start-no-Finish optimization nuance not carried
- **Severity:** Minor
- **Location:** EXPERIENCE.md "Ordering actions" (TSP-Sort).
- **PRD ref:** FR-15: TSP result total Travel Time ≤ pre-sort; with no Start/Finish designated it optimizes without that pin.
- **Issue:** Spine describes TSP-Sort as "computes an efficient loop (p95 ≤ 3s)" — the perf target is carried, but the correctness guarantee ("never makes the loop worse") and the no-pin case are not. Low UX-surface, but worth a clause so copy/empty-states don't imply a pin is required.
- **Fix:** Add: "TSP-Sort never returns a worse order than the current one; it works with or without a designated Start/Finish."

### m5 — FR-7 sync precision (pan marker into viewport / scroll row into view) softened
- **Severity:** Minor
- **Location:** EXPERIENCE.md Interaction Primitives ("List ↔ map two-way sync"); Component "Fidelity"/sync; DESIGN.md route-leg.
- **PRD ref:** FR-7: selecting a list Stop **pans the map so the marker is within the viewport** and emphasized; clicking a marker **scrolls its list row into view**; reuses existing marker-click interop **without regressing popup/tooltip**.
- **Issue:** Spine says "selecting a stop pans/highlights its marker; clicking a marker scrolls its list row into view" — faithful on direction, but drops the explicit "no regression to current popup/tooltip behavior" constraint, which is a stated PRD consequence guarding existing behavior.
- **Fix:** Add the non-regression clause: "reuses the existing marker-click interop without regressing current popup/tooltip behavior (FR-7)."

### m6 — FR-16 MCP "read ordered stops + computed legs" read-surface and `/mcp` auth guard under-specified
- **Severity:** Minor
- **Location:** EXPERIENCE.md "Ordering actions" (MCP/AI assignment).
- **PRD ref:** FR-16: MCP exposes read (ordered stops + legs), assign Stop Order, set Start/Finish, set Dwell; honors existing `/mcp` auth guard (loopback/LAN bypass or API key); no new unauthenticated surface.
- **Issue:** Spine notes MCP "may also set Start/Finish/dwell" (good) but omits the **read** capability and the auth-guard / no-new-unauthenticated-surface constraint. The auth posture aligns with the spine's stated privacy-first posture, so carrying it is consistent.
- **Fix:** Note that MCP trip ops are read+write (read ordered stops/legs; assign order; set Start/Finish/dwell) and run under the existing authenticated `/mcp` guard — no new unauthenticated surface.

---

## Coverage Matrix (PRD FR → spine)

| PRD surface | Carried? | Where |
|---|---|---|
| FR-1 Trip View toggle on/off, persist | Yes | EXP toggle pattern, Flow 1 |
| FR-2 Seed + persist Stop Order | Partial (m2) | seed/append/compact missing |
| FR-3 Manual drag reorder | Yes (m3 nuance) | EXP Stop list |
| FR-4 Unplaceable flag, exclude from route | Yes | EXP state + Flow 1 edge |
| FR-5 Draw ordered legs incl. roundtrip closing | Yes | DESIGN route-leg, Flow 1 |
| FR-6 Road geometry when Measured; dashed+muted non-Measured | Yes (resolved) | DESIGN route-leg, build note |
| FR-7 List↔map sync | Yes (m5 nuance) | EXP primitives |
| FR-8 Travel Mode per Trip + Any/Air Manual slice | Yes | EXP mode selector + manual entry |
| FR-9 Per-leg time from provider + Fidelity badge | Yes | DESIGN/EXP fidelity badge |
| FR-10 Graceful degradation, honest Fidelity | Yes (upgrade gap m1) | EXP "Routing provider down" |
| FR-11 Cache/invalidate + bg compute + recompute action | Partial (m1) | recompute action missing |
| FR-12 Dwell per Stop | Yes | EXP Stop list dwell field |
| FR-13 Itinerary Timeline | Partial (M1, M2) | accumulation/unplaceable rules missing |
| FR-14 Start/Finish + Roundtrip default | Yes | EXP Start/Finish, Flow 1 |
| FR-15 TSP-Sort button, on-demand, p95≤3s | Yes (m4 nuance) | EXP ordering actions |
| FR-16 MCP order assignment | Yes (m6 nuance) | EXP ordering actions |
| FR-17 Discoverable toggle (filtered-results region, ≥2 placeable) | Yes | DESIGN toggle, EXP IA + toggle |

**Cross-cutting NFRs:** a11y aria-label/aria-live (EXP Accessibility Floor) ✓ · i18n UiStrings (EXP Voice and Tone) ✓ · incremental redraw (EXP primitives/state) ✓ · TSP p95≤3s (EXP ordering) ✓ · privacy/egress consent (EXP posture + primitives "Banned" + build note) ✓ · OSM/ODbL attribution (DESIGN Leaflet map + EXP Responsive) ✓ · dual desktop/`Mobile*Screen` paths ✓.

---

## Journeys (UJ-1/2/3 fidelity)

All three journeys are reflected faithfully with protagonist names and climax beats:
- **UJ-1 Yurik** — Flow 1: hotel as Start, blank Finish=roundtrip, drag two stops, **climax "back to hotel by 18:10"** (PRD: 18:10). Faithful. Note the spine adds a concrete "10:00 start → +8h05m" — consistent with OQ6 (both offset + wall-clock) and the PRD's 18:10, not invention.
- **UJ-2 Mara** — Flow 2: 25 stops, Drive, TSP-Sort, MCP "museums in morning, rooftop bar last," **climax "manual order sticks."** Engine-down edge carried. Faithful.
- **UJ-3 Priya** — Flow 3: mobile, Any/Air, adds two POIs, TSP-Sort, airport as Finish, **Manual flight time**, **climax "40 min of slack."** Overrun edge + the v1 "can't mix Drive ground with Air" limit both carried (matches §6.2). Faithful.

---

## Invention check (UX over-reach)

No material invention found. Items that *look* additive all trace to the PRD or resolved decisions:
- "—" em-dash for empty Air legs → OQ4 (resolved).
- Dashed + muted non-Measured legs → FR-6 / §9 (resolved).
- Relative offset + wall-clock timeline → OQ6 (resolved).
- Per-collection persistence → OQ8 (resolved).
- **Concrete numbers** ("+8h05m", "2h20m", "40 min slack") → illustrative, consistent with PRD UJ figures; acceptable.
- **One genuinely new item, correctly flagged as an assumption (not silent invention):** keyboard-accessible stop reorder mechanism (EXPERIENCE Accessibility Floor + Open items). This is *required* by the PRD's a11y NFR ("every interactive control operable by keyboard") yet the PRD never specified a non-drag reorder path, so the spine appropriately marks it `[ASSUMPTION]` and defers the mechanism to build. This is the right move, not over-reach — no action needed beyond keeping the flag.
- DESIGN palette/typography/elevation formalize the *already-implemented* design system (explicitly scoped as such); not new invention.

---

## Faithfulness check (contradictions / weakenings)

No contradictions of PRD requirements. The two Major findings (M1, M2) are **omissions of defined timeline behavior**, not contradictions — the spines don't state a *conflicting* rule, they simply leave the FR-13 accumulation/unplaceable-dwell rules unspecified. Everything the spines *do* assert aligns with the PRD. The honesty posture ("humble — beats Estimated badge beats confident lie") strengthens rather than weakens FR-10/SM-C2.
