# Story 2.3: Self-explaining fidelity badges

Status: done

## Story

As a trip planner, I want each fidelity badge to explain what it means in plain language, so
that I understand "Estimated" / "Measured" / "Manual" without a circular "Provenance: Estimated"
tooltip.

## Acceptance Criteria

1. **Given** a leg's fidelity badge, **When** I hover it (or read it via AT), **Then** it shows a plain-language explanation: "Estimated — straight-line approximation, not road distance" / "Measured — real road route." / "Manual — you entered this time." (FR-7, UX-DR5/DR9).
2. **Given** the tooltip text, **When** it is rendered, **Then** it comes from `UiStrings` and is available to assistive technology at parity with the visible text (NFR6, NFR7); **And** the badge/line visuals are otherwise unchanged.

## Architecture & Code Context (RD11, FR-7)

**File:** `LucidCartographer/Components/Shared/Trip/FidelityBadge.razor`. Today both the `title`
and `aria-label` use `UiStrings.TripFidelityAria` = `"Provenance: {0}"` (circular — "Provenance:
Estimated"). The visible label stays the short word ("Estimated"/"Measured"/"Manual").

**Required:**
1. Add three plain-language tooltip strings to `UiStrings` (new `Trip*`-prefixed keys), exact copy
   per UX-DR9:
   - Estimated → `"Estimated — straight-line approximation, not road distance"`
   - Measured → `"Measured — real road route."`
   - Manual → `"Manual — you entered this time."`
2. In `FidelityBadge`, use the per-fidelity tooltip for BOTH `title` and `aria-label` (parity,
   NFR7), via a `Tooltip(fidelity)` helper mirroring the existing `Label(fidelity)` switch. The
   visible badge text (`Label`) and the pill visuals/tones are UNCHANGED. Placeholder/null still
   render no badge.
3. `TripFidelityAria` may be left in place if still used elsewhere, or removed if now unused — grep
   first. (It is currently used by some TESTS to build selectors; see Testing.)

## Constraints (NFRs)

- NFR6 — all copy via `UiStrings`; no hardcoded literals; Tailwind tokens unchanged.
- NFR7 — `title` and `aria-label` at parity; AT hears the same plain-language text.
- Shared layer — the badge renders on both desktop and mobile; keep mobile correct + tests green.

## Testing

- Update tests that build aria-label selectors from `UiStrings.TripFidelityAria` to the new
  per-fidelity tooltip strings, faithfully (same guarantee):
  - `LegConnectorTests.cs:~85,107` — these assert the Estimated badge is PRESENT; repoint to the
    new Estimated tooltip.
  - `TripStopListTests.cs:~781-784` and `TripTravelTimeRenderTests.cs:~146` — these assert a badge
    is ABSENT (Any/Air never Estimated/Manual; Placeholder no badge); repoint to the new tooltip
    strings so the absence assertion stays meaningful.
- Add/extend a `FidelityBadge` bUnit test asserting the badge's `title` AND `aria-label` both equal
  the plain-language tooltip for each of Measured/Estimated/Manual, and that the visible text is
  still the short word.
- Build clean; fast + Trip integration + mobile green.

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast tests: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`
- Mobile: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Mobile"`

## Dev Agent Record

Added three plain-language tooltip strings to `UiStrings` (`TripFidelity{Estimated,Measured,
Manual}Tooltip`, exact UX-DR9 copy with em-dashes). `FidelityBadge` now computes a
`Tooltip(fidelity)` and binds it to BOTH `title` and `aria-label` (parity, NFR7); visible label
and pill tones unchanged; Placeholder/null still render no badge. `TripFidelityAria` kept
(harmless, no longer referenced by production). Repointed the affected aria-label selectors in
LegConnectorTests / TripStopListTests / TripTravelTimeRenderTests faithfully (present/absent
guarantees preserved; Placeholder no-badge now asserts none of the three real tooltips). New
`FidelityBadgeTests` asserts title==aria-label==tooltip per fidelity + visible short word.

Review: focused orchestrator self-review (small UX-copy change) — verified title/aria parity in
source and the absence-test repoints stay meaningful. 823 fast + 20 Trip integration green
(mobile unaffected — text-only badge change); build clean.

## File List

- LucidCartographer/Services/UiStrings.cs (MOD — 3 tooltip keys)
- LucidCartographer/Components/Shared/Trip/FidelityBadge.razor (MOD — Tooltip helper, title+aria parity)
- LucidCartographer.Tests/Components/Trip/FidelityBadgeTests.cs (NEW)
- LucidCartographer.Tests/Components/Trip/LegConnectorTests.cs (MOD — repoint)
- LucidCartographer.Tests/Components/Trip/TripStopListTests.cs (MOD — repoint)
- LucidCartographer.Tests/Components/Trip/TripTravelTimeRenderTests.cs (MOD — repoint)
