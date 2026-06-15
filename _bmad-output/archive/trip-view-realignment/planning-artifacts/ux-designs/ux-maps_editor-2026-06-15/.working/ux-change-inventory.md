# UX-change inventory — Trip View realignment

Delta of the finalized PRD (`prds/prd-maps_editor-2026-06-15`) vs. the shipped UX
spines (`ux-maps_editor-2026-06-11`). Legend: **NEW** · **CHANGED** · **REMOVED** ·
**SAME (conformance)**.

## Component Patterns (EXPERIENCE.md)

| # | Existing spine | PRD change | Class | FR |
|---|---|---|---|---|
| C1 | Trip View toggle "switches plain↔trip" | Spine was already right; the **code** diverged (additive 256px column). Desktop list region must actually *become* the trip. | SAME (conformance) | FR-1/5/6 |
| C2 | Stop list row = badge · name · dwell · timeline | Row becomes a **wide table**: reorder gutter (drag **+** ▲▼) · stop# · name+address+enrichment icon · dwell · arrival · start/finish · actions(focus, open-maps). Leg time **leaves the row**. Drops select/coords/collection-chips/added/move-copy-delete **and the batch toolbar** in trip view. | CHANGED (major) | FR-2 |
| C3 | — | **Inter-row leg connector**: per-leg mode + travel time + distance + fidelity + edit/reset, on the boundary between rows; closing leg before the footer. | **NEW** | FR-3 |
| C4 | Travel-mode selector = **per-trip** | Per-trip selector **removed**; mode is **per-leg** (on the connector). | REMOVED → replaced | FR-19/23 |
| C5 | Manual time entry (per leg, Any/Air) | Generalized: **any leg** editable inline/popup → Manual override; **explicit reset to auto**. | CHANGED | FR-25 |
| C6 | Fidelity badge | Add **plain-language tooltip**; "all Estimated (Mock)" contextual note; **recommend enabling OSRM** (link to docs). Badge/line visuals unchanged. | CHANGED | FR-7/8/9/10 |
| C7 | Itinerary timeline | Accumulation rule **unchanged**; fix rounding so total = Σ legs; arrivals show **date + day rollover** (locale-driven); minute unit **"min"**. | CHANGED | FR-13/14/15/16/27 |
| C8 | Start/Finish controls; roundtrip default | Finish pins to end; readout flips **"Return to start" → "Finish"** + arrival; revertable. (Mostly matches; make readout behavior explicit + verify.) | CHANGED (verify) | FR-31/32/33 |
| C9 | Recompute | Copy must not imply a fidelity upgrade when no provider is configured. | CHANGED (copy) | FR-9 |
| C10 | Ordering actions (drag / TSP / MCP) | MCP gains **per-leg mode** read/write. | CHANGED | FR-24 |
| C11 | — | **Icon-button tooltips** on all controls (move/start/finish/TSP/Recompute). | **NEW** | FR-17/18 |

## Input affordances (DESIGN.md components + EXPERIENCE.md)

| Existing | PRD change | Class | FR |
|---|---|---|---|
| Start time (time-only) | **Date + time picker** | CHANGED | FR-26 |
| Dwell (minutes field) | **Duration picker (HH:MM)** | CHANGED | FR-30 |
| Time budget (minutes field) | Renamed **"Time limit"**; enter as **HH:MM duration** OR **finish-by deadline (date+time)** | CHANGED | FR-28/29 |
| "Over budget" flag | Renamed **"Over limit"** | CHANGED | FR-28 |

## State Patterns (EXPERIENCE.md)

| Existing | PRD change | Class | FR |
|---|---|---|---|
| Leg computing ("—" pending) | Distinct from a **new/undefined (Any) leg** — "—" because it **awaits a mode**, not because it's computing. | **NEW state** | FR-20 |
| Time-budget overrun (amber) | → **"Over limit"** (same amber soft-warn) | CHANGED | FR-28 |
| — | **Multi-day rollover**: arrival on a later day shows its date | **NEW** | FR-27 |
| Manual badge | + **reset-to-auto** affordance state | CHANGED | FR-25 |
| Mixed-fidelity, unplaceable, provider-down | unchanged | SAME | — |

## Interaction Primitives (EXPERIENCE.md)

- **NEW:** edit-in-place (or popup) a leg's travel time on the connector + reset (FR-25).
- **NEW:** set a leg's travel mode per connector (FR-19).
- SAME: drag-reorder (+ ▲▼ keyboard), list↔map sync, reversible toggles, the "Banned" list.

## DESIGN.md (visual)

- **NEW component specs:** inter-row leg connector; per-leg mode control (pill→picker); wide trip-table row; date-time picker; HH:MM duration picker; finish readout.
- **CHANGED:** stop-list row spec (old `badge·name·dwell·timeline` → new wide columns + connector).
- **SAME:** color/type/spacing tokens, badge visuals, map line-solidity rule.

## Open UX decisions (the PRD left to UX)

1. Connector layout — **left-indented under name** (per mockup) vs centered?
2. Per-leg mode control form — **pill that opens a menu** vs always-visible dropdown?
3. Leg-time edit — **inline** vs **popup**?
4. Date-time / duration / finish-by pickers — **native** inputs vs styled custom?
5. "Set mode" affordance for an undefined leg — how obvious? (mock shows `Any — set mode`).
6. Connector density — mode + time + dist + fidelity + edit/reset is a lot on one line; collapse rules?
7. Tooltip/legend wording (FR-7 copy).
