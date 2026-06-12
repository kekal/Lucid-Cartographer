# Adversarial Review — Trip Planning PRD (LucidCartographer)

**Verdict:** Plausible at the slogan level, leaky at the build level. The "lens over a Collection, identical to today when off" framing hides a real entity-with-state, and three FR clusters (Distance Matrix cache/invalidation, timeline math, drag-reorder vs Start/Finish pinning) are hand-waved exactly where the bugs will live. Ships only if you nail the edge cases the PRD currently treats as footnotes.

---

## CRITICAL

### C-1. "Identical to today" (FR-1 consequence) is an unfalsifiable, and probably false, claim.
**Location:** FR-1 Consequences ("With Trip View off, the Collection page is visually and behaviorally identical to today"); §6.1.
**Failure mode:** This is presented as a *testable* consequence but it is not testable as written — "identical to today" has no fixture, no golden snapshot, no baseline reference. Worse, it is almost certainly false the moment FR-2 runs: FR-2 says a Collection "receives a deterministic seed order on first Trip-View open" and FR-17 surfaces a *new toggle control in the filtered-results region*. The toggle itself is a visible change to the off-state page (it's rendered whether or not you flip it). And FR-2's seed write means the first toggle-on mutates persisted state — so "toggling does not modify... POI membership" is technically true but "identical to today" at the data layer is not (a new OrderIndex column is now populated). A reviewer cannot sign this off because there's no definition of the baseline.
**Fix:** Replace with a concrete, testable assertion: "With Trip View off, no Stop Order badges, Legs, timeline, or trip panel render; the existing POI list/map/popup behavior passes the current regression suite unchanged. The toggle control itself is the only added off-state element." Drop the word "identical." State explicitly that first toggle-on performs a one-time seed write.

### C-2. Distance Matrix cache invalidation is under-specified and the one stated rule is wrong/incomplete.
**Location:** FR-11 Consequences; Glossary "Distance Matrix"; addendum §A `RouteSegment`.
**Failure mode:** The cache is keyed `(FromPoiId, ToPoiId, TravelMode)` (addendum §A) but the invalidation rules listed are only "changing a Stop's coordinates invalidates Legs that touch that Stop" and "Stop Order change that doesn't introduce a new pair doesn't recompute." Missing invalidation triggers that will produce stale, wrong times shown as authoritative:
  - **Provider swap** (Mock→OSRM or vice versa). `Source` is stored on the row but nothing says a provider change invalidates or re-badges. A user enabling OSRM will keep seeing cached Estimated rows badged as-is, or worse, mixed Source rows with no recompute. SM-3 (≥95% Measured) silently fails because old Estimated rows never upgrade.
  - **FR-10's promised "upgrade Estimated→Measured on recompute"** has no trigger. What *initiates* that recompute? Nothing in FR-11 fires on "provider became reachable." This is begging the question — the consequence assumes a mechanism the FRs never provide.
  - **Mode-speed / assumed-speed config change** (the §8 OQ7 "Any/Air speed model") changes every Estimated/Placeholder value but isn't a documented invalidation input.
  - **Directionality:** `(From,To)` is ordered, but haversine is symmetric while road routing is not. Caching both directions for Drive doubles the matrix; the PRD's "N Legs / N−1 Legs" math assumes you only ever need consecutive pairs, but TSP-Sort needs the *full all-pairs* matrix (N²). FR-11's "don't recompute if the pair is already cached" quietly assumes the all-pairs matrix is pre-warmed — but nothing says when the N² fill happens, and SM-5's "warm matrix" caveat hides the cold-start cost entirely.
**Fix:** Enumerate the full invalidation set: coordinate change, mode change, assumed-speed config change, provider change, and provider-availability transition (with an explicit recompute trigger for the Estimated→Measured upgrade). Specify when the all-pairs matrix is computed vs. just consecutive Legs, and whether road-mode matrices store both directions.

### C-3. Start/Finish pinning contradicts the contiguous-unique Stop Order and the drag model.
**Location:** FR-2 ("Stop Order contiguous, unique"), FR-3 ("Start/Finish preserved across manual reorder"), FR-14 ("Setting a Start Stop moves it to Stop Order 1"), FR-15 ("never moves Start from first or Finish from last").
**Failure mode:** Three mechanisms write the same integer Stop Order, and they fight:
  - FR-14 says setting Start *moves it to Order 1*. So Start is defined by being Order 1? Or is Start a separate `StartPoiId` field (addendum §A says it is) that must be *kept consistent* with Order 1? If both exist, every reorder must maintain the invariant "StartPoiId's Stop has Order 1," but FR-3 lets the user *drag any stop to position 1*. What happens when a user drags a non-Start stop to slot 1? Does it become Start (contradicting FR-3's "a dragged Stop does not silently become Start")? Or does the drop get rejected/bounced (a drag interaction the PRD never describes)? This is a genuine contradiction with no stated resolution.
  - FR-15 pins Finish "last," but for a Roundtrip Finish *equals* Start (Glossary), which is Order 1. So in a roundtrip, the pinned "last" node and the pinned "first" node are the same Stop. The 2-opt "pin Start and Finish, only swap interior edges" (addendum §D) is coherent for the depot variant, but the PRD's *Stop Order numbering* (1..N, unique) cannot represent "Start = Finish = both ends" — you can't give one Stop both Order 1 and Order N. The data model and the algorithm disagree about what "Finish" is for a roundtrip.
**Fix:** Decide whether Start/Finish are derived from Order (1 and N) or are independent FK fields, and state the single source of truth. Define drag-to-slot-1 behavior explicitly (reject, or reassign Start with confirmation). Clarify that for a Roundtrip there is no distinct Finish Stop — the closing Leg is synthetic (N→1), and Finish-pinning applies only to open-path trips.

### C-4. The timeline math is asserted, never specified, and breaks on the stated edge cases.
**Location:** FR-13; Glossary "Itinerary Timeline" (`start + Σ(Travel + Dwell)`); FR-4 ("timeline computes over placeable Stops only"); UJ-3 red overrun.
**Failure mode:** "Σ(Travel Time + Dwell Time) along the Stop Order" is ambiguous at both ends and silent on the cases that matter:
  - **Does the Start Stop's Dwell count?** Do you dwell at the hotel before leaving? Does the *Finish* Stop's dwell count (you've arrived; do you "dwell" at the end)? The alternation "Travel then Dwell" is unspecified at boundaries — off-by-one in arrival times is the single most likely launch bug, and it's invisible because the Mock provides plausible-looking numbers.
  - **Unplaceable stops mid-route:** FR-4 says timeline computes over placeable stops only. But an Unplaceable Stop can carry a Dwell Time (FR-12 puts dwell on membership, independent of coordinates). Is that dwell counted or dropped? If dropped, the timeline silently understates the day. If a Stop becomes Unplaceable *between* two placeable stops, do you bridge the Leg (compute From→To skipping it) — and does that Leg exist in the cache, given the matrix only ever cached consecutive pairs?
  - **Placeholder/blank legs:** OQ4 admits it's unresolved whether Air legs show "—" or a placeholder number. The timeline *cannot sum a "—"*. So the entire timeline downstream of any un-entered Air Leg is undefined, yet FR-13 promises an arrival time "at each Stop" and UJ-3's whole climax depends on a computed airport-arrival time across an Air leg. v1's headline journey depends on a deferred decision (OQ4).
**Fix:** Write the timeline as an explicit recurrence with boundary rules: arrival[1] = start; depart[i] = arrival[i] + dwell[i]; arrival[i+1] = depart[i] + travel(i→i+1); define whether Start/Finish dwell count; define behavior when travel is undefined (timeline becomes "≥ X" / partial, not a single number). Resolve OQ4 before committing UJ-3 to v1.

---

## HIGH

### H-1. Zero-stop and single-stop trips are undefined across the whole model.
**Location:** FR-5 ("N Stops → N Legs"), FR-17 (≥2 placeable threshold), FR-14, FR-15.
**Failure mode:** FR-17 says the toggle "may be hidden or disabled" below 2 placeable POIs — *may*, not *must*, so the behavior is optional and untestable. With 1 placeable Stop: zero Legs, a "roundtrip" from a stop to itself, TSP-Sort on N=1 (no-op? error?), a timeline with one arrival and a finish equal to start. With 0 placeable Stops (all Unplaceable): FR-5's loop draws nothing, the Distance Matrix is empty, Start designation has no candidates. None of these are specified. The collection-level realities (a collection of all-no-coordinate POIs is entirely legal today) hit this immediately.
**Fix:** Make the ≥2-placeable threshold a hard MUST (toggle disabled with explanatory text below it), and specify N=0/N=1 degenerate behavior for TSP-Sort, Legs, timeline, and Start/Finish.

### H-2. Duplicate POIs in a Collection break the matrix key and the MCP order assignment.
**Location:** Glossary "Distance Matrix" keyed by Stops; addendum §A key `(FromPoiId, ToPoiId, TravelMode)`; FR-16.
**Failure mode:** If a Collection can contain the same POI twice (common: visit a hub café twice in a loop), the cache key `(FromPoiId, ToPoiId, Mode)` collapses the two occurrences — but they are distinct *Stops* with distinct Order and distinct Dwell. A self-loop Leg (PoiA→PoiA) has duration 0 in haversine but is meaningless. FR-16's "assign Stop Order numbers to the Collection's POIs" assumes POI-keyed assignment, which cannot address two Stops of the same POI distinctly. Either duplicates are forbidden (state it, and state what the importer/UI does on attempted dup) or the entire model must key on a Stop/membership id, not PoiId — which changes the addendum's cache key and MCP contract.
**Fix:** Decide if duplicate POIs are allowed. If yes, re-key everything (cache, MCP) on membership/Stop id, not PoiId. If no, state the constraint and its enforcement.

### H-3. POI deleted (or removed from collection) while in a Trip — no defined behavior.
**Location:** FR-2 ("removing a POI re-compacts the order"), addendum §A (StartPoiId/FinishPoiId nullable FKs, RouteSegment FKs).
**Failure mode:** FR-2 covers re-compaction for a removed POI but says nothing about: (a) removing the *Start* POI — does StartPoiId dangle, auto-reassign to the new Order-1, or block? (b) removing the *Finish* POI in an open-path trip — does it silently become a roundtrip? (c) cached RouteSegment rows referencing a deleted POI — orphan rows, FK cascade, or stale matrix entries that TSP-Sort later reads. A hard POI *delete* (not just collection removal) is a different path again and isn't mentioned. These dangling-reference cases are exactly where SQLite + EF cascade behavior bites silently.
**Fix:** Specify Start/Finish reassignment on removal, RouteSegment cascade/cleanup, and distinguish "remove from collection" vs "delete POI globally."

### H-4. Stop Order vs the Collection's own filtering/sorting is a direct, unaddressed collision.
**Location:** FR-17 (toggle lives in the "filtered-results region… the same region that shows the current POI result set"); FR-2 (Stop Order).
**Failure mode:** The toggle is deliberately placed in the *filtered-results region*. So the Collection already has filtering and sorting controls operating on the result set. When Trip View is on and Stop Order defines order, what happens to the existing sort control? If a user sorts by name, or filters to a subset, does the trip panel show filtered Stops only? Renumber? Does a filter that hides Stop 3 leave a gap (violating FR-2 contiguity) or re-compact the *view* while persistence keeps 1..N? Does TSP-Sort operate on the filtered subset or the whole collection? This is the single most likely UX/data confusion and the PRD picks the collision site (filtered-results region) without resolving it.
**Fix:** Specify the interaction: in Trip View, does sort lock to Stop Order? Does filtering scope the trip (subset trip) or just dim non-matching Stops? State whether TSP-Sort/timeline operate on filtered or full set.

### H-5. FR-15's "total Travel Time ≤ pre-sort" is true for the wrong reason and weakly testable.
**Location:** FR-15 Consequences ("resulting Stop Order has total Travel Time ≤ the pre-sort order").
**Failure mode:** This is asserted as a guarantee, but NN+2-opt is a heuristic with pinned endpoints; the *constructed* NN tour can be worse than a hand-curated pre-sort order, and 2-opt only guarantees ≤ its own starting point, not ≤ the user's arbitrary prior order. The PRD likely means "2-opt seeded from the current order never worsens it" — which requires the algorithm to seed from the existing order, a detail not stated (addendum §D seeds from nearest-neighbor, which can be worse than the user's order). As written the guarantee is either false or imposes an unstated implementation constraint. Also "≤" is untestable without specifying same provider, same Fidelity mix, and that no Leg degraded between measurements.
**Fix:** Either weaken to "2-opt is seeded from the current order so the result is never worse than the pre-sort order under the same matrix" (and require that seeding in the algorithm), or drop the guarantee. Pin the measurement conditions.

### H-6. v1's headline journeys depend on deferred decisions (begging the question / scope leak).
**Location:** UJ-3 (Air + ground feasibility) vs §6.2 "Per-Leg Travel-Mode override — deferred"; UJ-2/SM-3 vs §6.2 "which Measured provider — deferred."
**Failure mode:** UJ-3 is a flight leg *plus* ground stops in one trip — that is exactly per-Leg mixed mode, which §6.2 defers. The PRD patches this with the "Any/Air Manual Travel Time slice" (FR-8), but a trip in Any/Air mode computes *every ground leg* as great-circle haversine too (Glossary: Travel Mode is per-Trip). So Priya's ground hops in UJ-3 are straight-line air estimates, not drive times — her "40 minutes of slack" is computed on fantasy ground times. The journey reads as validated but the v1 mechanism can't actually produce a trustworthy version of it. Similarly SM-3 (≥95% Measured) and UJ-2's "real travel time" depend on the deferred provider decision; v1 with only Mock can never move SM-3.
**Fix:** Either pull single-leg mode override into v1 (the minimum to make UJ-3 honest) or rewrite UJ-3 to not claim ground-leg feasibility under Any/Air. Mark SM-3 explicitly N/A for the committed (Mock-only) v1 rather than as a primary metric.

---

## MEDIUM

### M-1. Seed order assumption (added-date) is unstable and not guaranteed available.
**Location:** FR-2 `[ASSUMPTION: seed by POI added-date ascending]`; addendum §A (`AddedDate`).
**Failure mode:** Assumes every membership row has a reliable AddedDate. Legacy/imported collections may have null or identical timestamps (bulk import → same instant), making "ascending" nondeterministic — directly contradicting "deterministic seed order." Ties need a stable tiebreaker (membership id).
**Fix:** Specify tiebreaker (e.g., AddedDate, then membership id ascending); define fallback when AddedDate is null.

### M-2. "One Collection ⇔ one Trip" plus persisted Trip fields means every Collection is now partly a Trip.
**Location:** Glossary "Trip" / "Collection"; addendum §A (TravelMode/Start/Finish on PoiCollection).
**Failure mode:** Storing TravelMode/StartPoiId/FinishPoiId/TripStartTime directly on PoiCollection means the migration adds trip columns to *all* collections, including ones the user will never tripify. Combined with the per-collection seed OrderIndex written on first toggle, the "it's just a lens" story is undercut by real schema weight on the base entity. Not fatal, but the "minimal new surface area" vision claim (§1) is overstated.
**Fix:** Acknowledge the base-entity migration cost honestly, or consider a sparse side-table so untripped collections carry no trip state.

### M-3. Background-job computation vs. the synchronous-feeling drag/timeline updates.
**Location:** FR-3 ("immediately updates Legs, Travel Times, Timeline"), FR-11 ("computation runs off the request thread; UI shows computing state"), SM-5 (≤3s warm).
**Failure mode:** FR-3 promises *immediate* update of Travel Times on drop; FR-11 says travel-time compute is an async background job with a "computing" state. These conflict: on a cold matrix a drag can't immediately show times. The PRD leans on "warm matrix" (SM-5) to hide this, but the first interaction on any trip is cold. The UX of "drag → spinner → numbers settle" is never described and the two FRs read as if both are simultaneously true.
**Fix:** Reconcile: FR-3 immediately updates *geometry/order*; Travel Times/timeline update asynchronously with a computing state when the matrix is cold. State the cold-first-interaction experience.

### M-4. MCP can set Start/Finish/Dwell/Order with no stated validation or conflict story.
**Location:** FR-16; FR-2 (contiguity/uniqueness invariants).
**Failure mode:** An external agent "assigns Stop Order numbers directly." Nothing says the system validates that the agent's assignment is contiguous/unique (FR-2 invariant) or rejects gaps/duplicates/out-of-range. Nothing covers concurrent edits (agent writes Order while user drags). "Persists identically to a manual drag" assumes the agent's raw numbers are normalized — unspecified.
**Fix:** State that MCP order assignment is normalized/validated to the same 1..N contiguous-unique invariant, with defined rejection or renumbering on malformed input, and a last-writer or optimistic-concurrency rule (the Version column exists per addendum §A).

### M-5. "Identical" line-style assumption and Fidelity-on-map are assumptions, not requirements.
**Location:** FR-6 `[ASSUMPTION: dashed for non-Measured]`; FR-9 ("badge in stop list and/or on the map").
**Failure mode:** "and/or on the map" is a requirement that permits the badge to be absent from the map — so a road-shaped line and a straight estimate could be visually indistinguishable if the implementer chooses list-only badging, defeating FR-6's "visually distinguishable" consequence. Two FRs disagree on whether map-level Fidelity indication is mandatory.
**Fix:** Make map-level distinction mandatory (line style is fine) and remove the "and/or" escape hatch for Fidelity indication where it concerns geometry.

### M-6. Time-budget flag (FR-13) references an input (the budget) the PRD never lets the user set.
**Location:** FR-13 ("exceeds a user-set time budget is flagged"); OQ9 ("where the user sets the budget — open").
**Failure mode:** A v1 consequence is gated on a UI affordance whose existence is an open question. The consequence is therefore untestable in v1.
**Fix:** Either drop the budget flag from MVP or commit a minimal budget input (per-trip field) and close OQ9.

---

## LOW

### L-1. "Working title — confirm" at the top of a launch-grade PRD.
**Location:** §title line 9. Leaves the product name unresolved in the document that feeds UX/architecture/epics. Resolve before downstream consumption.

### L-2. SM-2 "kept (not fully manually overridden) after one session" is fuzzy.
**Location:** SM-2. "Fully manually overridden" is undefined — one drag? all stops moved? Needs an operational definition or it can't be measured.

### L-3. Glossary "Routing Engine" vs "Travel-Time Provider" partial overlap.
**Location:** Glossary. "Routing Engine" is defined as a Measured Travel-Time Provider, but the PRD elsewhere uses "provider" generically. Two near-synonyms for the same contract invites the synonym-drift the Glossary preamble forbids.

### L-4. Roundtrip Leg-count math omits the all-Unplaceable / partial-placeable case.
**Location:** FR-5 ("N placeable Stops → N Legs"). N here is *placeable* Stops, but the closing Leg "to Start" assumes Start is placeable. If the designated Start is Unplaceable, the loop has no anchor. Unstated.

### L-5. "Computation mirrors PoiEnrichmentBackgroundService" assumes that service's serialization model scales to N² matrix writes.
**Location:** FR-11 assumption; addendum §G (SQLite write serialization). Enrichment is per-POI and sparse; a 30-stop trip is up to ~900 RouteSegment writes in a burst. The "mirror enrichment" assumption may not hold under matrix-sized write bursts on serialized SQLite. Flag for architecture.

---

## Summary of counts
- Critical: 4
- High: 6
- Medium: 6
- Low: 5
- **Total: 21**

The PRD's recurring tell is the word "identical" and the phrase "and/or": both are escape hatches that make consequences unfalsifiable. The data model's choice to key everything on PoiId (not membership/Stop id) is the structural fault line behind C-2, H-2, and H-3. Close those before architecture.
