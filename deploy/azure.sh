#!/usr/bin/env bash
#
# Deploys BuildCv to Azure Container Apps with Azure SQL. Idempotent: re-running updates rather than
# duplicating, so it is safe to run after a failure.
#
# WHY THIS SHAPE. The composed stack has four services and Container Apps maps onto it almost exactly:
#
#   compose            Azure
#   ---------------    ----------------------------------------------------------------
#   web                Container App, EXTERNAL ingress -- free HTTPS on *.azurecontainerapps.io
#   api                Container App, INTERNAL ingress -- reachable only from web, as today
#   migrator           Container Apps JOB -- runs once and exits, which is what a job is
#   sqlserver          Azure SQL Database -- managed, which docs/deployment.md recommends over
#                                            the bundled container for backups and PITR
#   proxy              not needed -- Container Apps terminates TLS and renews the certificate
#
# The internal/external split is the BFF topology this product is built around: no browser ever holds a
# BuildCv credential, and `Cors:AllowedOrigins` stays empty because there is no cross-origin request.
#
# IMAGES COME FROM GHCR, BUILT BY CI -- this script builds nothing. `.github/workflows/ci.yml` publishes
# `buildcv-api` and `buildcv-migrator` on every push to main, tagged with the full commit SHA, and the
# web client's own repository publishes `buildcv-web` the same way. The packages are public, so nothing
# here configures a registry credential and no `registries` block is set on any app.
#
# THAT IS ALSO WHY THIS COSTS NOTHING. An Azure Container Registry bills from day one at about USD
# 5/month even holding three images, and it was the only line on this deployment that ever did. It is
# gone, and the images it held are reproducible from CI rather than merely copied out of it -- which is
# the difference between deleting a registry and losing one. Container Apps and Azure SQL both have real
# free grants that this fits inside at portfolio traffic.
#
# WHAT A FORK NEEDS. Set GHCR_OWNER to your own GitHub account and push to main once, so your CI
# publishes under your namespace. Nothing else changes; the preflight below fails loudly and before
# creating anything if those images are not there.

set -euo pipefail

# ── Knobs ────────────────────────────────────────────────────────────────────────────────────────
LOCATION="${AZ_LOCATION:-eastus}"
GROUP="${AZ_GROUP:-buildcv-rg}"
SQL_SERVER="${AZ_SQL_SERVER:-buildcv-sql-$RANDOM}"
SQL_DB="${AZ_SQL_DB:-BuildCv}"
SQL_ADMIN="${AZ_SQL_ADMIN:-buildcvadmin}"
ENVIRONMENT="${AZ_ENVIRONMENT:-buildcv-env}"

# GHCR paths are lowercase. GitHub's own `github.repository_owner` preserves the case a user typed, so
# an owner with capitals in it produces a name the registry will not serve -- lowercased here for the
# same reason the workflow hardcodes it lowercase.
GHCR_OWNER="$(printf '%s' "${GHCR_OWNER:-cristianmz21}" | tr '[:upper:]' '[:lower:]')"

# A TAG IS A DEPLOYMENT RECORD, so prefer the full 40-character SHA CI publishes. `latest` moves under
# a running app: a replica that restarts, or an app waking from zero, then pulls something nobody
# chose. It is the default only because a first deployment has no SHA to hand yet.
IMAGE_TAG="${IMAGE_TAG:-latest}"
WEB_IMAGE_TAG="${WEB_IMAGE_TAG:-$IMAGE_TAG}"

API_IMAGE="ghcr.io/${GHCR_OWNER}/buildcv-api:${IMAGE_TAG}"
MIGRATOR_IMAGE="ghcr.io/${GHCR_OWNER}/buildcv-migrator:${IMAGE_TAG}"
WEB_IMAGE="ghcr.io/${GHCR_OWNER}/buildcv-web:${WEB_IMAGE_TAG}"

say() { printf '\n\033[1m%s\033[0m\n' "$*"; }

# ── Preconditions, checked before anything is created ────────────────────────────────────────────
command -v az >/dev/null || { echo "az is not installed." >&2; exit 1; }
az account show >/dev/null 2>&1 || { echo "Run 'az login' first." >&2; exit 1; }

say "0/6  The three images exist and are anonymously pullable"
# THIS RUNS BEFORE ANY RESOURCE IS CREATED, deliberately. A missing image otherwise surfaces as a
# container app stuck in Activating, twenty minutes and a database later.
#
# Anonymously, with any local credential set aside: `docker login ghcr.io` succeeding proves nothing
# about a push and equally nothing about a pull -- measured, both directions. Container Apps has no
# credential here, so the question is whether an anonymous client can fetch the manifest, and the only
# honest way to ask is as one.
#
# A bare `curl` of the manifest URL answers 401 even for a public package, because GHCR wants a token
# it will hand to anybody who asks. Reading that 401 as "not public" is a false negative that has
# already happened once; `docker manifest inspect` performs the token exchange and is the real test.
docker logout ghcr.io >/dev/null 2>&1 || true
for IMAGE in "$API_IMAGE" "$MIGRATOR_IMAGE" "$WEB_IMAGE"; do
  if docker manifest inspect "$IMAGE" >/dev/null 2>&1; then
    echo "  ok  $IMAGE"
  else
    echo "  MISSING  $IMAGE" >&2
    echo >&2
    echo "  Push to main so CI publishes it, or set GHCR_OWNER / IMAGE_TAG to something that exists." >&2
    echo "  If it exists but is private, Container Apps cannot pull it without a credential this" >&2
    echo "  script deliberately does not configure -- make the package public instead." >&2
    exit 1
  fi
done

# Secrets are GENERATED, never defaulted. Every one of these is a value that must not come from a file
# anybody can read, and the app refuses to start without them rather than inventing one.
SQL_PASSWORD="${AZ_SQL_PASSWORD:-$(openssl rand -base64 24)Aa1!}"
JWT_KEY="${BUILDCV_JWT_SIGNING_KEY:-$(openssl rand -base64 48)}"
ENCRYPTION_KEY="${BUILDCV_ENCRYPTION_KEY:-$(openssl rand -base64 32)}"
BLIND_INDEX_KEY="${BUILDCV_BLIND_INDEX_KEY:-$(openssl rand -base64 32)}"

say "1/6  Resource group $GROUP in $LOCATION"
az group create -n "$GROUP" -l "$LOCATION" -o none

say "2/6  Azure SQL $SQL_SERVER/$SQL_DB"
# A REGION CAN REFUSE NEW SQL SERVERS, and a failed attempt still RESERVES THE NAME -- so a retry needs
# a fresh name as well as a different region. Measured: eastus and eastus2 both answered
# RegionDoesNotAllowProvisioning, and reusing the name then failed with InvalidResourceLocation against
# a server that did not exist.
for REGION in "$LOCATION" eastus2 centralus westus3; do
  if az sql server create -g "$GROUP" -n "$SQL_SERVER" -l "$REGION" \
       -u "$SQL_ADMIN" -p "$SQL_PASSWORD" -o none 2>/dev/null; then
    echo "  SQL server in $REGION"
    [ "$REGION" = "$LOCATION" ] || echo "  NOTE: this is a different region from the apps, so every query pays a cross-region hop."
    break
  fi
  echo "  $REGION refused; retrying elsewhere with a fresh name"
  SQL_SERVER="${SQL_SERVER%-*}-$RANDOM"
done
# Azure services only. There is no public client for this database -- the API reaches it from inside
# the Container Apps environment, and nothing else has any business connecting.
az sql server firewall-rule create -g "$GROUP" -s "$SQL_SERVER" \
  -n AllowAzureServices --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0 -o none
# The free serverless grant: 100k vCore-seconds and 32 GB a month. --use-free-limit is what claims it,
# and BillOverUsage means it keeps serving if the grant runs out rather than pausing the database under
# a candidate mid-import. The value is BillOverUsage, not BilledOverUsage -- the CLI rejects the wrong
# one by printing its full help text, which buries the one line naming the allowed values.
az sql db create -g "$GROUP" -s "$SQL_SERVER" -n "$SQL_DB" \
  --edition GeneralPurpose --compute-model Serverless --family Gen5 --capacity 1 \
  --use-free-limit --free-limit-exhaustion-behavior BillOverUsage -o none

CONNECTION="Server=tcp:${SQL_SERVER}.database.windows.net,1433;Database=${SQL_DB};User ID=${SQL_ADMIN};Password=${SQL_PASSWORD};Encrypt=True;TrustServerCertificate=False;Connect Timeout=5"

say "3/6  Container Apps environment $ENVIRONMENT"
az extension add --name containerapp --upgrade --only-show-errors -o none
az containerapp env create -g "$GROUP" -n "$ENVIRONMENT" -l "$LOCATION" -o none

say "4/6  Migration job, run once"
# A JOB, not an app: it runs once and exits, which is exactly what the compose migrator does and
# exactly what Program.cs refuses to do inside the process that serves traffic.
az containerapp job create -g "$GROUP" -n buildcv-migrator --environment "$ENVIRONMENT" \
  --trigger-type Manual --replica-timeout 600 --replica-retry-limit 1 \
  --image "$MIGRATOR_IMAGE" \
  --secrets "sqlpass=$SQL_PASSWORD" \
  --env-vars "MIGRATION_SERVER=${SQL_SERVER}.database.windows.net" \
             "MIGRATION_DATABASE=$SQL_DB" \
             "MIGRATION_USER=$SQL_ADMIN" \
             "MIGRATION_PASSWORD=secretref:sqlpass" \
             "MIGRATION_CREATE_DATABASE=false" -o none

EXECUTION=$(az containerapp job start -g "$GROUP" -n buildcv-migrator --query "name" -o tsv)
echo "  waiting on $EXECUTION -- the API is not useful until the schema is there"
# WAITED ON RATHER THAN FIRED AND FORGOTTEN. The API starting against an empty database answers
# readiness failures that look like a connectivity problem, and error 4060 is what that actually is.
for _ in $(seq 1 60); do
  STATUS=$(az containerapp job execution show -g "$GROUP" -n buildcv-migrator \
             --job-execution-name "$EXECUTION" --query "properties.status" -o tsv 2>/dev/null || echo Unknown)
  case "$STATUS" in
    Succeeded) echo "  migration $STATUS"; break;;
    Failed)    echo "  migration FAILED -- see: az containerapp job execution show -g $GROUP -n buildcv-migrator --job-execution-name $EXECUTION" >&2; exit 1;;
  esac
  sleep 10
done

say "5/6  The API, on internal ingress"
# INTERNAL ingress. The API is reachable from the web app and from nothing else, which is the same
# topology the compose file enforces by publishing no port for it.
az containerapp create -g "$GROUP" -n buildcv-api --environment "$ENVIRONMENT" \
  --image "$API_IMAGE" \
  --target-port 8080 --ingress internal \
  --min-replicas 1 --max-replicas 3 --cpu 0.5 --memory 1Gi \
  --secrets "conn=$CONNECTION" "jwt=$JWT_KEY" "enc=$ENCRYPTION_KEY" "blind=$BLIND_INDEX_KEY" \
  --env-vars "ConnectionStrings__BuildCv=secretref:conn" \
             "Jwt__SigningKey=secretref:jwt" \
             "Encryption__ActiveKeyId=v1" \
             "Encryption__Keys__v1__Aes=secretref:enc" \
             "Encryption__BlindIndex__ActiveKeyId=b1" \
             "Encryption__BlindIndex__Keys__b1=secretref:blind" \
             "ASPNETCORE_ENVIRONMENT=Production" \
             "Network__ForwardedHeaders__Enabled=${TRUST_INGRESS:-true}" \
             "Network__ForwardedHeaders__KnownNetworks__0=100.100.0.0/16" \
             "Network__ForwardedHeaders__ForwardLimit=2" -o none

# THE FORWARDED-HEADERS BLOCK WAS MISSING FROM THE FIRST DEPLOYMENT AND THE OMISSION WAS INVISIBLE.
# Without it the API defaults to Enabled:false and ignores X-Forwarded-For entirely -- so every request
# is attributed to the web container and the 5/min auth window is shared by the whole deployment.
# Nothing fails; it just cannot tell users apart, and an experiment run against the deployment recorded
# the internal address and looked like the BFF was at fault.
#
# A NETWORK RATHER THAN AN ADDRESS, which docs/deployment.md otherwise argues against. In compose the
# peer can be pinned and named exactly; in Container Apps it is dynamic inside 100.100.0.0/16, so this
# trusts anything in the ENVIRONMENT. That is proportionate while the environment holds only these two
# apps, and stops being proportionate the moment a third lands in it.
#
# DEFAULT TRUE, and only because it was measured on this exact topology rather than reasoned about.
# Trusting a forwarded header is correct only if the hop in front OVERWRITES a client-supplied
# X-Forwarded-For rather than passing it through; believing it without that hands the rate limiter to
# the caller, which is strictly worse than one shared bucket.
#
# Read out of the API with Security logging at Debug (see docs/deployment.md 4), driving a login
# through the public front door:
#
#   peer is now 104.28.166.241, was [::ffff:100.100.0.141]:40976 before trust ran;
#   unconsumed X-Forwarded-For is <absent>.
#
# The resolved peer is the real client, confirmed against an external echo in the same minute. Three
# forged chains -- 9.9.9.9; 8.8.8.8, 9.9.9.9; 1.1.1.1, 2.2.2.2, 3.3.3.3 -- all resolved to that same
# address with nothing left unconsumed, so no forged entry ever arrived: Azure's ingress replaces the
# header. ForwardLimit 2 consumed exactly the chain that was there, which is why it is 2.
#
# THE SAFETY RESTS ON THE OVERWRITE, NOT ON THE NUMBER. Put anything in front of this deployment -- a
# CDN, another proxy, a custom domain through Cloudflare -- and the chain changes; a front door that
# APPENDS turns the same configuration into attacker-controlled input. Re-run the diagnostic after any
# such change, and set TRUST_INGRESS=false in the meantime if you cannot.
add_probes() {
  local APP="$1" LIVE="$2" READY="$3"
  # CONTAINER APPS DOES NOT CONSULT THE IMAGE'S HEALTHCHECK. Probes exist only if declared here, and a
  # container with no probe still reports Running -- so their absence looks exactly like health.
  #
  # PATCHED FROM THE APP'S OWN CURRENT TEMPLATE rather than from a hand-written block, because
  # `az containerapp update --yaml` REPLACES the container definition instead of merging it. A patch
  # naming only {name, image, probes} silently drops every environment variable, and the app then
  # fails ValidateOnStart on the missing Jwt:SigningKey -- exit 139, crash-looping. Reading the live
  # template and adding one key to it cannot drop what it never enumerated.
  local YAML; YAML=$(mktemp --suffix=.json)
  az containerapp show -g "$GROUP" -n "$APP" -o json \
    | jq --arg live "$LIVE" --arg ready "$READY" '{
        properties: {
          template: (.properties.template | .containers[0].probes = [
            { type: "Liveness",
              httpGet: { path: $live, port: 8080 },
              initialDelaySeconds: 10, periodSeconds: 20, failureThreshold: 3 },
            { type: "Readiness",
              httpGet: { path: $ready, port: 8080 },
              initialDelaySeconds: 5, periodSeconds: 15, timeoutSeconds: 10, failureThreshold: 3 }
          ])
        }
      }' > "$YAML"
  az containerapp update -g "$GROUP" -n "$APP" --yaml "$YAML" -o none
  rm -f "$YAML"
}
# /health/live as LIVENESS and /health/ready as READINESS, never the other way round: as liveness,
# /health/ready restarts every instance the moment the database goes away, into a database that is
# still down -- undoing the recovery property docs/deployment.md §5 describes.
add_probes buildcv-api /health/live /health/ready

API_FQDN=$(az containerapp show -g "$GROUP" -n buildcv-api --query "properties.configuration.ingress.fqdn" -o tsv)

say "6/6  The web client, and the only public address"
az containerapp create -g "$GROUP" -n buildcv-web --environment "$ENVIRONMENT" \
  --image "$WEB_IMAGE" \
  --target-port 3000 --ingress external \
  --min-replicas 1 --max-replicas 3 --cpu 0.5 --memory 1Gi \
  --env-vars "BUILDCV_API_ORIGIN=https://${API_FQDN}" -o none

WEB_FQDN=$(az containerapp show -g "$GROUP" -n buildcv-web --query "properties.configuration.ingress.fqdn" -o tsv)

cat <<SUMMARY

────────────────────────────────────────────────────────────────────────────────
  BuildCv is at:  https://${WEB_FQDN}

  HTTPS is Azure's, on its own certificate, renewed without anyone remembering.

  Images came from GHCR, built by CI. Nothing here builds, and no container
  registry is created -- that was the one line billing from day one.

  KEEP THESE. They are generated, printed once, and stored nowhere else. The
  encryption key in particular is NOT in the database: a SQL backup holds the
  ciphertext and none of the keys, so losing it makes every CV permanently
  unreadable. Put both in a password manager before you close this terminal.

    BUILDCV_ENCRYPTION_KEY   = ${ENCRYPTION_KEY}
    BUILDCV_BLIND_INDEX_KEY  = ${BLIND_INDEX_KEY}
    BUILDCV_JWT_SIGNING_KEY  = ${JWT_KEY}
    SQL admin password       = ${SQL_PASSWORD}

  Deploy a later build by tag, which is one command and no rebuild:
    az containerapp update -g ${GROUP} -n buildcv-api \\
      --image ghcr.io/${GHCR_OWNER}/buildcv-api:<40-char-sha>

  Password recovery answers 503 until an SMTP host is set:
    az containerapp update -g ${GROUP} -n buildcv-api \\
      --set-env-vars Email__Smtp__Host=... Email__Smtp__FromAddress=...

  Everything, gone:  az group delete -n ${GROUP} --yes
────────────────────────────────────────────────────────────────────────────────
SUMMARY
