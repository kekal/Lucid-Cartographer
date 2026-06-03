# Build stage. Must be a .NET 10 SDK: the code uses C# 14 (LangVersion is pinned
# to 14.0 in Directory.Build.props), which the .NET 8 SDK cannot compile. The app
# still targets net8.0 and runs on the .NET 8 runtime image below.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Download standalone Tailwind CLI (no Node.js needed)
ADD https://github.com/tailwindlabs/tailwindcss/releases/download/v3.4.17/tailwindcss-linux-x64 /usr/local/bin/tailwindcss
RUN chmod +x /usr/local/bin/tailwindcss

WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
# Build Tailwind CSS
RUN tailwindcss -i wwwroot/css/input.css -o wwwroot/css/tailwind.css --minify
RUN dotnet publish -c Release -o /app/publish

# Runtime stage — official Playwright image includes Chromium and all OS dependencies (CRIT-02 fix)
FROM mcr.microsoft.com/playwright/dotnet:v1.49.0-noble
WORKDIR /app

# Remote-view stack for the Google sign-in: Xvfb (virtual display for the headful
# Chromium), x11vnc (VNC server on that display), and noVNC + websockify (web
# client + websocket bridge). All bound to localhost at runtime and only reachable
# through the app's authenticated same-origin proxy. Installed as root before USER.
RUN apt-get update && apt-get install -y --no-install-recommends \
        xvfb x11vnc novnc websockify \
    && rm -rf /var/lib/apt/lists/* \
    # Xvfb runs as the non-root appuser and needs the X11 socket dir to exist
    # with sticky/world-writable perms (it cannot create it itself when euid != 0).
    && mkdir -p /tmp/.X11-unix && chmod 1777 /tmp/.X11-unix

# MED-02: Run as a non-root user. UID/GID are build args so the container user
# matches the owner of the bind-mounted /data volume on the host (e.g. a NAS
# PUID/PGID) — otherwise it can't write the SQLite DB or the OAuth keys. Defaults
# match the target NAS; override with --build-arg PUID/PGID (or via compose).
# NB: named PUID/PGID, not APP_UID/APP_GID — the .NET base image sets
# `ENV APP_UID=1654`, and an ENV overrides a same-named ARG, so APP_UID would
# silently resolve to 1654 (the base image's "app" user) instead of ours.
ARG PUID=1026
ARG PGID=100
RUN groupadd --gid ${PGID} appgroup 2>/dev/null || true && \
    useradd --uid ${PUID} --gid ${PGID} --home-dir /app --no-create-home --shell /usr/sbin/nologin appuser 2>/dev/null || true && \
    mkdir -p /data

# Copy the published app (root-owned at this point).
COPY --from=build /app/publish .

# Entrypoint that boots Xvfb + x11vnc + websockify, then the app.
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod 755 /usr/local/bin/docker-entrypoint.sh

# Set ownership and permissions in a shell RUN, NOT via `COPY --chown=${VAR}`:
# the COPY --chown directive does not reliably expand build-arg variables on
# older Docker daemons (e.g. Synology Container Manager), which silently leaves
# the files root-owned and unreadable to the non-root user. A shell RUN always
# expands ${PUID}/${PGID}. The extra `chmod -R a+rX /app` makes every file
# readable and every dir traversable by ANY uid — app files hold no secrets
# (those come from env vars), so this is safe and decouples "can read /app"
# from the container uid entirely. Only /data is uid-sensitive (its ownership
# is set on the host for the bind mount).
RUN chown -R ${PUID}:${PGID} /app /data && chmod -R a+rX /app

USER ${PUID}:${PGID}

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
# SQLite DB location inside the container — points at the /data volume created above.
# On non-container hosts this variable is unset and Program.cs falls back to
# <ContentRoot>/data/cartographer.db, which works on any OS.
ENV DB_PATH=/data/cartographer.db
# Keep the diagnostic log on the writable /data volume, not in /app.
ENV SCRAPE_DIAG_LOG=/data/scrape-diag.log
# Persist the shared Chrome profile on the /data volume so the Google sign-in
# survives container restarts (otherwise it lands under /app and is ephemeral).
ENV CHROME_PROFILE_PATH=/data/chrome-profile
# Virtual display used by the headful shared browser session.
ENV DISPLAY=:99
# Enable the embedded noVNC remote view for the Google sign-in (server only).
ENV Browser__RemoteView__Enabled=true

EXPOSE 8080

# MED-01: Health check so Docker/orchestrators can detect a hung process.
# Uses the /health endpoint mapped in Program.cs via MapHealthChecks.
# ARCH-LOW-08: wget fallback if curl is not available (e.g., slimmer base images)
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl --fail --silent http://localhost:8080/health || wget -q -O /dev/null http://localhost:8080/health || exit 1

ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
