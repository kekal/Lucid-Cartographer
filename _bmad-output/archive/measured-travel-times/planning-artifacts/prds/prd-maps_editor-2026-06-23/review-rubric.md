# PRD Quality Review — Measured Travel-Time & Distance Estimation (Valhalla + smart-haversine)

## Overall verdict

This is a genuinely strong Fast-path capability PRD: it has a clear thesis (close the gap between the two existing fidelity rungs without breaking the NFR7 privacy guarantee), trade-offs are stated honestly rather than smoothed, and nearly every FR carries a testable consequence. The shape — capability spec for a single operator role with technical-how pushed to the addendum — fits the product exactly. The only real risks are concentrated in the open-items area: several FRs carry inline `[confirm]`/`[ASSUMPTION]` tags that are still unresolved at "draft" status, and a couple of acceptance thresholds lean on "materially" without a measurement method, which downstream story creation will have to pin down.

## Decision-readiness — strong

A decision-maker can act on this. The central bet is stated plainly in §1 and §3: the problem is "purely the gap between the two existing rungs," and the two moves (smart-haversine default + Valhalla measured) are presented as decisions, not options to weigh. Trade-offs name what is given up: OSRM is "removed, not retained" (§8) and this is explicitly called "a breaking change for any operator currently running `TravelTime:Provider=Osrm`." The rejected alternatives in the addendum (Turnkey-OSRM "treats the symptom, not the cause"; Itinero "low maturity / single-maintainer risk"; SaaS "violates NFR7") show the choices were earned against real competition rather than asserted.

Open Questions are actually open and each carries a stated default leaning (OQ-4..OQ-7), which is the right posture for a Fast-path PRD. The counter-metrics section (§2) is a real strength — it names the three regressions the author is most worried about (privacy egress, reliability, data loss) rather than only celebrating the wins.

### Findings
- **medium** Unresolved `[confirm]` tags at draft status (FR-15, FR-16, FR-17; OQ-4..OQ-7) — These are correctly deferred, but FR-15 (warn+fallback vs fail-fast) and FR-16 (keep vs invalidate OSRM cache rows) are decisions with user-visible / data-safety consequences, not tuning knobs. *Fix:* before green-lighting build, resolve FR-15 and FR-16 explicitly (they gate boot behavior and data retention); OQ-1/2/3 are legitimately implementation-phase empirical items and can stay open.

## Substance over theater — strong

Almost nothing here reads as furniture. There is no persona section padding (correctly — single operator role, §5 says so by design). NFRs carry product-specific bounds, not boilerplate: NFR7 names the exact egress constraint and where the `.pbf` is fetched; the performance NFR cites "~4–8 GB" target RAM and ties tile build to "off the request path"; the build-discipline NFR even lists the specific analyzer rule ids (MA0002/MA0015/MA0046/MA0047/MA0074/VSTHRD200). The "fidelity ladder" framing is a genuine product concept, not invented novelty — it maps to existing badges and the PRD is careful to say no new badge is introduced (FR-17).

No findings.

## Strategic coherence — strong

The PRD has one thesis and every feature serves it. Feature A (smarter default) and Feature B (turnkey measured) are the two halves of "close the gap"; Features C/D/E (deployment, OSRM removal, badging) are the supporting work that makes the two rungs real and coherent. Prioritization follows the thesis rather than ease. Success Metrics validate the thesis directly — SM-1 measures the turnkey footprint reduction (the actual claim), SM-2 the privacy guarantee, SM-3 the accuracy uplift — and none are activity-vanity metrics. Counter-metrics are present and pointed.

No findings.

## Done-ness clarity — adequate

This is the weakest dimension and the one downstream story creation leans on. Most FRs are testable: FR-1 (apply detour factor to haversine then derive duration), FR-6 (one engine, costing map Drive/Walk/Cycle → auto/pedestrian/bicycle, no rebuild), FR-12 (the exact three-step enablement), FR-14 (an enumerated deletion list) all have clear done conditions, and the addendum sharpens them further (e.g. `.length` km → meters at the edge, `polyline6` precision check).

The soft spots are the accuracy claims. SM-3 and FR-3's surrounding language rely on "materially closer to real road time/distance" and "materially more realistic" with no measurement method or threshold. The PRD acknowledges this is empirical (OQ-1, OQ-2) but a story author cannot write a passing test for "materially." Similarly NFR performance carries "~4–8 GB" but flags the real numbers as empirical (OQ-3), which is acceptable for a target but not yet an acceptance bound.

### Findings
- **high** "Materially" accuracy targets are not testable (SM-3; §2 Success Metrics; FR-3 context) — "materially closer to real road time/distance than haversine" has no comparison baseline, sample, or tolerance, so no FR-level acceptance test can be derived. *Fix:* define a small fixed validation set (e.g. N known routes per mode) and a pass condition (e.g. measured-vs-actual within X%, smart-haversine beating raw straight-line on that set) — even a rough one converts this from adjective to test.
- **low** "Sane defaults" / "materially more realistic" (FR-2, §1) — adjective-graded; mitigated by the explicit `[ASSUMPTION]` numeric leanings (×1.3/×1.2/×1.15) and OQ-1. *Fix:* none required now; tune in implementation as stated.

## Scope honesty — strong

Omissions are explicit and do real work. §4 "Out of scope" names Itinero, external SaaS, the admin settings UI, and explicitly "No new mobile controls" with rationale tied to the existing deferral posture. Assumptions are tagged inline (`[ASSUMPTION]` on FR-2, FR-16, FR-17) and rolled up in §10. The breaking-change reality is stated head-on in §8 rather than buried. De-scoping (OSRM removal) is proposed loudly, not done silently.

Open-items density is appropriate for the stakes: 7 OQs plus a handful of inline tags on a shipping self-hosted infra change is reasonable, and each OQ has a disposition. The one caution (see Decision-readiness) is that two of the "confirm" items are decisions rather than tuning.

No additional findings.

## Downstream usability — adequate

This PRD feeds architecture and story creation, and mostly source-extracts cleanly. FR / SM / NFR / OQ IDs are contiguous and unique. Cross-references resolve (`[TRIP-DEGRADE-01]`, `[TRIP-MANUAL-01]`, NFR7/NFR8 are used consistently). The addendum gives downstream the exact file paths, the costing map, the compose snippet, and the deletion target list — unusually good raw material for stories.

The gap is the absence of a Glossary. Domain nouns (Fidelity, Estimated/Measured/Manual/Placeholder, Source, leg, rung, the badge names) are used consistently in practice, but there is no single definition table, and one term drifts: the NFRs are partly unnumbered ("NFR — Performance", "NFR — Reliability", etc.) while NFR7/NFR8 are numbered, so a story can cite "NFR8" cleanly but cannot cite the reliability NFR by id.

### Findings
- **medium** Unnumbered NFRs break clean citation (§7) — only NFR7 and NFR8 have ids; Performance, Reliability, Canonical-units, Build-discipline, and DI-seam NFRs are bullet-titled only, so downstream can't reference them by stable id. *Fix:* assign ids (NFR9..NFR13 or similar) to the remaining NFRs.
- **low** No Glossary table (whole doc) — terms are used consistently so impact is limited, but a story author has no single source for badge/Fidelity/Source vocabulary. *Fix:* add a short Glossary mapping Fidelity values, Source constants, "rung," and "ladder" — most can be lifted from the addendum.

## Shape fit — strong

The PRD is shaped correctly for what it is. A single-operator infrastructure change is written as a capability spec; the user-journey section is omitted with explicit rationale (§5: "single operator role, infrastructure-level change"), which is the right call, not under-formalization. Technical-how lives in the addendum by design, keeping the PRD body at capability altitude. It is not over-formalized (no invented UJs or personas) and not under-formalized (FRs are concrete and the NFRs carry real bounds). Brownfield references in the addendum are specific and file-pathed.

No findings.

## Mechanical notes

- **NFR id continuity:** numbered set is non-contiguous — NFR7 and NFR8 exist, but there is no NFR1..NFR6 in this document (they presumably live in a parent/product-wide NFR set) and the remaining NFRs here are unnumbered. Confirm NFR7/NFR8 numbering is intentionally inherited from a global registry; if so, a one-line note would prevent a reader assuming a gap.
- **Assumptions roundtrip:** inline `[ASSUMPTION]` tags (FR-2, FR-16, FR-17) all appear in the §10 table (OQ-1, OQ-5, OQ-6); roundtrip holds. FR-15's `[confirm]` maps to OQ-4 and OQ-7 maps to the §9 pinning note — clean.
- **Glossary:** none present (see Downstream usability). No term drift detected in actual usage.
- **Cross-refs:** `[TRIP-DEGRADE-01]`, `[TRIP-MANUAL-01]`, FR-15/FR-16 back-references, and NFR7/NFR8 all resolve. Addendum file paths match the FR-14 deletion list.
- **Required sections:** all present for a Fast-path capability PRD (Summary, Goals/SM, Background, Scope, Users, FRs, NFRs, Migration, Dependencies, Open Questions, Future).
