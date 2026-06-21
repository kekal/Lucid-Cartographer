---
title: "Bulk Travel Mode Assignment — PRD"
status: final
created: 2026-06-20
updated: 2026-06-20
amended: 2026-06-20 (FR-13 reversed during implementation)
project: maps_editor
feature_code: TRIP-BULKMODE-01
---

# Bulk Travel Mode Assignment — PRD

## 1. Overview

Trip View lets a planner assign a travel mode (Drive / Walk / Cycle / Any/Air) to **each individual leg** between consecutive stops. Two key actions — **Sort in Traveling Salesman order** and **Recompute travel times** — only become available once every leg has a settled travel time. Legs left in the default **Any/Air** state are never auto-computed, so they keep both actions disabled indefinitely.

Today the only way to bring a trip into a computable state is to set the mode on every leg, one at a time. For a multi-stop trip that is one selection per leg before anything useful happens.

This feature adds a **single control that assigns a travel mode to all legs of the active trip at once**, with an explicit choice of whether to overwrite legs that already carry a mode. It turns an N-action chore into one deliberate action and unblocks the trip's primary controls.

### 1.1 Glossary

| Term | Meaning |
|------|---------|
| **Leg** | A directional travel segment between two consecutive trip stops (and, on a roundtrip, the closing segment back to the start). |
| **From-stop** | The originating stop of a leg; a leg's travel mode is stored on its from-stop. |
| **Travel mode** | One of Drive / Walk / Cycle / Any/Air, assigned per leg. (Code enum form: `AnyAir`.) |
| **Any/Air** | The default, unset mode. Such legs are never auto-computed, so they have no travel time. |
| **Computable / settled** | A leg whose travel time has been computed (has a settled value), as opposed to one still unknown ("—"). |
| **Manual time** | A travel time the user typed for a leg, protected from background recompute. |

## 2. Goals & Success Metrics

**Primary goal.** Let a planner make an entire trip computable (or switch its whole mode) in a single action.

| Metric | Baseline | Target |
|--------|----------|--------|
| Actions to make every leg computable | N (one per leg) | 1 |
| Accidental overwrites of manually-set leg modes | n/a | 0 — overwriting is only possible behind an explicit opt-in |
| Sort / Recompute reachable after one bulk assignment on an all-Any trip | No | Yes (once background compute settles) |

**Counter-metric.** A planner who has hand-tuned individual leg modes must not lose that work by default; the bulk action must default to a non-destructive behavior.

## 3. Scope

**In scope**
- A bulk travel-mode control in the Trip stops panel header.
- Applying a chosen mode across all legs of the active trip, including the closing leg of a roundtrip.
- An explicit toggle controlling whether already-assigned legs are overwritten.
- Persisting the per-leg assignments through the existing single writer and triggering the existing background recompute.

**Out of scope**
- Changing stop order, start/finish selection, or the time budget.
- Reworking how leg-time computability (`IsAnyLegComputing`) is defined — this feature only gives a convenient way to reach a computable state.
- Adding new travel modes or providers beyond the existing Drive / Walk / Cycle / Any/Air.
- Selecting a subset of legs (this is all-legs; per-leg control already exists).

## 4. Users & Context

Single-operator, self-hosted map-planning tool. The user is the trip planner working in Trip View on a desktop browser. No multi-stakeholder flow, no separate persona/journey section is warranted at this scale. The mobile Trip panel exists but is out of scope for the first release (see Open Items).

## 5. Functional Requirements

### 5.1 The bulk control

- **FR-1.** A bulk travel-mode control appears in the Trip stops panel header, alongside the Sort / Recompute actions, and is shown under the same condition those actions are (when the trip has at least one leg).
- **FR-2.** The control offers exactly these modes: **Drive, Walk, Cycle, Any/Air**, presented consistently with the existing per-leg mode affordance.
- **FR-3.** The control includes a paired **"overwrite legs that already have a mode"** toggle, defaulting to **off** (non-destructive).
- **FR-4.** The mode selector opens in an unselected/placeholder state so that choosing a mode is always a deliberate act, never an accidental side effect of rendering. [ASSUMPTION A5]

### 5.2 Assignment behavior

- **FR-5.** With the overwrite toggle **off**, choosing a mode assigns it **only to legs currently in Any/Air** (unset). Legs that already carry an explicit mode are left unchanged.
- **FR-6.** With the overwrite toggle **on**, choosing a mode assigns it to **every leg** of the trip, replacing any existing per-leg mode (including manually set ones).
  - **[NOTE FOR PM]** Switching a leg's mode changes its travel-time cache key, so a leg that had a **Manual time** under its old mode will read "—" under the new mode until recomputed or re-entered. This is a consequence of the user's explicit overwrite-on action (distinct from the background-recompute protection in FR-12 / AC-7). **Open decision:** require a confirm prompt before an overwrite-on assignment that would clear Manual times? (Tracked as A6 in §9.)
- **FR-7.** Assignment covers all directional legs of the trip in the same composition the trip itself uses: each consecutive stop-to-stop leg, plus the closing leg back to the start on a roundtrip.
- **FR-8.** Choosing **Any/Air** in bulk returns the affected legs to Any/Air. Their travel times revert to unknown ("—") and Sort / Recompute become disabled again — this is a valid outcome, not an error. Existing ground-mode cache rows are not recomputed or discarded; they simply stop being referenced while the legs sit at Any/Air.
- **FR-9.** A bulk assignment changes only per-leg travel mode. It must not alter stop order, start/finish selection, or the time budget.

### 5.3 Persistence & recompute

- **FR-10.** Each affected leg's mode is persisted through the existing single source-of-truth writer for per-leg mode — no new parallel write path is introduced.
- **FR-11.** After a bulk assignment, the trip's projections refresh and the background travel-time computation is triggered for ground modes, exactly as a single per-leg change does today. Once all legs settle, Sort / Recompute enable automatically.
- **FR-12.** A bulk assignment must never overwrite or downgrade a leg's manually entered travel time as part of background recompute (the existing Manual/Measured protection holds).

### 5.4 Availability

- **FR-13.** (Corrected 2026-06-20 during implementation.) The bulk control must **not** gate on `IsAnyLegComputing`. Because an Any/Air leg has no settled time, `IsAnyLegComputing` is true precisely on the trips the control exists to fix — gating on it would make the control permanently unusable in its main scenario (and overwrite-off could never fire). The control is always enabled when legs are present, and is disabled only transiently while its own bulk request is in flight (anti-double-submit).

## 6. Non-Functional Requirements

- **NFR-1.** The view component issues only a view-model command; it does not touch services or the database directly (consistent with the project's existing presentation-layer rule).
- **NFR-2.** A bulk assignment is one logical operation: a single projection refresh and a single state-change notification at the end, with no more than one visible flip of the action buttons' enabled state.
- **NFR-3.** Idempotent: re-applying the same mode with the same toggle produces no duplicate state and no errors, and never auto-downgrades an existing Manual/Measured cache row (the existing upsert guard holds — see addendum §A/§D).
- **NFR-4.** Accessibility: the selector and toggle expose labels/titles, sourced through the shared UI-strings layer; no hard-coded display text.
- **NFR-5.** A bulk assignment over a realistic trip (tens of stops) persists as a single write transaction with no per-leg database round-trip, and triggers exactly one projection refresh (per NFR-2) — not one per leg.

## 7. UX Notes

- The control lives in the existing header action row next to Sort / Recompute (inline, not a new full-width row). [ASSUMPTION A4]
- The overwrite toggle is visually subordinate to the selector (smaller/secondary), since it modifies the selector's behavior rather than being a standalone action.
- The selector's resting label reads as an invitation to act on the whole list (e.g. "Set mode for all…") rather than showing a current value.
- Optional: a light hint when Any/Air is chosen in bulk, noting times will show "—". Deferred. [ASSUMPTION A3]
- Detailed component/file touchpoints are in `addendum.md`.

## 8. Acceptance Criteria

- **AC-1.** On a trip where every leg is Any/Air, selecting **Drive** (overwrite off) assigns Drive to all legs; after background compute, Sort and Recompute become enabled.
- **AC-2.** On a trip where some legs are already Walk: with overwrite **off**, selecting **Drive** changes only the Any/Air legs; the Walk legs stay Walk.
- **AC-3.** Same trip with overwrite **on**: selecting **Drive** moves every leg (including the Walk ones) to Drive.
- **AC-4.** Selecting **Any/Air** in bulk returns all legs to Any/Air; times read "—"; Sort / Recompute become disabled.
- **AC-5.** On a roundtrip, the closing leg back to the start also receives the chosen mode.
- **AC-6.** Stop order, start/finish, and time budget are unchanged after a bulk assignment.
- **AC-7.** A leg with a manually entered time is not silently overwritten by *background recompute* following a bulk assignment. (This does not cover a user-initiated overwrite-on mode switch, which can clear the Manual time under the old mode key — see the NOTE at FR-6 and open item A6.)
- **AC-9.** On an all-Any/Air trip (`IsAnyLegComputing` true), the bulk control is enabled and usable — it is not gated on compute state (FR-13).
- **AC-8.** New and affected automated tests pass, including the Trip integration suite.

## 9. Open Items & Assumptions

| ID | Item | Disposition |
|----|------|-------------|
| A1 | Disable the bulk control while a recompute is in flight (`IsAnyLegComputing`). | **Rejected** during implementation — self-defeating (see FR-13). Control is always enabled when legs present, disabled only during its own in-flight request. |
| A2 | Mirror the control into the mobile Trip panel (`MobileTripPanel.razor`). | **Deferred** to tech-debt (consistent with prior mirror-to-mobile defer). Index-only deferral — not a build assumption. |
| A3 | Hint/toast when Any/Air is chosen in bulk. | **Deferred** (optional polish). |
| A4 | Inline placement in the header action row (vs. a dedicated row). | Assumed inline; confirm with UX. |
| A5 | Selector defaults to an unselected placeholder. | Assumed; confirm with UX. |
| A6 | Require a confirm prompt before an overwrite-on assignment that would clear Manual times (see NOTE at FR-6). | **Open** — product/UX decision; default for now is **no confirm** (overwrite-on is already an explicit opt-in). |
