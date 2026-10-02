#!/usr/bin/env bash
# Creates the demo directory: groups that map to Stockline roles, a read-only service account, and a few people.
# Idempotent: running it again leaves what exists untouched. Needs the container from docker-compose.yml.
set -euo pipefail

export DOCKER_CONTEXT="${STOCKLINE_DOCKER_CONTEXT:-default}"
CONTAINER="${DIRECTORY_CONTAINER:-stockline-directory}"
PASSWORD="${DIRECTORY_USER_PASSWORD:-Demo-Pass-2026!}"
SERVICE_PASSWORD="${DIRECTORY_SERVICE_PASSWORD:-Svc-Bind-2026!}"
DOMAIN="stockline.test"

sam() { docker exec "$CONTAINER" samba-tool "$@"; }
exists() { docker exec "$CONTAINER" samba-tool "$1" show "$2" >/dev/null 2>&1; }

# Demo accounts do not expire: nobody should be locked out of a portfolio demo by a password-age policy.
sam domain passwordsettings set --complexity=on --min-pwd-length=8 --min-pwd-age=0 --max-pwd-age=0 >/dev/null

for group in stockline-admins stockline-buyers stockline-suppliers stockline-production purchasing-team; do
  exists group "$group" || sam group add "$group" >/dev/null
done
# A nested group: members of purchasing-team are buyers through it (the API resolves groups at any depth).
sam group addmembers stockline-buyers purchasing-team >/dev/null 2>&1 || true

user() { # account  Given  Surname  [group...]
  local account="$1" given="$2" surname="$3"; shift 3
  exists user "$account" || sam user create "$account" "$PASSWORD" --given-name="$given" --surname="$surname" \
    --mail-address="${account}@${DOMAIN}" >/dev/null
  for group in "$@"; do sam group addmembers "$group" "$account" >/dev/null 2>&1 || true; done
}

exists user svc-stockline || sam user create svc-stockline "$SERVICE_PASSWORD" --description="Read-only account Stockline searches the directory with" >/dev/null

user dan.admin Dan Admin stockline-buyers stockline-admins
user ben.buyer Ben Buyer stockline-buyers
user sam.supplier Sam Supplier stockline-suppliers
user pat.production Pat Production stockline-production
user pia.purchasing Pia Purchasing purchasing-team
user nina.nogroup Nina Nogroup

echo "Directory ready: realm STOCKLINE.TEST, users dan.admin ben.buyer sam.supplier pat.production pia.purchasing nina.nogroup (password: ${PASSWORD})"
