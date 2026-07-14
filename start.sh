#!/usr/bin/env bash
# Start the full M365 Migration Platform stack (postgres + api + web) in Docker.
# Usage: ./start.sh [--build] [--rebuild]
#   --build    force a rebuild of the api/web images before starting
#   --rebuild  alias for --build
set -euo pipefail
cd "$(dirname "$0")"

BUILD_ARGS=()
for arg in "$@"; do
  case "$arg" in
    --build|--rebuild) BUILD_ARGS+=(--build) ;;
    *) echo "Unknown option: $arg" ; exit 1 ;;
  esac
done

# --- Preflight: is Docker running? -------------------------------------------
if ! command -v docker >/dev/null 2>&1; then
  echo "❌ Docker is not installed or not on PATH."
  echo "   Install Docker Desktop and enable WSL integration (Settings → Resources → WSL Integration)."
  exit 1
fi
if ! docker info >/dev/null 2>&1; then
  echo "❌ Docker is installed but the engine isn't running."
  echo "   Start Docker Desktop (whale icon) and wait until it says 'Engine running', then re-run ./start.sh"
  exit 1
fi

# The API bind-mounts settings.override.json (runtime-writable settings that
# must survive rebuilds). If the host file is missing, Docker would create a
# DIRECTORY at that path and break the mount — seed an empty JSON object.
if [ ! -f apps/api/settings.override.json ]; then
  echo '{}' > apps/api/settings.override.json
  echo "📝 Created empty apps/api/settings.override.json (runtime settings live here)."
fi

echo "🐳 Starting the migration platform (postgres + api + web)…"
echo "   First run builds the images and can take a few minutes."
docker compose up -d "${BUILD_ARGS[@]}"

# --- Wait for the API to report healthy --------------------------------------
echo -n "⏳ Waiting for the API to become healthy"
for _ in $(seq 1 60); do
  if curl -fsS http://localhost:5000/health/live >/dev/null 2>&1; then
    echo " ✓"
    break
  fi
  echo -n "."
  sleep 3
done

if ! curl -fsS http://localhost:5000/health/live >/dev/null 2>&1; then
  echo ""
  echo "⚠️  The API didn't report healthy yet. It may still be starting — check with ./status.sh or ./logs.sh api"
else
  echo ""
  echo "✅ Ready!"
  echo "   Web:      http://localhost:3000"
  echo "   API:      http://localhost:5000   (Swagger: http://localhost:5000/swagger)"
  echo "   Postgres: localhost:5432"
  echo ""
  echo "   Sign in with your admin credentials (first ever run: admin / MigrationAdmin123!,"
  echo "   which prompts for a new password on first login)."
  echo "   Stop with ./stop.sh   ·   status with ./status.sh   ·   logs with ./logs.sh"
fi
