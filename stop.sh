#!/usr/bin/env bash
# Stop the migration platform stack.
# Usage: ./stop.sh [--down] [--clean]
#   (no args)  stop containers, keep them and the database volume (fast restart)
#   --down     remove containers + network (keeps the database volume)
#   --clean    remove containers + network + DELETE the database volume (fresh start)
set -euo pipefail
cd "$(dirname "$0")"

case "${1:-}" in
  "")       echo "⏹️  Stopping containers (database volume preserved)…"; docker compose stop ;;
  --down)   echo "⏹️  Removing containers + network (database volume preserved)…"; docker compose down ;;
  --clean)  echo "🧹 Removing containers + network + DELETING the database volume…"; docker compose down -v ;;
  *)        echo "Unknown option: $1  (use --down or --clean)"; exit 1 ;;
esac
echo "✅ Done."
