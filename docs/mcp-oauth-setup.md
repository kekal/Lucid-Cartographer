# MCP server & OAuth frontdoor

LucidCartographer exposes a [Model Context Protocol](https://modelcontextprotocol.io)
server at **`/mcp`** (Streamable HTTP) so AI clients like Claude can read and
manage POIs/collections directly. This document covers how the endpoint is
authenticated and how to publish it so **Claude.ai connectors** can sign in over
your own OAuth, behind a plain HTTPS tunnel (no edge auth).

## Authentication modes

`/mcp` accepts a request if **any** of these hold (see `McpApiKeyFilter`):

1. **LAN bypass** — the caller is loopback/RFC1918 *and*
   `Mcp:AllowLocalNetworkBypass` is `true`. This is on in Development (zero-config
   local Claude Code) and **off in Production** (`appsettings.Production.json`),
   because behind a proxy/tunnel the peer is itself an RFC1918 address.
2. **Static API key** — `Authorization: Bearer <key>` or `X-Api-Key: <key>`
   matching `MCP_API_KEY` (env) / `Mcp:ApiKey` (config). Best for Claude Code and
   scripts: `claude mcp add --transport http lucid https://host/mcp --header "Authorization: Bearer <key>"`.
3. **OAuth access token** — issued by this app's own OAuth frontdoor (below) and
   validated in-process. This is how **Claude.ai custom connectors** authenticate,
   because they can't send a static header — they run an OAuth flow.

If none match, the endpoint returns `401` with a `WWW-Authenticate` header
pointing at `/.well-known/oauth-protected-resource`, which lets an OAuth-capable
client discover the authorization server and start signing in.

## The OAuth frontdoor

When `OAuth:Issuer` (env `OAuth__Issuer`) is set to the app's public https base
URL, the app turns on a built-in OAuth 2.1 authorization server (OpenIddict):

- **Discovery** — `/.well-known/oauth-authorization-server` (and
  `/.well-known/openid-configuration`).
- **Dynamic Client Registration** — `/connect/register` (RFC 7591). OpenIddict
  has no built-in DCR, so this is a small custom endpoint advertised in the
  discovery document. It lets *any* Claude client self-register — no client ID to
  paste, which is what makes the connector usable by other people.
- **Authorize** — `/connect/authorize` (authorization-code + mandatory PKCE
  S256). It reuses the app's existing cookie login: Claude opens a browser, the
  user signs in with their app account, and is redirected back.
- **Token** — `/connect/token` (code + refresh-token exchange).

Signing/encryption keys are 2048-bit RSA, generated on first run and persisted as
`oauth-signing.key` / `oauth-encryption.key` next to the SQLite DB (the `/data`
volume), so tokens survive restarts. The OAuth client/token/authorization records
live in the same database (`OpenIddict*` tables, added by the
`AddOAuthFrontdoor` migration).

If `OAuth:Issuer` is **empty**, the frontdoor is disabled and `/mcp` is reachable
via the API key / LAN bypass only.

> **Who can log in.** The OAuth consent step is gated by the app's normal login,
> so anyone using the connector needs an account in the `Users` table. There is
> currently only the bootstrapped `admin` account; multi-user management is a
> separate, future concern.

## Publishing behind a Cloudflare (or any) HTTPS tunnel

The deployment is assumed to be a plain HTTPS tunnel — TLS terminates at the edge
and forwards plain HTTP to the container. Two things must be configured:

1. **`OAuth__Issuer` = your public URL**, e.g. `https://maps.example.com`. This
   becomes the OAuth issuer and the base of every advertised endpoint and
   redirect URI, so it must exactly match the public hostname.

2. **Trust the tunnel for forwarded headers.** The app enforces HTTPS for the
   OAuth endpoints in Production. The tunnel forwards `X-Forwarded-Proto: https`,
   but the app only honors it from a *trusted* proxy. Add the tunnel's source IP
   (as seen by the container — usually the Docker gateway, e.g. `172.18.0.1`) to
   `Auth:TrustedProxies`:

   ```
   Auth__TrustedProxies__0=172.18.0.1
   ```

   Find it with `docker network inspect <network>` or by checking the app logs
   for the remote IP. Without this, the request scheme stays `http` and the OAuth
   endpoints reject it.

`docker-compose.yml` already passes `OAuth__Issuer=${PUBLIC_URL}`. Point your
existing tunnel's ingress at the app's published port (`8080`). No new container
is needed.

### Adding the connector in Claude

1. In Claude → Settings → Connectors → **Add custom connector**.
2. Enter the MCP URL: `https://maps.example.com/mcp`.
3. Claude discovers the protected-resource metadata, finds the authorization
   server, **registers itself** (DCR), and opens a browser to `/connect/authorize`.
4. Sign in with your app account and approve. Claude receives a token and the
   tools appear.

## Quick verification (without Claude)

```bash
# Discovery advertises endpoints incl. our custom registration endpoint:
curl https://maps.example.com/.well-known/oauth-authorization-server

# Protected-resource metadata points back at the authorization server:
curl https://maps.example.com/.well-known/oauth-protected-resource

# /mcp without a credential challenges with resource metadata:
curl -i -X POST https://maps.example.com/mcp \
  -H 'Accept: application/json, text/event-stream' \
  -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
# -> 401 with: WWW-Authenticate: Bearer resource_metadata="https://maps.example.com/.well-known/oauth-protected-resource"
```
