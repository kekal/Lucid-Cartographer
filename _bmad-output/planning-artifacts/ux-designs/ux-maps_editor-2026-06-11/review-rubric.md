# UX Spine Rubric Review — LucidCartographer

**Reviewed:** 2026-06-11
**Files:**
- `DESIGN.md` (visual spine)
- `EXPERIENCE.md` (experience spine)

**Verdict:** No blockers. Both spines are well-formed, internally consistent, and correctly cross-referenced. A handful of minor specificity/coverage gaps noted below.

---

## 1. Section Coverage

### DESIGN.md — canonical order check
Required order: Brand & Style · Colors · Typography · Layout & Spacing · Elevation & Depth · Shapes · Components · Do's and Don'ts.

| Section | Present | In order |
|---|---|---|
| Brand & Style | ✅ | ✅ |
| Colors | ✅ | ✅ |
| Typography | ✅ | ✅ |
| Layout & Spacing | ✅ | ✅ |
| Elevation & Depth | ✅ | ✅ |
| Shapes | ✅ | ✅ |
| Components | ✅ | ✅ |
| Do's and Don'ts | ✅ | ✅ |

**All 8 sections present, in canonical order.** Frontmatter carries all required token groups: `colors`, `typography`, `rounded`, `spacing`, `components`. ✅

### EXPERIENCE.md — required + triggered sections
| Section | Present |
|---|---|
| Foundation | ✅ |
| Information Architecture | ✅ |
| Voice and Tone | ✅ |
| Component Patterns | ✅ |
| State Patterns | ✅ |
| Interaction Primitives | ✅ |
| Accessibility Floor | ✅ |
| Key Flows (named protagonist + climax) | ✅ |
| *Inspiration & Anti-patterns* (optional) | ✅ |
| *Responsive & Platform* (optional) | ✅ |

**All 8 required sections present; both optional sections justified** (Trip View framing benefits from Inspiration & Anti-patterns; dual desktop/mobile surfaces justify Responsive & Platform). ✅

---

## 2. Internal Consistency (DESIGN ↔ EXPERIENCE)

No contradictions found. Cross-checked claims that appear in both files:

- **Breakpoint 768px** — DESIGN Layout & Spacing and EXPERIENCE Foundation agree. ✅ (Note: see Finding M-1 re: `desktop-breakpoint` token.)
- **Distinct render paths** (`Viewport.IsMobile` → `Mobile*Screen`) — stated identically in both. ✅
- **Header height 64px** — DESIGN `components.header-height` ↔ EXPERIENCE IA `{components.header-height} 64px`. ✅
- **Table row 44px** — DESIGN `components.table-row` ↔ EXPERIENCE Component Patterns `{components.table-row} 44px`. ✅
- **`warn` (amber) for overrun, never `tertiary` red** — DESIGN Do's/Don'ts and Itinerary timeline ↔ EXPERIENCE State Patterns time-budget overrun + Flow 3 edge. ✅
- **Em-dash "—" for empty Air/Any leg time, no Placeholder badge (decision OQ4)** — DESIGN Do's/Don'ts + Fidelity badge ↔ EXPERIENCE Component Patterns / Flow 3 step 2. ✅
- **Non-Measured legs dashed + muted** — DESIGN Route leg ↔ EXPERIENCE State Patterns / Flows. ✅
- **Trip View toggle enabled only at ≥2 placeable POIs** — DESIGN Trip View toggle ↔ EXPERIENCE Component Patterns + State Patterns. ✅
- **Trip View persists per-collection (OQ8)** — EXPERIENCE Component Patterns ↔ Flow 1 step 5. ✅
- **Contrast deferral** — DESIGN owns contrast (`on-surface-muted #5e6470` AA-tuned); EXPERIENCE Accessibility Floor correctly defers visual contrast to DESIGN. ✅

---

## 3. Cross-Reference Correctness

All `{path.to.token}` references in EXPERIENCE.md resolve to tokens defined in DESIGN.md frontmatter:

| Reference | Resolves to frontmatter token | OK |
|---|---|---|
| `{components.header-height}` (IA) | `components.header-height: 64px` | ✅ |
| `{components.table-row}` (Component Patterns) | `components.table-row: 44px` | ✅ |
| `{colors.primary}` (State Patterns: Loading) | `colors.primary` | ✅ |
| `{colors.secondary}` (State Patterns: Import success) | `colors.secondary` | ✅ |
| `{colors.tertiary}` (State Patterns: Import error) | `colors.tertiary` | ✅ |
| `{colors.warn}` (State Patterns: overrun; Flow 3 edge) | `colors.warn` | ✅ |

**No dangling references.** Every named token cross-referenced from EXPERIENCE.md exists in DESIGN.md frontmatter. (`sources` frontmatter refs in EXPERIENCE.md — `{planning_artifacts}`, `{project-root}` — are path variables, not DESIGN tokens, and are out of scope of the token-resolution check.)

**Note (minor, see I-1):** Many semantic-color references in EXPERIENCE.md prose are by bare name (e.g. "Measured (`secondary`/confirmed tone)" lives in DESIGN; in EXPERIENCE, "Per-POI `hourglass` (amber)") rather than `{colors.x}` syntax. Token color names like `surface`, `surface-container-low` etc. appear unbraced in EXPERIENCE Foundation ("the `surface-*` / `on-surface-*` / `primary` token palette"). This is acceptable narrative reference but slightly under-uses the cross-ref convention.

---

## 4. Journey Quality

Every Key Flow has a **named protagonist** and an explicit **Climax** beat (each is literally labeled `**Climax:**`).

| Flow | Protagonist | Surface | Climax beat | Edge case |
|---|---|---|---|---|
| 1 | **Yurik** (the maintainer) | desktop | Timeline shows loop fits the day; `+8h05m` / "back to hotel by 18:10" | Unplaceable POI flagged, not dropped |
| 2 | **Mara** | desktop | Hand-nudged stops stick; no system reshuffle ("The trip is hers") | Routing engine down → straight-line estimates |
| 3 | **Priya** | mobile | Airport arrival with 40 min slack — feasible; trusts Manual flight, reads Estimated hops as rough | Extra stop → `{colors.warn}` overrun flag |

**All three flows pass.** Protagonists are distinct and named; each climax is a genuine turning/payoff beat, not just a final step; each carries a labeled edge case. Coverage spans desktop + mobile and all three ordering paths (manual drag, TSP-Sort, MCP/AI). Strong. ✅

---

## 5. Specificity Findings

### MINOR — M-1: `desktop-breakpoint` token defined but never cross-referenced
**Location:** DESIGN.md frontmatter (`components.desktop-breakpoint: 768px`); EXPERIENCE.md Foundation & IA cite "768px" as a literal.
**Issue:** The 768px breakpoint is a defined token but is written as a magic literal in both spines instead of `{components.desktop-breakpoint}`. Mild duplication risk if the breakpoint ever changes.
**Fix:** In EXPERIENCE.md Foundation, reference `{components.desktop-breakpoint}` (768px) the same way `{components.header-height}` and `{components.table-row}` are referenced.

### MINOR — M-2: Open `[ASSUMPTION]` — keyboard reorder mechanism unspecified
**Location:** EXPERIENCE.md Accessibility Floor (Keyboard) + Open items for build.
**Issue:** "Stop reordering must have a keyboard-accessible path … `[ASSUMPTION]` … to be specified." This is a real accessibility-floor gap (drag-only reorder is not keyboard-operable) deliberately deferred to build. Correctly flagged, but it is the one place the Accessibility Floor is not yet satisfiable.
**Fix:** Acceptable to defer, but it should be tracked as a build-blocking a11y item, not a soft note — decide move-up/down buttons vs arrow-key reorder before the Trip View stop list ships.

### MINOR — M-3: TSP-Sort perf bound scoped to N≤30, behavior for larger N unstated
**Location:** EXPERIENCE.md Component Patterns (Ordering actions): "p95 ≤ 3s for N≤30".
**Issue:** The toggle is enabled at ≥2 placeable POIs with no stated upper cap; Flow 2 uses 25. Behavior/expectation for N>30 (timeout? degraded? disabled?) is unspecified.
**Fix:** State the N>30 behavior (e.g. still allowed but no p95 guarantee, or a soft cap with a notice).

### MINOR — M-4: "soft caution" thresholds left abstract
**Location:** DESIGN.md Colors (`warn`: "time-budget overrun") + EXPERIENCE.md State Patterns (time-budget overrun "when arrival exceeds the trip's budget").
**Issue:** "the trip's budget" is referenced but never defined — there's no IA/Component element where a user sets a day/time budget. The overrun flag's trigger condition is therefore underspecified.
**Fix:** Either point to where the budget is set (a trip start time + implied end?) or define what "budget" means concretely (e.g. derived from start time + a configurable day length).

### MINOR — M-5: "striped fallback" hero unspecified visually
**Location:** DESIGN.md Components (POI detail pane): "colored scrim, striped fallback."
**Issue:** "striped fallback" is named but not specified (stripe color/angle/source). Minor; a builder would guess.
**Fix:** One line on the fallback pattern derivation (e.g. diagonal stripes tinted from the collection color), or drop to "placeholder scrim."

### INFO — I-1: Under-use of `{colors.x}` cross-ref convention in EXPERIENCE prose
**Location:** EXPERIENCE.md Foundation, Component Patterns, Voice/Tone.
**Issue:** Semantic colors are often named bare ("amber", "`surface-*`", "`primary`") rather than braced. Not wrong — the load-bearing state references (Loading/Import/overrun) do use `{colors.x}` — but consistency would help the spine's tooling story.
**Fix (optional):** Brace named color tokens where they denote a specific token value.

---

## Severity Summary

| Severity | Count |
|---|---|
| Blocker | 0 |
| Major | 0 |
| Minor | 5 (M-1…M-5) |
| Info | 1 (I-1) |

Both spines satisfy the rubric: complete sections in canonical order, no internal contradictions, zero dangling cross-references, and all three Key Flows carry a named protagonist plus a labeled climax. Remaining items are specificity refinements and one deliberately-deferred a11y assumption.
