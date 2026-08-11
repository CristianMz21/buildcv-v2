#!/usr/bin/env bash
#
# Verifies the DEPLOYED product, not the build. Read-only: it creates one throwaway account and touches
# nothing else.
#
# WHY THIS EXISTS. Every defect this deployment has had was found by running the product and none by
# running the suite -- 1867 tests passed while the composed stack served nothing but 500s, because
# nothing in the suite brings the real thing up. CI's `compose-smoke` job closed that for compose. This
# closes it for the deployment, where the failures are different in kind: a registry credential that
# stopped being needed, an env var a `--yaml` patch silently dropped, a rate limiter partitioning on a
# CDN's edge instead of a client. None of those fail a build and none of them turn a probe red.
#
# WHAT IT DOES NOT DO. It does not read a health status and call that verification. `healthState` is a
# small closed value that a working app and an unprobed one produce identically, and a revision reports
# `Running` with no probes declared at all. Every check below either drives the product or reads a
# concrete configured value.
#
#   ./deploy/verify.sh                                   # the live deployment
#   SITE=https://staging.example.com ./deploy/verify.sh  # somewhere else
#   SKIP_AZURE=1 ./deploy/verify.sh                      # HTTP checks only, no az login needed

set -uo pipefail

SITE="${SITE:-https://buildcv.cristianarellano.com}"
GROUP="${AZ_GROUP:-buildcv-rg}"
API_APP="${AZ_API_APP:-buildcv-api}"
JOB="${AZ_JOB:-buildcv-migrator}"
# The hostname that must NOT serve. Ingress is restricted to the CDN's ranges, and the whole point of
# putting a CDN in front evaporates if the origin still answers on its own name.
ORIGIN_HOST="${AZ_ORIGIN_HOST:-buildcv-web.prouddesert-a13e517c.eastus.azurecontainerapps.io}"

PASS=0; FAIL=0; UNKNOWN=0
ok()   { printf '  \033[32mok\033[0m    %s\n' "$1"; PASS=$((PASS+1)); }
bad()  { printf '  \033[31mFAIL\033[0m  %s\n' "$1"; FAIL=$((FAIL+1)); }
# A THIRD OUTCOME, because "could not be checked" is neither of the other two. Counting it as a pass
# hides that coverage was silently narrowed; counting it as a failure cries wolf at a healthy
# deployment. It is reported separately and gets its own exit code.
huh()  { printf '  \033[33m????\033[0m  %s\n' "$1"; UNKNOWN=$((UNKNOWN+1)); }
note() { printf '        %s\n' "$1"; }
head_() { printf '\n\033[1m%s\033[0m\n' "$1"; }

HOST="${SITE#https://}"; HOST="${HOST%%/*}"

# RESOLVE THROUGH A PUBLIC RESOLVER, NOT THE LOCAL ONE. On a machine running a VPN or Cloudflare WARP
# the local resolver can hand back the ORIGIN rather than the CDN, so curl connects straight to it,
# arrives from an address the ingress does not allow, and answers 403 `RBAC: access denied` -- which
# reads exactly like an outage and is not one. Measured on a real workstation. Pinning the address here
# means this script tests the path a user takes rather than the path this machine happens to take.
RESOLVE=()
if command -v dig >/dev/null; then
  CDN_IP=$(dig +short @1.1.1.1 "$HOST" 2>/dev/null | rg '^[0-9.]+$' | head -1)
  [ -n "${CDN_IP:-}" ] && RESOLVE=(--resolve "$HOST:443:$CDN_IP")
fi

# Every request carries Origin: the BFF refuses cross-site writes, so a probe without it measures that
# guard rather than the thing it was aiming at.
req() { curl -s "${RESOLVE[@]}" -H "Origin: $SITE" "$@"; }
code() { req -o /dev/null -w '%{http_code}' "$@"; }

head_ "1. The front door, through the edge"

STATUS=$(code "$SITE/")
case "$STATUS" in
  200|307|308) ok "$SITE answers ($STATUS)" ;;
  403) bad "403 from $SITE"
       note "If this says 'RBAC: access denied' it is DNS, not the deployment: you reached the origin"
       note "directly. Install dig, or pass --resolve yourself. See docs/deployment.md 4." ;;
  *)   bad "$SITE answered $STATUS" ;;
esac

if [ ${#RESOLVE[@]} -gt 0 ]; then
  # cf-ray proves the request went THROUGH the CDN. Without it every header assertion below could be
  # reading the origin's own response, which is a different set of values.
  if req -I "$SITE/" | rg -qi '^cf-ray:'; then ok "went through the CDN (cf-ray present)"
  else note "no cf-ray -- headers below are the origin's, not what a browser receives"; fi
fi

head_ "2. The origin hostname must refuse"
# Not decoration: after the DNS moved to the CDN this hostname still answered 200, and every edge
# control was one hostname away from being bypassed.
ORIGIN_STATUS=$(curl -s -o /dev/null -w '%{http_code}' "https://$ORIGIN_HOST/" 2>/dev/null)
if [ "$ORIGIN_STATUS" = "403" ]; then ok "$ORIGIN_HOST refuses (403)"
else bad "$ORIGIN_HOST answered $ORIGIN_STATUS -- the CDN can be bypassed"; fi

head_ "3. The product, driven rather than probed"
EMAIL="verify-$(od -An -N6 -tx1 /dev/urandom | tr -d ' ')@example.com"
JAR=$(mktemp); trap 'rm -f "$JAR"' EXIT

# THIS SCRIPT THROTTLES ITSELF, and that is the rate limiter working rather than a bug in either. The
# auth window is 5 per minute per partition, and the partition is now the CLIENT -- so running this
# twice inside a minute earns a 429 from your own deployment. Reported as inconclusive rather than as a
# failure: a 429 here says the limiter partitions on you, which is the property we spent the evening
# establishing, and it says nothing at all about whether the product works. Wait a minute and re-run.
THROTTLED=0

REG=$(code -c "$JAR" -X POST "$SITE/api/auth/register" -H 'Content-Type: application/json' \
  -d "{\"email\":\"$EMAIL\",\"password\":\"Str0ngPassw0rd!2026\",\"fullName\":\"Verify\",\"role\":\"Candidate\"}")
case "$REG" in
  201) ok "register -> 201" ;;
  429) huh "register -> 429, throttled"; THROTTLED=1
       note "Your own auth window (5/min per client). Wait a minute and run this again." ;;
  *)   bad "register -> $REG" ;;
esac

if [ "$THROTTLED" = "1" ]; then
  huh "login -- not attempted, the account was never created"
  huh "authenticated read -- not attempted, there is no session"
else
  LOGIN=$(code -c "$JAR" -b "$JAR" -X POST "$SITE/api/auth/login" -H 'Content-Type: application/json' \
    -d "{\"email\":\"$EMAIL\",\"password\":\"Str0ngPassw0rd!2026\"}")
  case "$LOGIN" in
    200) ok "login -> 200" ;;
    429) huh "login -> 429, throttled"; THROTTLED=1 ;;
    *)   bad "login -> $LOGIN" ;;
  esac

  # A READ, not just the two writes. Registration can succeed against a database the query path cannot
  # use, and a session that mints a token it cannot then spend is the failure the migrator exists to
  # prevent -- error 4060 while /health/live still answers 200.
  if [ "$THROTTLED" = "1" ]; then
    huh "authenticated read -- not attempted, there is no session"
    huh "account cleanup -- not attempted, there is no session"
  else
    BODY=$(req -b "$JAR" "$SITE/api/resumes")
    if printf '%s' "$BODY" | rg -q '"items"'; then ok "authenticated read returns a page"
    else bad "authenticated read did not return a page: ${BODY:0:120}"; fi

    # IT CLEANS UP AFTER ITSELF. A verification that leaves a row behind every time it runs is a slow
    # leak into the product's own database -- and this is meant to be run often and on a schedule.
    # Deleting is also the only honest way to check deletion, so the cost buys a check rather than
    # merely avoiding one.
    DEL=$(code -b "$JAR" -c "$JAR" -X DELETE "$SITE/api/auth/me" -H 'Content-Type: application/json' \
      -d '{"currentPassword":"Str0ngPassw0rd!2026"}')
    if [ "$DEL" = "204" ]; then ok "the throwaway account deletes itself -> 204"
    else bad "account deletion -> $DEL (a row is now left behind: $EMAIL)"; fi

    # The tombstone, not just the 204. Delete writes a domain status AND a shadow DeletedAt, and the
    # global query filter is what makes the account unreachable; a 204 that left the row loadable would
    # look identical from here.
    AFTER=$(code -X POST "$SITE/api/auth/login" -H 'Content-Type: application/json' \
      -d "{\"email\":\"$EMAIL\",\"password\":\"Str0ngPassw0rd!2026\"}")
    case "$AFTER" in
      200) bad "a deleted account can still log in" ;;
      429) huh "post-deletion login -> 429, throttled; the tombstone was not checked" ;;
      *)   ok "a deleted account cannot log in ($AFTER)" ;;
    esac
  fi
fi

head_ "4. Errors keep their shape"
# ProblemDetails is asserted by the CONTENT TYPE, because the bodies were always right and the header
# was the half that broke -- WriteAsJsonAsync(value) overwrites Response.ContentType.
CT=$(req -o /dev/null -w '%{content_type}' -X POST "$SITE/api/auth/login" \
  -H 'Content-Type: application/json' -d '{"email":"nobody@example.com","password":"wrong"}')
case "$CT" in
  *problem+json*) ok "a rejected login is application/problem+json" ;;
  *)              bad "a rejected login answered '$CT'" ;;
esac

azure_checks() {
  head_ "5. How the deployment is configured"

  if ! az account show >/dev/null 2>&1; then
    note "not logged in to az -- skipping. Run 'az login', or SKIP_AZURE=1 to silence this."
    return
  fi

  local APP; APP=$(az containerapp show -g "$GROUP" -n "$API_APP" -o json 2>/dev/null)

  # ONE FAILURE, AND NEVER A PASS, WHEN THE APP CANNOT BE READ. Pointed at an app that does not exist,
  # an earlier version of this block reported eight confusing failures and one GREEN -- "the diagnostic
  # is off" -- because that check's default-when-absent is the safe value, and absent-because-
  # unreadable is indistinguishable from absent-because-unset. A check that cannot fail when nothing is
  # known is not a check, which is why the whole section returns here instead of continuing.
  if [ -z "$APP" ] || [ "$(printf '%s' "$APP" | jq -r 'has("properties")' 2>/dev/null)" != "true" ]; then
    bad "cannot read container app '$API_APP' in '$GROUP' -- nothing below was verified"
    return
  fi

  local IMAGE; IMAGE=$(printf '%s' "$APP" | jq -r '.properties.template.containers[0].image')
  # A TAG IS A DEPLOYMENT RECORD. `latest` moves under a running app: a replica that restarts, or an
  # app waking from zero, pulls whatever it points at then -- which nobody chose.
  if printf '%s' "$IMAGE" | rg -q ':[0-9a-f]{40}$'; then ok "API pinned to a 40-char SHA"
  else bad "API image is not SHA-pinned: $IMAGE"; fi

  # An empty registries array is what proves the public-GHCR path really works. A leftover credential
  # would let a deleted registry look fine right up until the next cold pull.
  if [ "$(printf '%s' "$APP" | jq -r '.properties.configuration.registries | length')" = "0" ]; then
    ok "no registry credential configured"
  else bad "a registry credential is still configured"; fi

  # THE --yaml TRAP. `az containerapp update --yaml` REPLACES the container definition rather than
  # merging it, so a patch adding a probe silently drops every environment variable and the app fails
  # ValidateOnStart on the missing Jwt:SigningKey -- exit 139, crash-looping.
  local ENVC; ENVC=$(printf '%s' "$APP" | jq -r '.properties.template.containers[0].env | length')
  if [ "$ENVC" -ge 10 ]; then ok "API carries $ENVC environment variables"
  else bad "API carries only $ENVC environment variables (expected >= 10)"; fi

  # Never the other way round: as liveness, /health/ready restarts every instance the moment the
  # database goes away, into a database that is still down.
  local PROBES; PROBES=$(printf '%s' "$APP" \
    | jq -r '[.properties.template.containers[0].probes[]? | "\(.type):\(.httpGet.path)"] | sort | join(" ")')
  if [ "$PROBES" = "Liveness:/health/live Readiness:/health/ready" ]; then ok "probes declared the right way round"
  else bad "probes are '$PROBES'"; fi

  # An address is personal data and a log line carries none of this repository's encryption. The
  # diagnostic is meant to be switched on for one request and off again; left on it writes one line per
  # request forever. Reached only after the app was confirmed readable, so absent here really does mean
  # unset.
  local DEBUG; DEBUG=$(printf '%s' "$APP" \
    | jq -r '.properties.template.containers[0].env[]? | select(.name=="Logging__LogLevel__BuildCv.Api.Security") | .value')
  if [ "${DEBUG:-Information}" = "Debug" ]; then bad "ForwardedHeaderDiagnostics is still at Debug in production"
  else ok "the forwarded-header diagnostic is off"; fi

  local JOB_IMAGE; JOB_IMAGE=$(az containerapp job show -g "$GROUP" -n "$JOB" \
    --query "properties.template.containers[0].image" -o tsv 2>/dev/null)
  # They must move together. A migrator behind the API is a schema the API expects and does not have,
  # which surfaces as runtime errors rather than as anything a deploy reports. Found real drift on this
  # script's first run: the API had been repointed twice and the job left behind.
  if [ -n "$JOB_IMAGE" ] && [ "${JOB_IMAGE##*:}" = "${IMAGE##*:}" ]; then ok "migrator and API are on the same tag"
  else bad "migrator is on '${JOB_IMAGE##*:}', API on '${IMAGE##*:}'"; fi
}

if [ "${SKIP_AZURE:-0}" = "1" ]; then
  head_ "Skipping the Azure checks (SKIP_AZURE=1)"
else
  azure_checks
fi

head_ "$PASS passed, $FAIL failed, $UNKNOWN inconclusive"

# Three exit codes for three outcomes. 2 rather than 0 because "nothing is known to be broken" is not
# "everything was checked", and a pipeline that treats those as the same thing is the reason silent
# coverage loss goes unnoticed.
[ "$FAIL" -gt 0 ] && exit 1
[ "$UNKNOWN" -gt 0 ] && exit 2
exit 0
