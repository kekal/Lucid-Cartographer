# Enabling OSRM measured travel times

By default LucidCartographer computes travel times with the built-in **Mock**
provider — straight-line (haversine) estimates that need no routing
infrastructure. This is the shipping default and is never changed unless you opt
in.

You can optionally run a self-hosted **OSRM** backend to get **measured** road
durations, distances, and road geometry for Drive / Walk / Cycle legs. OSRM is
opt-in, per deployment.

Because OSRM is self-hosted, stop coordinates never leave your deployment — there
is no third-party out-call and no egress consent is required (NFR7). Enabling
OSRM does bring the OpenStreetMap / ODbL **attribution** obligation; the map UI
renders that attribution (Story 4.2).

## How it works

- One `osrm-routed` backend serves exactly **one profile** — the profile its data
  extract was built with. So each ground mode talks to its own backend:
  **Drive → car**, **Walk → foot**, **Cycle → bike**.
- Any/Air legs are never routed by OSRM (they stay straight-line placeholders).
- A mode whose base URL you leave unset has no OSRM coverage and automatically
  falls back to a straight-line Estimated value — the trip is never blank.

## 1. Prepare the data extracts (one time, per profile)

Download a region extract (`.osm.pbf`) — for example from Geofabrik — sized to
your area. Then run the OSRM preprocessing pipeline once per profile using the
same pinned image as the sidecars. The example below prepares the **car** profile
into `./appdata/osrm/car`:

```bash
mkdir -p appdata/osrm/car
cp region.osm.pbf appdata/osrm/car/region.osm.pbf

IMG=ghcr.io/project-osrm/osrm-backend:v6.0.0
cd appdata/osrm/car

docker run --rm -v "$PWD:/data" $IMG osrm-extract   -p /opt/car.lua    /data/region.osm.pbf
docker run --rm -v "$PWD:/data" $IMG osrm-partition  /data/region.osrm
docker run --rm -v "$PWD:/data" $IMG osrm-customize  /data/region.osrm
```

Repeat for the other profiles you want, swapping the profile script and folder:

- **foot:** `-p /opt/foot.lua` into `appdata/osrm/foot`
- **bike:** `-p /opt/bicycle.lua` into `appdata/osrm/bike`

(`osrm-partition` + `osrm-customize` prepare the MLD algorithm the sidecars run
with.)

## 2. Start the sidecars

The OSRM containers are gated behind the `osrm` compose profile, so a plain
`docker compose up` starts none of them. Bring them up explicitly:

```bash
docker compose --profile osrm up -d
```

This starts `osrm-car` (host port 5000), `osrm-foot` (5001), and `osrm-bike`
(5002), each serving its read-only extract.

## 3. Point the app at OSRM

Set the provider to `Osrm` and give each mode you prepared its backend URL. In
`docker-compose.yml`, uncomment the OSRM block under the `cartographer` service's
`environment:`:

```yaml
- TravelTime__Provider=Osrm
- TravelTime__Osrm__DriveBaseUrl=http://osrm-car:5000
- TravelTime__Osrm__WalkBaseUrl=http://osrm-foot:5000
- TravelTime__Osrm__CycleBaseUrl=http://osrm-bike:5000
```

(Equivalently, set `TravelTime:Provider` and `TravelTime:Osrm:*` in
`appsettings.json` for a non-Docker run.) Only set URLs for the profiles you
actually prepared; the rest fall back to straight-line estimates.

Recreate the app container to apply:

```bash
docker compose --profile osrm up -d
```

## 4. Upgrade existing estimates

Legs already cached as Estimated (including straight-line fallbacks) are upgraded
when you run the existing **Recompute travel times** action on a collection: those
rows are cleared and refilled from OSRM as **Measured**. Manual times and existing
Measured rows are never overwritten.

## Turning it back off

Set `TravelTime__Provider=Mock` (or remove the override) and recreate the app
container. The OSRM sidecars stop when you omit `--profile osrm`.
