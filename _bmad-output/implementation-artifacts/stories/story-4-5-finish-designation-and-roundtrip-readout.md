# Story 4.5: Finish designation & roundtrip readout

Status: ready-for-dev

## Story

As a trip planner, I want to designate a final stop and have the readout say "Finish" with its
arrival, so that an open-path trip doesn't misreport as "Return to start."

## Acceptance Criteria

1. **Given** a trip with no Finish designated, **When** the footer renders, **Then** it reads "Return to start" + the return-to-Start arrival (roundtrip default, FR-31).
2. **Given** I press Finish on a stop, **When** the designation is applied, **Then** that stop becomes the Finish, is pinned to the end of the list (order N), and the footer switches to "Finish" + that stop's arrival time/date (date-aware per Story 4.2) — never "Return to start" while a Finish is set (FR-32, UX-DR7).
3. **Given** a Finish is designated, **When** I unset it, **Then** the trip reverts to roundtrip and the footer to "Return to start," with no data loss (FR-33).
4. **Given** the switch logic largely exists today (`IsRoundtrip => FinishPoiId is null`, Finish pins to N), **When** this story is implemented, **Then** the behaviour is verified on the running app, any reported misbehaviour is fixed, and finish/return readout is covered by tests; all copy comes from `UiStrings` (NFR6, NFR8).

## Architecture & Code Context (RD13, FR-31/32/33, UX-DR7)

This is largely **verify-and-fix** — the spine exists:
- `TripViewModel`: `IsRoundtrip => FinishPoiId is null`; `SetFinishAsync`/`ClearFinishAsync` →
  `TripOrderingService` pins Finish to order N / reverts to roundtrip (Story 3.3 made the closing-leg
  mode reset shape-flip-aware).
- `TripStopList.razor` footer: `Vm.Timeline.FinishOrReturn` renders `TripTimelineFinishLabel`
  ("Return to start") on a roundtrip vs `TripTimelineFinishOpenLabel` ("Finish") on an open path,
  with the arrival via `ArrivalText(finish)`.
- The per-row Finish (⚑) control + its `title`/`aria` (Story 2.5) sets/clears via the VM.

**Required:**
1. **Verify** (via tests, and via the app where practical) the three states behave per AC1-AC3:
   roundtrip (no Finish) → "Return to start" + return arrival; press Finish → stop pinned to N, footer
   "Finish" + that stop's arrival, never "Return to start" while Finish set; unset Finish → reverts to
   roundtrip + "Return to start", no data loss (order/dwell/modes intact). Fix any gap found.
2. **Date-aware finish/return arrival (ties to Story 4.2):** the footer arrival must be date-aware —
   if the finish/return arrival is on a later calendar day than the start, it shows its date
   (UX-DR7 + FR-27). Ensure the footer uses the same date-aware `ArrivalText` the rows use (it
   already calls `ArrivalText(finish)`, so once 4.2 makes that date-aware this is covered — verify).
3. **Copy** all via `UiStrings` ("Return to start" / "Finish" / the finish-by deadline of 4.3 is a
   DIFFERENT thing — keep distinct). No new logic unless a gap is found; this is mainly coverage +
   any fix.
4. **Tests (NFR8):** cover finish/return readout across the three states (roundtrip, open-path with
   Finish, revert), the pin-to-N behavior, no-data-loss on revert, and the date-aware footer arrival
   on a multi-day trip. Trip integration filter green; mobile green.

## Constraints (NFRs)

- NFR6 — copy via `UiStrings`; "Return to start"/"Finish" distinct from the 4.3 finish-by deadline.
- NFR8 — finish/return readout test coverage; Trip integration after any VM/ordering touch.
- NFR9 — no regression to Start/Finish designation, pin behavior, selection sync.

## Testing

- VM/component: roundtrip footer = "Return to start" + return arrival; SetFinish → Finish pinned to N
  + footer "Finish" + its arrival (never "Return to start" while set); ClearFinish → reverts, footer
  "Return to start", order/dwell/modes preserved (no data loss). Date-aware footer arrival on a
  multi-day trip (later-day finish shows its date). Reuse existing Start/Finish + timeline test
  patterns.
- Trip integration filter green; mobile green.

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`
- Mobile: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Mobile"`

## Dev Notes

Closes Epic 4 and the feature. Largely verify-and-fix on existing logic; the main net-new is
date-aware footer arrival coverage (4.2) + comprehensive readout tests. If a real misbehaviour is
found, fix it in the VM/ordering (not the component) and add a regression test.

## Dev Agent Record

(to be filled)

## File List

(to be filled)
