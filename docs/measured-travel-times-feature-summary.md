# Feature Summary — Measured Travel-Time & Distance Estimation

**Status:** Shipped 2026-06-24 (`451a0ec`) · **Type:** Multi-epic brownfield feature on the Trip slice
**Scope:** 3 epics · 12 stories · 17 FRs (+FR-13a) · 7 NFRs · Desktop + provider/infra
**Sources:** PRD `_bmad-output/planning-artifacts/prds/prd-maps_editor-2026-06-23/prd.md`
(status:final, +`addendum.md` + reviewer artifacts) · Architecture
`_bmad-output/planning-artifacts/architecture.md` (9 ADs) · Epics
`_bmad-output/planning-artifacts/epics.md` · Stories + retros
`_bmad-output/implementation-artifacts/` (`story-*.md`, `epic-{1,2,3}-retro-2026-06-24.md`).
As-built reference: [trip-planning.md](trip-planning.md), [architecture.md](architecture.md),
[data-models.md](data-models.md), [deployment-guide.md](deployment-guide.md),
[valhalla.md](valhalla.md). Baseline commit: `da4b888`; shipped in `451a0ec`.

> The travel-time milestone. Earlier Trip work made the schedule honest and the panel compact; this
> milestone makes the *numbers* honest. It replaces a naïve straight-line estimate with a smarter
> estimate by default, adds a self-hosted **measured**-routing option that never leaks coordinates,
> and retires the old hand-rolled OSRM pipeline that was too heavy to ship as a feature.

---

## What shipped

Three coordinated changes to how leg travel-times and distances are produced, sequenced lowest-risk-first.

**1 — A smarter honest default (smart-haversine).** The estimate path now multiplies straight-line
distance by a configurable **per-mode detour factor** (drive ×1.3 / cycle ×1.2 / walk ×1.15, all
tunable in `appsettings.json`) before deriving duration from per-mode speed. It is still an *estimate*
(badged as such), but a materially better one — zero infrastructure, applied at a single code edge so
both the default `Mock` provider and the provider-failure fallback inherit it for free. Any/Air stays a
placeholder. The TSP ordering cost matrix deliberately stays on **raw** haversine, so stop ordering is
mode-invariant.

**2 — Self-hosted Valhalla measured routing (turnkey opt-in).** A new `ValhallaTravelTimeProvider`
delivers genuinely **measured** road distances/times for all ground modes via one self-hosted
`docker-valhalla` container (one engine, per-request costing per mode). Selected by config
(`TravelTime:Provider=Valhalla`). A new capability flag on the provider seam,
`ITravelTimeProvider.ProducesMeasuredFidelity`, gates a broadened background recompute that *upgrades*
existing estimated/placeholder legs to measured — without churning the Mock default. During the
one-time tile-build window the service degrades gracefully to Estimated with an operator-visible signal.

**3 — Retire OSRM.** The old multi-sidecar OSRM pipeline (download `.pbf` → 3 preprocessing passes → 3
profile containers) is removed outright. A retired/unknown `Provider` id now boots with a prominent
"ESTIMATED, not MEASURED" warning and falls back to the smart-haversine default instead of failing. A
one-time startup purge invalidates legacy `Source="OSRM"` cache rows (preserving Manual times); purged
legs refill via the existing missing-row trigger. All hand-rolled OSRM artifacts — provider, options,
exception, the `osrm` HttpClient, compose services, appsettings section, docs, and tests — are deleted.

---

## Key decisions & rationale

- **Privacy is a hard constraint (NFR7), so external routing APIs are out.** Stop coordinates must never
  leave the deployment. This is the axis the whole feature turns on: the "measured" option had to be
  self-hosted. Valhalla fetches its `.pbf` at *tile-build* time, not per route, so no per-request egress
  — verified by a dedicated `NoEgressTests` guard. ODbL attribution is wired (NFR8).

- **smart-haversine = detour factors only.** `MockTravelTimeProvider` already applied per-mode *speeds*;
  the honest-default work adds per-mode *detour factors* on top. The factors land in **one** place
  (`EstimatedTravelTime.Compute`) so the default and the fallback share a single estimate edge (AD-1).

- **The detour factor must never reach the TSP matrix (AD-1 / RD3).** `DistanceMatrixService` keeps the
  cost matrix on raw haversine so stop ordering is identical regardless of detour config — pinned by a
  regression test that runs the ordering under default vs. exaggerated factors.

- **A capability seam, not a type-check, drives the recompute upgrade (AD-2).** The background service
  broadened from "no row exists" to "OR upgrade-eligible (Estimated/Placeholder & Source∈{Mock,Fallback})",
  gated on `ProducesMeasuredFidelity` (Mock=false, Valhalla=true) so the Mock default never thrashes.

- **OSRM removed outright, not kept as legacy (FR-14/FR-16, reversed at PRD time).** The research phase
  suggested keeping OSRM as an optional legacy provider; the PRD reversed this — it was the ops burden the
  feature exists to escape. A stale `Provider=Osrm` config now warns + falls back rather than breaking.

- **Legacy OSRM cache rows are invalidated, not kept (FR-16, reversed after adversarial review).** A
  one-time startup purge matches the literal `Source="OSRM"` (the constant itself is deleted), skips
  Manual rows, logs the count, and is self-retiring/idempotent.

- **Valhalla image pinned by tag, config/env-only (no admin UI).** Operators tune via env (`tile_urls`)
  and compose; no in-app provider admin surface.

---

## Architecture deltas (vs. before this feature)

| Area | Before | After |
|------|--------|-------|
| Estimate quality | straight-line haversine + per-mode speed | + per-mode **detour factor** (`TravelTimeOptions.DetourFactorFor`) applied in `EstimatedTravelTime.Compute` |
| Measured routing | hand-rolled OSRM (3 sidecars, 3 preprocessing passes) | self-hosted **Valhalla** (1 container, per-request costing) via `ValhallaTravelTimeProvider` |
| Provider seam | `ITravelTimeProvider` | + `bool ProducesMeasuredFidelity` capability flag |
| Background recompute | fills only missing rows | + upgrades estimate→measured when provider is measured-capable (capability-gated) |
| Provider selection | `=="Osrm"` DI branch | `ClassifyProvider` → Default / Valhalla / RetiredOrUnknown; retired id warns + falls back |
| Cache migration | — | one-time startup purge of `Source="OSRM"` rows (Manual preserved) in `StartupCleanupService` |
| `RouteSegment.Source` | …, `OSRM` | …, **`Valhalla`** (`OSRM` const removed; literal `"OSRM"` survives only in the purge) |
| Compose | `osrm-*` profile services | single `valhalla` profile service (image pin, port 8002, `tile_urls`, self-healing build window) |
| In-app docs | `/docs/osrm.md` | `/docs/valhalla.md` (operator guide) |
| Polyline decoding | precision-5/6 conditional (OSRM was the precision-5 source) | unconditional **precision-6** (OSRM removed) |
| VM recommendation | `RecommendsOsrm` | `RecommendsMeasuredProvider` (re-gated on the capability seam) |

**No DB schema change.** `Source` gains a value but the table/migration count is unchanged. Single Blazor
Server container + SQLite; default provider remains the (now smarter) estimate; measured routing is a
self-hosted opt-in. Mobile panel unchanged.

---

## Lessons

- **One estimate edge pays off again (A1).** Because `EstimatedTravelTime.Compute` is the sole estimate
  site, the detour factor reached both the default and the failure-fallback with a single change and no
  second code path to keep in sync.

- **Removal can resolve a carry-over better than new code (A10).** Epic 2 left a concern about a global
  polyline precision flip (1e-5→1e-6) that OSRM temporarily shared. Epic 3 didn't fix it with code — once
  OSRM (the only precision-5 source) was deleted, precision-6 became unconditionally correct.

- **Re-aligning a test ≠ weakening it (A3).** Two tests that asserted equal ground-mode distances were
  re-aligned by setting equal detour factors, so they still isolate the per-mode *speed* effect rather
  than being deleted or loosened.

- **Scope discipline on manual legs (A4).** `TripViewModel.SetManualLegTimeAsync` still records raw
  haversine for a user-typed leg time — left intentionally, since the detour factor belongs to the
  *estimate* path and a manual leg's duration is user-supplied.

---

## Known follow-ups (deferred — need a live Docker host)

These three were flagged in the Epic 2 retro and remain open because Docker was unavailable during the
build session (compose/tile-build were validated structurally and via operator-manual steps):

- **Pin the Valhalla image by `@sha256:` digest** (currently the `:3.5.1` tag).
- **Document the `use_tiles_ignore_pbf` rebuild caveat** — tiles aren't auto-rebuilt on a `.pbf` change
  without clearing the volume; an operator-doc nuance vs. FR-11.
- **Measure & finalize tile-build cost** (time/disk/RAM, OQ-3) in [valhalla.md](valhalla.md) — currently
  an `[ASSUMPTION]`/operator-verify placeholder.

---

## Verification at close

Shipped `done`, merged to `master` (`451a0ec`, clean tree, pushed). Build clean under
`TreatWarningsAsErrors` (0/0). **Full suite 1190/1190 passing** (Trip integration filter green
throughout). New coverage includes `ValhallaTravelTimeProviderTests`, `TravelTimeOptionsTests`,
`TravelTimeSourceTests`, `NoEgressTests` (NFR7 egress guard), `TripViewModelRecommendsMeasuredProviderTests`,
`ValhallaDocsLinkIntegrationTests`, the Epic 3.1 provider-classifier tests, and the Epic 3.2 OSRM-purge
tests; OSRM-specific tests were removed alongside the provider.
