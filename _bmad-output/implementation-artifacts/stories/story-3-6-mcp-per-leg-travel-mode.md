# Story 3.6: MCP per-leg travel mode (get_trip + set_leg_travel_mode)

Status: done

Adversarial review: 0 CRIT / 0 HIGH / 0 MED / 1 LOW → SHIP. Per-leg `(From,To,Mode)` cache
selection proven by a multi-mode seeding test (Walk 9999s vs Drive 600s → Drive leg returns 600);
FR-24 trip-level `travelMode` removal complete (zero dead references; camelCase via the MCP SDK
serializer); `set_leg_travel_mode` delegates to the validated sole-writer, signals compute for
ground only, throws on invalid; the three projection sites (MCP get_trip / VM BuildLegs /
background DirectionalPairs) are exact mirrors. 884 fast + 20 Trip integration green.

## Story

As an AI assistant using the `map_editor` MCP, I want to read and set each leg's travel mode, so that
AI-assigned trips can choose modes instead of being stranded at Any.

## Acceptance Criteria

1. **Given** `get_trip` (`TripTools.GetTrip` → `TripDto`), **When** it returns a trip, **Then** each leg DTO carries its own `travelMode` (camelCase JSON) alongside the existing seconds/meters/fidelity, and the single trip-level `travelMode` field is removed (FR-24).
2. **Given** a new tool `set_leg_travel_mode`, **When** it is called with a From-stop `PoiId` and one of `TravelMode.All`, **Then** it sets that leg's `OutgoingTravelMode` (leg keyed by its From stop, mirroring `set_dwell_time`); a ground mode triggers compute, AnyAir leaves it manual-only (FR-24); **And** the tool name is verb-first per the existing `TripTools` convention and rides the unchanged three-tier `/mcp` auth.
3. **Given** the MCP contract change, **When** `TripToolsTests` run, **Then** they assert `get_trip` per-leg mode and `set_leg_travel_mode` behaviour, and the Epic-3 AI-assignment flow still round-trips (FR-24, NFR8).

## Architecture & Code Context (RD6, FR-24)

**File:** `LucidCartographer/Services/Mcp/TripTools.cs`. DTOs are at the bottom of the trip MCP area
(`TripDto(CollectionId, TravelMode, Stops, Legs)`, `TripLegDto(FromPoiId, ToPoiId, DurationSeconds,
DistanceMeters, Fidelity)`). MCP tools are auto-discovered (`WithToolsFromAssembly`) and resolve
services via method DI parameters; the sole-writer `ITripOrderingService.SetOutgoingTravelModeAsync`
(added in Story 3.4) and the `TravelTimeTrigger` singleton are both DI-resolvable.

**Required:**
1. **`TripLegDto` gains `TravelMode`** (string, one of `TravelMode.All`; JSON camelCase `travelMode`).
   **Remove the trip-level `TravelMode` from `TripDto`** (FR-24 — no dead duplicate). Update the
   `TripDto` constructor/record and all its usages.
2. **`get_trip` per-leg mode + per-leg-mode cache read.** Mirror the VM (Story 3.2):
   - Read each placeable stop's `OutgoingTravelMode` (null→AnyAir) — extend the existing
     `PoiCollectionItems` read (which already pulls dwell) to also select `OutgoingTravelMode`.
   - For each consecutive pair (and the roundtrip closing leg), the leg's mode = the From-stop's
     `OutgoingTravelMode` (null→AnyAir). Read the cached `RouteSegment` rows for the poi set across
     the per-leg modes (drop the single trip-wide `WHERE r.TravelMode == travelMode` filter; read
     by the poi set and select each leg by `(From,To,legMode)`), so each `TripLegDto` reports its
     own mode + the seconds/meters/fidelity for THAT mode's row (null when none). The roundtrip vs
     open-path shape decision is unchanged (distinct Finish ⇒ open path).
3. **New tool `set_leg_travel_mode`** (verb-first, snake `set_leg_travel_mode`):
   - Signature mirrors `set_dwell_time`: `(ITripOrderingService ordering, IDbContextFactory<AppDbContext>
     dbFactory, TravelTimeTrigger travelTimeTrigger, int collectionId, int fromPoiId, string travelMode,
     CancellationToken ct = default)`.
   - Calls `ordering.SetOutgoingTravelModeAsync(collectionId, fromPoiId, travelMode, ct)` (the
     sole-writer; it validates `travelMode` is null or one of `TravelMode.All` and throws on invalid
     — let that surface to the client). For a GROUND mode (Walk/Drive/Cycle) call
     `travelTimeTrigger.Signal()` so the leg computes (FR-21); AnyAir leaves it manual-only (no
     signal). Return `await GetTrip(...)`.
   - `[Description(...)]` explains: leg identified by its From-stop PoiId; valid modes
     AnyAir/Drive/Walk/Cycle; ground modes get an automatic Estimated/Measured time, Any/Air is
     manual-only. Rides the unchanged three-tier `/mcp` auth (no Program.cs change).
4. **No business logic in `TripTools`** — it delegates to `ITripOrderingService` / signals the
   trigger (matching the existing tools' shape). No new ctor dependency anywhere (the trigger is
   already a registered singleton).

## Constraints (NFRs)

- Sole-writer — `OutgoingTravelMode` set only via `ITripOrderingService.SetOutgoingTravelModeAsync`.
- NFR3 — `RouteSegment` cache shape/directional key unchanged; per-leg cache read selects by mode.
- NFR8 — `TripToolsTests` updated; run them + the Trip integration filter.
- FR-24 — trip-level `travelMode` removed from the DTO; per-leg `travelMode` added.

## Testing

- `TripToolsTests` (`LucidCartographer.Tests/`): `get_trip` returns each leg's `travelMode` and the
  seconds/meters/fidelity for that leg's mode row; the trip-level `travelMode` field is gone (the
  `TripDto` no longer has it). `set_leg_travel_mode(from, "Drive")` writes the From-stop's
  `OutgoingTravelMode` (verify via a fresh read / the returned DTO leg) and the returned trip
  reflects it; `set_leg_travel_mode(from, "AnyAir")` sets Any/Air; an invalid mode throws. The
  Epic-3 AI-assignment round-trip (assign_stop_order then get_trip) still works with per-leg modes.
- Run the Trip integration filter; mobile green (mobile unaffected).

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`

## Dev Notes

Closes the per-leg mode spine (data → projection → compute → UI → MCP). Reuses the 3.4 sole-writer
`SetOutgoingTravelModeAsync`. Last Epic 3 story → retrospective after.

## Dev Agent Record

- `TripLegDto` gained a `TravelMode` field (string, one of `TravelMode.All`); the
  trip-level `TravelMode` was removed from `TripDto` (FR-24) — the record is now
  `TripDto(CollectionId, Stops, Legs)`.
- `get_trip` now reads each placeable stop's `OutgoingTravelMode` (null→AnyAir) by
  extending the membership read, drops the single `WHERE r.TravelMode == travelMode`
  cache filter, reads RouteSegment rows for the poi set across all modes keyed
  `(From, To, Mode)`, and selects each leg by `(From, To, legMode)` where
  `legMode` is the From-stop's mode. Each leg DTO reports its own mode + the
  seconds/meters/fidelity for that mode's row (null when none). Roundtrip/open-path
  shape decision unchanged.
- New tool `set_leg_travel_mode` delegates to `ITripOrderingService.SetOutgoingTravelModeAsync`
  (sole-writer, validates/throws), signals `TravelTimeTrigger` for a ground mode
  (Walk/Drive/Cycle), no signal for AnyAir, then returns `GetTrip`. No new ctor
  dependency (the trigger is a method-DI parameter).
- Build clean (0 warnings, TreatWarningsAsErrors). Fast suite 884/884 pass; Trip
  integration 20/20 pass.

## File List

- `LucidCartographer/Services/Mcp/McpDtos.cs` (TripDto/TripLegDto records)
- `LucidCartographer/Services/Mcp/TripTools.cs` (GetTrip per-leg cache read + set_leg_travel_mode)
- `LucidCartographer.Tests/Services/McpTripToolsTests.cs` (updated + added tests)
