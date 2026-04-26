# Sonarr-style auth — proposal

## Problem

Today's auth requires the operator to:

1. Generate a PBKDF2 hash on a separate machine.
2. Paste it into `.env` as `AUTH__PASSWORDHASH=pbkdf2$100000$…$…`.
3. Restart the container.

If they skip the hash and use plaintext, the secret sits in `.env` in the
clear. There is no per-user account, no UI to change the password, and no way
to bootstrap a fresh deployment without a separate hash-generation step.

## Goals

- Drop the configured-secret ceremony entirely.
- Auto-create an `admin` user on first run; print the random password to the
  container logs once.
- Let LAN clients bypass auth (Sonarr's "Disabled for Local Addresses").
- WAN clients still get a username + password login form.

## The model

| Caller location | What happens |
|---|---|
| Local network (RFC1918, loopback, link-local) | No auth — synthetic principal injected, app sees them as authenticated |
| Anywhere else | Login form → username + password → cookie session (existing flow) |

Sonarr calls this "Forms auth with **Disabled for Local Addresses**" — same
idea.

## Concrete changes

### 1. New `User` entity + EF migration

```csharp
public class User
{
    public int Id { get; set; }
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }   // PBKDF2, same hasher
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}
```

- Add `DbSet<User>` to `AppDbContext`.
- `dotnet ef migrations add AddUsers`.

### 2. First-run admin in `StartupCleanupService`

After `MigrateAsync`, check `Users.Count()`. If zero:

- Generate a 24-char URL-safe password from `RandomNumberGenerator`.
- Hash it with the existing `PasswordHasher.HashPassword()`.
- Insert `User { Username = "admin", PasswordHash = hash, CreatedAt = UtcNow }`.
- Log it with a banner so it stands out in `docker compose logs`:

```
════════════════════════════════════════════════════════
  INITIAL ADMIN USER CREATED
      Username: admin
      Password: x7Jq2vL9pK4mNw3RtY8sCb6h
  Save this password — it will not be shown again.
════════════════════════════════════════════════════════
```

### 3. Replace config-secret with DB lookup

- Delete `Auth:Password` and `Auth:PasswordHash` config keys.
- Delete `AuthSecretReader.cs`.
- Delete the "changeme" startup check.
- Rewrite `/login` POST in `AuthEndpoints.cs`: read `username` + `password`
  from form, look up user by username, verify with
  `PasswordHasher.Verify(plain, user.PasswordHash)`, update `LastLoginAt`,
  sign in.
- `Login.razor` gets a `username` field added next to `password`.

### 4. LAN-bypass middleware

Replace `AuthRouteGuardExtensions.cs` with a new `UseLanBypassOrAuth`
middleware:

```csharp
if (BypassLocalAddresses && IsLocalNetwork(context.Connection.RemoteIpAddress))
{
    context.User = new ClaimsPrincipal(
        new ClaimsIdentity([new Claim(ClaimTypes.Name, "lan")], "lan-bypass"));
    await next();
    return;
}
// existing redirect-to-login logic
```

`IsLocalNetwork` checks `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`,
`127.0.0.0/8`, `::1`, `fe80::/10`.

### 5. Reverse-proxy trust (mandatory for LAN bypass to work safely)

If the app is behind Cloudflare or a Synology reverse proxy on the NAS, every
request looks "local" because the immediate peer is the proxy. Without proxy
trust, **WAN attackers get LAN bypass for free.**

Add `UseForwardedHeaders` early in the pipeline plus `KnownProxies` from
config:

```json
"Auth": {
  "BypassLocalAddresses": true,
  "TrustedProxies": ["127.0.0.1", "::1"]
}
```

### 6. Change-password page (Phase B)

Once logged in as the auto-generated admin, the user will want to change to
something memorable. Small `/account` page with current-password +
new-password + confirm; updates `User.PasswordHash`.

## What gets simpler

| Before | After |
|---|---|
| Generate hash on a separate machine, paste into `.env` | Just run the container; copy password from logs |
| `AUTH__PASSWORDHASH=pbkdf2$100000$...` env var | Nothing in `.env` for auth |
| App crashes if you forget the secret | App self-bootstraps |
| Single shared password | Per-user accounts (extensible later) |

## Verification plan

1. **Fresh container, empty DB** → log shows admin password banner; `/login`
   accepts username `admin` + that password.
2. **Existing DB with users** → no banner, no auto-create.
3. **Request from `127.0.0.1` or `192.168.x.x`** → goes straight to `/`
   without login.
4. **Request from public IP** → redirected to `/login`.
5. **Behind reverse proxy with `X-Forwarded-For: <public-ip>`**:
   - Proxy in `TrustedProxies` → treated as public IP, redirected to
     `/login`.
   - Proxy **not** in `TrustedProxies` → request is treated as coming from
     the proxy itself (LAN), bypass kicks in. This is the safe default
     — operator must opt in by listing the proxy.

## Scope

About one focused session of work:

- 1 entity + migration
- 1 endpoint rewrite (`/login`)
- 1 middleware (`UseLanBypassOrAuth`)
- 1 service extension (auto-admin in `StartupCleanupService`)
- Small `Login.razor` change (add username field)
- Optional `/account` page (Phase B)

No breaking changes to existing services. No schema changes outside the new
`Users` table.

## Phasing

- **Phase A** (must-have): entity + migration + auto-admin + login rewrite +
  LAN bypass + `ForwardedHeaders`. Ships a working Sonarr-like experience.
  One commit, stop for review.
- **Phase B** (nice-to-have): `/account` change-password page, multi-user
  CRUD.

## Out of scope

- TOTP / 2FA.
- OIDC / SSO integration.
- Account lockout / rate limit on login (existing per-IP rate limiter stays
  in place).
- Per-user permissions (single role: anyone authenticated has full access,
  matching Sonarr's default).
