# Enabling Valhalla measured travel times

By default LucidCartographer computes travel times with the built-in **Mock**
provider — smart-haversine (straight-line, per-mode detour-adjusted) estimates
that need no routing infrastructure. This is the shipping default and is never
changed unless you opt in.

You can optionally run a single self-hosted **Valhalla** engine to get
**measured** road durations, distances, and road geometry for Drive / Walk /
Cycle legs. Valhalla is opt-in, per deployment, and **turnkey** — one container,
one compose profile, one region knob. There is no manual extract / partition /
customize pipeline: the container downloads your region and builds its routing
tiles by itself.

## Migrating from OSRM (breaking change)

The previous hand-rolled **OSRM** measured-routing path has been **retired** and
replaced by Valhalla. If your deployment was set to `TravelTime:Provider=Osrm`:

- **Your app still boots.** A retired or unrecognized `TravelTime:Provider` value
  no longer fails startup — the app **falls back to the smart-haversine default**
  (warn-and-fall-back, never fail-fast).
- **But routing silently downgrades from Measured to Estimated.** Because that is
  easy to miss, a **prominent startup warning** is logged naming the offending
  value and stating that routing is now Estimated, not Measured.
- **To restore measured road times**, switch to Valhalla: set
  `TravelTime__Provider=Valhalla`, start the `valhalla` compose profile, and set
  your region (`tile_urls`) — see the setup steps below.
- Stale cache rows produced by the old OSRM provider (`Source=OSRM`, Measured)
  are **invalidated once on startup** so they recompute under the active provider
  instead of being pinned forever; your `Manual` legs are never touched.

## Privacy guarantee (NFR7)

Because Valhalla is **self-hosted**, **stop coordinates never leave your
deployment.** This is a hard guarantee, not a setting:

- Every per-route request (the `/route` POST that carries your stop
  coordinates) goes **only** to the one configured internal host
  (`http://valhalla:8002` over the Docker compose network by default). It never
  reaches any third party, and no egress consent is required.
- The **only** outbound access the `valhalla` container ever needs is the
  **one-time, build-time** fetch of the region extract (`.osm.pbf`) from the
  `tile_urls` you configure (e.g. Geofabrik). That download carries *map data
  into* the deployment; it never carries *your stop coordinates out*.
- The default Mock provider is even stricter: it computes **in-process** with no
  HTTP client at all, so there is nothing to egress at any time.

This containment is verified by an automated no-egress test (the default
provider issues no outbound HTTP; Valhalla contacts only the configured host;
Air legs make no HTTP call) and is restated here as the operator contract.
A documented operator check to confirm it on a live deployment is in the last
section below.

Enabling Valhalla does bring the OpenStreetMap / **ODbL attribution**
obligation (NFR8); the map UI surfaces that attribution whenever Valhalla is the
active provider.

## How it works

- **One engine, all ground modes.** A single Valhalla container serves every
  ground mode via dynamic costing: **Drive → auto**, **Walk → pedestrian**,
  **Cycle → bicycle**. There are no per-profile sidecars to prepare.
- **Any/Air legs are never routed by Valhalla** — they stay straight-line
  Placeholders ("—" in the trip view), computed locally with no HTTP call.
- **Self-building, self-healing.** On first start (and whenever the source
  `.pbf` changes) the container auto-downloads the region extract from
  `tile_urls` and auto-builds its routing tiles into a mapped volume. **During
  that build window Valhalla is unreachable**, and ground legs degrade to the
  smart-haversine **Estimated** value rather than erroring or hanging the trip
  view. Once tiles finish and the engine answers on `8002`, the background pass
  re-attempts and upgrades those Estimated legs to **Measured** — automatically,
  with no operator action (FR-13a).
- **Manual and Measured rows are never downgraded.** As the ladder climbs from
  Estimated to Measured, any leg you entered by hand (Manual) or that is already
  Measured is left byte-for-byte intact.

## 1. Start the Valhalla profile

The `valhalla` service is gated behind the `valhalla` compose profile, so a
plain `docker compose up` starts none of it (the default deployment stays
Mock / smart-haversine). Bring it up explicitly:

```bash
docker compose --profile valhalla up -d
```

This starts a single `valhalla` service that serves on host port `8002` and
reaches the app at `http://valhalla:8002` over the internal compose network.

## 2. Select your region (the single `tile_urls` knob)

Region selection is **one knob**: point `tile_urls` at the region extract
(`.osm.pbf`) you want — for example a Geofabrik download sized to your area.
Set it via `VALHALLA_TILE_URLS` in your `.env` (the compose service reads
`${VALHALLA_TILE_URLS:-…}`):

```bash
# .env
VALHALLA_TILE_URLS=https://download.geofabrik.de/europe/germany-latest.osm.pbf
```

The container downloads that extract and builds its tiles on first start. To
change region later, set a new URL and clear the built tiles
(`./appdata/valhalla`) so the engine rebuilds from the new extract.

## 3. Point the app at Valhalla

Set the provider to `Valhalla`. In `docker-compose.yml`, uncomment the Valhalla
block under the `cartographer` service's `environment:`:

```yaml
- TravelTime__Provider=Valhalla
- TravelTime__Valhalla__BaseUrl=http://valhalla:8002
```

`BaseUrl` already defaults to `http://valhalla:8002` (the service name + port),
so if you keep the default service you can rely on `appsettings.json` and leave
the `BaseUrl` line commented. (Equivalently, set `TravelTime:Provider` and
`TravelTime:Valhalla:BaseUrl` in `appsettings.json` for a non-Docker run.)

Recreate the app container to apply:

```bash
docker compose --profile valhalla up -d
```

That is the complete enable: **start the profile, set `tile_urls`, set
`TravelTime__Provider=Valhalla`.** No extract/partition/customize, no per-mode
backends.

## One-time tile-build cost

Building the routing tiles is a **one-time, region-sized, RAM-heavy** step paid
on first start (and again only if you change the region `.pbf`). The mapped
volume (`./appdata/valhalla:/custom_files`) persists the built tiles across
container recreates, so you pay the build once, not on every `up`.

> **[ASSUMPTION] / operator-verify — these figures are not measured in this
> environment.** Docker was not available during implementation, so the numbers
> below are the published gis-ops/docker-valhalla guidance and the project's
> NFR-9 footprint targets, **not** measured on your hardware. Measure them on
> your own deployment and region during first boot.

Rough, region-dependent guidance (verify for your region):

| Resource | Guidance ([ASSUMPTION], operator-verify) |
|----------|------------------------------------------|
| **RAM**  | On the order of **~4–8 GB** for the build (NFR-9 target); larger regions need more. |
| **Build time** | **Minutes** for a small region (e.g. a single small country) up to **tens of minutes** for a large one. |
| **Disk**  | On the order of the source `.pbf` **plus** the built tiles — budget a few× the `.pbf` size. |

To measure your own figures during the first `--profile valhalla up`:

- **Time:** watch `docker compose logs -f valhalla` and note the elapsed time
  from "downloading / building tiles" to the engine answering on `:8002`
  (the `/status` healthcheck flips to healthy).
- **RAM:** watch `docker stats valhalla` during the build (peak memory).
- **Disk:** `du -sh appdata/valhalla` once the build completes.

Record the observed values for your region so future operators can plan
capacity. The tile-build cost is an **operator/manual** measurement, not a CI
test.

## Upgrade existing estimates

Legs already cached as Estimated (including smart-haversine defaults and
straight-line fallbacks from the build window) are upgraded to **Measured** by
the background compute pass once Valhalla is reachable — no manual action
needed. You can also run the existing **Recompute travel times** action on a
collection to refill its eligible legs from Valhalla. **Manual times and
existing Measured rows are never overwritten or downgraded** by this upgrade.

## Operator egress check (NFR7 verification)

To confirm on a live deployment that no stop-coordinate egress occurs during
normal routing, use this repeatable check (not a one-off): while you plan a trip
(legs visibly computing), observe that the `valhalla` container's **only**
outbound connection was the one-time `.pbf` fetch, and that per-route traffic
stays on the internal compose network.

- **Inspect the build-time fetch vs. per-route traffic.** Tail the container's
  log: `docker compose logs valhalla`. You should see the `.pbf` download +
  tile build at startup, and **no** outbound connections to any external host
  while routing requests are being served afterwards.
- **Watch live connections during routing.** While the trip view is computing
  legs, observe the container's active network connections — e.g. from inside
  the container `ss -tnp` (or a host-level `ss` / firewall log filtered to the
  container's IP). Per-route `/route` traffic should appear **only** between the
  app (`cartographer`) and `valhalla` on the internal compose network; there
  should be **no** outbound connection to any public host carrying coordinates.
- **Confirm the host port is internal-only if you wish.** `8002` is published so
  you can reach the engine for diagnostics; the app reaches it over the internal
  network regardless. If your policy forbids host exposure, you can remove the
  `ports:` mapping and the app still routes over the compose network.

Repeat this check after any compose or region change. The expectation is
constant: **one** build-time `.pbf` fetch, then internal-only routing — stop
coordinates never leave the deployment.

## Turning it back off

Set `TravelTime__Provider=Mock` (or remove the override) and recreate the app
container. The Valhalla engine stops when you omit `--profile valhalla`. Cached
Measured legs remain until recomputed; new computation reverts to the
smart-haversine Estimated default.
