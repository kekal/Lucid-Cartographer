# Story 2.1: Reconciled travel-time arithmetic (round-once display model)

Status: done

Adversarial review: 0 CRIT / 0 HIGH / 0 MED / 2 LOW (both observational) → SHIP. The
round-once invariant provably holds (incl. the ≥60-min hours split); both timeline fold
sites + both total paths converted; honesty intact; new tests exercise drift-prone values
(90+90→4m) with no weakened assertions. 818 fast + 20 Trip integration + 53 mobile green.

## Story

As a trip planner,
I want the per-leg times, arrivals, and trip total to agree with each other,
So that I can trust the schedule instead of seeing legs sum to one number while the total
shows another.

## Acceptance Criteria

1. **Given** a trip with computed legs, **When** the timeline produces its display model, **Then** each leg is rounded **once** from canonical seconds to whole minutes (nearest minute, round-half-up; sub-minute non-zero → "<1 min"), and BOTH the cumulative arrivals AND the trip total are derived from those same rounded per-leg minutes (+ integer dwell) — TRIP-RECONCILE-01.
2. **Given** the display model is rendered, **When** I read the figures, **Then** the displayed trip total equals the sum of the displayed per-leg times (FR-13); **And** the displayed arrivals follow the existing `ItineraryTimeline` accumulation rule (Start dwell counts once; each subsequent stop = prior arrival + leg travel + that stop's dwell) and reconcile with the displayed per-leg/total figures (FR-14).
3. **Given** a leg is uncomputed or Any/Air, **When** the display model is produced, **Then** that leg contributes "—" and the total shows the partial-trip em-dash (no silent zero), and the mixed-fidelity aggregate behaviour is preserved (FR-15).
4. **Given** the canonical accumulation is unchanged, **When** arithmetic runs, **Then** it lives in the service/VM layer (`ItineraryTimeline` / `TravelTimeFormatting` / `TripViewModel`), never in the `.razor` component (NFR1); **And** a unit test asserts the reconciliation invariant (total == Σ displayed legs; arrivals reconcile; partial-trip "—" and engine-unreachable fallback intact), and the Trip integration filter stays green (NFR8).

## Architecture & Code Context (RD4, TRIP-RECONCILE-01)

**The bug:** today each leg displays via `TravelTimeFormatting.Duration(legSeconds)` which **truncates**
`seconds/60`, while the trip total displays `Duration(TotalTravelTimeSeconds)` where
`TotalTravelTimeSeconds = Σ legSeconds` (raw). So `Duration(Σ seconds) ≠ Σ Duration(legSeconds)` —
the legs and the total drift (PRD: legs read 78, total reads 80). Arrivals (`ItineraryTimeline`)
also accumulate raw seconds, so a sequence of legs whose seconds each have a sub-minute remainder
drifts from the sum of the displayed per-leg minutes.

**The fix — round each leg ONCE, derive everything from the rounded legs (round-once-at-edge):**

1. **Single rounding helper.** Introduce ONE per-leg rounding function used everywhere a leg's
   minutes are shown OR summed — e.g. `TravelTimeFormatting.DisplayMinutes(int seconds)` =
   `(int)Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero)` (nearest minute, round-half-up).
   This is the sole rounding edge.
2. **`TravelTimeFormatting.Duration(int? seconds)`** must present `DisplayMinutes(seconds)` (NOT
   truncation): hours = `DisplayMinutes/60`, minutes = `DisplayMinutes%60`; when
   `DisplayMinutes == 0 && seconds > 0` → "<1 min" (the existing `TripDurationSubMinute`); when
   `seconds == 0` → the zero string; null/negative → the em-dash. **Keep the unit text as-is** —
   FR-16 ("m"→"min") is Story 2.2; do NOT change the unit string here, only the rounding.
3. **`ItineraryTimeline.Compute` accumulates from the SAME rounded per-leg minutes.** Instead of
   adding raw `leg.DurationSeconds`, add `DisplayMinutes(leg.DurationSeconds) * 60` for each
   contributing (known, ground/known-fidelity) leg; dwell stays minutes×60 as today. Result:
   `OffsetSeconds`/`TotalSeconds` are whole-minute multiples derived from the rounded legs, so
   wall-clock arrivals carry no stray seconds and the cumulative arrivals reconcile with the
   per-leg display. **Preserve ALL honesty logic unchanged**: unknown propagation (a null-duration
   or Placeholder/Any leg ⇒ `IsUnknown` from there on), the fidelity-rank/qualifier model, the
   partial-trip em-dash total, `IsOverBudget` (now comparing the reconciled total to the budget),
   and the unplaceable-dwell-into-total rule. The accumulation RULE (Start dwell once; stop =
   prior + leg + dwell) is unchanged — only the per-leg value is rounded-once first.
4. **The displayed trip "total travel time" must equal Σ of the rounded per-leg minutes** — NOT
   `Duration(Σ rawSeconds)`. In `TripViewModel.RecomputeTotal`, compute the total as
   `Σ DisplayMinutes(leg.DurationSeconds)` (×60 to keep the seconds-typed field, or expose a
   reconciled minutes value) when all legs are known; null when any leg is uncomputed/Any (the
   partial em-dash, unchanged). Because the per-leg connector shows `Duration(legSeconds)` =
   `DisplayMinutes(leg)` and the total now sums those same rounded minutes, FR-13 holds:
   `displayed total == Σ displayed per-leg`.
   - NOTE the corner: a leg with `DisplayMinutes == 0 && seconds > 0` displays "<1 min" but
     contributes 0 to the sum — that is consistent (it adds 0; the "<1 min" is a 0-contribution
     annotation). Document it; do not special-case it into the sum.
5. **Altitude (NFR1):** all of this is in `TravelTimeFormatting` / `ItineraryTimeline` /
   `TripViewModel`. The `.razor` components keep calling `Duration(...)`, `Arrival(...)`, and the
   VM total property — no arithmetic moves into markup.

**Shared layer (NFR5):** `ItineraryTimeline`, `TravelTimeFormatting`, and `TripViewModel` are run
by BOTH desktop and `MobileTripPanel`. This change reaches mobile by nature — keep mobile data/
strings/times correct and mobile trip tests green; do NOT fork the math per surface.

## Constraints (NFRs)

- NFR1 — arithmetic in service/VM only; `.razor` unchanged (still calls the formatters/VM).
- NFR2 — canonical units fixed at edges: `RouteSegment` seconds/meters and dwell minutes are
  UNCHANGED; only the DISPLAY model rounds. Do not mutate stored seconds.
- NFR5 — shared layer; mobile stays correct, no per-surface fork.
- NFR6 — no string/format changes beyond rounding (unit text stays "m" until Story 2.2); all via
  `UiStrings`.
- NFR8 — reconciliation unit test + Trip integration filter green.

## Testing

- **Reconciliation invariant unit test (TRIP-RECONCILE-01):** for representative leg-second sets
  (incl. sub-minute remainders that previously drifted, e.g. legs of 90s/90s), assert
  `displayed total == Σ displayed per-leg minutes (+ dwell)` and that each displayed arrival ==
  running sum of displayed per-leg minutes + dwell. Include a partial trip (one uncomputed/Any
  leg) → leg "—" and total em-dash; a mixed-fidelity trip → qualifier preserved; the
  engine-unreachable fallback (EstimatedFallback) note path intact.
- Update existing `ItineraryTimelineTests` / `TravelTimeFormattingTests` for the truncate→round
  change faithfully (re-express expected values from the new round-half-up rule; do not weaken).
- Run the Trip integration filter; keep mobile trip tests green.

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast tests: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`

## Dev Notes

This is shared-layer correctness — it also fixes mobile. Story 2.2 ("min" unit) and the rest of
Epic 2 build on the reconciled model. Tag the round-once decision `TRIP-RECONCILE-01` in source.

## Dev Agent Record

TRIP-RECONCILE-01 implemented round-once-at-edge:
- `TravelTimeFormatting.DisplayMinutes(int)` is the sole rounding edge (round-half-up).
- `TravelTimeFormatting.Duration` now presents DisplayMinutes (not truncation); unit text unchanged.
- `ItineraryTimeline.Compute` accumulates `DisplayMinutes(travel)*60` per contributing leg (both the per-stop loop and the roundtrip closing leg); dwell unchanged; all honesty logic (unknown propagation, fidelity rank/qualifier, partial em-dash, unplaceable dwell, budget compare-to-reconciled-total) preserved.
- `TripViewModel.RecomputeTotal` stores `Σ DisplayMinutes(leg)*60`; null when any leg uncomputed/Any. Corner case (DisplayMinutes==0 && seconds>0 → "<1m", contributes 0) documented, not special-cased.
- No arithmetic in .razor (NFR1); shared layer reaches mobile unchanged (NFR5).

Build: clean (0 warnings, TreatWarningsAsErrors). Fast tests: 818 passed. Trip integration: 20 passed.

## File List

- LucidCartographer/Services/Trip/TravelTimeFormatting.cs (modified)
- LucidCartographer/Services/Trip/ItineraryTimeline.cs (modified)
- LucidCartographer/Components/Shared/Trip/TripViewModel.cs (modified)
- LucidCartographer.Tests/Services/TravelTimeFormattingTests.cs (added)
- LucidCartographer.Tests/Services/ItineraryTimelineTests.cs (modified — reconciliation tests)
- LucidCartographer.Tests/ViewModels/TripViewModelTravelTimeTests.cs (modified — reconciliation tests)
