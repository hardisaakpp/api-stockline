#!/usr/bin/env bash
# Starts the development directory and seeds it. Re-runnable.
set -euo pipefail

export DOCKER_CONTEXT="${STOCKLINE_DOCKER_CONTEXT:-default}"
export DIRECTORY_ADMIN_PASSWORD="${DIRECTORY_ADMIN_PASSWORD:-Dir-Admin-2026!}"
cd "$(dirname "$0")"

docker compose up -d --wait --wait-timeout 180
./seed.sh
