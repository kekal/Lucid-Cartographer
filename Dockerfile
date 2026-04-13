# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
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

EXPOSE 8080

# MED-01: Health check so Docker/orchestrators can detect a hung process.
# Uses the /health endpoint mapped in Program.cs via MapHealthChecks.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "LucidCartographer.dll"]
