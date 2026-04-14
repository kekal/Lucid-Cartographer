# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
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
COPY --from=build /app/publish .

# MED-02: Run as non-root user for container security
RUN adduser --disabled-password --gecos '' --home /app appuser && \
    mkdir -p /data && chown -R appuser:appuser /app /data
USER appuser

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
# SQLite DB location inside the container — points at the /data volume created above.
# On non-container hosts this variable is unset and Program.cs falls back to
# <ContentRoot>/data/cartographer.db, which works on any OS.
ENV DB_PATH=/data/cartographer.db

EXPOSE 8080

# MED-01: Health check so Docker/orchestrators can detect a hung process.
# Uses the /health endpoint mapped in Program.cs via MapHealthChecks.
# ARCH-LOW-08: wget fallback if curl is not available (e.g., slimmer base images)
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl --fail --silent http://localhost:8080/health || wget -q -O /dev/null http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "LucidCartographer.dll"]
