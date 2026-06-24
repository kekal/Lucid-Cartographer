# Deployment Guide

_Deployment is via Docker. There are no CI/CD pipelines — builds and deploys are manual._

## Docker Image (multi-stage)

1. **Build stage** — .NET **10** SDK (needed for C# 14; app targets `net8.0`). Downloads the Tailwind standalone CLI (v3.4.17), compiles CSS, publishes Release to `/app/publish`.
2. **Runtime stage** — `mcr.microsoft.com/playwright/dotnet:v1.49.0-noble` (Chromium + OS deps). Adds the remote-view stack (Xvfb + x11vnc + noVNC + websockify) for in-app Google sign-in. Runs as a **non-root** user via `PUID`/`PGID` build args (default 1026/100, override to match a NAS owner of the data volume — MED-02). Entrypoint (`docker-entrypoint.sh`) boots Xvfb/x11vnc/websockify, then the app. Health check curls `/health` (wget fallback, ARCH-LOW-08).

```bash
docker-compose up
```

## Ports & Volumes

- **Port:** container `8080` → host `5087` (docker-compose).
- **Volume:** `./appdata` ↔ `/data` — holds the SQLite DB, OAuth signing/encryption keys, Data Protection keys, the persistent Chrome profile, and diagnostic logs. Set ownership on the host.

## Environment (production / Docker)

| Var | Value | Purpose |
|-----|-------|---------|
| `ASPNETCORE_ENVIRONMENT` | `Production` | enables HTTPS redirect/HSTS, disables dev bypasses |
| `ASPNETCORE_URLS` | `http://+:8080` | listen address |
| `DB_PATH` | `/data/cartographer.db` | SQLite file on the mounted volume |
| `CHROME_PROFILE_PATH` | `/data/chrome-profile` | persists Google sign-in across restarts |
| `SCRAPE_DIAG_LOG` | `/data/scrape-diag.log` | scraper diagnostics |
| `DISPLAY` | `:99` | Xvfb virtual display |
| `Browser__RemoteView__Enabled` | `true` | noVNC embed for sign-in |
| `MCP_API_KEY` | _(set)_ | required for `/mcp` when LAN bypass is off |
| `OAuth__Issuer` | public https URL | enables the OAuth 2.1 frontdoor for Claude.ai connectors |

## Optional: Valhalla Measured routing

Trip Planning ships on the in-process smart-haversine **Mock** provider
(`TravelTime:Provider` unset/`Mock`) — zero routing infrastructure. To enable
**Measured** road routing, run the single self-hosted **Valhalla** engine, gated behind
the `valhalla` compose profile (a plain `docker compose up` starts none of it):

```bash
docker compose --profile valhalla up -d
```

One engine serves all ground modes via dynamic costing (Drive→auto, Walk→pedestrian,
Cycle→bicycle) — no per-mode backends and no manual extract/partition/customize. To
enable it: start the profile, pick your region by setting `VALHALLA_TILE_URLS` in `.env`
(the single region knob), and set `TravelTime__Provider=Valhalla` on the cartographer
service (optionally `TravelTime__Valhalla__BaseUrl=http://valhalla:8002`, which is also
the default). On first start (and whenever the `.pbf` changes) the container auto-downloads
the region extract and builds its routing tiles into `./appdata/valhalla`; during that
build window routing degrades to smart-haversine **Estimated** and self-heals to
**Measured** once tiles finish (FR-13a). The engine is version-pinned
(`ghcr.io/gis-ops/docker-valhalla/valhalla:3.5.1`, never `:latest`) and listens on
`8002:8002`.

Valhalla is self-hosted, so stop coordinates never egress — the only outbound is the
build-time `.pbf` fetch (NFR7); enabling it brings the OSM/ODbL attribution obligation
(NFR8). Full operator guide: **[valhalla.md](./valhalla.md)**.

## Authentication Hardening (read before exposing)

- **LAN bypass:** `Auth:BypassLocalAddresses` accepts unauthenticated RFC1918/loopback requests. Behind a reverse proxy you **must** also list the proxy IP in `Auth:TrustedProxies` (and/or CIDRs in `Auth:TrustedNetworks`) so `ForwardedHeaders` substitutes the real client IP — otherwise every proxied request looks "local" and bypasses auth.
- **ForwardedHeaders runs first** in the pipeline (rewrites `X-Forwarded-Proto`); HTTPS redirect + HSTS apply in non-dev (ARCH-HIGH-05).
- **CSP** is strict (ARCH-CRIT-04); `script-src 'unsafe-inline'` is required by Blazor's SignalR bootstrap, `'unsafe-eval'` removed. `X-Frame-Options: DENY` except the noVNC subtree (SAMEORIGIN).
- **Login** is rate-limited (5/min/IP) and antiforgery-validated (ARCH-CRIT-03).
- **MCP** three-tier auth: LAN bypass (off in prod) → static `MCP_API_KEY` → OAuth token.

## Publishing the MCP server for Claude.ai

Set `OAuth__Issuer` to the public URL, trust the tunnel/proxy in `Auth:TrustedProxies`, and the OAuth frontdoor (`/connect/authorize|token|register`, discovery docs, RFC 7591 DCR) becomes available. See `README.md` references to `docs/mcp-oauth-setup.md`.

## Health

`GET /health` (MED-01) — used by the Docker health check (30s interval, 5s timeout, 10s start period, 3 retries).
