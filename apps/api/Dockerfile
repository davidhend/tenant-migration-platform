# syntax=docker/dockerfile:1
# Multi-stage build for the MigrationPlatform API.
# Build context is ./apps/api (see docker-compose.yml / CI).

# ---- build ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first (cached until the csproj changes). The dotnet-ef tool manifest
# lives at the repo root and is design-time only; EF migrations are compiled
# into the assembly, so the runtime build does not need it.
COPY ["MigrationPlatform.Api.csproj", "./"]
RUN dotnet restore "MigrationPlatform.Api.csproj"

# Copy the rest and publish. scripts/** (runbook) and Migrations/ are part of
# the project and land in the publish output automatically.
COPY . .
RUN dotnet publish "MigrationPlatform.Api.csproj" \
    -c Release -o /app/publish /p:UseAppHost=false

# ---- runtime --------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final

# curl is used by the container HEALTHCHECK; the aspnet image ships without it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build --chown=app:app /app/publish .

# DataProtection key ring and the runtime settings-override file are written
# under the content root; make them writable by the non-root user.
RUN mkdir -p /app/keys && chown -R app:app /app/keys /app

# .NET 8 runtime images ship a pre-created non-root user "app" (UID 1654).
USER app

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# Liveness probe: the API exposes an anonymous /health/live endpoint.
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD curl -fsS http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "MigrationPlatform.Api.dll"]
