# PRD Quality Review — Bulk Travel Mode Assignment (TRIP-BULKMODE-01)

## Overall verdict

This is a tight, well-scoped capability spec that knows exactly what it is: a single-operator brownfield feature, correctly shaped as a capability spec rather than a persona/journey document. It is decision-ready and unusually unforgiving on done-ness — the central trade-off (non-destructive default vs. explicit overwrite) is named, defended with a counter-metric, and carried consistently from Goals through FRs to ACs. The only real soft spot is one genuine tension flagged in the addendum but not surfaced in the PRD body (AC-7 protects against *background* downgrade but not against *user-initiated* mode-switch wiping a Manual time), which deserves a `[NOTE FOR PM]` in the main document.

## Decision-readiness — strong

A decision-maker can act on this immediately. The pivotal decision — overwrite defaults to **off** / non-destructive — is stated as a decision (FR-3), motivated in the Overview ("with an explicit choice of whether to overwrite"), and reinforced by a real counter-metric (§2: "must not lose that work by default"). Trade-offs are named with what is given up: FR-8 explicitly accepts that bulk Any/Air *re-disables* Sort/Recompute and calls this "a valid outcome, not an error" rather than smoothing it away. Open Items (§9) are genuinely open and dispositioned (Assumed/Deferred), not rhetorical.

The one decision-readiness gap is that the sharpest real tension lives only in the addendum (§D): switching a leg's mode with overwrite **on** changes its `(From, To, Mode)` cache key, so a Manual time silently becomes "—" until re-entered. The addendum itself says "worth a confirm for the overwrite-on case … Flag for UX," but the PRD body never raises it — and AC-7's wording ("not silently overwritten by background recompute") could read as broader protection than actually exists.

### Findings
- **medium** Manual-time loss on overwrite-on not surfaced in PRD body (§8 AC-7, addendum §D) — The only protection asserted in the PRD is against *background* recompute downgrade; user-initiated overwrite-on can still blank a Manual time, a fact confined to the addendum. *Fix:* add a `[NOTE FOR PM]` near FR-6/AC-7 stating that overwrite-on may clear Manual times under the old mode key, and decide whether a confirm is required.

## Substance over theater — strong

No furniture. There are no personas, no innovation/differentiation section, no swap-anywhere Vision paragraph — and for a single-operator internal tool that is the correct call, not an omission. The NFRs are product-specific rather than boilerplate: NFR-2 sets a concrete bound ("a single projection refresh and a single state-change notification … no more than one visible flip of the action buttons"), NFR-1 ties to the project's actual presentation-layer rule, NFR-3 names idempotency against a real re-apply scenario. The Goals table is operational and earned (N→1 actions; 0 accidental overwrites), exactly the SM shape the rubric expects for this product type.

## Strategic coherence — strong

The PRD has a clear thesis stated in the Overview: the trip's primary controls (Sort, Recompute) are gated on every leg having a settled time, Any/Air legs never auto-settle, so today reaching a computable state is "one selection per leg" — and this feature "turns an N-action chore into one deliberate action." Every FR serves that arc; there is no grab-bag of adjacent capabilities. Scope (§3) actively defends the thesis by ruling out the tempting-but-off-thesis items (subset selection, redefining `IsAnyLegComputing`, new modes). The success metrics validate the thesis (actions-to-computable, reachability of Sort/Recompute) rather than measuring vanity activity.

## Done-ness clarity — strong

This is the strongest dimension and the one story creation will lean on. Twelve FRs, each with a testable consequence, mapped to eight concrete ACs covering the cross-product that matters: all-Any baseline (AC-1), partial with overwrite-off (AC-2), partial with overwrite-on (AC-3), bulk Any/Air revert (AC-4), roundtrip closing leg (AC-5), no-mutation invariants (AC-6), Manual-time protection (AC-7), and the test suite (AC-8). The addendum's leg-composition section (§C) pins down the otherwise-ambiguous "all legs" to a precise rule (every from-stop; closing leg keyed by `stops[^1]`), which is exactly the kind of detail that prevents an engineer from guessing.

One adjective slips through: NFR-5 "Performance is acceptable for realistic trip sizes (tens of stops) without a perceptible UI stall." It bounds the input ("tens of stops") but the consequence is an adjective ("acceptable," "perceptible"). Minor, given low stakes and the NFR-2 bound that does the real work.

### Findings
- **low** NFR-5 uses unbounded performance language (§6 NFR-5) — "acceptable" / "no perceptible UI stall" are adjectives, not thresholds. *Fix:* tie to NFR-2's single-refresh guarantee or state a rough bound (e.g. "single write transaction; no per-leg round-trip"), which the addendum §B already implies via the batch-method suggestion.

## Scope honesty — adequate

Omissions are explicit. §3 has a real Out-of-scope list doing real work, and §9 dispositions every deferral (mobile mirror A2 deferred to tech-debt; Any/Air hint A3 deferred; in-flight disable A1 assumed-yes-confirm-at-finalize). Inferences are tagged: A3, A4, A5 carry inline `[ASSUMPTION]` markers and round-trip to the §9 index. Open-items density (5 items) is entirely appropriate for the stakes.

Two honesty gaps keep this from `strong`. First, the assumption tagging is incomplete: A1 and A2 appear in the §9 index but have **no inline `[ASSUMPTION]` callout** at their point of use (A1's in-flight disable is never mentioned in the FRs/NFRs at all; A2's mobile defer appears only in §4 prose without a tag). Second, the overwrite-on Manual-time loss is a de-scoping of protection that happens quietly relative to AC-7's reassuring wording (cross-referenced from Decision-readiness above).

### Findings
- **medium** A1 (in-flight disable) indexed but absent from requirements (§9, §5–6) — Whether the bulk control is disabled during `IsAnyLegComputing` is a behavioral requirement, but it exists only as an Open Item, not as an FR or `[NOTE FOR PM]` at the control's definition. *Fix:* add an FR (or explicit deferral note) near FR-1 so the assumed "yes" is visible where an implementer reads behavior.
- **low** A1/A2 lack inline `[ASSUMPTION]` callouts (§9 vs §4) — Index entries A1 and A2 have no inline tag at point of use, breaking the assumptions round-trip the rubric expects. *Fix:* add inline markers or note in §9 that these are index-only deferrals.

## Downstream usability — adequate

This PRD feeds architecture/story creation, and the addendum makes it sourceable: §B gives exact file touchpoints, §C the leg-composition contract, §D the data-layer toggle semantics, §E the specific test files. FR/AC/NFR IDs are contiguous, unique, and the cross-references that exist resolve (AC-7 ↔ FR-12; addendum §D ↔ PRD AC-7). Each section stands largely alone.

What is missing for `strong` is a **Glossary**. The PRD leans on domain nouns that are used precisely but never defined in one place — "Any/Air" vs "Any-Air" vs "AnyAir", "leg," "from-stop," "computable," "settled," "Manual/Measured," "Fidelity." For a single-operator brownfield tool with the addendum carrying the precise definitions, this is a real but bounded gap; see Mechanical notes for the specific drift.

### Findings
- **low** No Glossary; domain nouns defined only implicitly (whole doc) — "leg," "computable," "settled," "Fidelity," "Manual/Measured" are load-bearing and used consistently but never centrally defined. *Fix:* a short glossary (5–8 terms) would let story/UX agents extract without reading the addendum's code references.

## Shape fit — strong

Correctly shaped. This is an internal, single-operator brownfield capability, and the PRD adopts the capability-spec shape the rubric prescribes for exactly that case: no UJs, no personas, operational SMs, and an explicit justification in §4 ("No multi-stakeholder flow, no separate persona/journey section is warranted at this scale"). It is neither over-formalized (no UJ density manufactured for a solo tool) nor under-formalized. The brownfield requirement that existing-code references be accurate is met by the addendum's concrete, plausibly-real touchpoints (`TripStopList.razor`, `TripViewModel.SetLegModeAsync`, `ITripOrderingService.SetOutgoingTravelModeAsync`, `TravelTimeComputationBackgroundService`), and the feature is consistently framed as a *workaround that does not change* the existing `IsAnyLegComputing` definition — the right brownfield posture. No findings.

## Mechanical notes

- **Glossary drift — "Any/Air":** rendered three ways across the corpus — "Any-Air" (§1 Overview, §3 Out-of-scope), "Any/Air" (FR-2, FR-5, FR-8, ACs), and "AnyAir" (addendum §A, §D, as the code enum). The slash form dominates the PRD body; the hyphen form in §1/§3 should be normalized to it. Low impact (code form is legitimately distinct).
- **ID continuity:** FR-1..12, AC-1..8, NFR-1..5, A1..5 all contiguous, unique, no gaps. Cross-refs (FR-12↔AC-7, FR-10↔addendum §D, FR-7↔addendum §C) resolve.
- **Assumptions round-trip:** inline `[ASSUMPTION A3/A4/A5]` all appear in the §9 index; A1 and A2 are index-only with no inline tag (see Scope honesty findings).
- **`[NOTE FOR PM]` usage:** none present. Acceptable for the stakes, but the overwrite-on Manual-time tension (addendum §D) is the one place a `[NOTE FOR PM]` would do real work in the body.
- **Required sections:** Overview, Goals/SM, Scope, Users/Context, FRs, NFRs, UX, Acceptance, Open Items all present — complete for an internal capability spec. Glossary absent (noted above).

## Finding counts by severity
- critical: 0
- high: 0
- medium: 2
- low: 4
