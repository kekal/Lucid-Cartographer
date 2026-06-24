# Adversarial Review — Measured Travel-Time & Distance Estimation PRD

**Reviewer posture:** Cynical / adversarial. Assume problems exist; find the holes a friendly reviewer waves away.
**Artifacts reviewed:** `prd.md`, `addendum.md`, source research (`technical-travel-time-distance-estimation-research-2026-06-23.md`).
**Date:** 2026-06-23

**Severity counts:** Critical 3 · High 6 · Medium 6 · Low 4 (19 findings)

---

## CRITICAL

### C-1. The privacy guarantee (NFR7) is asserted, not designed — `tile_urls` auto-download is an unaudited egress vector
**Location:** NFR7 (§7), SM-2 (§2), FR-11 (§6 Feature C), Dependencies (§9), addendum "docker-valhalla compose".

NFR7 says coordinates "must never leave the deployment ... This is non-negotiable and must be verifiable." But the PRD's own design hands the privacy boundary to a **third-party container image** (`ghcr.io/nilsnolde/docker-valhalla`) that the PRD has not audited and over which the operator has no control. The claim "the `.pbf` is fetched only at tile-build time, never per route" is repeated as fact (SM-2, NFR7, FR-11, research line 103) but **nowhere does the PRD describe how this is verified or enforced.** "Must be verifiable" is stated; the verification method is absent. Concretely unaddressed:

- The container talks to the network *by design* (it downloads `tile_urls` over HTTPS). So "no egress" is already false at the container boundary — the actual claim is the narrower "no *coordinate* egress," which is much harder to prove and is never tested.
- docker-valhalla supports more than tile download: depending on version/config it can fetch elevation tiles, admin/timezone data, and (if misconfigured) could expose or proxy. None of these are enumerated or pinned off.
- There is **no network-policy requirement** (egress firewall, `network_mode`, `internal:` network after build, DNS allowlist). A hard, "non-negotiable, verifiable" privacy constraint with **zero containment controls** specified is not a guarantee — it is a hope.
- **DNS leak:** even at tile-build time, resolving `download.geofabrik.de` leaks the operator's region interest to their DNS resolver. Minor vs. coordinate egress, but the PRD claims an absolute ("never leaves the deployment") that this contradicts.

**Why critical:** The single hard constraint of the feature is treated as satisfied-by-assertion. SM-2 says "verified" but defines no verification. This is the one requirement that, if wrong, sinks the feature.

**Demand:** A concrete NFR7 verification procedure (e.g., run the routing path with host egress blocked and assert routes still resolve; packet-capture assertion that no request body contains lat/lon during a route). A containment requirement: after tile build, the Valhalla container runs on an `internal` docker network / egress-denied. An explicit statement that the third-party image's network behavior was reviewed at the pinned tag.

### C-2. First-boot / tile-build window has undefined behavior — does routing error, hang, or silently degrade?
**Location:** FR-11 ("builds tiles on first start ... then serves"), FR-8 (degrade), NFR Performance (§7), OQ-3.

The PRD admits tile build is a "one-time cost" of unknown duration (OQ-3: "Measure") but **never specifies what the system does while tiles are building.** The operator sets `TravelTime:Provider=Valhalla` and starts the profile. During the (possibly long — continental extracts can take tens of minutes to hours and many GB) build, Valhalla's HTTP endpoint is either down, returning 503, or returning errors. What happens?

- FR-8 says "on any Valhalla failure ... degrades to smart-haversine." Does a *not-yet-ready* Valhalla count as a "failure" that degrades cleanly, or does it surface as repeated errors / a stuck background pass? The PRD does not say. "Unreachable, timeout, no-route" are listed; **"booted but tiles not built yet"** is a distinct state and is not.
- If it degrades, the operator who just turned on "measured" gets Estimated badges for an hour with no signal *why*. There is no requirement for a "tiles building" status, log line, or operator-visible signal. (The PRD even defers admin status UI to §11.)
- Will the background service hammer a 503ing endpoint every pass, and does the Polly pipeline back off? Unspecified.
- **`.pbf` change → auto-rebuild (FR-11):** the same undefined-behavior window reopens *in production* every time the region file updates. This is not a one-time first-boot concern; it is recurring.

**Why critical:** This is the most likely real-world first impression of the whole feature, and it is undefined. A degraded-but-silent first boot directly violates counter-metric "degraded legs must remain visibly honest" if there's no signal that *the whole provider* is warming up.

**Demand:** Explicit FR for the warming/unavailable state: detection, degrade behavior, backoff, and an operator-visible signal (log at minimum). Define whether "tiles building" is distinguishable from "Valhalla down."

### C-3. The headline accuracy metric (SM-3) is not measurable as written — no baseline, no ground truth, no threshold
**Location:** SM-3 (§2), Goals ("materially more accurate"), OQ-2, FR-2.

SM-3: "Measured (Valhalla) legs materially closer to real road time/distance than haversine; smart-haversine default materially closer than raw straight-line on ground modes." This is the central value claim and it is **untestable**:

- **"Materially closer"** — no number. 5%? 30%? Closer on average, median, or worst-case? Acceptance is undefined, so the metric can be declared "met" by inspection of one cherry-picked leg.
- **"Real road time/distance"** — what is the ground-truth oracle? The PRD has no source of truth. Valhalla itself isn't ground truth; comparing Valhalla to haversine only shows they differ, not that Valhalla is *correct*. OQ-2 punts ("validate during implementation") with no method or dataset.
- **Smart-haversine "materially closer than raw straight-line"** is near-tautological: multiplying distance by a detour factor >1 will, on average, increase ground-mode distance toward reality — but the *detour factors themselves are guesses* (FR-2: ×1.3/×1.2/×1.15, "exact values to be sourced/empirically tuned"). You cannot claim "materially closer" using factors you admit you haven't validated. A bad factor makes it *materially wrong in the other direction*.

**Why critical:** The feature's primary justification ("materially more accurate") cannot be confirmed or refuted at acceptance. A success metric you can't fail is not a success metric.

**Demand:** A defined ground-truth set (e.g., N representative routes with known real-world drive times, or a documented reference such as Google/known distances used once for tuning only), a numeric threshold for "materially closer," and the statistic (median absolute % error, etc.).

---

## HIGH

### H-1. OSRM removal is a breaking change with a silent-downgrade trap, and the fallback decision is still open
**Location:** §8, FR-14, FR-15, OQ-4.

The PRD calls OSRM removal "breaking" but the migration story relies on FR-15: an unknown `Osrm` value "falls back to smart-haversine default and logs a startup warning." Problems:

- **This is exactly the silent downgrade the counter-metrics forbid in spirit.** An operator who upgrades without reading release notes goes from *Measured* routing to *Estimated* — silently, except for one startup log line they will never see. Their trip times degrade in accuracy and the only signal is buried in boot logs. That is a production regression dressed as "safe-by-default."
- FR-15 is marked `[confirm — warn+fallback vs fail-fast]` and OQ-4 is still open. **The migration safety model is undecided in a PRD that calls itself a breaking change.** You cannot finalize a breaking-change PRD while the breaking-change handling is a TBD.
- Fail-fast vs warn-fallback is a genuine fork with opposite failure modes (won't boot vs. boots wrong). The PRD "leans" but doesn't commit. Pick one and own the consequence.

**Demand:** Resolve OQ-4 before sign-off. If warn+fallback is chosen, add a *runtime, operator-visible* signal (not just a startup log) that the configured provider was rejected.

### H-2. Old `Source=OSRM` cache rows are kept but become permanently un-reproducible and indistinguishable in fidelity from Valhalla
**Location:** FR-16, OQ-5, SM-4, NFR Reliability ("never downgrade Measured").

FR-16 keeps old OSRM rows as valid Measured data, "no longer produced." Cynic's read:

- These rows are now **orphaned measurements**: produced by an engine that no longer exists, on graph data of unknown vintage, never refreshed. They sit in the cache badged *Measured* — visually identical to a fresh Valhalla measurement — and the "never downgrade Measured" guard (`[TRIP-MANUAL-01]`) means **a new Valhalla pass may be blocked from overwriting a stale OSRM row.** So an operator who switches to Valhalla may keep seeing *old OSRM numbers* indefinitely for already-computed legs. Is that intended? The PRD doesn't address the interaction between FR-16 and the no-downgrade guard at all.
- OQ-5 is still open (`[confirm]`). Again, a core data-lifecycle decision is unresolved.
- SM-4 promises "no regression to ... Measured cached data" — but keeping un-reproducible rows that block fresh computation *is* arguably a regression in data quality, just not in data presence.

**Demand:** Resolve OQ-5. Specify the interaction: do OSRM rows get re-measured by Valhalla (requiring an exception to the no-downgrade-Measured guard), or are they frozen? If frozen, justify why stale, un-reproducible measurements outrank fresh ones.

### H-3. Image pinning is "confirm pinning" — a hard privacy + reproducibility dependency left as an open question
**Location:** §9, OQ-7, addendum compose (`<pinned-tag>`), research compose (`:latest`).

The PRD says pin the image "rather than `latest`" but marks it `[confirm]` (OQ-7) — and the **research doc's compose example uses `:latest`**, so the source-of-truth artifact already contradicts the recommendation. For a feature whose privacy guarantee depends on the *exact network behavior of a third-party image* (see C-1), running `:latest` means the privacy posture can change silently on any `docker pull`. Pinning is not a nice-to-have here; it is a precondition for C-1's "we reviewed the image's network behavior" to mean anything.

**Demand:** Make pinning a hard requirement, not an open question. Specify a digest pin (`@sha256:`), not just a tag (tags are mutable). State the reviewed version.

### H-4. "Turnkey" (SM-1) ignores the real operator costs — disk, RAM, build time, and a multi-GB download
**Location:** SM-1, FR-12, NFR Performance, OQ-3.

SM-1 reduces success to "1 container + 1 env var" and FR-12 says "exactly: start profile, set one env var, set provider." This is marketing, not an honest footprint:

- The operator must also provision a **mapped volume with enough disk** for the `.pbf` + built tiles (can be many GB for a country/continent), **enough RAM** for the build, and **tolerate the build time** — all admitted as unknown in OQ-3 and "empirical." "1 env var" hides "and pick a region small enough that your box can build and serve it."
- There is no requirement to **fail loudly on insufficient disk/RAM** during build. docker-valhalla failing mid-build on a small box is a likely real-world outcome and the PRD has no story for it (relates to C-2).
- "1 container replaces 3" is true but the *3 OSRM containers each held one small preprocessed profile*; the single Valhalla container holds the whole multi-modal tileset. The resource comparison is not as flattering as SM-1 implies, and NFR Performance's "~4–8 GB RAM" is lifted from the vendor/research with no validation on the operator's actual region.

**Demand:** SM-1 should acknowledge resource provisioning as part of the footprint. Add an FR for build-failure surfacing (out-of-disk/RAM). Validate the RAM/disk/time figures for at least one concrete target region and document per FR-13.

### H-5. No requirement that Valhalla's costing models actually match the product's travel modes / speeds
**Location:** FR-6, FR-1/FR-2 (smart-haversine speeds), addendum costing mapping.

FR-6 maps Drive→auto, Walk→pedestrian, Cycle→bicycle and calls it done. But:

- The smart-haversine rung uses **operator-configured per-mode speeds** (Drive 50 / Walk 5 / Cycle 15 km/h). Valhalla's `auto`/`pedestrian`/`bicycle` use their *own* internal speed/costing assumptions, which the operator cannot align to those configured speeds. So the two rungs of the "fidelity ladder" can disagree in *direction* (Valhalla faster than the estimate, or slower) in ways that look like a bug to the end user. The PRD never requires consistency or even acknowledges the discontinuity.
- The degrade path (FR-8) falls back from Valhalla (auto costing) to smart-haversine (configured speed). A leg can therefore **flip its travel time noticeably** depending on whether Valhalla was up — the same leg, two very different numbers, both "honest." No requirement addresses jarring value swings across the degrade boundary.

**Demand:** A requirement (or at least an acknowledged open question) on cross-rung consistency: how much may Estimated and Measured diverge before it's considered a defect, and is the degrade-induced value swing acceptable.

### H-6. Geometry/polyline precision mismatch is flagged in the addendum but absent from the PRD's requirements and acceptance
**Location:** addendum ("Valhalla returns `polyline6` ... confirm precision matches the map's decoder"), FR-7, NFR Attribution.

The addendum quietly notes Valhalla returns `polyline6` and the existing decoder expected OSRM's precision-6 — "confirm." This is a concrete way the map silently breaks: a precision mismatch renders route geometry **offset or scrambled on the map** while every number (duration/distance) looks fine, so it can pass a numbers-only test and ship visibly broken. FR-7 ("carry the road-geometry polyline for map display") states the requirement but neither it nor SM-4 mentions geometry correctness as an acceptance criterion.

**Demand:** Promote polyline-precision correctness into FR-7 and into acceptance (visual/render check), not buried as an addendum "confirm."

---

## MEDIUM

### M-1. Detour factors are global constants, but real detour ratio varies enormously by geography
**Location:** FR-1, FR-2.

A single per-mode multiplier (×1.3 drive) is applied everywhere. Real route-to-straight-line ratio differs wildly: dense gridded city vs. fjord/mountain/island geography vs. crossing a river/bay with one bridge. The smart-haversine estimate will be *systematically* wrong (not just noisy) for any operator whose region has constrained geography — and the PRD presents one global factor as "materially more realistic" without bounding the error. For short legs the absolute error is small; for a leg that straight-lines across a bay the estimate can be off by multiples.

**Demand:** At minimum acknowledge the limitation in FR-1 and in SM-3's honesty framing; consider documenting that the factor is a crude average and tuning is regional.

### M-2. "Smart-haversine" branding overclaims for a fixed multiplier
**Location:** Summary (§1), FR-1, naming throughout.

There is nothing "smart" about `distance × constant`. It is a linear correction factor. The naming sets an expectation (adaptive, learned, geography-aware) that the implementation (FR-1/FR-2 fixed constants) does not meet. Counter-metric language stresses honesty; the *name itself* is the least honest thing in the document.

**Demand:** Either rename (e.g., "detour-adjusted estimate") or explicitly define "smart" = "per-mode detour factor" up front so reviewers/operators don't over-trust it.

### M-3. Air/AnyAir legs are "Placeholder / —" forever — but this feature touched the estimator and never reconsidered it
**Location:** FR-3, FR-9, FR-17.

Air legs remain "—". Fine as stated, but the PRD is about *improving estimates* and great-circle distance is *exactly right* for air legs (it's literally what aircraft approximate). Leaving air as an empty placeholder while shipping a haversine engine that already computes the perfect air distance is a curious gap. Not a defect, but the PRD doesn't justify why the one mode where haversine is accurate gets no number.

**Demand:** A one-line rationale for why air stays Placeholder despite the estimator now being in hand.

### M-4. No requirement covering request timeout / latency budget for Valhalla per-leg routing
**Location:** FR-8, NFR Performance, addendum (`RequestTimeoutSeconds`).

The addendum mentions a timeout config but the PRD has no NFR bounding per-leg latency or total background-pass time for a large trip. A 200-stop trip = ~199 sequential `/route` calls; if each is slow (or timing out then degrading), the pass could take a long time or thrash. "Off the request path" protects the Blazor circuit but says nothing about pass duration or whether legs are batched/parallelized. Valhalla also offers matrix endpoints that could collapse N calls — unmentioned.

**Demand:** An NFR for per-leg timeout and a position on pass-level performance for large trips (sequential vs. matrix/parallel).

### M-5. Attribution swap (NFR8) can leave a window where the wrong/no attribution shows
**Location:** NFR8, FR-10, addendum attribution wiring.

The attribution string is provider-driven: Valhalla active → show Valhalla/ODbL; smart-haversine active → show nothing. But during degrade (Valhalla → smart-haversine fallback at the *leg* level), the *provider* is still nominally Valhalla while the *data* is smart-haversine. Which attribution shows for a trip that is a mix of Measured and Estimated-fallback legs? The wiring is per-provider, not per-leg. ODbL is a licensing obligation; showing it when no OSM data was used, or *failing* to show it when some legs are OSM-derived, are both wrong. The PRD doesn't address mixed-provenance trips.

**Demand:** Define attribution behavior for mixed Measured/EstimatedFallback trips. ODbL exposure should track whether *any* displayed leg used OSM data.

### M-6. Counter-metrics and SM-4 promise "no regression / no data loss" but define no test to prove it
**Location:** §2 counter-metrics, SM-4, NFR Reliability.

"Manual and Measured cache rows must never be downgraded or deleted." Good intent, but the only mechanism cited is the existing guard. The OSRM *removal* (FR-14 deletes the `Source=OSRM` constant) interacts with rows that still carry that source value (FR-16 keeps them). If `TravelTimeSource.Osrm` is deleted from code (addendum deletion targets) but rows in the DB still say `Source=OSRM`, does anything that maps/parses source strings break, throw, or mislabel? The PRD asserts the rows "remain valid" without confirming the code that reads them survives deletion of the constant.

**Demand:** A regression test requirement specifically for "DB rows with a now-deleted source constant still load and display correctly," not just a restated guarantee.

---

## LOW

### L-1. "Off-circuit" / `[TRIP-DEGRADE-01]` / `[TRIP-MANUAL-01]` tags assume reader context
**Location:** throughout §3, §6, §7. These code-tag shorthands are meaningful to the author but the PRD never expands them inline; a downstream implementer or reviewer without the codebase open is guessing. Minor, but a self-contained PRD shouldn't lean on tribal tags.

### L-2. Six of seven open questions "lean" to a default but none are resolved
**Location:** §10 (OQ-1..OQ-7). A PRD marked for build with its breaking-change handling (OQ-4), data lifecycle (OQ-5), badging model (OQ-6), and image pinning (OQ-7) all still `[confirm]` is not decision-complete. "Leaning" is not deciding. At least the breaking/privacy-adjacent ones (OQ-4, OQ-5, OQ-7) must be closed before sign-off.

### L-3. `tile_urls` is documented as a single region URL — multi-region operators have no story
**Location:** FR-12, addendum compose. docker-valhalla `tile_urls` can take multiple URLs, but FR-12 hard-codes "the region via one env var" as the whole story. An operator spanning two Geofabrik extracts (e.g., a trip crossing a region boundary) gets no-route at the seam and silently degrades to Estimated. Not wrong, but "1 env var = done" oversimplifies for cross-region trips, and cross-region no-route degradation is unmentioned.

### L-4. Success metric SM-4 conflates two claims into one untestable line
**Location:** SM-4. "Every leg correctly badged ... with no regression to Manual/Measured cached data" bundles badging correctness and data-preservation into one row with one undefined pass/fail. Split them; each needs its own check.

---

## Summary judgment

The PRD is well-organized and honest about *some* of its gaps (it flags assumptions and open questions), but it **finalizes a breaking change while its breaking-change handling, data-lifecycle, and image-pinning decisions are all still open**, and — most seriously — it treats its one hard constraint (NFR7 privacy) and its one headline metric (SM-3 accuracy) as satisfied by assertion rather than by any specified verification. The privacy guarantee is delegated to an unaudited third-party image with **no containment controls and no defined verification**; the accuracy claim has **no ground truth and no threshold**; and the **first-boot / tile-build window is undefined behavior** that will likely be every operator's first experience of the feature. These three (C-1, C-2, C-3) should block sign-off until specified. The OSRM-removal migration (H-1, H-2) risks a *silent* Measured→Estimated downgrade — precisely the class of regression the counter-metrics claim to forbid.

**Recommendation:** Do not sign off as-is. Resolve OQ-4/OQ-5/OQ-7, specify NFR7 verification + container egress containment, define the tile-build/warming behavior with an operator-visible signal, and give SM-3 a ground truth and a number.
