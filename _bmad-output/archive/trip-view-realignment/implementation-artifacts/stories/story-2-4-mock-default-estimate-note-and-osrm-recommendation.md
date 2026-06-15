# Story 2.4: Mock-default estimate note & OSRM recommendation

Status: done

## Story

As a trip planner on a default deployment, I want the panel to tell me why every leg is
"Estimated" and how to get measured times, so that I understand the state and know the optional
path to road-accurate times.

## Acceptance Criteria

1. **Given** the deployment has no measured provider (default `Mock`) and all legs are non-fallback `Estimated`, **When** the trip panel renders, **Then** a quiet contextual note explains the state ("All times are straight-line estimates…") and **recommends enabling OSRM**, linking to `docs/osrm.md` (FR-8, FR-10, UX-DR5/DR9); **And** this note is distinct from the existing engine-unreachable fallback note (which keeps meaning "we tried to measure and couldn't").
2. **Given** no measured provider is configured, **When** I read the "Recompute travel times" control and its copy, **Then** the copy does not imply that recomputing will upgrade fidelity (FR-9).
3. **Given** this PRD does not stand up OSRM (Non-Goal), **When** the recommendation is shown, **Then** it only guides the operator (link/explanation); it does not configure or enable OSRM; **And** all copy is sourced from `UiStrings` (NFR6).

## Architecture & Code Context (RD11, FR-8/9/10)

**Provider signal:** `TripViewModel` holds the injected `ITravelTimeProvider? travelTimeProvider`
(optional; null in some tests). The Mock provider has `Source == "Mock"` and `Attribution == null`;
OSRM has `Source == TravelTimeSource.Osrm ("OSRM")` and a non-null Attribution. So "no measured
provider configured (default Mock)" ⇔ `travelTimeProvider?.Source != TravelTimeSource.Osrm`
(equivalently the provider is null or Mock). `TravelTimeSource.Osrm` is the constant to compare to.

**Existing distinct state:** `TripViewModel.IsShowingApproximateEstimates` = any leg `IsFallback`
(`RouteSegment.Source == EstimatedFallback`) — the **engine-unreachable** note ("we tried to
measure and couldn't"), already rendered in `TripStopList.razor` (~lines 118-125,
`TripApproximateEstimatesNote`). The NEW note is a DIFFERENT state and must not be conflated.

**Required:**
1. **Add a VM signal** e.g. `bool RecommendsOsrm` (or `IsUsingEstimatedProvider`) on
   `TripViewModel`: true when there is no measured provider (`travelTimeProvider?.Source !=
   TravelTimeSource.Osrm`) AND the trip currently shows non-fallback Estimated legs — i.e. there
   is at least one leg whose fidelity is `Estimated` and which is NOT a fallback
   (`!IsShowingApproximateEstimates`-style, but check per-leg: a non-fallback Estimated leg
   exists). The quiet note appears only when this is the actual state (a default Mock deployment
   producing straight-line estimates) — NOT when the engine-unreachable fallback note already
   covers it (keep them mutually distinct; if both could be true, the fallback note wins or they
   are clearly separate — prefer: show the OSRM-recommendation note for the normal Mock-Estimated
   state, and the fallback note for `IsShowingApproximateEstimates`).
2. **Add `UiStrings`** (`Trip*`-prefixed) for the note copy (UX-DR9 voice): e.g.
   `TripMockEstimateNote = "All times are straight-line estimates. Enable OSRM for measured road
   times."` plus a link label, and the `docs/osrm.md` href. The link points to `docs/osrm.md`
   (relative), opens appropriately (`target="_blank" rel="noopener"` if a new tab). All copy via
   `UiStrings`.
3. **Render the note in `TripStopList.razor`** as a quiet contextual note (token-styled, muted —
   mirror the existing approximate-estimates note's quiet styling; an `info` glyph + `role=status`
   aria-live is fine), shown when `Vm.RecommendsOsrm`. Keep it visually/semantically DISTINCT from
   the fallback note. Desktop is in scope; the VM signal is shared so mobile can mirror later — do
   NOT break `MobileTripPanel` (the signal is additive).
4. **Recompute copy (FR-9):** verify `TripRecomputeLabel`/`TripRecomputeAria` ("Recompute travel
   times") and any adjacent copy do NOT imply a fidelity upgrade when no measured provider is
   configured. The current label is neutral; if any nearby copy implies "upgrade to measured",
   fix it. Do NOT add an implication. (No behavior change to the recompute action.)
5. **Do NOT** stand up/configure OSRM — link + explanation only (PRD Non-Goal).

## Constraints (NFRs)

- NFR6 — all copy via `UiStrings`; Tailwind `surface-*`/`on-surface-*` tokens; no hardcoded text.
- NFR5 — VM signal is shared/additive; mobile stays correct, not broken.
- NFR1 — the provider-state logic is a VM property; the `.razor` only renders it.

## Testing

- VM unit test: `RecommendsOsrm` true for a Mock/null provider with non-fallback Estimated legs;
  false when the provider is OSRM; false when the only "estimates" are the engine-unreachable
  fallback (so the two notes stay distinct); false when there are no legs / legs are Any-Air or
  computed Measured.
- bUnit: the note renders (with the `docs/osrm.md` link) when `RecommendsOsrm`; absent otherwise;
  distinct from the fallback note; recompute copy carries no upgrade implication.
- Build clean; fast + Trip integration + mobile green.

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`
- Mobile: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Mobile"`

## Dev Agent Record

Added `TripViewModel.RecommendsOsrm` => `travelTimeProvider?.Source != TravelTimeSource.Osrm &&
OrderedLegs.Any(l => l.Fidelity == Estimated && !l.IsFallback)` — true only for a default
Mock/null-provider deployment actually showing normal (non-fallback) Estimated legs. New
`UiStrings`: `TripMockEstimateNote`, `TripMockEstimateOsrmLink`, `TripMockEstimateOsrmHref =
"docs/osrm.md"`. `TripStopList` renders a quiet note + docs/osrm.md link
(`target=_blank rel=noopener`) gated `RecommendsOsrm && !IsShowingApproximateEstimates` — mutually
exclusive with the engine-unreachable fallback note (fallback wins on a mixed trip). Recompute
copy verified neutral (FR-9, no upgrade implication). No OSRM standup/config. VM ctor unchanged;
MobileTripPanel untouched (signal additive/shared).

Adversarial review: 0 CRIT / 0 HIGH / 0 MED / 1 LOW → SHIP. LOW (the note was a `role=status`
aria-live region — would re-announce on re-render for a persistent hint) **fixed**: the OSRM note
now renders in normal document flow (no live region); the transient fallback note keeps its live
region. Test updated to assert the note is present but NOT a live region. 834 fast + 20 Trip
integration + 53 mobile green; build clean.

## File List

- LucidCartographer/Components/Shared/Trip/TripViewModel.cs (MOD — RecommendsOsrm)
- LucidCartographer/Components/Shared/Trip/TripStopList.razor (MOD — OSRM note, no live region)
- LucidCartographer/Services/UiStrings.cs (MOD — 3 strings)
- LucidCartographer.Tests/ViewModels/TripViewModelRecommendsOsrmTests.cs (NEW — 7 tests)
- LucidCartographer.Tests/Components/Trip/TripMockEstimateNoteRenderTests.cs (NEW — 4 tests)
