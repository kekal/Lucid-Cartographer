# API & Endpoint Contracts

_Minimal-API endpoints (`Endpoints/*.cs`) and the MCP tool surface (`Services/Mcp/*.cs`). Wired in `Program.cs`._

## HTTP Endpoints

### Auth — `Endpoints/AuthEndpoints.cs`
- **POST `/auth/login`** — rate-limited (5/min/IP, policy `login`) and antiforgery-validated (ARCH-CRIT-03; returns **400** on token failure rather than redirect). Verifies `Users` row via `PasswordHasher.Verify`, creates a session via `SessionStore`, signs in with a 30-day sliding cookie, redirects to a local `returnUrl` (open-redirect rejected) or `/`.
- **GET `/logout`** — revokes the server-side session, signs out the cookie, redirects to `/login`.

### POI image — `Endpoints/PoiImageEndpoints.cs`
- **GET `/api/poi-image/{id:int}`** — streams bytes from `PoiImages`. Strong ETag (SHA-256 of bytes); honors `If-None-Match` → **304**. `Cache-Control: no-cache`, `Content-Disposition: inline`, `X-Content-Type-Options: nosniff`.

### OAuth 2.1 frontdoor — `Endpoints/OAuthEndpoints.cs` (only when `OAuth:Issuer` set)
- **GET/POST `/connect/authorize`** — auth-code + PKCE (S256). Challenges to `/login` if no cookie. Echoes RFC 8707 `resource`.
- **POST `/connect/token`** — auth-code / refresh-token exchange.
- **POST `/connect/register`** — RFC 7591 Dynamic Client Registration (custom; OpenIddict has no built-in DCR). Creates public (PKCE) or confidential (secret) clients; `ConsentType=Implicit`.
- Discovery: `.well-known/openid-configuration`, `.well-known/oauth-authorization-server`.

### noVNC reverse proxy — `Endpoints/NoVncProxyEndpoint.cs` (only when `Browser:RemoteView:Enabled`)
- **`/google-session/novnc/**`** — authenticated; proxies HTTP + WebSocket to the backend websockify so the Google sign-in browser can be viewed/driven in-app. This subtree gets `X-Frame-Options: SAMEORIGIN` and a scoped CSP.

### Health
- **GET `/health`** (MED-01) — for Docker/orchestration health checks.

### Blazor
- `MapRazorComponents<App>()` with InteractiveServer render mode serves the UI.

## MCP Server — `/mcp`

`MapMcp("/mcp")` with `WithHttpTransport(Stateless = true)` — each tool call is an independent request with a fresh DI scope. Tools/prompts/resources auto-discovered from the assembly. Server instructions describe the discover → inspect → organize → enrich workflow, duplicate-avoidance, allowed categories, and coordinate ranges.

**Auth (`McpApiKeyFilter`)** — any one of three passes:
1. **LAN bypass** — `Mcp:AllowLocalNetworkBypass` (dev only): RFC1918 + loopback.
2. **Static API key** — `Authorization: Bearer <key>` or `X-Api-Key`, matched against `MCP_API_KEY` env or `Mcp:ApiKey` (timing-safe).
3. **OAuth token** — OpenIddict in-process validation.
On failure: **401** with `WWW-Authenticate: Bearer resource_metadata="…/.well-known/oauth-protected-resource"`.

### Read tools (`PoiReadTools`)
- `list_collections()` → collections + counts
- `list_pois_in_collection(collectionId)` → POI summaries
- `search_pois(query)` → phrase/substring match across name/address/notes/tags (one term per call; ≤100)
- `get_poi(poiId)` → full detail + collection memberships
- `get_poi_image(poiId)` → stored bytes or external URL

### Write tools (`PoiWriteTools`)
- `create_collection(name, color?)`
- `create_poi(collectionId, name, latitude?, longitude?, googleMapsUrl?, address?, category?, notes?, website?, phone?, rating?, imageUrl?)` — **not enriched**; image downloaded pre-commit
- `copy_poi(poiId, …)` · `move_poi(poiId, from, to)` · `delete_poi(poiId)`

### Enrichment tools (`EnrichmentTools`)
- `enrich_poi(poiId)` — idempotent; discards current link and re-runs name search
- `set_poi_google_maps_url(poiId, url)` — manual override, then re-enrich
- `get_enrichment_status()` → `{ remaining, in_progress }`

## Configuration Keys (selected)

`Auth:BypassLocalAddresses`, `Auth:TrustedProxies`, `Auth:TrustedNetworks`, `Auth:InitialAdminPassword`; `Database:Path`; `Mcp:ApiKey`, `Mcp:AllowLocalNetworkBypass`; `OAuth:Issuer`; `Browser:ProfilePath`, `Browser:Headless`, `Browser:RemoteView:*`; `Enrichment:Concurrency|MaxConcurrentPages|BatchSize|IdlePollSeconds|MaxRetries|BackoffBaseSeconds`; `Deduplication:*`. Env vars: `DB_PATH`, `MCP_API_KEY`, `OAuth__Issuer`, `CHROME_PROFILE_PATH`, `SCRAPE_DIAG_LOG`, `ASPNETCORE_ENVIRONMENT`, `DISPLAY`.
