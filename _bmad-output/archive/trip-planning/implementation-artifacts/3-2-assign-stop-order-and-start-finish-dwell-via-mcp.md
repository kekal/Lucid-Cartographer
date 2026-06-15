---
baseline_commit: dea236c4c2c10381e964c9032f468b6a002d3be2
---

# Story 3.2: Assign Stop Order (and Start/Finish/dwell) via MCP

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a user with a connected AI agent,
I want the agent to read my trip and assign an order honoring soft constraints,
so that I can say "museums in the morning, rooftop bar last" and have it applied.

## Acceptance Criteria

_(FR-16, AR-8; epics.md#Story-3.2; + Epic-2 retro action item A5)_

1. **TripTools exist on the existing MCP server.** Given the existing authenticated `/mcp` server, when the trip tools are added in `Services/Mcp/TripTools.cs`, then they expose, at minimum: (a) **read** the ordered stops + computed legs for a collection; (b) **assign Stop Order numbers** to the collection's POIs; (c) **set Start / Finish**; and (d) **set Dwell Time**.
2. **No new auth surface.** The trip tools ride the existing three-tier `/mcp` guard (LAN → API key → OAuth) with **no new unauthenticated surface** added. They are discovered by the existing `WithToolsFromAssembly()` (a `[McpServerToolType]` class) and served by the same `app.MapMcp("/mcp")` endpoint already exempt from the login redirect and gated by `McpApiKeyFilter` — nothing in Program.cs's auth wiring changes.
3. **Writes go through the single ordering write path.** Given an order assigned via MCP, when it is written, then it goes through the **same** `ITripOrderingService` (1-based `OrderIndex`, `SqliteWriteLock`) as a manual drag — no second `OrderIndex` writer, no direct `PoiCollectionItem.OrderIndex` mutation in `TripTools`. Start/Finish writes reuse the existing `SetStartAsync`/`SetFinishAsync`/`ClearStartAsync`/`ClearFinishAsync`; dwell writes go through a service method (not duplicated in the tool).
4. **Persists identically + reflected in the UI.** An MCP-assigned order persists identically to a manual drag and is reflected in the map, times, and timeline (on the next projection refresh / page load — MCP does not push to an already-open circuit, the same as the existing `create_poi`/`edit_poi` tools). The 1-based contiguous-unique invariant (AR-11) holds.
5. **Remains drag-editable.** An MCP-assigned order remains editable by a subsequent manual drag — no system reshuffle undoes the user's later edit (it is just another `OrderIndex` write, carrying no lock or immutability).
6. **A5 — MCP coordinate edits invalidate cached legs.** When `edit_poi` (`PoiWriteTools.EditPoi`) actually changes a POI's latitude/longitude, it routes the change through `IRouteSegmentInvalidationService.InvalidateForPoiAsync(poiId)` so agent-driven coordinate edits invalidate that POI's cached legs (parity with the in-app coordinate-change hook, TRIP-INVALIDATE-01). The deferred `TODO` + `#pragma warning disable MA0026` in `EditPoi` is removed.

## Tasks / Subtasks

- [x] **Task 1 — Order-assignment + dwell service methods on `ITripOrderingService`/`TripOrderingService`** (AC: #3, #4)
  - [x] Add `Task AssignOrderAsync(int collectionId, IReadOnlyList<int> orderedPoiIds, CancellationToken ct = default)`: validate that `orderedPoiIds` is **exactly** the set of the collection's placeable, ordered Stops (no unknown id, no unplaceable id, no missing Stop, no duplicate) — throw `ArgumentException` with a clear message otherwise (the MCP runtime surfaces it as a tool error). Then map ids → tracked `ItemRow`s in the supplied order, run through the existing `ArrangeWithPins(orderedRows, startPoiId, finishPoiId)` → `Renumber` → `SetOrderAsync` (the SAME single writer drag/keyboard/TSP use, AR-11). Pins win: a designated Start/Finish keeps Order 1 / N regardless of the supplied position.
  - [x] Add `Task SetDwellMinutesAsync(int collectionId, int poiId, int? minutes, CancellationToken ct = default)`: the validated dwell persist (bounds `[0, MaxDwellMinutes]`, `null` clears), writing `PoiCollectionItem.DwellMinutes` under `SqliteWriteLock`. **Refactor** `TripViewModel.PersistDwellMinutesAsync` to delegate to this service method (the VM keeps its guard + `RefreshProjectionsAsync` + `Notify` + the out-of-range pre-check; the DB write moves to the service so MCP and the UI share one implementation). Keep the `MaxDwellMinutes` bound in one place (move the constant to the service, or have both reference one source) — no divergent bounds.
  - [x] Full XML docs on both new interface members (mirror the existing method docs; reference AR-8, AR-11, TRIP-DWELL-01).
- [x] **Task 2 — `Services/Mcp/TripTools.cs` (NEW)** (AC: #1, #2, #3)
  - [x] `[McpServerToolType] public static class TripTools` — auto-discovered by `WithToolsFromAssembly()`; **no Program.cs change**. Mirror `PoiReadTools`/`PoiWriteTools`: static methods, `[McpServerTool(Name = "...")]` + `[Description(...)]`, services resolved from the per-request DI scope as method parameters. **Delegate to services — no business logic in the tool** (the established `PoiWriteTools` contract).
  - [x] `get_trip` (read): given a collection id, return the ordered stops (PoiId, name, OrderIndex, IsStart, IsFinish, DwellMinutes) + the computed legs (FromPoiId, ToPoiId, DurationSeconds, DistanceMeters, Fidelity) for the collection's persisted `TravelMode`. Read order via `ITripOrderingService.GetPlaceableStopsAsync` + the collection's Start/Finish + dwell + cached `RouteSegment` rows. Return a new DTO in `Services/Mcp/McpDtos.cs` (e.g. `TripDto { TravelMode, IReadOnlyList<TripStopDto> Stops, IReadOnlyList<TripLegDto> Legs }`).
  - [x] `assign_stop_order` (write): given a collection id + an ordered list of PoiIds, call `ITripOrderingService.AssignOrderAsync`. Description must explain it sets the full interior order and that a designated Start/Finish stays pinned. Return the refreshed `TripDto` (or a short confirmation).
  - [x] `set_trip_start` / `set_trip_finish` / `clear_trip_start` / `clear_trip_finish`: delegate to the existing `SetStartAsync`/`SetFinishAsync`/`ClearStartAsync`/`ClearFinishAsync`. Surface the `InvalidOperationException` (Start==Finish) as a tool error with a clear message.
  - [x] `set_dwell_time`: given collection id + poi id + minutes (null/omitted clears), delegate to `ITripOrderingService.SetDwellMinutesAsync`.
  - [x] Update the MCP server instructions (`McpServerExtensions.ServerInstructions`) with a short "Trips" section so an agent discovers the toolset (mirror the existing workflow bullets). Optionally extend the MCP usage resource if one documents tools.
- [x] **Task 3 — A5: MCP coordinate-edit invalidation in `PoiWriteTools.EditPoi`** (AC: #6)
  - [x] Inject `IRouteSegmentInvalidationService` into `EditPoi`. When the edit **actually changes** `Latitude` or `Longitude` (compare old vs new before assigning), call `InvalidateForPoiAsync(poiId, ct)` after the successful save. Remove the deferred `TODO` comment and the `#pragma warning disable/restore MA0026` around it.
  - [x] Mirror the in-app behavior (TRIP-INVALIDATE-01): invalidate both directions / all non-Manual modes for that POI; the background compute refills on the next trigger. A no-op coordinate edit (same value, or coordinate not supplied) invalidates nothing.
- [x] **Task 4 — Tests** (AC: all)
  - [x] **Service unit (`Services/`):** `AssignOrderAsync` — applies a valid full reorder through the single writer (contiguous 1..N); **honors Start/Finish pins** (supplied order overridden at the ends); **rejects** an incomplete / unknown-id / unplaceable / duplicate list (throws `ArgumentException`); remains drag-editable afterwards (a follow-up `ReorderStopAsync` still works). `SetDwellMinutesAsync` — writes/clears `DwellMinutes`, rejects out-of-range, no-op on absent membership.
  - [x] **MCP tool tests:** drive each `TripTools` method directly (static methods with service args, the pattern used by existing MCP tool tests if present — otherwise construct services over an in-memory DB like the service tests). `get_trip` returns the ordered stops + legs; `assign_stop_order` reorders via the service and the result reads back; `set_trip_start`/`set_trip_finish` set pins and a Start==Finish attempt errors; `set_dwell_time` sets + clears dwell.
  - [x] **A5 regression:** an `edit_poi` that changes coordinates invalidates the POI's cached `RouteSegment` rows (assert the rows are deleted); an edit that does **not** touch coordinates leaves them intact; a Manual row is never invalidated.
  - [x] **Integration:** run the **Trip integration filter** after the new service methods + the VM dwell-delegation refactor (DI/VM-ctor-adjacent change — the recurring integration-host regression point). No new VM constructor parameter is expected (the VM already has `factory`/`writeLock`); confirm the host still boots.
  - [x] **No new unauthenticated surface (AC2):** assert (or document via the existing auth-guard test surface) that `/mcp` remains behind `McpApiKeyFilter` and TripTools added nothing outside it. If a test already covers the `/mcp` guard, no new tool path bypasses it (the tools are just new `[McpServerTool]` methods on the same endpoint).

## Dev Notes

### How MCP tooling works here (read first)

- `Configuration/McpServerExtensions.cs` registers the server with `WithToolsFromAssembly()` — **any** `[McpServerToolType]` class in the assembly is auto-discovered. A new `TripTools.cs` therefore needs **no** registration and **no** Program.cs edit (AC2). The endpoint is `app.MapMcp("/mcp")` (Program.cs:95), stateless HTTP transport, so scoped services (`ITripOrderingService`, `IDbContextFactory`, `IRouteSegmentInvalidationService`) resolve **per request** as tool-method parameters.
- Auth: `Configuration/AuthRouteGuardExtensions.cs` exempts `/mcp` from the cookie/login redirect; `McpApiKeyFilter` enforces the three-tier LAN → API key → OAuth guard (with the OAuth frontdoor in `OAuthFrontdoorExtensions.cs`). Adding tool methods on the same endpoint inherits this guard — do not add any new route or endpoint.
- `ITripOrderingService`, `IRouteSegmentInvalidationService`, and `IDistanceMatrixService` are all registered in `AddTripServices()` (parameterless overload), called from `AddTripServices(configuration)` in Program.cs:22 — so they're available to the MCP request scope in production.

### Key source files

- `LucidCartographer/Services/Mcp/PoiWriteTools.cs` — **the pattern to mirror**: static `[McpServerTool]` methods, rich `[Description]`, "all calls delegate to the existing service; no business logic duplicated here". **Contains the A5 TODO** (lines ~162-168, the `#pragma warning disable MA0026` block in `EditPoi`) — this story removes it and wires `InvalidateForPoiAsync`.
- `LucidCartographer/Services/Mcp/PoiReadTools.cs` — read-tool shape (`list_collections`, `list_pois_in_collection`, `get_poi`); mirror for `get_trip`.
- `LucidCartographer/Services/Mcp/McpDtos.cs` — DTO conventions (`CollectionDto`, `PoiSummaryDto`, `PoiDetailDto` with `From(...)` factories); add `TripDto`/`TripStopDto`/`TripLegDto` here.
- `LucidCartographer/Services/Trip/TripOrderingService.cs` + `ITripOrderingService.cs` — the single `OrderIndex` writer. **Reuse** `ArrangeWithPins` / `Renumber` / `SetOrderAsync` for `AssignOrderAsync` exactly as Story 3.1's `SortTravelingSalesmanAsync` does; reuse `ReadAsync`/`ReadPinsAsync`. The Start/Finish setters already exist and already guard Start==Finish.
- `LucidCartographer/Components/Shared/Trip/TripViewModel.cs` — `SetDwellMinutesAsync`/`PersistDwellMinutesAsync` (≈1286-1336) and `MaxDwellMinutes` (≈1270). Refactor the persist to delegate to the new service method; keep the VM's guard/refresh/notify.
- `LucidCartographer/Services/Trip/RouteSegmentInvalidationService.cs` — `InvalidateForPoiAsync(poiId, ct)` (both directions, all non-Manual modes) is exactly what A5 needs.
- `LucidCartographer/Configuration/McpServerExtensions.cs` — `ServerInstructions`; add a short Trips section.
- `LucidCartographer/Data/Entities/PoiCollectionItem.cs` (`OrderIndex`, `DwellMinutes`), `PoiCollection.cs` (`StartPoiId`, `FinishPoiId`, `TravelMode`), `RouteSegment.cs` (directional key, seconds/meters, `Fidelity`).

### Architecture compliance / guardrails

- **AR-8 / AR-11:** all four ordering paths (drag, keyboard, TSP, **MCP**) write the same `OrderIndex` through one `TripOrderingService` method — MCP is path #4 and must not open a second write route. No new unauthenticated surface (D7). Tag new code with `TRIP-*` codes (e.g. `TRIP-MCP-01`).
- **Layering:** MCP tools are an entry point like a Component — they delegate to Services and hold no business logic. Canonical units at the edge: `OrderIndex` 1-based, durations seconds, distances meters; dwell minutes.
- **Build discipline:** `TreatWarningsAsErrors=true`, `Nullable=enable`; no group-B analyzer violations (`MA0002/0015/0046/0047/0074`, `VSTHRD200`); **no `ConfigureAwait(false)`**. Removing the `EditPoi` TODO also removes its `MA0026` suppression — make sure no new `MA0026` (TODO) is introduced.
- **Concurrency (deferred-work + Story 3.1 review):** MCP is the first **off-circuit** caller of the ordering write path — unlike the Blazor circuit, two MCP requests (or an MCP request + a UI drag) are not serialized by a single circuit. The existing methods read-validate in one `AsNoTracking` context and write under `SqliteWriteLock.Gate` (only `SaveChangesAsync` is gated), and `PoiCollection.Version` catches a concurrent collection edit on pin writes. This is the exact scenario the "OrderIndex write-path atomicity" / "pin-order atomicity" defers named for revisit. **Do not silently widen the gap**: keep the existing pattern, and in Completion Notes state explicitly whether MCP introduces a realistic lost-update window (single-user self-hosted app — low, but now genuinely multi-entry) so the reviewer and Epic-3 retro can weigh whether to promote the atomicity defer.

### Testing standards

Three layers (project-context.md). MCP tools are static methods taking service params — test them by calling the method with services constructed over an in-memory DB (`TestDbHelper`), the same way the service tests do. `InternalsVisibleTo` is set. Run the **Trip integration filter** after the VM dwell refactor.

### Project Structure Notes

- New: `Services/Mcp/TripTools.cs`; `TripDto`/`TripStopDto`/`TripLegDto` in `Services/Mcp/McpDtos.cs`; test files (`McpTripToolsTests`, `TripOrderingServiceAssignTests`, A5 coverage in the MCP write-tool tests).
- Updated: `ITripOrderingService.cs`, `TripOrderingService.cs` (AssignOrderAsync + SetDwellMinutesAsync), `TripViewModel.cs` (delegate dwell persist), `PoiWriteTools.cs` (A5), `McpServerExtensions.cs` (instructions).
- **No EF migration** — reads/writes only existing schema.

### References

- [Source: epics.md#Story-3.2] — ACs (FR-16, AR-8).
- [Source: architecture.md#AR-8 (line 78), lines 202-207, 389-390, 425] — TripTools in `Services/Mcp/`, existing three-tier `/mcp` guard, single ordering write path, `AssignStopOrder` → `TripOrderingService`.
- [Source: deferred-work.md] — OrderIndex write-path atomicity & pin/order concurrency (MCP named as a multi-writer revisit point).
- [Source: epic-2-retro-2026-06-14.md / sprint-status.yaml A5] — MCP coord-invalidation TODO promoted to Story 3.2.
- [Source: PoiWriteTools.cs:162-168] — the exact deferred TODO this story resolves.
- [Source: project-context.md] — build/layering/testing/units rules.

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Claude Opus 4.8) — via bmad-story-automator manual cycle (no tmux, Windows).

### Debug Log References

- `dotnet build LucidCartographer/LucidCartographer.csproj` → clean (0 warnings).
- `dotnet test --filter "Assign|McpTripTools|PoiWriteTools"` → 40 passed.
- `dotnet test` (full suite, incl. Trip integration) → **886/886 passed**, 5m14s.

### Completion Notes List

- **MCP TripTools (`Services/Mcp/TripTools.cs`, NEW):** `get_trip`, `assign_stop_order`, `set_trip_start`, `set_trip_finish`, `clear_trip_start`, `clear_trip_finish`, `set_dwell_time`. Auto-discovered by the existing `WithToolsFromAssembly()` — **no Program.cs change**, served by the same `/mcp` endpoint behind `McpApiKeyFilter` (LAN → API key → OAuth). No new unauthenticated surface. Every write delegates to `ITripOrderingService`; no business logic in the tool (mirrors the `PoiWriteTools` contract).
- **Single write path (AC3):** new `ITripOrderingService.AssignOrderAsync` validates the supplied ids are exactly the placeable Stops then funnels through the existing `ArrangeWithPins` → `Renumber` → `SetOrderAsync`. Pins win — a designated Start/Finish keeps Order 1/N regardless of the supplied position. Start/Finish/clear reuse the existing setters; dwell goes through the new `SetDwellMinutesAsync`.
- **Dwell DRY:** the dwell DB-write moved from `TripViewModel.PersistDwellMinutesAsync` to `ITripOrderingService.SetDwellMinutesAsync`; the VM now delegates (keeping its guard/refresh/notify). `MaxDwellMinutes` centralized on `TripOrderingService` (the VM const aliases it) — one bound, no divergence.
- **A5 resolved (AC6):** `PoiWriteTools.EditPoi` now injects `IRouteSegmentInvalidationService` and, when a coordinate value actually changes, calls `InvalidateForPoiAsync` after the save. The deferred `TODO` and its `#pragma warning disable MA0026` are gone. A no-op / identical-value coordinate edit invalidates nothing; a Manual leg is never invalidated.
- **`get_trip` leg shape** mirrors the UI / background service: consecutive directional pairs plus the closing leg back to the first Stop on a Roundtrip (no distinct Finish); a distinct Finish ⇒ open path (no closing leg). Uncached pairs report null duration.
- **Concurrency (deferred-work + 3.1 review):** MCP is now the **first off-circuit caller** of the ordering write path — two MCP requests, or an MCP write racing a UI drag, are not serialized by a single Blazor circuit. I kept the existing pattern (read-validate in one context; only `SaveChangesAsync` under `SqliteWriteLock.Gate`; `PoiCollection.Version` guards concurrent pin writes) and did **not** widen the window. For this single-user self-hosted app the realistic lost-update risk stays low, but the entry surface is now genuinely multi-path — **flagging the "OrderIndex write-path atomicity" / "pin-order atomicity" defers for the Epic-3 retrospective** to decide whether to promote them (e.g. a single read-validate-write transaction under the gate).
- **No EF migration** — reads/writes only existing schema.

### File List

**New (production):**
- `LucidCartographer/Services/Mcp/TripTools.cs`

**New (tests):**
- `LucidCartographer.Tests/Services/TripOrderingServiceAssignTests.cs`
- `LucidCartographer.Tests/Services/McpTripToolsTests.cs`

**Modified (production):**
- `LucidCartographer/Services/Trip/ITripOrderingService.cs` — `AssignOrderAsync` + `SetDwellMinutesAsync`.
- `LucidCartographer/Services/Trip/TripOrderingService.cs` — implementations + `MaxDwellMinutes` constant.
- `LucidCartographer/Components/Shared/Trip/TripViewModel.cs` — dwell persist delegates to the service; `MaxDwellMinutes` aliases the service constant.
- `LucidCartographer/Services/Mcp/McpDtos.cs` — `TripDto`/`TripStopDto`/`TripLegDto`.
- `LucidCartographer/Services/Mcp/PoiWriteTools.cs` — A5: inject `IRouteSegmentInvalidationService`, invalidate on coordinate change, remove the deferred TODO + MA0026 suppression.
- `LucidCartographer/Configuration/McpServerExtensions.cs` — server instructions gain a Trips section.

**Modified (tests):**
- `LucidCartographer.Tests/Services/PoiWriteToolsTests.cs` — pass the invalidation service to `EditPoi`; +3 A5 regression tests.

## Change Log

| Date | Change |
|------|--------|
| 2026-06-14 | Story 3.2 implemented: MCP TripTools (read/assign-order/start/finish/dwell) on the existing /mcp 3-tier auth via WithToolsFromAssembly; new AssignOrderAsync + SetDwellMinutesAsync through the single OrderIndex writer; VM dwell-persist delegates to the service. A5 resolved: EditPoi invalidates cached legs on a coordinate change. Full suite 886/886 green incl. Trip integration. Status → review. |
| 2026-06-14 | Fresh-context adversarial review (0 CRITICAL/0 HIGH/1 MED/2 LOW). MED auto-fixed: `assign_stop_order`'s MCP input param `IReadOnlyList<int>` → `int[]` for robust JSON-schema binding across clients (service signature unchanged). 2 LOW accepted: off-circuit ordering-writer atomicity (flagged for Epic-3 retro), and per-write trip re-read (acceptable MCP ergonomics). Full suite 886/886 green. Status → done. |

## Senior Developer Review (AI)

**Reviewer:** adversarial fresh-context review via bmad-story-automator-review (claude-opus-4-8)
**Date:** 2026-06-14
**Outcome:** Approve (0 CRITICAL, 0 HIGH, 1 MED fixed, 2 LOW accepted)

File List cross-checked against `git status` — exact match. All 6 ACs verified: TripTools auto-discovered on the existing authenticated `/mcp` with no new surface (AC1/AC2); all writes funnel through `ITripOrderingService` (AC3) with pins-win arrangement; persists like a drag and stays drag-editable (AC4/AC5); A5 coordinate-edit invalidation wired and the deferred TODO + MA0026 suppression removed (AC6). The dwell write is now shared (VM delegates to the service) — no divergent bounds.

### Action Items

- [x] [AI-Review][MED] `assign_stop_order` MCP input type `IReadOnlyList<int>` → `int[]` for reliable cross-client JSON-schema binding (service keeps `IReadOnlyList<int>`).
- [x] [AI-Review][LOW] (accepted → retro) MCP is the first off-circuit ordering writer; the read-validate-write atomicity defer is now genuinely multi-path — flagged for the Epic-3 retrospective.
- [x] [AI-Review][LOW] (accepted) Each write tool re-reads the full trip to return it — minor extra DB round-trip, acceptable for agent ergonomics.
