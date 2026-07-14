#!/usr/bin/env bash
# Show the status of the migration platform stack.
set -euo pipefail
cd "$(dirname "$0")"

if ! docker info >/dev/null 2>&1; then
  echo "❌ Docker engine isn't running. Start Docker Desktop, then re-run ./status.sh"
  exit 1
fi

echo "🐳 Containers:"
docker compose ps
echo ""

echo "🩺 API readiness (http://localhost:5000/health/ready):"
if body=$(curl -fsS http://localhost:5000/health/ready 2>/dev/null); then
  if command -v python3 >/dev/null 2>&1; then
    echo "$body" | python3 -c 'import sys,json
d=json.load(sys.stdin)
print("   overall:", d.get("status"))
for c in d.get("checks",[]): print("   -", c["name"] + ":", c["status"])' 2>/dev/null || echo "   $body"
  else
    echo "   $body"
  fi
else
  echo "   (API not reachable — it may be starting, stopped, or unhealthy. Try ./logs.sh api)"
fi
