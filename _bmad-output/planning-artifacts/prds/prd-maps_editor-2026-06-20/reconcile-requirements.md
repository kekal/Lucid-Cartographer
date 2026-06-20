# Requirements Reconciliation — Bulk Travel Mode (TRIP-BULKMODE-01)

- **Source input:** `_bmad-output/bulk-travel-mode/planning-artifacts/requirements.md`
- **Compared against:** `prd.md` + `addendum.md` (prd-maps_editor-2026-06-20)
- **Date:** 2026-06-20

## Method

Walked every section of the source draft (context, FR-1..11, NFR-1..5, UX notes, code touchpoints, AC-1..8, out-of-scope, open questions) and mapped each item to the PRD or addendum. Items intentionally moved to the addendum (technical touchpoints, root-cause chain, leg composition, testing pointers) and items intentionally tracked as open/deferred (Q1→A1, Q2→A2, Q3→A3) are treated as covered, per instructions.

## Coverage summary (covered, not gaps)

- Context / problem / root cause → PRD §1 + Addendum §A (root-cause chain preserved, including FR-21 ground-only compute behavior).
- FR-1..FR-11 (source) → PRD FR-1..FR-12 (renumbered; all behaviors mapped, see notes below).
- NFR-1, NFR-2, NFR-3, NFR-4 → PRD NFR-1, NFR-2, NFR-3, NFR-4.
- NFR-5 (mirror to mobile) → PRD Open Item A2 (deferred — fine).
- AC-1..AC-8 → PRD AC-1..AC-8 (1:1).
- Out-of-scope list → PRD §3 (all three items preserved, plus an added "subset of legs" exclusion).
- UX notes → PRD §7 (placement, subordinate toggle, placeholder label, Any/Air hint).
- Open questions Q1/Q2/Q3 → Open Items A1/A2/A3 (fine).
- Code touchpoints (source §6) → Addendum §B (fine — intentionally moved).

## Gaps found

### G1 — Source NFR-5 (mobile mirror) weakened from "mirror OR defer" to defer-only without re-confirmation
- **Source:** NFR-5 states behavior is mirrored into `MobileTripPanel.razor` **or** explicitly carried to tech-debt; Q2 asks to *confirm* which. This is a genuine NFR with two valid dispositions.
- **PRD:** A2 unilaterally marks it **Deferred** ("consistent with prior mirror-to-mobile defer").
- **Severity:** Low. This is a defensible disposition, but the source framed it as an open decision (Q2) requiring confirmation, and the PRD closed it without flagging it for sign-off the way A1/A4/A5 are ("confirm at finalize"). Recommend adding a "confirm at finalize" note to A2 so the decision is explicit rather than assumed.

### G2 — Source NFR-3 idempotency tie to `UpsertAsync` Manual/Measured protection is generalized away
- **Source:** NFR-3 specifically names that idempotency must "not break Manual/Measured guarantees (`UpsertAsync` already protects Manual/Measured from auto-downgrade)."
- **PRD:** NFR-3 is reduced to "re-applying the same mode with the same toggle produces no duplicate state and no errors" — the explicit Manual/Measured auto-downgrade guarantee is dropped from the NFR.
- **Mitigation:** The Manual/Measured protection survives elsewhere (PRD FR-12, AC-7, Addendum §D), so the intent is not fully lost. But the *idempotency* requirement no longer references it.
- **Severity:** Low–Medium. The substantive guarantee is preserved via FR-12/AC-7; the weakening is only that NFR-3 itself no longer carries the `UpsertAsync` linkage. Recommend re-adding the Manual/Measured clause to NFR-3 for traceability.

### G3 — Source FR-9 phrase "ground cache rows are not recomputed" is softened
- **Source:** FR-9 (Any/Air bulk) explicitly states "наземные кэш-строки не пересчитываются" — ground cache rows are NOT recomputed when reverting to Any/Air.
- **PRD:** FR-8 (the renumbered equivalent) says times "revert to unknown ('—')" and buttons disable, but drops the explicit statement that existing ground cache rows are left untouched / not recomputed.
- **Severity:** Low. The user-visible outcome ("—", buttons disabled) is preserved; the dropped detail is the implementation-facing assertion about cache rows. It is partially recoverable from Addendum §D (mode-key change leaves old Manual rows readable only under old mode), but the "not recomputed" intent for Any/Air specifically is not stated.

### G4 — Counter-metric "0 accidental overwrites" vs source's softer framing (minor, PRD is stronger — not a gap)
- Noted for completeness: PRD §2 adds a stronger 0-overwrite counter-metric than the source articulated. This is an *improvement*, not an omission.

## Net assessment

No high-severity omissions. All functional requirements, acceptance criteria, and the out-of-scope boundary transferred faithfully (with renumbering). The three gaps are all **low / low-medium severity** and are primarily *traceability* weakenings (NFR-3's `UpsertAsync` linkage, FR-9's cache-row clause) plus one disposition that was closed without the "confirm at finalize" tag the source's Q2 implied (mobile mirror). Substantive intent is preserved across PRD+addendum in every case.
