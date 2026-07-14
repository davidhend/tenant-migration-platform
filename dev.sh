#!/usr/bin/env bash
# Dev mode with hot reload: run PostgreSQL in Docker, and the API + web locally
# (dotnet watch / npm run dev) so code changes reload instantly.
# The fully-containerized path is ./start.sh — use that if you just want to run it.
set -euo pipefail
cd "$(dirname "$0")"

if ! docker info >/dev/null 2>&1; then
  echo "❌ Docker engine isn't running. Start Docker Desktop, then re-run ./dev.sh"
  exit 1
fi

echo "🐳 Starting PostgreSQL only (api + web run locally for hot reload)…"
docker compose up -d postgres

echo -n "⏳ Waiting for PostgreSQL"
for _ in $(seq 1 30); do
  if docker compose exec -T postgres pg_isready -U migration_user -d migration_platform >/dev/null 2>&1; then
    echo " ✓"; break
  fi
  echo -n "."; sleep 1
done

DOTNET="dotnet"
[ -x "$HOME/.dotnet/dotnet" ] && DOTNET="$HOME/.dotnet/dotnet"

cat <<EOF

✅ Database is up at localhost:5432.

Now run these in TWO separate terminals for hot reload:

  Backend  (→ http://localhost:5000):
    cd apps/api && $DOTNET watch run

  Frontend (→ http://localhost:3000):
    cd apps/web && npm install && npm run dev

Sign in with the dev login: admin / MigrationAdmin123!
Stop the database later with:  ./stop.sh
EOF
