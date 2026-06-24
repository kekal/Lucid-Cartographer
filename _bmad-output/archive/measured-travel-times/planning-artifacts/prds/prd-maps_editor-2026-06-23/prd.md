---
title: "Measured Travel-Time & Distance Estimation (Valhalla provider + smart-haversine default)"
status: final
created: 2026-06-23
updated: 2026-06-23
---

# PRD — Measured Travel-Time & Distance Estimation

## 1. Summary

LucidCartographer computes per-leg trip travel time and distance through a clean provider seam
(`ITravelTimeProvider`). Today only two providers exist, with a wide gap between them: a coarse
straight-line **haversine estimate** (the shipping default, zero infrastructure) and a **measured
OSRM** path that is honest but requires an afternoon of ops — a Geofabrik download, three
preprocessing passes, and three sidecar containers (one OSRM backend per profile).

This feature closes that gap with two moves, keeping the product's hard privacy guarantee intact:

1. **A smarter honest default.** Upgrade the straight-line estimate to a *smart-haversine* model
   that applies per-mode detour/winding factors on top of the great-circle distance — materially
   more realistic, still zero infrastructure, still badged as an estimate.
2. **A turnkey measured provider.** Add a **Valhalla** provider backed by a single auto-building
   `docker-valhalla` container that serves all travel modes from one engine. One container plus one
   environment variable replaces "three containers plus three preprocessing runs."

The hand-rolled OSRM provider and its multi-step setup are **removed** — Valhalla fully supersedes it.

The result is a clean fidelity ladder with three honest rungs — **raw straight-line → smart
estimate → measured** — surfaced to the user as two badges (Estimated / Measured), entirely
self-hosted, with stop coordinates never leaving the deployment at any rung.

---

## 2. Goals & Success Metrics

### Goals

- Deliver **measured** road durations/distances with a **turnkey** footprint — a product feature,
  not an ops project.
- Make the zero-infrastructure default **materially more accurate** without changing its honesty.
- **Preserve the hard privacy guarantee** (NFR7) at every fidelity rung.
- **Retire** the rigid, high-friction OSRM setup.

### Success Metrics

| # | Metric | Target |
|---|---|---|
| SM-1 | **Turnkey footprint** — steps to reach measured routing | From "download .pbf + 3 prep passes + 3 containers + compose edits" → **1 container + 1 env var** (`docker compose --profile valhalla up` + `tile_urls` + `TravelTime:Provider=Valhalla`) |
| SM-2 | **Privacy preserved (NFR7)** | **Zero** stop-coordinate egress at every rung, verified — no per-route external call; `.pbf` fetched only at tile-build time |
| SM-3 | **Accuracy uplift** | Verified by a repeatable method: on a fixed set of representative ground-mode legs with a known reference (Valhalla or an offline ground-truth set), the **smart-haversine** default's mean absolute duration error is at least *T₁* lower than raw straight-line, and **Valhalla** measured legs are within *T₂* of the reference. Thresholds *T₁/T₂* set during implementation (OQ-2). No adjective-only target. |
| SM-4 | **Clean fidelity ladder** | Every leg correctly badged across the estimate → measured progression in the trip view, with no regression to Manual/Measured cached data |

### Counter-metrics (watch for regressions)

- **No privacy regression:** the feature must not introduce any default code path that sends
  coordinates off-box (an external SaaS provider is admissible only as a strictly-consented opt-in,
  and is out of scope here).
- **No reliability regression:** a single failing leg must never fail the background pass; degraded
  legs must remain visibly honest (badged Estimated), not silently wrong.
- **No data loss:** Manual and Measured cache rows must never be downgraded or deleted by the change.

---

## 3. Background & Problem

The provider seam (`ITravelTimeProvider`) is already in place and needs no rework: providers slot in
behind it, are selected by `TravelTime:Provider` config, and the off-circuit
`TravelTimeComputationBackgroundService` degrades to a haversine estimate on any provider failure
(`Source=EstimatedFallback`, `[TRIP-DEGRADE-01]`). Canonical units are fixed at the edges (duration
in **seconds**, distance in **meters**). An OSM-derived provider already declares ODbL attribution
that surfaces on the map (NFR8).

The problem is purely the **gap between the two existing rungs**:

- **Default (Mock / haversine):** great-circle distance ÷ per-mode speed. Zero infra, but the
  straight-line distance ignores that roads wind — so it systematically under-estimates ground travel.
- **OSRM (opt-in):** measured and accurate, but reaching it is "an afternoon of ops, not a product
  feature": OSRM serves exactly one profile per backend, so all-mode coverage means three containers
  and three one-time `extract`/`partition`/`customize` passes, plus compose edits. Changing a profile
  means a full graph rebuild.

The **hard constraint** (NFR7) rules out external routing SaaS as a default: stop coordinates must
never leave the deployment. Both new rungs honor this.

---

## 4. Scope

### In scope

- A smart-haversine upgrade to the default provider (per-mode detour factors + existing per-mode speeds).
- A new self-hosted **Valhalla** measured provider behind the existing seam, config-selected.
- A single auto-building `docker-valhalla` compose service.
- **Full removal** of the hand-rolled OSRM provider, options, DI branch, compose services, tests, and docs.
- Operator documentation for the turnkey measured setup, replacing `docs/osrm.md`.
- Migration handling for operators currently on `TravelTime:Provider=Osrm`.

### Out of scope

- **Itinero** (in-process .NET routing) — fallback only; not built. Revisited only if a docker-free
  in-process engine becomes a requirement and its maturity risk is acceptable.
- **External routing SaaS** opt-in provider — admissible only as a strictly-consented opt-in; not built here.
- **In-app admin settings UI** for provider selection/status — configuration stays config/env-only,
  matching today's pattern. (Flagged as a possible follow-on.)
- No change to the trip ordering algorithm, the leg-projection sites, the cache key model, or the
  schedule/dwell model.
- No new mobile controls (consistent with the existing desktop/mobile deferral posture; this feature
  changes providers/config, not trip-view controls).

---

## 5. Users & Use

This is operator-facing infrastructure with a single relevant role:

- **Deployment operator** (self-hosts LucidCartographer). Wants accurate trip times without becoming
  a routing-engine ops specialist. Today they either accept coarse straight-line estimates or commit
  to the OSRM project. After this feature they get a better default for free, and measured routing is
  one container + one env var away — with the assurance that their stop coordinates never leave the box.

The **trip planner / end user** experiences this only as better numbers and correct fidelity badges in
the trip view; no new end-user workflow is introduced. (A standalone user-journey section is omitted by
design — single operator role, infrastructure-level change.)

---

## 6. Functional Requirements

### Feature A — Smart-haversine default (the honest default rung)

- **FR-1.** The default provider computes a per-leg ground estimate by applying a **per-mode
  detour/winding factor** to the great-circle (haversine) distance, then deriving duration from the
  existing per-mode speed. (Today the default applies per-mode speed to the *raw* straight-line
  distance with no detour factor.)
- **FR-2.** Detour factors and per-mode speeds are **operator-configurable** via the existing
  `TravelTime` config section and ship with documented sane defaults. `[ASSUMPTION]` defaults:
  Drive ≈ ×1.3, Cycle ≈ ×1.2, Walk ≈ ×1.15 detour factor over today's per-mode speeds — exact values
  to be sourced/empirically tuned in the implementation phase.
- **FR-3.** Smart-haversine ground legs are badged **Estimated** (not Measured) and report the
  adjusted distance (meters) and duration (seconds) through the existing seam. Air/AnyAir legs remain
  **Placeholder** ("—"), unchanged.
- **FR-4.** The provider-failure **degrade path** uses the *same* smart-haversine computation, so a
  degraded leg matches the default rung's accuracy and remains honestly badged
  (`Source=EstimatedFallback`, Fidelity Estimated). `[TRIP-DEGRADE-01]` is preserved.

### Feature B — Valhalla measured provider

- **FR-5.** A new `ValhallaTravelTimeProvider` implements `ITravelTimeProvider`, is selected via
  `TravelTime:Provider=Valhalla`, and returns **measured** road duration (seconds), distance (meters),
  and route geometry.
- **FR-6.** One Valhalla engine serves **all ground modes** via per-request dynamic costing; the
  provider maps Drive/Walk/Cycle to Valhalla's costing models (auto / pedestrian / bicycle). There is
  no per-mode backend and no rebuild to switch modes.
- **FR-7.** Measured legs are badged **Measured** and carry the road-geometry polyline for map
  display, matching today's OSRM-era behavior.
- **FR-8.** On any Valhalla failure (unreachable, timeout, no-route), the leg **degrades to
  smart-haversine** (`Source=EstimatedFallback`); one bad leg never fails the background pass.
- **FR-9.** Air/AnyAir legs are **not routed** through Valhalla (remain Placeholder), matching the
  existing ground-only computation policy.
- **FR-10.** The Valhalla provider declares its own OSM/ODbL **attribution**, which surfaces on the
  map through the existing attribution wiring (see NFR8). When the default (smart-haversine) provider
  is active, no routing attribution is shown (it is not OSM-derived).

### Feature C — Turnkey deployment

- **FR-11.** A single `docker-valhalla` compose service (under a `valhalla` profile) replaces the
  three OSRM sidecars. It **auto-downloads** the region `.pbf` from a configured URL and **builds
  tiles on first start** into a mapped volume, then serves; it auto-rebuilds when the `.pbf` changes.
- **FR-12.** The operator enables measured routing with exactly: start the profile
  (`docker compose --profile valhalla up`), set the region via **one** env var (`tile_urls`), and set
  `TravelTime:Provider=Valhalla`. **No** manual `extract`/`partition`/`customize` steps and no
  per-profile setup.
- **FR-13.** Operator documentation (replacing `docs/osrm.md`) describes the turnkey setup, region
  selection, expected one-time tile-build cost (time / disk / RAM for the operator's region), and
  restates the privacy guarantee.
- **FR-13a — First-boot / tile-build window.** While Valhalla is building tiles (first start, and on
  every `.pbf` auto-rebuild) it is unreachable; during this window measured routing is **not** yet
  available and ground legs **degrade to the smart-haversine estimate** (Fidelity Estimated) rather
  than erroring or hanging the trip view. The condition must be **operator-visible** — at minimum a
  clear startup/health log line stating tiles are building and routing is temporarily estimated — and
  the operator doc (FR-13) must set the expectation. The background pass re-attempts measured routing
  once Valhalla becomes reachable (legs are recomputed when the cache holds only an Estimated/fallback
  row, never overwriting Manual/Measured). `[confirm — is a log line sufficient, or is an in-app status indicator wanted? see OQ-8]`

### Feature D — OSRM removal & migration

- **FR-14.** Remove all hand-rolled OSRM artifacts: `OsrmTravelTimeProvider`, `OsrmOptions`,
  `OsrmRouteUnavailableException`, the OSRM branch in `TripServicesExtensions`, the
  `TravelTimeSource.Osrm` constant, the named `"osrm"` `HttpClient` registration, the three
  `osrm-*` compose services + their commented env block, OSRM-specific tests, and `docs/osrm.md`.
- **FR-15.** `TravelTime:Provider=Osrm` is **no longer recognized**. When an **unknown/retired**
  provider id is configured, the app **falls back to the smart-haversine default rather than failing
  to boot** — but because this silently downgrades a deployment from Measured to Estimated (the exact
  regression the counter-metrics forbid), the fallback must be **prominently surfaced**: a high-level
  startup warning naming the retired value *and* a release-note / migration-doc callout. It must not
  be a downgrade an operator can miss. `[confirm — warn+fallback (default) vs fail-fast; OQ-4]`
- **FR-16.** Existing cached `RouteSegment` rows produced by OSRM (`Source=OSRM`, Fidelity Measured)
  are **invalidated on a one-time migration** so they are recomputed by the active provider rather
  than persisting as stale, un-reproducible measurements that the never-downgrade-Measured guard
  (`[TRIP-MANUAL-01]`) would otherwise pin indefinitely. `Manual` rows are **never** touched. (This
  reverses the initial "keep" leaning after adversarial review — see OQ-5.) `[confirm — invalidate (default) vs keep; OQ-5]`

### Feature E — Fidelity ladder & badging

- **FR-17.** The trip view presents a coherent fidelity ladder using the **existing** badges —
  **Estimated** (smart-haversine: default and fallback), **Measured** (Valhalla), **Manual**
  (user-entered), and **Placeholder / "—"** for un-routable Air. No new badge type is introduced;
  the smart-haversine upgrade improves the accuracy *behind* the Estimated rung rather than adding a
  third visible tier. `[ASSUMPTION — confirm two-badge model is the intended "ladder"]`

---

## 7. Non-Functional Requirements

- **NFR7 — Privacy (HARD CONSTRAINT).** Stop coordinates must never leave the deployment at any
  fidelity rung. The smart-haversine default computes in-process; Valhalla routes against locally
  built tiles, fetching the `.pbf` from Geofabrik only at **tile-build time** (never per route). No
  default code path may send coordinates to an external service. This is non-negotiable.
  - **Containment (not just intent).** The constraint must be *designed in*, not asserted: the only
    permitted outbound network access from the Valhalla container is the **build-time** `.pbf`
    fetch. Routing requests from the app reach Valhalla over the internal compose network only. The
    deployment guidance must show how to keep it that way (e.g. no published egress beyond the build
    fetch; document that the third-party `docker-valhalla` image is the trust boundary and pin it —
    OQ-7). DNS/host resolution of the Geofabrik host at build time is the one acknowledged outbound
    contact and carries no stop coordinates.
  - **Verification method.** NFR7 is verified, not assumed: (a) a test/asserted check that the
    active provider issues **no per-route outbound call** carrying coordinates (the default computes
    in-process; Valhalla targets the internal endpoint only), and (b) a documented operator check
    that no stop-coordinate egress occurs during normal routing. `[confirm verification depth — automated test vs documented procedure; OQ-9]`
- **NFR8 — Attribution.** When an OSM-derived provider (Valhalla) is active, its OSM/ODbL routing
  attribution must surface on the map's attribution control. The smart-haversine default declares no
  routing attribution (not OSM-derived). The OSRM attribution string is replaced by a Valhalla one.
- **NFR-9 — Performance / footprint.** Valhalla's tile-based on-demand loading targets lower RAM
  (~4–8 GB) than OSRM's full-graph mapping. Tile build is a one-time cost per region (and per `.pbf`
  change), incurred off the request path. The off-circuit background computation service is unchanged;
  routing latency must not block the Blazor circuit. `[tile build time/disk/RAM — empirical, see OQ-3]`
- **NFR-10 — Reliability / graceful degradation.** A single leg's provider failure (including the
  whole tile-build window, FR-13a) degrades to the smart-haversine estimate and never fails the batch
  (`[TRIP-DEGRADE-01]`). The cache upsert must **never downgrade** a Manual or Measured row
  (`[TRIP-MANUAL-01]`); conversely, an Estimated/EstimatedFallback row must remain eligible for
  later upgrade to a measured value once the provider is reachable (see FR-13a).
- **NFR-11 — Canonical units.** Duration in seconds, distance in meters, fixed at the provider edge;
  no mid-layer conversion. Unchanged by this feature.
- **NFR-12 — Build discipline.** The new provider and config must compile clean under the project's
  `TreatWarningsAsErrors` + analyzer regime; no new group-B analyzer violations
  (MA0002/MA0015/MA0046/MA0047/MA0074/VSTHRD200).
- **NFR-13 — DI seam integrity.** The parameterless `AddTripServices()` overload registers the
  smart-haversine default (what the integration host composes by hand); the `IConfiguration` overload
  adds the config-selected Valhalla provider. The Trip integration test filter must pass after the DI
  change (recurring integration-host regression point).

---

## 8. Migration & Deprecation

- **OSRM is removed, not retained.** This is a **breaking change** for any operator currently running
  `TravelTime:Provider=Osrm`. The release notes / migration note must state: OSRM is gone; switch to
  Valhalla (single container) or accept the improved smart-haversine default.
- **Migration path for an OSRM operator:** stop the three `osrm-*` services, start the `valhalla`
  profile with a `tile_urls` pointing at the same region's `.pbf`, set `TravelTime:Provider=Valhalla`.
  Old `Source=OSRM` cache rows are invalidated once on migration and recomputed by Valhalla (FR-16);
  `Manual` rows are untouched.
- **Safe-by-default fallback:** a now-unknown `Osrm` value falls back to the smart-haversine default
  with a **prominent** warning (FR-15), so a stale config never bricks a deployment — while making the
  Measured→Estimated downgrade impossible to miss.

---

## 9. Dependencies & Constraints

- **`docker-valhalla` (nilsnolde)** image — the turnkey auto-download/auto-build container. External
  dependency; pin a known-good image tag rather than `latest` for reproducibility. `[confirm pinning]`
- **Geofabrik** `.pbf` regional extracts — the OSM data source, fetched at tile-build time only.
- **OpenStreetMap / ODbL** licensing obligation (NFR8) — already wired; attribution text updated.
- Existing seam, background service, cache model, and unit conventions — reused unchanged.

---

## 10. Open Questions & Assumptions

| ID | Item | Disposition |
|---|---|---|
| OQ-1 | Exact smart-haversine per-mode detour factors and speeds (drive ≈ ×1.3 from research; Walk/Cycle factors are PRD-introduced estimates) | `[ASSUMPTION]` defaults in FR-2; source/empirically tune in implementation |
| OQ-2 | Valhalla routing accuracy vs OSRM on representative regions; **set SM-3 thresholds T₁/T₂** | Validate + fix thresholds during implementation |
| OQ-3 | Tile build time + disk + RAM for the operator's region | Measure; document in FR-13 / NFR-9 |
| OQ-4 | Unknown/retired provider id → warn+fallback vs fail-fast (FR-15) | `[confirm]` — default: warn+fallback, **prominently surfaced** |
| OQ-5 | Keep vs invalidate existing `Source=OSRM` cache rows (FR-16) | `[confirm]` — default **revised to invalidate** (was keep) after adversarial review |
| OQ-6 | Two-badge ladder (Estimated/Measured) vs a distinct "good estimate" tier (FR-17) | `[confirm]` — default leaning: two badges |
| OQ-7 | Pin `docker-valhalla` image tag vs `latest` | `[confirm]` — default leaning: pin |
| OQ-8 | Tile-build window signal: startup/health log only vs in-app status indicator (FR-13a) | `[confirm]` — default leaning: log + doc now, in-app indicator a follow-on |
| OQ-9 | NFR7 verification depth: automated no-egress test vs documented operator procedure | `[confirm]` — default leaning: both, automated where feasible |

---

## 11. Future / Possible Follow-ons

- **In-app admin settings UI** for provider selection and Valhalla health/tile-build status.
- **Itinero** in-process provider, if a fully docker-free routing option is later required.
- **Strictly-consented external SaaS** opt-in provider for operators who explicitly waive NFR7.
- **Mobile trip-view control parity** (tracked separately; not affected by this feature).
