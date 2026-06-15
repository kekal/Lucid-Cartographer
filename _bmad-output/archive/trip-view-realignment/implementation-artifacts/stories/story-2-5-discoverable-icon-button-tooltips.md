# Story 2.5: Discoverable icon-button tooltips

Status: done

## Story

As a sighted trip planner, I want every icon-only control to reveal what it does on hover, so that
I get the same affordance screen-reader users already have from `aria-label`.

## Acceptance Criteria

1. **Given** the trip list's icon-only controls (move up/down, Set/Unset Start ○, Set/Unset Finish ⚑, TSP-Sort, Recompute), **When** I hover any of them, **Then** a native `title` tooltip names the action (FR-17), matching the drag-handle precedent.
2. **Given** a control with state, **When** its tooltip renders, **Then** the text reflects the control's state ("Set as Start" vs "Unset Start"), disabled edge/pinned controls read sensibly, and the text is at parity with the existing `aria-label` (FR-18, UX-DR10); **And** tooltip text is sourced from `UiStrings`, reusing each control's `aria-label` where apt (NFR6, NFR7).

## Architecture & Code Context (RD12, FR-17/18)

`TripStopList.razor` icon-only controls already carry state-reflecting `aria-label`s sourced from
`UiStrings`; the drag handle already has BOTH `aria-label` and `title` (the precedent). This story
adds a `title` at parity with each control's `aria-label`:
- TSP-Sort (`TripSortTspAria`), Recompute (`TripRecomputeAria`)
- Move up (`TripMoveStopUp`), Move down (`TripMoveStopDown`)
- Set/Unset Start (`StartControlAria(role,name)` — already state-reflecting "Set as Start"/"Unset
  Start"), Set/Unset Finish (`FinishControlAria(role,name)`)

The Focus-on-map / Open-in-Google-Maps actions (Story 1.2) already have titles. Markup-only; no new
strings (reuse the aria-label expressions); no logic. Desktop only (mobile controls deferred).

## Constraints (NFRs)

- NFR6 — tooltip text via `UiStrings` (reuse aria-label expressions); no hardcoded literals.
- NFR7 — `title` at parity with `aria-label`; AT + sighted parity.
- NFR1 — markup only.

## Testing

- bUnit: for each control, assert `title` is present and equals its `aria-label`; for Start/Finish,
  assert the title reflects state (unpinned → "Set as …"; pinned → "Unset …").

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`

## Dev Agent Record

Added `title` at parity with the existing `aria-label` on the six icon-only trip controls in
`TripStopList.razor`: TSP-Sort, Recompute, move up, move down, Set/Unset Start, Set/Unset Finish.
The Start/Finish titles reflect state via the existing `StartControlAria`/`FinishControlAria` (Set
vs Unset). Markup-only; reuses existing `UiStrings`; no new strings. The Focus/Open-in-Maps actions
(Story 1.2) keep their short title vs descriptive aria-label (PoiTable precedent) — out of this set.

Review: orchestrator self-review (markup-only parity change). Two bUnit tests added: parity of
title==aria-label for the six controls, and Start title state-reflection (Set vs Unset). Build
clean; 836 fast + 20 Trip integration green.

## File List

- LucidCartographer/Components/Shared/Trip/TripStopList.razor (MOD — title on 6 controls)
- LucidCartographer.Tests/Components/Trip/TripStopListTests.cs (MOD — 2 parity/state tests)
