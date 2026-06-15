---
stepsCompleted: [1, 2, 3, 4, 5, 6, 7, 8]
lastStep: 8
status: 'complete'
completedAt: '2026-06-15'
inputDocuments:
  - _bmad-output/planning-artifacts/prds/prd-maps_editor-2026-06-15/prd.md
  - _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-15/EXPERIENCE.md
  - _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-15/DESIGN.md
  - _bmad-output/project-context.md
  - _bmad-output/archive/trip-planning/planning-artifacts/architecture.md
workflowType: 'architecture'
project_name: 'maps_editor'
user_name: 'Yurik'
date: '2026-06-15'
---

# Architecture Decision Document

_This document builds collaboratively through step-by-step discovery. Sections are appended as we work through each architectural decision together._

## Project Context Analysis

### Requirements Overview

**Functional Requirements:** 33 FRs across 8 feature groups (A–H) on the
already-shipped Trip Planning feature. Architecturally they separate into three
risk classes, NOT one uniform change:
- **Layout-only (A, C; FR-1–6, 11–12):** desktop Trip View *replaces* the wide
  filtered-results region with the trip stop table (hiding PoiTable), drops the
  256px `w-64` side column and the selection batch toolbar, and moves per-leg
  travel time onto an inter-row connector on the shared edge of consecutive rows.
  Markup/Tailwind in `MapPage.razor` reusing the existing `TripStopList`/VM — no
  new ordering or timeline logic. Mobile already does this switch (no change).
- **Shared-layer correctness/legibility (B, D, E, H; FR-7–10, 13–18, 31–33):**
  reconciled arithmetic (display total == sum of displayed per-leg minutes,
  round-once-at-edge), the minute unit "m"→"min" in `UiStrings`, self-explaining
  fidelity badges + a Mock-default "all estimates / enable OSRM" note, icon-button
  `title` tooltips at `aria-label` parity, and the Finish-vs-"Return to start"
  readout. These live in code mobile also runs (`ItineraryTimeline`,
  `TravelTimeFormatting`, `UiStrings`, `TripViewModel`) so they reach BOTH surfaces
  by nature and must keep `MobileTripPanel` correct.
- **New capability (F, G; FR-19–30):** per-leg travel mode and a multi-day
  schedule — the only structurally novel work (see below).

**Non-Functional Requirements:** strict Component→VM→Service→Data layering (the
`.razor` stays a markup-only bridge; arithmetic never in the component); canonical
units fixed at the edges (seconds/meters/minutes, convert only at UI/provider
boundary); no change to `RouteSegment` cache semantics, the directional
`(From,To,Mode)` key (TRIP-CACHE-01), or the default `Mock` provider; per-leg mode
adds a nullable column via a small EF migration constrained by `TravelMode.All`
(TRIP-SCHEMA-01); shared-layer changes authored once must not break mobile; all
copy via `UiStrings`; Tailwind `surface-*`/`on-surface-*`/`primary` tokens only;
no group-B analyzer violations, `TreatWarningsAsErrors` holds; a11y parity
(tooltips to AT, `aria-live`, keyboard reorder + list↔map sync intact after the
takeover); after any Trip VM/DI/schema change run the Trip integration filter.

**Scale & Complexity:**
- Primary domain: full-stack web (Blazor Server monolith, .NET 8 / C# 14)
- Complexity level: Medium — multi-epic with one schema migration and one MCP
  contract change, but mostly lands on proven Trip-slice patterns; novelty is
  bounded to Features F and G.
- Estimated architectural components: ~1 EF migration (per-leg mode column),
  1 VM projection reshape (trip-wide mode → per-leg), 1 MCP contract extension
  (per-leg-mode read + new set-leg-mode tool), display-edge arithmetic
  consolidation, plus the desktop layout takeover (no new service-layer slices).

### Technical Constraints & Dependencies

- **Brownfield-delta-first:** extends the established Trip architecture
  (`_bmad-output/archive/trip-planning/planning-artifacts/architecture.md`) and
  the 24 project-context rules; reuse `TripViewModel`, `TripOrderingService`
  (sole `OrderIndex` writer), the `RouteSegment` cache, the provider seam, and the
  background compute service unchanged in shape.
- **DI seam discipline (recurring regression point):** Trip services register a
  parameterless `AddTripServices()` (what `IntegrationTestBase` composes by hand)
  plus an `AddTripServices(IConfiguration)` overload. Any new VM/service dependency
  from Features F/G must be added to BOTH or the integration host breaks while
  `Program.cs` still boots. Run the Trip integration filter after any such change.
- **Schema:** one additive migration via startup `MigrateAsync` — nullable
  `PoiCollectionItem.OutgoingTravelMode` constrained by the `TravelMode.All` check
  pattern (TRIP-SCHEMA-01). `TripStartTime`/`TimeBudgetMinutes`/`DwellMinutes`
  already exist — Feature G is UI-edge affordances, no schema change. Never
  EnsureCreated; never hand-edit an applied migration.
- **MCP contract migration:** retiring `PoiCollection.TravelMode` (FR-23) forces a
  matching `map_editor` MCP change — `get_trip` must report each leg's mode and a
  new tool must set a leg's mode, or AI-assigned trips are stranded at Any.
- **Cross-surface invariant:** shared-layer edits (units, arithmetic, F data/VM,
  G persistence) apply to both surfaces; mobile new-feature *controls* are deferred
  to a follow-up mirror phase, but mobile data/strings/times must stay correct.

### Cross-Cutting Concerns Identified

- **Honesty/Fidelity model carried end-to-end** — reconciled arithmetic
  (round-once-at-edge), Any/Air never auto-estimated, Manual never auto-overwritten
  (TRIP-MANUAL-01), "—" for uncomputed/undefined legs, mixed-fidelity aggregate.
- **Per-leg mode as the new shared spine** — schema + cache key (already
  directional, TRIP-CACHE-01) + VM projection + connector UI + MCP, with
  newly-appeared legs resetting to Any/Air while unchanged legs retain mode+time.
- **Display-edge unit conversion** — all minute/HH:MM/date formatting at the UI
  boundary; canonical seconds/meters/minutes never converted mid-layer.
- **Cross-surface shared layer** — one authored change reaches desktop + mobile;
  `MobileTripPanel` must not regress.
- **Accessibility parity after the takeover** — `title` tooltips to AT, `aria-live`,
  keyboard reorder, and list↔map two-way sync all preserved when the list owns the
  wide region.
- **DI/integration-host seam** — the parameterless `AddTripServices()` overload is
  the known break point for VM-ctor/dependency changes.

### Open Questions routed to this workflow
- OQ-A — connector placement/encoding for the per-leg mode + time + reset
  affordance (UX [ASSUMPTION]: left-indented under the name column; confirm at mock).
- OQ-B — round-then-sum strategy for FR-13/15: does displaying total as the sum of
  displayed per-leg minutes preserve `ItineraryTimeline` honesty (partial-trip "—",
  fallback) semantics?
- OQ-C — MCP per-leg-mode tool shape (new `set_leg_travel_mode` vs extending an
  existing tool) and how `get_trip` surfaces per-leg modes without breaking the
  Epic-3 AI-assignment contract.

## Starter Template Evaluation

### Primary Technology Domain

Full-stack web — Blazor Server (.NET 8 / C# 14) monolith. **Brownfield delta**: a
feature extension on the existing, running LucidCartographer app (already carrying
the shipped Trip Planning slice), not a new project.

### Starter Options Considered

**None — not applicable.** Starter-template evaluation is a greenfield concern.
This work is an additive *delta* on the mature codebase's existing `Services/Trip/`
+ `Components/Shared/Trip/` slice; the PRD and UX explicitly require it to reuse
`TripViewModel`, `TripStopList`, `ItineraryTimeline`, the `RouteSegment` cache, and
the provider seam unchanged in shape. Adopting any starter would discard a working
app and its conventions, contradicting the feature's premise. No web research into
starters was performed because the stack is fully committed and appropriate.

### Selected Foundation: Existing LucidCartographer codebase (no new starter)

**Rationale:** The established stack already satisfies every requirement this delta
imposes — server-driven interactive UI, the Trip ViewModel + background-compute
precedent, a directional mode-keyed leg cache, an MCP slice with trip tools, EF
Core migrations via startup `MigrateAsync`, a dual desktop/`Mobile*Screen` render
system, and the `UiStrings` localization seam. The architectural task is
integration within these patterns, not foundation selection.

**Initialization Command:** N/A — no project scaffolding. The first implementation
story is the **EF Core migration adding the nullable per-leg
`PoiCollectionItem.OutgoingTravelMode` column** (constrained by `TravelMode.All`,
TRIP-SCHEMA-01), applied through the existing startup `MigrateAsync` path.

**Architectural Decisions Already Fixed by the Existing Codebase:**

- **Language & Runtime:** C# 14 (`LangVersion 14.0`) on `net8.0`; .NET 10 SDK
  required to build (CS9202 on mismatch). `Nullable=enable`, `ImplicitUsings=enable`,
  `TreatWarningsAsErrors=true`, `EnforceCodeStyleInBuild=true`. Meziantou +
  VisualStudio.Threading analyzers; new code introduces no group-B violation; no
  `ConfigureAwait(false)`.
- **Styling Solution:** Tailwind v3.4.17 (standalone CLI auto-downloaded into
  `obj/`, no Node) with the project's `surface-*` / `on-surface-*` / `primary` token
  palette; dual desktop/`Mobile*Screen` render paths.
- **Build Tooling:** MSBuild + `dotnet`; Docker multi-stage build (keep SDK +
  Tailwind versions in sync with `Directory.Build.props` and the app `.csproj`).
- **Testing Framework:** xUnit + FluentAssertions + Moq (unit); bUnit (component);
  `IntegrationTestBase` (real WebApplication + Playwright + per-test temp SQLite) +
  `MobileTestBase` (integration). `InternalsVisibleTo("LucidCartographer.Tests")`.
- **Code Organization:** Components (`.razor` + `*ViewModel.cs`) → ViewModels
  (Transient, `StateChanged`) → Services (interface-first vertical slices under
  `Services/<Slice>/`) → Data (EF Core via `IDbContextFactory`). DI in
  `Configuration/*Extensions.cs` (Trip DI in `TripServicesExtensions.cs`, with the
  parameterless + `IConfiguration` overload pair); MCP tools in `Services/Mcp/`.
- **Development Experience:** `dotnet run --project LucidCartographer` or
  `docker-compose up`; `dotnet test` (Trip integration filter after VM/DI/schema
  changes); admin password seeded to first-run log.

**Note:** Because there is no scaffolding step, the first implementation story is
the `AddOutgoingTravelMode` EF Core migration (Feature F), not a project-init
command.

## Core Architectural Decisions

> **Brownfield delta.** These decisions (RD1–RD13) EXTEND the shipped Trip
> architecture (archived `architecture.md`, D1–D11), which stays authoritative for
> everything not restated here: the provider seam, the directional `RouteSegment`
> cache (TRIP-CACHE-01), `SqliteWriteLock` single-writer, the background compute
> service, `TripOrderingService` as sole `OrderIndex` writer, canonical units, and
> string-persisted enums. **No new technology or dependency is introduced** — all
> choices land on the committed stack, so no version verification applies.

### Decision Priority Analysis

**Critical Decisions (Block Implementation):**
- RD1 Per-leg travel-mode storage (nullable `PoiCollectionItem.OutgoingTravelMode`;
  retire `PoiCollection.TravelMode` as the leg driver) — one additive EF migration
- RD2 Leg-projection reshape: per-leg mode → per-leg cache lookup; newly-appeared
  legs reset to Any/Air, unchanged legs retain mode+time; compute only ground legs
- RD3 TSP-Sort cost basis is mode-invariant (decouples ordering from per-leg modes)
- RD4 Reconciled display model: round-once-at-edge so total == Σ displayed legs and
  arrivals derive from the same rounded source
- RD5 Minute unit "m" → "min" in `UiStrings` (disambiguate from distance meters)
- RD6 MCP contract migration: `get_trip` per-leg mode + new `set_leg_travel_mode`
- RD7 Per-leg manual time override + reset, generalized to any leg (Manual fidelity)
- RD8 Desktop layout takeover (TripStopList replaces the PoiTable region)

**Important Decisions (Shape Architecture):**
- RD9 Inter-row leg connector component (new shared UI element)
- RD10 Multi-day schedule input affordances (no schema change) + date-aware arrivals
- RD11 Fidelity legibility (self-explaining badges + Mock-default note + OSRM hint)
- RD12 Discoverable icon-button tooltips (native `title` at `aria-label` parity)
- RD13 Finish designation & roundtrip readout (verify-and-fix; logic largely exists)

**Deferred Decisions (Out of scope / later):**
- Mobile new-feature **control** surfacing → the follow-up mirror-to-mobile phase
  (shared data/strings/times are NOT deferred — they reach mobile by nature).
- "Apply one mode to all legs" bulk action (possible later nice-to-have, FR-23).
- Standing up OSRM / changing the default `Mock` provider (PRD Non-Goal §6).
- Drag-resizable map/list splitter; overnight ("stop for the night") modeling;
  default-value auto-fill (PRD Non-Goals).

### Data Architecture

**RD1 — Per-leg travel mode (Feature F, the only schema change):**
- Add nullable `PoiCollectionItem.OutgoingTravelMode` (string, one of
  `TravelMode.All` = {AnyAir, Drive, Walk, Cycle}) — the mode of the leg LEAVING
  this stop toward the next stop in Stop Order. Null is **semantically identical to
  AnyAir** ("undefined / Any-Air" is one state per FR-20): no auto time, manual-only.
- One additive EF migration `AddOutgoingTravelMode` via startup `MigrateAsync`,
  constrained by the `TravelMode.All` check pattern (TRIP-SCHEMA-01) exactly like
  the existing `PoiCollection.TravelMode`/`RouteSegment.TravelMode` constraints.
- **Retire `PoiCollection.TravelMode` as the leg driver (FR-23).** It no longer
  feeds leg computation, the VM projection, the MCP, or TSP. **Decision RD1a — drop
  vs keep the column:** *recommended* — DROP it in the same migration (EF Core 8
  SQLite table-rebuild handles `DropColumn`) for cleanliness, since nothing reads it
  after this feature. Fallback if the rebuild proves risky: leave it as a dead
  column and stop referencing it. (Confirm at migration-story time.)
- **No other schema change.** `TripStartTime` (`DateTime?`), `TimeBudgetMinutes`
  (`int?`), `DwellMinutes` (`int?`) already exist — Feature G is UI-edge affordances
  only. `RouteSegment` cache shape and its directional `(From,To,Mode)` key are
  unchanged; per-leg modes simply select different existing cache rows.

**RD7 — Per-leg manual override + reset (generalizes today's Any/Air-only entry):**
- A user-typed leg time writes a `RouteSegment` row at `Fidelity = Manual`, never
  auto-overwritten (TRIP-MANUAL-01) and never deleted by invalidation. Editing is
  allowed on ANY leg (ground or Any/Air), not just Any/Air.
- **Reset (↺)** clears the Manual override and returns the leg to its auto value:
  Estimated/Measured for a ground mode (recompute), or "—"/undefined for Any/Air.
  Implemented as a delete-then-recompute on that leg's cache key, under
  `SqliteWriteLock`. The write path stays inside the Trip slice (see RD2).

### API & Communication Patterns

**RD6 — MCP contract migration (Feature F, FR-24):** the `map_editor` MCP must
move from one trip-wide mode to per-leg modes or AI-assigned trips strand at Any:
- `get_trip` (`TripTools.GetTrip` → `TripDto`): **drop the single trip-wide
  `travelMode`** field; instead each leg DTO in `legDtos` carries its own
  `travelMode` (+ existing seconds/meters/fidelity). The leg DTO already exists, so
  this is an additive field on the leg plus a removal at the trip level.
- **New tool `set_leg_travel_mode`** (alongside `assign_stop_order` /
  `set_dwell_time`): identifies the leg by its **From stop** (the leg leaving that
  stop) and sets one of `TravelMode.All`. Verb-first naming per the existing
  `TripTools` convention. Setting a ground mode triggers compute; setting AnyAir
  leaves it manual-only. This is the sole new external surface; it rides the
  unchanged three-tier `/mcp` auth. *(OQ-C resolved: a dedicated set-leg tool, leg
  keyed by From-stop, mirrors the per-stop `set_dwell_time` shape.)*

### Frontend Architecture

**RD2 — Leg-projection reshape (Feature F core, shared layer):**
- `TripViewModel.BuildLegs` reads **each leg's `OutgoingTravelMode`** (from the
  From-stop membership) instead of one trip mode, and looks each leg up in the cache
  by its own `(From, To, mode)` key. `TripLeg` gains a `Mode` field; `OrderedLegs`
  drives the new connector UI.
- **Newly-appeared legs reset to Any/Air; unchanged legs retain mode + time
  (FR-20/22).** After any reorder/TSP/MCP/add-remove, a leg whose `(From→To)` pair
  is unchanged keeps its `OutgoingTravelMode` and cached time (the directional
  mode-keyed cache already preserves it); a pair that did not exist before defaults
  to AnyAir (null), reads "—". The reorder write path nulls `OutgoingTravelMode`
  only for stops whose successor changed.
- **Compute only ground-mode legs (FR-21).** The background compute pass enqueues a
  leg iff its mode ∈ {Walk, Drive, Cycle}; AnyAir legs are never auto-estimated.
- **DI seam:** this is data + projection + UI; it adds no new VM/service constructor
  dependency, so the `AddTripServices()` / `AddTripServices(IConfiguration)` pair is
  untouched. **If a story nonetheless adds a service dependency, it MUST be
  registered in BOTH overloads** (the parameterless one is the integration-host
  regression point); run the Trip integration filter after any such change.

**RD3 — TSP-Sort cost basis is mode-invariant:** TSP-Sort must order stops BEFORE
per-leg modes exist (newly-appeared legs are Any/Air with no time), so it cannot
optimize on per-leg modes. It builds its cost matrix from a **single mode-invariant
basis — straight-line/haversine distance** (equivalently a fixed nominal ground
mode): under `Mock`, time = distance × a monotone speed scalar, so the optimal order
is identical regardless of mode; under OSRM, optimizing on one ground profile is an
accepted heuristic. After ordering, the resulting legs default to Any/Air per RD2.
This decouples ordering (distance) from per-leg timing/display (mode) and removes
the chicken-and-egg. No change to the NN+2-opt algorithm itself.

**RD4 — Reconciled display model (Features D, round-once-at-edge):**
- Canonical accumulation is unchanged: `ItineraryTimeline.Compute` keeps summing
  **seconds** and applying the existing rule (Start dwell counts once; each stop =
  prior arrival + leg travel + that stop's dwell). The bug is display-only: today
  each leg is truncated `seconds/60` independently while the total formats the
  summed seconds, so they drift (PRD: legs 78 vs total 80).
- **Fix:** the timeline emits a single **display model** that rounds each leg ONCE
  from canonical seconds to whole minutes, then derives BOTH the cumulative arrivals
  AND the trip total from those **same rounded per-leg minutes** (+ integer dwell).
  Result: displayed total == Σ displayed legs, and arrivals reconcile (FR-13/14/15).
  Honesty qualifiers are preserved: an uncomputed/Any leg contributes "—" and yields
  a partial-trip em-dash total (no silent zero); mixed-fidelity aggregate unchanged.
- **Rounding:** nearest-minute (round-half-up) from seconds, with the existing
  `<1 min` bucket for a sub-minute non-zero leg. *[ASSUMPTION-FR-15] round-then-sum
  from displayed per-leg minutes; confirm it keeps partial-trip + fallback intact —
  existing timeline tests will be updated and the Trip integration filter re-run.*
- **Altitude:** the display model lives in the service layer (`ItineraryTimeline` /
  `TravelTimeFormatting`), surfaced via `TripViewModel`; the `.razor` stays a
  markup-only bridge (no arithmetic in the component). Shared layer → both surfaces.

**RD5 — Minute unit:** change `UiStrings.TripDuration*` from `"m"` to `"min"`
("{0}h {1}min", "{0}min", "<1 min"); hours stay "h"; distance meters stay "m".
Canonical seconds unchanged. Shared layer (applies to mobile too).

**RD8 — Desktop layout takeover (Feature A/C):** when `TripVm.IsTripViewEnabled`,
the desktop **filtered-results region renders `TripStopList` instead of `PoiTable`**
(not in addition to it); the additive `w-64` column and the selection batch toolbar
are removed. Toggling off restores `PoiTable` unchanged. The map and list↔map
two-way sync are preserved. This is a markup/Tailwind move in `MapPage.razor`
reusing the existing `TripStopList`/VM — no new ordering or timeline logic. Mirrors
the switch mobile already performs (`MapPage.razor:160-165`). The plain
filtered-results list renders in Stop Order when an order exists (FR-4; single-
collection scope, OQ resolved).

**RD9 — Inter-row leg connector (new shared component):** a compact, single-line
connector on the shared edge between two consecutive stop rows (and a closing
connector after the last row, before the finish/return footer), carrying the leg's
**mode pill** (RD2), travel time ("min"), distance, fidelity badge, and the
**edit/reset** affordance (RD7); an undefined/Any or uncomputed leg reads "—".
Per-leg time/mode is NOT a row column. *[ASSUMPTION-OQ-A] left-indented under the
name column per the carried-in mockup; confirm at mock review.* New component under
`Components/Shared/Trip/`; desktop now, mobile control deferred to the mirror phase.

**RD10 — Multi-day schedule affordances (Feature G, no schema change):**
- **Start** = native `datetime-local` writing the existing `TripStartTime`
  (`DateTime?`); empty ⇒ relative offsets only. Replaces the `type="time"` +
  `DateTime.Today` hard-pairing.
- **Time limit** (renamed from "Time budget"; overrun "Over budget" → "Over limit"
  in `UiStrings`): entered as an **HH:MM duration** OR via a **finish-by deadline**
  (`datetime-local`) computed **once** as `deadline − start` and then stored as the
  fixed-goal `TimeBudgetMinutes`; it does NOT recompute when start/trip change
  (FR-29). HH:MM ↔ minutes only at the UI edge.
- **Dwell** = HH:MM duration picker writing canonical `DwellMinutes` (FR-30).
- **Date-aware arrivals (FR-27):** wall-clock arrivals roll across midnight/days; an
  arrival on a later day shows its date; date/time are locale-driven
  (`CultureInfo.CurrentCulture`), no hard-coded order. Formatting at the UI edge;
  continuous accumulation unchanged (no overnight modeling). Shared layer.

**RD11 — Fidelity legibility (Feature B, no provider change):** fidelity badges
become self-explaining (plain-language `title`/AT tooltip via `UiStrings`); when all
legs are default-`Mock` Estimated, a quiet contextual note explains the state and
**recommends enabling OSRM** (links to `docs/osrm.md`) — distinct from the existing
engine-unreachable fallback note. Recompute copy must not imply a fidelity upgrade
when no measured provider is configured. This PRD does not stand up OSRM (Non-Goal).

**RD12 — Discoverable tooltips (Feature E):** every icon-only trip control gets a
native `title` (move up/down, Set/Unset Start ○, Set/Unset Finish ⚑, TSP-Sort,
Recompute), state-reflecting and at parity with its existing `aria-label`, sourced
from `UiStrings`. Matches the drag-handle precedent.

**RD13 — Finish designation & roundtrip readout (Feature H):** roundtrip default →
footer "Return to start" + return arrival; pressing Finish pins that stop to order N
and flips the footer to "Finish" + its (date-aware) arrival; revertable to
roundtrip, no data loss. Logic largely exists (`IsRoundtrip => FinishPoiId is null`,
Finish pins to N); this is primarily **verify-on-running-app and fix any gap**.

### Infrastructure & Deployment

No change. Default deployment (single Blazor Server container + SQLite volume) is
untouched; the `Mock` haversine provider stays the default; OSRM remains an opt-in
sidecar this PRD only *recommends*, never configures. The one migration applies
through the existing startup `MigrateAsync` path.

### Decision Impact Analysis

**Implementation Sequence (suggested):**
1. RD1 `AddOutgoingTravelMode` migration (the only schema change) — first story.
2. RD5 minute unit + RD4 reconciled display model (shared-layer correctness; cheap,
   high-value, independently testable) — keep mobile green.
3. RD2 leg-projection reshape + RD3 TSP cost basis (per-leg mode end-to-end on the
   VM/cache/compute side).
4. RD6 MCP migration (`get_trip` per-leg mode + `set_leg_travel_mode`).
5. RD8 desktop layout takeover + RD9 connector + RD7 manual/reset (desktop UI).
6. RD10 schedule affordances; RD11 fidelity legibility; RD12 tooltips; RD13 finish
   readout verify-and-fix.
7. Mirror-to-mobile (deferred phase): surface controls in `MobileTripPanel`.

**Cross-Component Dependencies:**
- RD1 (schema) gates RD2/RD6/RD7 — they all read/write per-leg mode.
- RD2 (per-leg cache lookup + ground-only compute) is the spine the connector (RD9),
  MCP (RD6), and manual/reset (RD7) all build on.
- RD3 keeps TSP independent of per-leg modes, so ordering and timing don't deadlock.
- RD4/RD5 are shared-layer and reach mobile by nature → `MobileTripPanel` must stay
  correct even before its new controls land (cross-surface invariant).
- RD8 is pure desktop markup reusing the existing VM — lowest risk, no data coupling.

## Implementation Patterns & Consistency Rules

> Generic conventions (naming, DI lifetimes, layering, error/loading handling,
> no-hardcoded-text, analyzer discipline, canonical units, 1-based `OrderIndex`,
> directional cache key, the `TRIP-*` comment-code convention) are already fixed by
> `project-context.md` and the shipped Trip slice — agents inherit them unchanged.
> This section pins only the **delta-specific** patterns this feature introduces.

### Critical Conflict Points Identified
9 feature-specific areas where independent AI agents could diverge.

### Naming Patterns

- **Per-leg mode lives on the FROM stop.** The new column is
  `PoiCollectionItem.OutgoingTravelMode` (string?, one of `TravelMode.All`). A
  "leg" is always keyed/identified by its **From stop** — in the data
  (`OutgoingTravelMode` on the From membership), in the MCP (`set_leg_travel_mode`
  takes the From `PoiId`), and in the projection (`TripLeg.FromPoiId`). Never invent
  a separate leg entity or a To-keyed mode.
- **MCP naming** stays verb-first per existing `TripTools`: new tool
  `SetLegTravelMode` (snake `set_leg_travel_mode`); `get_trip`'s leg DTO field is
  `travelMode` (camelCase JSON), and the trip-level `travelMode` field is REMOVED
  (not left as a dead duplicate).
- **New `UiStrings` keys** follow the existing `Trip*` prefix: `TripDuration*`
  (unit "min"), `TripFidelity*Tooltip`, `TripMockEstimateNote`, `TripLegModeAny`,
  `TripTimeLimit*` / `TripOverLimit` (renamed from budget), tooltip strings reusing
  each control's `aria-label` text. No literal UI text in `.razor`/`.cs`.
- **New `TRIP-*` design codes** for this feature's invariants, e.g.
  `TRIP-LEGMODE-01` (From-stop owns the outgoing leg; null == AnyAir),
  `TRIP-RECONCILE-01` (round-once display model), `TRIP-SCHEDULE-01` (finish-by
  computed once). Tag the source so the decision is greppable.

### Structure Patterns

- **`OutgoingTravelMode` null ≡ AnyAir is ONE state** (TRIP-LEGMODE-01 / FR-20).
  Do not introduce a separate "unset" sentinel; do not store `"AnyAir"` and `null`
  as different meanings. Reads "—", manual-only, never auto-estimated.
- **Leg data renders only on the connector, never as a stop-row column** (FR-3).
  `TripStopRow` carries stop-scoped data; `TripLeg` (mode/time/distance/fidelity/
  reset) drives the new inter-row `LegConnector` component under
  `Components/Shared/Trip/`. The closing leg renders after the last row.
- **New UI is desktop-first; mobile controls are deferred, shared logic is NOT.**
  Per-leg mode/connector/schedule-picker/tooltip CONTROLS land on desktop now and in
  `MobileTripPanel` in the mirror phase — but shared-layer code (VM projection,
  `ItineraryTimeline`, `TravelTimeFormatting`, `UiStrings`, entities) is authored
  once and MUST keep `MobileTripPanel` correct. Don't fork shared logic per surface.

### Format Patterns

- **Reconciled display model — round once, then derive (TRIP-RECONCILE-01 /
  FR-13–15).** Canonical storage stays seconds/meters/minutes. At the display edge,
  round EACH leg once (nearest minute from seconds; sub-minute non-zero → "<1 min"),
  then compute BOTH cumulative arrivals AND the trip total from those same rounded
  per-leg minutes. Never truncate legs independently while summing seconds for the
  total (today's drift bug). Uncomputed/Any leg → "—" → partial-trip em-dash total.
- **Minute unit is "min", distance is "m"** — never reuse "m" for minutes.
- **Schedule conversions happen only at the UI edge.** `DateTime?`/`int?` minutes
  stay canonical; HH:MM ↔ minutes and date formatting are display-edge only;
  finish-by deadline → `TimeBudgetMinutes` is computed ONCE (`deadline − start`) and
  never recomputed (TRIP-SCHEDULE-01 / FR-29). Dates are locale-driven
  (`CultureInfo.CurrentCulture`), no hard-coded order.

### Communication Patterns

- **One ordering write-path, extended for mode.** Reorder/TSP/MCP still write
  `OrderIndex` through `TripOrderingService.SetOrderAsync` (sole writer, under
  `SqliteWriteLock`). The same path nulls `OutgoingTravelMode` ONLY for stops whose
  successor changed (newly-appeared legs reset; unchanged legs retain mode + cached
  time). Never mutate `OrderIndex` or `OutgoingTravelMode` outside this path.
- **Ground-only auto-compute.** The background compute pass enqueues a leg iff its
  mode ∈ {Walk, Drive, Cycle}; AnyAir legs are never auto-estimated (FR-21). Manual
  overrides write `Fidelity = Manual` and are never auto-overwritten or invalidated
  (TRIP-MANUAL-01); reset = delete the leg's cache row then recompute (ground) or
  leave "—" (AnyAir). Results land via `StateChanged`, never direct mutation.
- **TSP cost basis is mode-invariant** (RD3): build the matrix from straight-line/
  haversine distance (or a fixed nominal mode), independent of per-leg display
  modes. Never feed per-leg `OutgoingTravelMode` into the ordering matrix.

### Process Patterns

- **Schema + DI discipline:** the one `AddOutgoingTravelMode` migration applies via
  startup `MigrateAsync`, constrained by the `TravelMode.All` check pattern; never
  EnsureCreated, never hand-edit an applied migration. If any story adds a Trip
  VM/service dependency, register it in BOTH `AddTripServices()` overloads and run
  the Trip integration filter (`FullyQualifiedName~Integration&FullyQualifiedName~Trip`).
- **Layering:** arithmetic (reconciliation, HH:MM, date rollover) lives in the
  service/VM layer; the `.razor` stays a markup-only bridge.

### Enforcement Guidelines

**All AI agents MUST:**
- Treat `OutgoingTravelMode` null ≡ AnyAir as one state; key every leg by its From
  stop; never create a leg entity or a To-keyed mode.
- Apply the round-once display model; keep "min" for minutes and "m" for meters;
  convert units/dates only at the UI edge; compute finish-by once.
- Route order + mode resets through `TripOrderingService`; auto-compute ground modes
  only; keep Manual fidelity sacrosanct; signal UI via `StateChanged`.
- Keep TSP mode-invariant; keep shared-layer changes mobile-correct; surface new
  controls on desktop now and defer only the mobile control surfacing.
- Tag new invariants with `TRIP-*` codes; route all copy through `UiStrings`.

**Pattern Enforcement:** warnings-as-errors + analyzers catch style; a unit test
asserts the reconciliation invariant (total == Σ displayed legs; arrivals reconcile);
a bUnit test asserts Trip-View-on hides `PoiTable` and shows the wide stop list;
the Trip integration filter covers VM/DI/schema changes; `TRIP-*` codes make
decisions greppable. Pattern changes are amended here deliberately.

### Pattern Examples

**Good:** `set_leg_travel_mode(fromPoiId, "Drive")` writes
`PoiCollectionItem.OutgoingTravelMode` on the From membership → triggers compute for
that one ground leg → `RouteSegment` row at the `(From,To,Drive)` key → `StateChanged`
→ connector shows the Measured/Estimated time. A reorder that leaves the
station→old-town pair intact keeps its Walk mode and cached time; the two newly
adjacent stops get `OutgoingTravelMode = null` and read "—".

**Anti-patterns:** a second "unset" mode distinct from AnyAir; per-leg mode stored on
the To stop or in a new leg table; truncating each leg's seconds while summing
seconds for the total (drift); recomputing the finish-by limit when the trip changes;
feeding per-leg modes into the TSP matrix; auto-estimating an AnyAir leg; forking the
timeline math per surface so mobile drifts from desktop; hard-coding "min"/date order
or a UI string outside `UiStrings`.

## Project Structure & Boundaries

> Brownfield delta only. `[NEW]` = file to create; `[MOD]` = existing file to
> extend. Everything else follows the existing source tree and the shipped Trip
> slice. No files are moved or renamed. Mobile-control files are listed as the
> deferred **mirror phase**; shared-layer `[MOD]`s are NOT deferred.

### Complete Project Directory Structure (additions & modifications)

```
LucidCartographer/
├── Data/
│   ├── AppDbContext.cs                          [MOD] Fluent config + TravelMode.All
│   │                                                  check constraint for the new
│   │                                                  OutgoingTravelMode column; drop
│   │                                                  PoiCollection.TravelMode (RD1a)
│   └── Entities/
│       ├── PoiCollectionItem.cs                 [MOD] + OutgoingTravelMode (string?,
│       │                                              null ≡ AnyAir) — TRIP-LEGMODE-01
│       └── PoiCollection.cs                      [MOD] retire TravelMode as leg driver
│                                                       (drop or deprecate, RD1a)
├── Migrations/
│   └── <ts>_AddOutgoingTravelMode.cs            [NEW] single migration (MigrateAsync):
│                                                       add per-leg mode col + check,
│                                                       drop PoiCollection.TravelMode
│
├── Services/
│   ├── Trip/
│   │   ├── ItineraryTimeline.cs                 [MOD] emit reconciled display model
│   │   │                                              (round-once; arrivals+total from
│   │   │                                              same rounded legs) TRIP-RECONCILE-01
│   │   ├── TravelTimeFormatting.cs              [MOD] "min" unit; date-aware arrival
│   │   │                                              formatting (locale, multi-day);
│   │   │                                              HH:MM ↔ minutes helpers
│   │   ├── TripOrderingService.cs               [MOD] on reorder, null OutgoingTravelMode
│   │   │                                              only for stops whose successor
│   │   │                                              changed (FR-20/22); TSP matrix stays
│   │   │                                              mode-invariant (RD3)
│   │   ├── RouteSegmentInvalidationService.cs   [MOD] per-leg manual reset = delete cache
│   │   │                                              row then recompute (RD7); never
│   │   │                                              downgrade Manual (TRIP-MANUAL-01)
│   │   └── TravelTimeComputationBackgroundService.cs [MOD] enqueue ground-mode legs only;
│   │                                                       skip AnyAir (FR-21)
│   └── Mcp/
│       └── TripTools.cs                          [MOD] get_trip: per-leg travelMode in leg
│                                                       DTO, drop trip-level mode; + new
│                                                       SetLegTravelMode tool (RD6)
│
├── Configuration/
│   └── TripServicesExtensions.cs                [MOD] only if a story adds a Trip service
│                                                       dependency — then BOTH overloads
│                                                       (else untouched)
│
├── Components/
│   ├── Pages/
│   │   └── MapPage.razor                         [MOD] desktop takeover: render TripStopList
│   │                                                   in the wide results region instead of
│   │                                                   PoiTable when Trip View on; remove the
│   │                                                   w-64 column + batch toolbar (RD8)
│   └── Shared/Trip/
│       ├── TripViewModel.cs                      [MOD] per-leg mode projection (TripLeg.Mode);
│       │                                              drop trip-wide TravelMode driver; expose
│       │                                              reconciled display model + schedule edits;
│       │                                              SetLegMode / manual / reset commands
│       ├── TripStopList.razor                    [MOD] wide trip-scoped table (full name +
│       │                                              address + enrichment icon, dwell HH:MM,
│       │                                              date-aware arrival, Start/Finish, focus +
│       │                                              open-in-maps); leg data NOT a row column
│       ├── LegConnector.razor                    [NEW] inter-row leg strip: mode pill, time
│       │                                              ("min"), distance, fidelity, edit/reset;
│       │                                              "—" for Any/uncomputed (RD9/FR-3)
│       ├── LegModePill.razor                     [NEW] per-leg mode control → Walk/Drive/Cycle/
│       │                                              Any-Air menu (replaces trip-wide selector)
│       ├── FidelityBadge.razor                   [MOD] self-explaining tooltip + Mock-default
│       │                                              "enable OSRM" note (RD11)
│       ├── TripScheduleControls.razor            [NEW or MOD] datetime-local start, HH:MM/finish-
│       │                                              by Time limit, "Over limit" (RD10)
│       │                                              [verify whether schedule UI is inline in
│       │                                              TripStopList today vs its own component]
│       └── MobileTripPanel.razor                 [MIRROR-PHASE] surface per-leg mode, connector,
│                                                       date/limit/dwell pickers, tooltips later;
│                                                       MUST stay correct on shared-layer [MOD]s now
│
└── (UiStrings)                                   [MOD] "m"→"min" (TripDuration*); fidelity
                                                        tooltips; Mock-estimate note; Time limit /
                                                        Over limit rename; control title strings

LucidCartographer.Tests/
├── Services/Trip/
│   ├── ItineraryTimelineTests.cs                [MOD] reconciliation invariant: total == Σ
│   │                                                  displayed legs; arrivals reconcile;
│   │                                                  partial-trip "—" + fallback intact
│   ├── TravelTimeFormattingTests.cs             [MOD] "min" unit; multi-day/locale arrivals;
│   │                                                  HH:MM round-trip
│   └── TripOrderingServiceTests.cs              [MOD] reorder resets only changed legs; TSP
│                                                       mode-invariant
├── ViewModels/TripViewModelTests.cs            [MOD] per-leg mode projection, manual/reset
├── Components/Trip*Tests.cs                     [MOD/NEW] bUnit: Trip-View-on hides PoiTable &
│                                                       shows wide list; LegConnector states;
│                                                       mode pill menu; schedule pickers
├── Mcp/TripToolsTests.cs                        [MOD] get_trip per-leg mode; set_leg_travel_mode
└── Integration/ (+ Mobile)                      [MOD] desktop takeover flow; Trip integration
                                                        filter green; mobile trip tests stay green
```

### Architectural Boundaries

**API / external boundaries:** No new HTTP endpoints. The only external surface is
the existing `/mcp` server: `TripTools` gains `SetLegTravelMode` and its `get_trip`
leg DTO gains a per-leg `travelMode` (trip-level mode removed), all behind the
unchanged three-tier auth guard. No provider/OSRM change.

**Component boundaries:** `.razor` files stay markup-only bridges; all per-leg mode,
reconciliation, schedule, and manual/reset logic lives in `TripViewModel` →
`Services/Trip/`. `LegConnector`/`LegModePill` are presentational, driven by
`TripLeg`; they raise VM commands, never mutate state or call services directly.
Desktop and `MobileTripPanel` share the one `TripViewModel`.

**Service boundaries:** `TripOrderingService` remains the sole `OrderIndex` (and now
`OutgoingTravelMode`-reset) writer; the background service is the sole ground-leg
auto-computer; the cache/invalidation service is the sole `RouteSegment`
reader/invalidator (Manual rows sacrosanct). TSP's matrix is mode-invariant.

**Data boundaries:** one additive nullable column (`OutgoingTravelMode`) + the drop
of the now-dead `PoiCollection.TravelMode`; no `RouteSegment` shape change. All
access via `IDbContextFactory<AppDbContext>`; all writes under `SqliteWriteLock`.

### Requirements to Structure Mapping

| Feature / FRs | Lives in |
|---|---|
| **A,C** Desktop takeover + clean rows (FR-1–6,11,12) | `MapPage.razor`, `TripStopList.razor` |
| **B** Fidelity legibility (FR-7–10) | `FidelityBadge.razor`, `UiStrings`, `TripViewModel` |
| **D** Reconciled arithmetic + "min" (FR-13–16) | `ItineraryTimeline.cs`, `TravelTimeFormatting.cs`, `UiStrings`, `TripViewModel` |
| **E** Tooltips (FR-17,18) | `TripStopList.razor`, `LegModePill.razor`, `UiStrings` |
| **F** Per-leg mode (FR-19–25) | `PoiCollectionItem.cs`, migration, `TripViewModel`, `LegConnector`/`LegModePill`, `TripOrderingService.cs`, `TravelTimeComputationBackgroundService.cs`, `RouteSegmentInvalidationService.cs`, `TripTools.cs` |
| **G** Multi-day schedule (FR-26–30) | `TripScheduleControls.razor`, `TravelTimeFormatting.cs`, `TripViewModel`, `UiStrings` |
| **H** Finish/roundtrip readout (FR-31–33) | `TripStopList.razor`, `TripViewModel` (verify-and-fix) |

**Cross-cutting:** per-leg mode spine → `OutgoingTravelMode` + `TripLeg.Mode` +
cache key; honesty model → reconciled display model + Manual fidelity + "—";
cross-surface → shared `[MOD]`s keep `MobileTripPanel` correct; a11y → `title`
tooltips + preserved keyboard reorder/sync; all copy → `UiStrings`.

### Integration Points

**Internal communication:** `StateChanged`/`InvokeAsync(StateHasChanged)` for VM→UI;
`TravelTimeTrigger` for compute wakeups; `SqliteWriteLock` for write serialization —
all unchanged from the shipped slice.

**External integrations:** none added. OSRM remains an opt-in sidecar this feature
only *recommends* (a `docs/osrm.md` link), never configures.

**Data flow:** set leg mode (UI pill or `set_leg_travel_mode`) → `TripViewModel`/
`TripOrderingService` writes `OutgoingTravelMode` under lock → `TravelTimeTrigger` →
background service computes the leg iff ground-mode, writes `RouteSegment` at the
`(From,To,Mode)` key (AnyAir skipped; Manual untouched) → `StateChanged` →
`TripViewModel` rebuilds legs + the reconciled display model → `LegConnector` shows
time/fidelity, timeline shows reconciled arrivals/total.

### Development Workflow Integration

Unchanged: `dotnet run --project LucidCartographer` / `docker-compose up`;
`dotnet test`, with the **Trip integration filter** after the migration and any
VM/DI change. Build/deploy is identical for the default `Mock` deployment — the one
migration applies on startup. No CI, container, or asset-pipeline changes.

## Architecture Validation Results

### Coherence Validation ✅

**Decision Compatibility:** All RD1–RD13 compose cleanly and inherit the shipped
slice's seams without contradiction. The one schema change (RD1) gates the per-leg
mode spine (RD2) which the connector (RD9), MCP (RD6), and manual/reset (RD7) all
build on — a single dependency chain, no cycles. RD3 (mode-invariant TSP) resolves
the only genuine tension surfaced (ordering needs costs before per-leg modes exist)
by decoupling ordering-cost from display-mode. RD4/RD5 are display-edge only and
leave canonical units untouched, so they can't conflict with the data layer. RD8 is
pure markup reuse. No new technology → no version-compatibility surface.

**Pattern Consistency:** Naming, units, and triggers extend the existing
enrichment/Trip precedents (`*BackgroundService`, `TravelTimeTrigger`,
`SqliteWriteLock`, `StateChanged`, string-persisted `TravelMode`/`Fidelity` with
check constraints, 1-based `OrderIndex`, directional `(From,To,Mode)` cache key).
The new invariants are pinned and greppable (`TRIP-LEGMODE-01`, `TRIP-RECONCILE-01`,
`TRIP-SCHEDULE-01`) alongside the inherited `TRIP-CACHE-01`/`TRIP-MANUAL-01`/
`TRIP-SCHEMA-01`. "min" vs "m" disambiguation is consistent across the layer.

**Structure Alignment:** The delta honors Component→VM→Service→Data layering (arithmetic
and conversions in service/VM; `.razor` stays a markup bridge). Boundaries stay clean:
one `OrderIndex`/mode-reset writer, one ground-leg auto-computer, one cache owner
(Manual rows sacrosanct), one MCP surface behind unchanged auth. The cross-surface
rule is structurally enforced — shared `[MOD]`s live in code mobile runs, so mobile
stays correct while only its *controls* defer to the mirror phase.

### Requirements Coverage Validation ✅

**Functional Requirements Coverage (33/33):**
- FR-1–6 (Feature A, desktop takeover) → RD8 (`MapPage.razor` renders `TripStopList`
  in the wide region, hides PoiTable, drops w-64 + batch toolbar; FR-4 single-order
  via `TripOrderingService`; FR-5 map+sync preserved) ✅
- FR-7–10 (Feature B, fidelity legibility) → RD11 (self-explaining badges, Mock note,
  OSRM recommendation link; recompute copy fixed) ✅
- FR-11–12 (Feature C, clean rows) → RD8/RD9 (aligned columns; leg data off the row,
  onto the connector; row states covered) ✅
- FR-13–16 (Feature D, arithmetic + units) → RD4 (round-once display model: total ==
  Σ legs, arrivals reconcile, honesty qualifiers intact) + RD5 ("min") ✅
- FR-17–18 (Feature E, tooltips) → RD12 (state-reflecting `title` at `aria-label`
  parity via `UiStrings`) ✅
- FR-19–25 (Feature F, per-leg mode) → RD1 (schema), RD2 (projection + reset rule +
  ground-only compute), RD7 (manual/reset any leg), RD6 (MCP) ✅
- FR-26–30 (Feature G, multi-day schedule) → RD10 (datetime-local start; HH:MM /
  finish-by Time limit computed once; HH:MM dwell; date-aware multi-day arrivals);
  no schema change confirmed ✅
- FR-31–33 (Feature H, finish readout) → RD13 (verify-and-fix; logic largely exists) ✅

**Non-Functional Coverage:** Layering/units (RD4 altitude, canonical units fixed) ✅;
cross-surface invariant (shared `[MOD]`s keep `MobileTripPanel` correct) ✅; cache
semantics + default provider unchanged ✅; schema discipline (one additive migration,
check constraint, `MigrateAsync`) ✅; a11y parity (tooltips to AT, keyboard reorder +
list↔map sync intact after takeover) ✅; i18n (`UiStrings`, locale-driven dates) ✅;
analyzer/warnings-as-errors discipline ✅; DI-seam regression guard (both
`AddTripServices` overloads + Trip integration filter) ✅; no-regression to map legs,
badges, sync, toggle persistence ✅.

### Implementation Readiness Validation ✅

**Decision Completeness:** All critical decisions (RD1–RD8) documented; the genuine
fork points are flagged for story-time confirmation (RD1a drop-vs-keep column;
[ASSUMPTION-FR-15] round-then-sum preserves honesty; [ASSUMPTION-OQ-A] connector
placement). No new versions to pin. **Structure Completeness:** every new/modified
file enumerated and FR-mapped, with the mirror-phase mobile work explicitly tagged.
**Pattern Completeness:** the 9 delta conflict points pinned with good examples and
anti-patterns.

### Gap Analysis Results

**Critical Gaps:** None — nothing blocks implementation.

**Important Gaps (resolve at story time, not architecture time):**
- **RD1a drop-vs-keep `PoiCollection.TravelMode`:** recommend DROP; confirm the EF
  Core 8 SQLite table-rebuild is clean against the live schema during the migration
  story (fallback: leave as a dead column).
- **[ASSUMPTION-FR-15] round-then-sum semantics:** verify the round-once display
  model keeps partial-trip "—" and engine-unreachable fallback behavior intact;
  existing `ItineraryTimeline`/formatting tests will be updated — exact rounding
  (nearest vs the current truncate) is a one-line decision confirmed against tests.
- **Schedule UI host:** confirm whether today's start/budget controls are inline in
  `TripStopList`/header or a separate component, to decide `TripScheduleControls` as
  `[NEW]` vs `[MOD]` (noted in the tree).
- **`set_leg_travel_mode` leg identity:** From-stop `PoiId` is the chosen key; confirm
  the MCP DTO shape against the Epic-3 AI-assignment story so `get_trip` round-trips.

**Nice-to-Have Gaps:**
- Connector visual placement (left-indent under name) finalized at mock review.
- "Apply one mode to all legs" bulk action — explicitly deferred (FR-23).

### Validation Issues Addressed
The one real architectural tension — TSP-Sort needing costs before per-leg modes
exist — is resolved by RD3 (mode-invariant ordering basis), so ordering and per-leg
timing never deadlock. No other issues required resolution.

### Architecture Completeness Checklist

**Requirements Analysis**
- [x] Project context thoroughly analyzed
- [x] Scale and complexity assessed
- [x] Technical constraints identified
- [x] Cross-cutting concerns mapped

**Architectural Decisions**
- [x] Critical decisions documented with versions (N/A — no new tech; inherited stack)
- [x] Technology stack fully specified (inherited & committed)
- [x] Integration patterns defined
- [x] Performance considerations addressed

**Implementation Patterns**
- [x] Naming conventions established
- [x] Structure patterns defined
- [x] Communication patterns specified
- [x] Process patterns documented

**Project Structure**
- [x] Complete directory structure defined
- [x] Component boundaries established
- [x] Integration points mapped
- [x] Requirements to structure mapping complete

### Architecture Readiness Assessment

**Overall Status:** READY FOR IMPLEMENTATION (all 16 checklist items confirmed; no
Critical Gaps — the open items are story-time confirmations, not architectural
decisions).

**Confidence Level:** High — the feature lands almost entirely on proven, shipped
Trip-slice patterns; the genuinely novel work (per-leg mode spine, reconciled display
model, desktop takeover) is bounded and well-specified, with the one coupling risk
(TSP vs per-leg modes) resolved.

**Key Strengths:**
- One additive nullable column is the entire schema cost; everything else reuses the
  existing cache, provider seam, ordering writer, and background compute unchanged.
- Single mode-spine (data → cache key → VM projection → connector → MCP) and the
  round-once display model give low conflict surface for parallel AI-agent work.
- Honesty/Fidelity model carried end-to-end and made *self-consistent* (total == Σ
  legs) without redefining the accumulation rule.
- Cross-surface correctness is structural: shared `[MOD]`s can't silently break
  mobile, and the integration filter + reconciliation unit test enforce it.

**Areas for Future Enhancement:**
- Mirror-to-mobile phase: surface per-leg mode, connector, schedule pickers, tooltips
  in `MobileTripPanel`.
- "Apply one mode to all legs" bulk action (FR-23 nice-to-have).
- Standing up OSRM as the measured provider (this feature only recommends it).

### Implementation Handoff

**AI Agent Guidelines:**
- Follow RD1–RD13 and the delta patterns exactly; respect the inherited Trip-slice
  boundaries (sole `OrderIndex`/mode writer, sole cache owner, ground-only compute,
  Manual sacrosanct), canonical units, and the directional cache key.
- Treat `OutgoingTravelMode` null ≡ AnyAir as one state; key legs by From-stop; keep
  TSP mode-invariant; apply the round-once display model; convert units/dates only at
  the UI edge; keep shared-layer changes mobile-correct; route all copy through
  `UiStrings`; tag new invariants with `TRIP-*` codes.
- After the migration and any Trip VM/DI change, run the Trip integration filter.

**First Implementation Priority:** the `AddOutgoingTravelMode` EF Core migration
(RD1) — add the nullable per-leg mode column (TravelMode.All check) and retire
`PoiCollection.TravelMode`. It unblocks the per-leg mode spine (RD2/RD6/RD7); the
shared-layer correctness fixes (RD5 "min" + RD4 reconciliation) can land in parallel
as they have no schema dependency.
