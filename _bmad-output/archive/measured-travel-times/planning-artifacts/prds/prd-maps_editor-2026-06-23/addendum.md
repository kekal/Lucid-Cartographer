# Addendum — Measured Travel-Time & Distance Estimation

Technical depth and as-built context that belongs to downstream documents (architecture / solution
design), preserved here so it is not lost from the capability-level PRD.

## As-built provider seam (current state)

- **`ITravelTimeProvider`** (`Services/Trip/ITravelTimeProvider.cs`): members `string Source`,
  `string? Attribution`, `Task<TravelLegResult> GetLegAsync(TravelEndpoint from, TravelEndpoint to,
  string travelMode, CancellationToken ct)`. `TravelEndpoint(int PoiId, double Latitude, double
  Longitude)`; `TravelLegResult(DurationSeconds, DistanceMeters, Fidelity, GeometryPolyline?)`.
  Units: seconds + meters. Directional (A→B ≠ B→A).
- **`MockTravelTimeProvider`** (default): `Source="Mock"`, `Attribution=null`. Computes
  `GeoUtils.HaversineDistance()` then `distance ÷ TravelTimeOptions.SpeedFor(mode)`. Per-mode speeds
  already exist (Drive 50 / Walk 5 / Cycle 15 km/h, configurable). **No detour factor today** — that
  is the smart-haversine delta. Fidelity = Estimated (ground), Placeholder (Air).
- **`TravelTimeOptions`** bound from config section `"TravelTime"` (speeds live here; detour factors
  to be added here per FR-2).
- **DI selection** (`Configuration/TripServicesExtensions.cs`): parameterless `AddTripServices()`
  registers `MockTravelTimeProvider`; `AddTripServices(IConfiguration)` reads `TravelTime:Provider`
  and, if `=="Osrm"`, swaps in OSRM (binds `OsrmOptions`, registers named HttpClient `"osrm"`),
  else keeps Mock; then `AddHostedService<TravelTimeComputationBackgroundService>()`. **Valhalla
  branch replaces the OSRM branch here.** Integration host uses the parameterless overload → smart
  Mock; run `dotnet test --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"` after.
- **`TravelTimeComputationBackgroundService`**: off-circuit; loads pending ground legs (Walk/Drive/
  Cycle; AnyAir excluded), calls provider via Polly `"travel-time"` pipeline, on exception degrades to
  `EstimatedTravelTime.Compute(...)` with `Source=EstimatedFallback` (`[TRIP-DEGRADE-01]`), upserts
  under `SqliteWriteLock` with a guard that never downgrades Manual/Measured.
- **Source constants** (`Services/Trip/TravelTimeSource.cs`): `Mock`, `Manual`, `Osrm`,
  `EstimatedFallback`. **Fidelity** (`Data/Entities/Fidelity.cs`): `Measured`, `Estimated`,
  `Placeholder`, `Manual`. **Add `Valhalla`; remove `Osrm`.**
- **Badging** (`Components/Shared/Trip/FidelityBadge.razor`): Measured / Estimated / Manual badges +
  tooltips; Placeholder → no badge, leg shows "—". No new badge needed for this feature.
- **Attribution wiring**: `provider.Attribution` → `TripViewModel.RoutingAttributionHtml` →
  `MapPage.razor` → `LeafletMap.SetRoutingAttributionAsync` → `IMapService` → JS → Leaflet control.
  String in `UiStrings.cs` (`TripRoutingAttributionOsm`). **Replace with a Valhalla attribution
  string** (still ODbL/OSM): e.g. `"Routing © Valhalla · Map data © OpenStreetMap contributors (ODbL)"`.

## Valhalla provider — implementation notes (FR-5..FR-10)

- HTTP to a single Valhalla service (default `http://valhalla:8002`); config under
  `TravelTime:Valhalla` (BaseUrl, RequestTimeoutSeconds, geometry precision). Valhalla returns
  `polyline6` by default — confirm precision matches the map's decoder (OSRM used precision-6).
- **Costing mapping:** Drive → `auto`, Walk → `pedestrian`, Cycle → `bicycle`. Single `/route`
  request per leg; response JSON differs from OSRM (`trip.summary.time` seconds, `.length` km →
  convert to meters at the edge; `trip.legs[].shape` encoded polyline). Mapping is the main net-new
  code; scope ≈ the existing OSRM provider.
- New `ValhallaRouteUnavailableException` (analogue of the OSRM one) so the background service's
  degrade path catches it cleanly.

### Recompute-trigger nuance (FR-13a + FR-16)

The background service today computes a leg **iff no cache row exists** for its key. FR-13a (degrade
during the tile-build window) and FR-16 (invalidate OSRM rows) both depend on a leg that *already has*
an `Estimated`/`EstimatedFallback` (or invalidated) row being **re-attempted** once Valhalla is
reachable. So the trigger must broaden from "no row exists" to "no row exists **or** the row is a
non-authoritative Estimated/fallback row eligible for upgrade" — while the upsert guard still never
overwrites `Manual`/`Measured`. FR-16 invalidation is the clean way to force the OSRM→Valhalla
recompute without weakening the never-downgrade-Measured guard. Implementation decides invalidate-by-
delete vs invalidate-by-marking.

## docker-valhalla compose (FR-11)

```yaml
  valhalla:
    image: ghcr.io/nilsnolde/docker-valhalla/valhalla:<pinned-tag>   # OQ-7: pin, not :latest
    ports: ["8002:8002"]
    volumes: ["./appdata/valhalla:/custom_files"]
    environment:
      - tile_urls=https://download.geofabrik.de/europe/<region>-latest.osm.pbf
      - server_threads=2
    profiles: ["valhalla"]
```

App env (analogue of the old commented OSRM block):
```yaml
# - TravelTime__Provider=Valhalla
# - TravelTime__Valhalla__BaseUrl=http://valhalla:8002
```

## OSRM deletion targets (FR-14)

`OsrmTravelTimeProvider.cs`, `OsrmOptions.cs`, `OsrmRouteUnavailableException`,
`TravelTimeSource.Osrm`, the OSRM branch + `"osrm"` HttpClient registration in
`TripServicesExtensions.cs`, the three `osrm-{car,foot,bike}` services + commented env in
`docker-compose.yml`, `OsrmTravelTimeProviderTests.cs` + OSRM references in
`TravelTimeComputationBackgroundServiceTests.cs`, and `docs/osrm.md`.

## Rejected / deferred options (research provenance)

- **A. Turnkey-OSRM** (one-shot prep init service automating extract/partition/customize): treats the
  symptom, not the cause — OSRM's one-profile-per-backend model keeps three containers + three passes
  inherent. Superseded by Valhalla.
- **B. Itinero** (in-process .NET): perfect privacy, no docker, but low maturity / single-maintainer
  risk (stable 1.5.1; main repo last substantive update early 2024; Itinero 2 unreleased for years);
  in-process OOM/crash lands in the app; must verify clean build under strict analyzers. Fallback only.
- **C. External routing SaaS** (Google Routes / Mapbox / ORS / GraphHopper Directions): trivial
  integration but sends coordinates off-box → violates NFR7. Opt-in-with-consent only.

Source research: `_bmad-output/planning-artifacts/research/technical-travel-time-distance-estimation-research-2026-06-23.md`.
