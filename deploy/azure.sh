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
# WHAT IT COSTS, honestly. Container Apps and Azure SQL both have real free grants that this fits inside
# at portfolio traffic. The container registry does not: Basic is about USD 5/month and is the only line
# that bills from day one.

set -euo pipefail

# ── Knobs ────────────────────────────────────────────────────────────────────────────────────────
LOCATION="${AZ_LOCATION:-eastus}"
GROUP="${AZ_GROUP:-buildcv-rg}"
# Registry names are globally unique and alphanumeric-only, so a suffix is not optional.
REGISTRY="${AZ_REGISTRY:-buildcvacr$RANDOM}"
SQL_SERVER="${AZ_SQL_SERVER:-buildcv-sql-$RANDOM}"
SQL_DB="${AZ_SQL_DB:-BuildCv}"
SQL_ADMIN="${AZ_SQL_ADMIN:-buildcvadmin}"
ENVIRONMENT="${AZ_ENVIRONMENT:-buildcv-env}"
WEB_CONTEXT="${AZ_WEB_CONTEXT:-../buildcv-web}"

say() { printf '\n\033[1m%s\033[0m\n' "$*"; }

# ── Preconditions, checked before anything is created ────────────────────────────────────────────
command -v az >/dev/null || { echo "az is not installed." >&2; exit 1; }
az account show >/dev/null 2>&1 || { echo "Run 'az login' first." >&2; exit 1; }
[ -d "$WEB_CONTEXT" ] || { echo "The web client is not at $WEB_CONTEXT. Set AZ_WEB_CONTEXT." >&2; exit 1; }

# Secrets are GENERATED, never defaulted. Every one of these is a value that must not come from a file
# anybody can read, and the app refuses to start without them rather than inventing one.
SQL_PASSWORD="${AZ_SQL_PASSWORD:-$(openssl rand -base64 24)Aa1!}"
JWT_KEY="${BUILDCV_JWT_SIGNING_KEY:-$(openssl rand -base64 48)}"
ENCRYPTION_KEY="${BUILDCV_ENCRYPTION_KEY:-$(openssl rand -base64 32)}"
BLIND_INDEX_KEY="${BUILDCV_BLIND_INDEX_KEY:-$(openssl rand -base64 32)}"

say "1/7  Resource group $GROUP in $LOCATION"
az group create -n "$GROUP" -l "$LOCATION" -o none

say "2/7  Container registry $REGISTRY"
az acr create -g "$GROUP" -n "$REGISTRY" --sku Basic --admin-enabled true -o none

say "3/7  Building both images IN Azure (no local Docker push)"
# az acr build uploads the context and builds server-side, which avoids pushing gigabytes over a home
# connection and means the build runs on the same architecture the apps run on.
az acr build -r "$REGISTRY" -t "buildcv-api:latest" -f Dockerfile --target runtime . -o none
az acr build -r "$REGISTRY" -t "buildcv-migrator:latest" -f Dockerfile --target migrator . -o none
az acr build -r "$REGISTRY" -t "buildcv-web:latest" "$WEB_CONTEXT" -o none

say "4/7  Azure SQL $SQL_SERVER/$SQL_DB"
az sql server create -g "$GROUP" -n "$SQL_SERVER" -l "$LOCATION" \
  -u "$SQL_ADMIN" -p "$SQL_PASSWORD" -o none
# Azure services only. There is no public client for this database -- the API reaches it from inside
# the Container Apps environment, and nothing else has any business connecting.
az sql server firewall-rule create -g "$GROUP" -s "$SQL_SERVER" \
  -n AllowAzureServices --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0 -o none
# The free serverless grant: 100k vCore-seconds and 32 GB a month. --use-free-limit is what claims it,
# and BilledOverUsage means it keeps serving if the grant runs out rather than pausing the database
# under a candidate mid-import.
az sql db create -g "$GROUP" -s "$SQL_SERVER" -n "$SQL_DB" \
  --edition GeneralPurpose --compute-model Serverless --family Gen5 --capacity 1 \
  --use-free-limit --free-limit-exhaustion-behavior BilledOverUsage -o none

CONNECTION="Server=tcp:${SQL_SERVER}.database.windows.net,1433;Database=${SQL_DB};User ID=${SQL_ADMIN};Password=${SQL_PASSWORD};Encrypt=True;TrustServerCertificate=False;Connect Timeout=5"

say "5/7  Container Apps environment $ENVIRONMENT"
az extension add --name containerapp --upgrade --only-show-errors -o none
az containerapp env create -g "$GROUP" -n "$ENVIRONMENT" -l "$LOCATION" -o none

REGISTRY_SERVER="${REGISTRY}.azurecr.io"
REGISTRY_PASSWORD=$(az acr credential show -n "$REGISTRY" --query "passwords[0].value" -o tsv)

say "6/7  Migration job, then the API"
# A JOB, not an app: it runs once and exits, which is exactly what the compose migrator does and
# exactly what Program.cs refuses to do inside the process that serves traffic.
az containerapp job create -g "$GROUP" -n buildcv-migrator --environment "$ENVIRONMENT" \
  --trigger-type Manual --replica-timeout 600 --replica-retry-limit 1 \
  --image "$REGISTRY_SERVER/buildcv-migrator:latest" \
  --registry-server "$REGISTRY_SERVER" --registry-username "$REGISTRY" --registry-password "$REGISTRY_PASSWORD" \
  --secrets "sqlpass=$SQL_PASSWORD" \
  --env-vars "MIGRATION_SERVER=${SQL_SERVER}.database.windows.net" \
             "MIGRATION_DATABASE=$SQL_DB" \
             "MIGRATION_USER=$SQL_ADMIN" \
             "MIGRATION_PASSWORD=secretref:sqlpass" \
             "MIGRATION_CREATE_DATABASE=false" -o none

az containerapp job start -g "$GROUP" -n buildcv-migrator -o none
echo "  migration job started; it must finish before the API is useful"

# INTERNAL ingress. The API is reachable from the web app and from nothing else, which is the same
# topology the compose file enforces by publishing no port for it.
az containerapp create -g "$GROUP" -n buildcv-api --environment "$ENVIRONMENT" \
  --image "$REGISTRY_SERVER/buildcv-api:latest" \
  --registry-server "$REGISTRY_SERVER" --registry-username "$REGISTRY" --registry-password "$REGISTRY_PASSWORD" \
  --target-port 8080 --ingress internal \
  --min-replicas 1 --max-replicas 3 --cpu 0.5 --memory 1Gi \
  --secrets "conn=$CONNECTION" "jwt=$JWT_KEY" "enc=$ENCRYPTION_KEY" "blind=$BLIND_INDEX_KEY" \
  --env-vars "ConnectionStrings__BuildCv=secretref:conn" \
             "Jwt__SigningKey=secretref:jwt" \
             "Encryption__ActiveKeyId=v1" \
             "Encryption__Keys__v1__Aes=secretref:enc" \
             "Encryption__BlindIndex__ActiveKeyId=b1" \
             "Encryption__BlindIndex__Keys__b1=secretref:blind" \
             "ASPNETCORE_ENVIRONMENT=Production" -o none

API_FQDN=$(az containerapp show -g "$GROUP" -n buildcv-api --query "properties.configuration.ingress.fqdn" -o tsv)

say "7/7  The web client, and the only public address"
az containerapp create -g "$GROUP" -n buildcv-web --environment "$ENVIRONMENT" \
  --image "$REGISTRY_SERVER/buildcv-web:latest" \
  --registry-server "$REGISTRY_SERVER" --registry-username "$REGISTRY" --registry-password "$REGISTRY_PASSWORD" \
  --target-port 3000 --ingress external \
  --min-replicas 1 --max-replicas 3 --cpu 0.5 --memory 1Gi \
  --env-vars "BUILDCV_API_ORIGIN=https://${API_FQDN}" -o none

WEB_FQDN=$(az containerapp show -g "$GROUP" -n buildcv-web --query "properties.configuration.ingress.fqdn" -o tsv)

cat <<SUMMARY

────────────────────────────────────────────────────────────────────────────────
  BuildCv is at:  https://${WEB_FQDN}

  HTTPS is Azure's, on its own certificate, renewed without anyone remembering.

  KEEP THESE. They are generated, printed once, and stored nowhere else. The
  encryption key in particular is NOT in the database: a SQL backup holds the
  ciphertext and none of the keys, so losing it makes every CV permanently
  unreadable. Put both in a password manager before you close this terminal.

    BUILDCV_ENCRYPTION_KEY   = ${ENCRYPTION_KEY}
    BUILDCV_BLIND_INDEX_KEY  = ${BLIND_INDEX_KEY}
    BUILDCV_JWT_SIGNING_KEY  = ${JWT_KEY}
    SQL admin password       = ${SQL_PASSWORD}

  Password recovery answers 503 until an SMTP host is set:
    az containerapp update -g ${GROUP} -n buildcv-api \\
      --set-env-vars Email__Smtp__Host=... Email__Smtp__FromAddress=...

  Everything, gone:  az group delete -n ${GROUP} --yes
────────────────────────────────────────────────────────────────────────────────
SUMMARY
