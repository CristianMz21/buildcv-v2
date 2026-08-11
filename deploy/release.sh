#!/usr/bin/env bash
#
# Moves the deployment to one commit: migrator and API together, schema before code, verified after.
#
#   ./deploy/release.sh                  # whatever main is at right now
#   ./deploy/release.sh <40-char-sha>    # a specific build
#
# WHY THIS EXISTS. Releasing was one command from the runbook -- `az containerapp update ... --image` --
# for ONE of the two things that have to move together. That is exactly how the migrator ended up two
# deployments behind the API: it was repointed twice and the job left behind. `deploy/verify.sh` catches
# the drift afterwards; this makes it impossible instead.
#
# THE ORDER IS THE POINT, and it is the same order docker-compose.app.yml enforces with
# `service_completed_successfully`: the schema is applied by a job that runs to completion BEFORE the
# process serving traffic is replaced. Backwards, the new API serves against a schema it expects and
# does not have -- which surfaces as runtime errors on whichever request touches the new column first,
# not as anything the deploy reports.
#
# It refuses to start on anything it cannot verify, because every check here is cheaper before the
# change than after it.

set -uo pipefail

GROUP="${AZ_GROUP:-buildcv-rg}"
API_APP="${AZ_API_APP:-buildcv-api}"
JOB="${AZ_JOB:-buildcv-migrator}"
GHCR_OWNER="$(printf '%s' "${GHCR_OWNER:-cristianmz21}" | tr '[:upper:]' '[:lower:]')"

say()  { printf '\n\033[1m%s\033[0m\n' "$*"; }
die()  { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }
note() { printf '  %s\n' "$*"; }

SHA="${1:-$(git rev-parse HEAD 2>/dev/null)}"
# A 40-CHAR SHA OR NOTHING. `latest` moves under a running app -- a replica that restarts, or an app
# waking from zero, pulls whatever it points at then, which nobody chose and no record names. A short
# sha is refused for the same reason it is refused in verify.sh: the tag CI publishes is the long one,
# and a short one would simply not be found, one step later and less clearly.
rg -q '^[0-9a-f]{40}$' <<< "$SHA" || die "Not a 40-character commit SHA: '$SHA'"

API_IMAGE="ghcr.io/${GHCR_OWNER}/buildcv-api:${SHA}"
MIGRATOR_IMAGE="ghcr.io/${GHCR_OWNER}/buildcv-migrator:${SHA}"

command -v az >/dev/null || die "az is not installed."
az account show >/dev/null 2>&1 || die "Run 'az login' first."

say "1/5  Both images exist and are anonymously pullable"
# BEFORE ANYTHING IS TOUCHED. A missing image otherwise surfaces as a revision stuck in Activating, or
# worse as a migration job that never starts after the old one has already been repointed.
#
# Anonymously, with any local credential set aside: Container Apps has no credential here, so the only
# honest question is whether an anonymous client can fetch the manifest. `docker manifest inspect`
# performs GHCR's token exchange; a bare curl answers 401 even for a public package.
if command -v docker >/dev/null; then
  docker logout ghcr.io >/dev/null 2>&1 || true
  for IMAGE in "$API_IMAGE" "$MIGRATOR_IMAGE"; do
    docker manifest inspect "$IMAGE" >/dev/null 2>&1 || die "Not published: $IMAGE
  CI publishes on push to main. Check the run finished, or pass a SHA that has one."
    note "ok  $IMAGE"
  done
else
  note "docker not installed -- skipping the preflight. A missing image will surface as a stuck revision."
fi

say "2/5  Migrator -> $SHA"
az containerapp job update -g "$GROUP" -n "$JOB" --image "$MIGRATOR_IMAGE" -o none \
  || die "Could not repoint the migration job."

say "3/5  Applying the schema, and waiting for it"
EXECUTION=$(az containerapp job start -g "$GROUP" -n "$JOB" --query name -o tsv) \
  || die "Could not start the migration job."
note "execution $EXECUTION"
# WAITED ON, NOT FIRED AND FORGOTTEN -- the whole reason this script exists is that the two halves must
# not drift, and starting a job without waiting is drift with extra steps.
for _ in $(seq 1 60); do
  STATUS=$(az containerapp job execution show -g "$GROUP" -n "$JOB" \
             --job-execution-name "$EXECUTION" --query "properties.status" -o tsv 2>/dev/null || echo Unknown)
  case "$STATUS" in
    Succeeded) note "migration Succeeded"; break ;;
    Failed)    die "Migration FAILED. The API was NOT touched, so the running deployment is unchanged.
  az containerapp job execution show -g $GROUP -n $JOB --job-execution-name $EXECUTION" ;;
  esac
  sleep 10
done
[ "${STATUS:-}" = "Succeeded" ] || die "Migration did not finish within 10 minutes. The API was NOT touched."

say "4/5  API -> $SHA"
# --image, never --yaml: the latter REPLACES the container definition rather than merging it, so it
# silently drops every environment variable and the app then fails ValidateOnStart on the missing
# Jwt:SigningKey -- exit 139, crash-looping.
REVISION=$(az containerapp update -g "$GROUP" -n "$API_APP" --image "$API_IMAGE" \
             --query "properties.latestRevisionName" -o tsv) \
  || die "Could not update the API. The schema is already at $SHA, which is forward-compatible; re-run."
note "revision $REVISION"

for _ in $(seq 1 30); do
  STATE=$(az containerapp revision show -g "$GROUP" -n "$API_APP" --revision "$REVISION" \
            --query "properties.runningState" -o tsv 2>/dev/null || echo Unknown)
  case "$STATE" in
    Running) note "revision Running"; break ;;
    Failed)  die "Revision $REVISION failed to start. The PREVIOUS revision keeps serving traffic, so
  this is not an outage -- read the logs, then roll forward:
  az containerapp logs show -g $GROUP -n $API_APP --revision $REVISION --tail 100" ;;
  esac
  sleep 10
done

say "5/5  Verifying the deployed product"
# The release is not the update; it is the update plus evidence. Exit 2 (inconclusive) is passed through
# rather than swallowed -- usually this machine hitting the deployment's own 5/min auth window, which
# says the limiter works and says nothing about whether the product does.
"$(dirname "$0")/verify.sh"
VERIFY=$?

case "$VERIFY" in
  0) say "Released $SHA, and verified." ;;
  2) say "Released $SHA. Verification was INCONCLUSIVE -- wait a minute and run ./deploy/verify.sh." ;;
  *) say "Released $SHA, and verification FAILED. Read the checks above before doing anything else."
     printf '  Roll back:  az containerapp update -g %s -n %s --image ghcr.io/%s/buildcv-api:<previous-sha>\n' \
       "$GROUP" "$API_APP" "$GHCR_OWNER"
     printf '  Note the schema stays at %s. Migrations here are forward-only in practice; see\n' "$SHA"
     printf '  docs/deployment.md 3.\n' ;;
esac

exit "$VERIFY"
