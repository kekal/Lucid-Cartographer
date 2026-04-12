# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .

# TODO [CRIT-02/MED-03]: Playwright requires Chromium OS dependencies (libx11, libnss3, libatk, etc.)
# that are NOT included in the aspnet base image. The scraper will crash in this container.
# Preferred fix: extract the scraper to a separate sidecar container (see REVIEW_ARCHITECTURE.md CRIT-02).
# Stopgap: uncomment the following line to install Playwright deps in this image (~200MB added):
# RUN apt-get update && apt-get install -y --no-install-recommends wget && \
#     dotnet tool install --global Microsoft.Playwright.CLI && \
#     /root/.dotnet/tools/playwright install-deps chromium && \
#     apt-get clean && rm -rf /var/lib/apt/lists/*

# MED-02: Run as non-root user for container security
RUN adduser --disabled-password --gecos '' --home /app appuser && \
    mkdir -p /data && chown -R appuser:appuser /app /data
USER appuser

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

# MED-01: Health check so Docker/orchestrators can detect a hung process.
# Uses the /health endpoint mapped in Program.cs via MapHealthChecks.
# Note: wget is available in the aspnet base image (Debian-based).
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "LucidCartographer.dll"]
