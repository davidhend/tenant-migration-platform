#!/usr/bin/env bash
# Tail logs from the stack.  Usage: ./logs.sh [service]   e.g. ./logs.sh api
set -euo pipefail
cd "$(dirname "$0")"
if [ $# -gt 0 ]; then
  docker compose logs -f "$1"
else
  docker compose logs -f
fi
