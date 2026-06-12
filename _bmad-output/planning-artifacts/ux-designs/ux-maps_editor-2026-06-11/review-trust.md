# Trust & Honest-Signaling Review — LucidCartographer Spines

**Reviewer lens:** Trust / honest signaling only — does the product ever imply more certainty than the system has?
**Files reviewed:**
- `DESIGN.md` (Design Spine)
- `EXPERIENCE.md` (Experience Spine)
**Date:** 2026-06-11

**Severity counts:** Blocker 0 · Major 5 · Minor 4

The honest-signaling intent is strong and largely coherent: the Fidelity badge vocabulary matches across files, the em-dash/Placeholder decision (OQ4) is stated identically in both, and SM-C1 (Trip View additive) is genuinely protected by spine rules. The gaps are all of the same shape — **the badge taxonomy is well-defined per-leg, but aggregation, edge states, and styling completeness are under-specified, leaving room for a number to read as more certain than its weakest input.**

---

## MAJOR findings

### M1 — Cumulative timeline mixes fidelities into one number with no aggregate honesty signal
**Severity:** Major
**Location:** `DESIGN.md` "Itinerary timeline" (line ~172); `EXPERIENCE.md` State Patterns "Leg computing" / Flow 3 (lines 87, 152–157).
**Problem:** Per-leg badges are rigorous, but the timeline rolls legs into a single cumulative offset (`+2h15m`, `+8h05m`) and a wall-clock arrival (`18:10`). A loop containing one **Measured** leg, several **Estimated** legs, and one **Manual** flight produces a total that is **only as trustworthy as its softest leg** — yet the cumulative value and the wall-clock arrival carry no fidelity qualifier. This is precisely the SM-C2 ("never chase false precision") failure mode: a single confident-looking `back to hotel by 18:10` derived from mostly-estimated inputs. Flow 3 even narrates this exact mix (Manual flight + Estimated hops) and then prints a clean arrival with "40 min of slack."
**Concrete fix:** Add a spine rule: *a cumulative/total time inherits the lowest fidelity of its constituent legs.* Surface it — e.g. the running timeline value and the final arrival show a derived badge or qualifier ("Estimated arrival" / "~18:10") whenever any contributing leg is non-Measured/non-Manual. At minimum, define in `DESIGN.md` Itinerary timeline how a mixed-fidelity total is visually distinguished from an all-Measured one (the `~` tilde or a roll-up badge), and add a Do/Don't row: "Don't render a cumulative time more confidently than its weakest leg."

### M2 — Em-dash "—" rule is scoped only to Air/Any; other unentered/unresolved legs have undefined display
**Severity:** Major
**Location:** `DESIGN.md` Do/Don't (line 183) + Fidelity badge (line 171); `EXPERIENCE.md` Component Patterns "Fidelity badge" (line 71).
**Problem:** Both files say the em-dash applies specifically to *"empty Air/Any leg time"* / *"Air/Any with no manual entry."* But **Placeholder** is defined as "used internally for unresolved legs." What does a *Drive/Walk/Cycle* leg show when its time is genuinely unknown (provider not yet returned a real value, or returned nothing)? The spine says Placeholder must not appear in the user-facing slot (em-dash is the substitute) — but the em-dash is only authorized for Air/Any. This leaves a ground-mode unresolved leg with **no defined user-facing time treatment**, which invites either a leaked "Placeholder" badge or a fabricated number — both honest-signaling violations.
**Concrete fix:** Generalize the rule: *any leg whose time the system has not measured, estimated, or had manually entered displays "—", never a Placeholder badge,* regardless of travel mode. Update both the DESIGN Do/Don't row and the EXPERIENCE Fidelity-badge row to drop the "Air/Any" qualifier (or explicitly state the same em-dash treatment for unresolved ground legs).

### M3 — Leg geometry styling is undefined for Manual and Placeholder fidelities
**Severity:** Major
**Location:** `DESIGN.md` "Route leg (map)" (line 170) and "Fidelity badge" (line 171).
**Problem:** The map-line styling rule is binary: **Measured → solid full-color**, **Non-Measured (straight-line/estimated) → dashed + muted**. But there are **four** fidelities. Where does a **Manual** leg's line render? Manual is "trusted" (badge in `primary`), but its *geometry* is not road-measured — it's a user-asserted duration over an unknown/straight path (e.g. a flight). If a Manual flight leg renders as a **solid full-color** line, the map implies measured road geometry the system does not have — a direct "estimate rendered as a confident solid line" violation (the very Don't on line 184). Conversely **Placeholder** legs have no line styling defined at all.
**Concrete fix:** Make the styling map total over all four fidelities. Recommended: *line solidity tracks **geometric** fidelity, not time-trust* — Measured road geometry = solid; everything else (Estimated, Manual, Placeholder, Air) = dashed. Manual's trust is carried by the **badge**, not the line. State explicitly in `DESIGN.md` that a Manual time does **not** promote its leg line to solid, and define the Placeholder leg line (dashed + most-muted). Add a Do/Don't row.

### M4 — Provider egress consent is explicitly hand-waved ("TBD at build")
**Severity:** Major
**Location:** `EXPERIENCE.md` "Open items for build" → "Provider egress" (line 164); cross-ref Foundation/Posture (lines 21) and Interaction Primitives "Banned" (line 100).
**Problem:** The privacy posture is asserted strongly ("coordinates stay local unless an opt-in out-calling provider is enabled and surfaced first"; sending coordinates to a third party without surfacing it is *Banned*), but the **mechanism that enforces it** is deferred: *"must surface a consent/notice… exact placement TBD at build."* A Banned behavior whose only guardrail is an unspecified future consent surface is not actually protected by the spine — it is protected by a promise to design protection later. There is also no specification of **what** is disclosed (which provider, what data leaves, that coordinates specifically egress), **when** (before first call, blocking), or **how revocation/opt-out** works.
**Concrete fix:** Promote provider egress consent from an open item to a concrete State/Component pattern: a **blocking, pre-first-call consent gate** that names the provider, states explicitly that POI coordinates will leave the deployment, and defaults to off. Specify that until consent is given, routing silently uses the local mock/straight-line provider (no egress). Define where the opt-in lives (Operations or a settings surface) and that it is revocable. Keep only true visual-placement nuance in "Open items."

### M5 — "Manual" badge grants full trust with no integrity signal on a user-typed guess
**Severity:** Major
**Location:** `DESIGN.md` Fidelity badge (line 171, "Manual — user-entered, **trusted**"); `EXPERIENCE.md` "Manual time entry" (line 72), Flow 3 step 3.
**Problem:** A **Manual** entry is treated as fully trusted and propagates into the cumulative total at the same confidence as **Measured**. But a Manual value is a *human estimate* (Flow 3: the user types a flight duration "e.g. 2h20m"). Folding it into a total at Measured-equivalent confidence — and then printing a clean wall-clock arrival from it — is a subtle false-precision path: the system is treating an unverified human guess as ground truth. This is defensible (the user asserted it) but it must not silently elevate the *aggregate's* apparent precision (ties back to M1).
**Concrete fix:** Keep Manual as trusted at the leg level, but for aggregation treat Manual as *non-Measured* for the purpose of the total's fidelity (per M1, the cumulative shows a qualifier when any leg is Estimated **or Manual**). Optionally clarify in `EXPERIENCE.md` Voice/Tone that Manual copy frames it as the user's figure ("your 2h20m flight time"), not a system measurement.

---

## MINOR findings

### m1 — "Estimated" badge tone collides conceptually with muted text token
**Severity:** Minor
**Location:** `DESIGN.md` Fidelity badge (line 171): "Estimated (neutral `on-surface-muted`)."
**Problem:** Estimated uses `on-surface-muted`, the same token reserved for *least-emphasis/tertiary text*. Honest signaling is fine (muted = humble), but Placeholder is also described as "least emphasis," so Estimated and Placeholder risk being visually indistinguishable if both surface. Since Placeholder is supposed to be em-dash-substituted (M2), this is minor, but the badge-tone ladder (Measured=secondary, Manual=primary, Estimated=muted, Placeholder=least) compresses two of four into near-identical muting.
**Concrete fix:** Define distinct, non-overlapping tones for Estimated vs Placeholder (e.g. Estimated = muted outline pill; Placeholder = even-lower-contrast or simply never user-facing). State that Estimated must remain distinguishable from plain disabled/muted text.

### m2 — Rounding/precision of leg and timeline values is unspecified
**Severity:** Minor
**Location:** `DESIGN.md` Itinerary timeline (line 172), "per-leg distances" (mono); `EXPERIENCE.md` Flow examples (`+8h05m`, `14:10`, "40 min of slack").
**Problem:** No rule on rounding. An Estimated straight-line leg shown as `14 min` (or `14:10` to the minute) implies minute-level precision the estimate doesn't have. False precision via spurious significant figures is exactly an SM-C2 risk.
**Concrete fix:** Add a precision rule: Estimated values round coarsely (e.g. nearest 5 min, or "~15 min"); Measured/Manual may show finer. Wall-clock derived from any non-Measured input shows a `~` or rounds. Define in DESIGN typography/Itinerary section.

### m3 — Wall-clock "only when a start time exists" rule is stated but not defended against derived clocks
**Severity:** Minor
**Location:** `DESIGN.md` line 172 + Do/Don't line 185; `EXPERIENCE.md` Flow 1 step 4.
**Problem:** The rule correctly suppresses wall-clock without a start time. But there's no stated rule preventing a *partial* start (e.g. start time set, but trip spans midnight, or DST) from producing a misleading clock. Edge, but the "never imply a precise clock" Don't isn't fully covered for derived/overflow cases.
**Concrete fix:** Note that wall-clock crossing day boundaries shows the day offset (e.g. `18:10 +1d`), and that an all-relative fallback is always available. Low urgency.

### m4 — Counter-metric SM-C2 is asserted but not bound to a testable spine rule
**Severity:** Minor
**Location:** `EXPERIENCE.md` "Counter-metrics" (line 124).
**Problem:** SM-C1 ("collections must never feel forced into trips") **is** concretely protected: Trip View is a per-collection additive toggle, hidden below 2 placeable POIs, IA gains no "Trips" section, off restores plain collection intact (lines 39, 66, 85, 96, 119). Good. SM-C2 ("never chase false precision"), by contrast, is only asserted as a principle — the concrete mechanisms it needs (mixed-fidelity total flagging M1, generalized em-dash M2, geometry-fidelity styling M3, rounding m2) are the very gaps above. So SM-C2 is currently *aspirational*, not *enforced*.
**Concrete fix:** Once M1/M2/M3/m2 are addressed, add an explicit back-reference under Counter-metrics listing the spine rules that enforce SM-C2 (as SM-C1 implicitly has). A counter-metric with no enforcing rule is a slogan.

---

## What is already coherent (no action)
- Fidelity badge **vocabulary** (Measured/Estimated/Manual/Placeholder) is identical across both files.
- Em-dash vs Placeholder-badge decision (OQ4) is stated **consistently** in DESIGN (line 183) and EXPERIENCE (line 71) — no contradiction; the gap is scope (M2), not disagreement.
- Estimated-leg fallback when routing is down is coherent end-to-end: dashed+muted + Estimated badge + honest copy (`EXPERIENCE.md` line 88, Flow 2 edge).
- SM-C1 is genuinely spine-protected (see m4).
- "Not placeable" POI handling is honest and consistent (kept, excluded, never silently dropped) across Voice/Tone, State Patterns, Interaction "Banned," and Flow 1 edge.
