#!/usr/bin/env bash
#
# Applies the schema, once, before the API starts. Run as a one-shot compose service.
#
# This exists because Program.cs deliberately refuses to do it: auto-migration is gated on
# IsDevelopment(), and the container sets ASPNETCORE_ENVIRONMENT=Production. That gate is correct --
# the process serving traffic should not own the schema, and it would re-run once per instance -- so
# the fix is the separate step that comment asks for, not a weaker gate. Flipping the container to
# Development would also re-open the in-memory persistence branch, which the Dockerfile closes on
# purpose.
#
# What it applies is an idempotent script generated at image build time (`dotnet ef migrations script
# --idempotent`), so it is a reviewable artifact rather than a migration the app invents at boot, and
# re-running it on every `up` is a no-op.

set -euo pipefail

SERVER="${MIGRATION_SERVER:-sqlserver}"
DATABASE="${MIGRATION_DATABASE:-BuildCv}"
SCRIPT="${MIGRATION_SCRIPT:-/migrations/BuildCv.sql}"

# The login is configurable, and it has to be. `sa` exists on the compose container and on nothing else:
# a managed instance (Azure SQL, RDS) has no `sa` a customer can use, so hardcoding it made this
# container work in exactly the environment that does not need it and fail in the one that does.
#
# MIGRATION_PASSWORD falls back to MSSQL_SA_PASSWORD so the compose file keeps working unchanged --
# there, the two ARE the same credential.
USER_NAME="${MIGRATION_USER:-sa}"
PASSWORD="${MIGRATION_PASSWORD:-${MSSQL_SA_PASSWORD:-}}"
if [ -z "$PASSWORD" ]; then
  echo "migrate: set MIGRATION_PASSWORD (or MSSQL_SA_PASSWORD for the bundled SQL Server)" >&2
  exit 1
fi

SQLCMD=/opt/mssql-tools18/bin/sqlcmd

# -C trusts the server certificate, which is self-signed inside the compose network.
# -b is LOAD-BEARING: without it sqlcmd exits 0 on a failed batch, so a migration that died half way
#    would still satisfy `service_completed_successfully` and the API would start against a schema
#    with some of its tables. Measured -- the first attempt here stopped after one table and would
#    have reported success.
# -l 5 bounds the LOGIN TIMEOUT, and it is what makes the retry loop below finite. Without it an
# unresolvable host blocks each attempt in DNS for ~15s, so a typo in MIGRATION_SERVER hung for over
# eight minutes -- measured -- and `docker compose up` waited on all of it. In a deploy pipeline that
# reads as a hang rather than as a failure, which is the worst way for a wrong hostname to present.
readonly BASE=("-S" "$SERVER" "-U" "$USER_NAME" "-P" "$PASSWORD" "-C" "-b" "-l" "5")

# Compose already gates this on the server's healthcheck, but a server that answers SELECT 1 can
# still refuse the next connection while it finishes recovery, and that would fail the whole `up`.
probe=""
for attempt in $(seq 1 12); do
  if probe=$("$SQLCMD" "${BASE[@]}" -Q "SELECT 1" 2>&1); then
    break
  fi

  # A REJECTED LOGIN IS NOT A SLOW START, and waiting sixty seconds before saying so sends whoever
  # typed the password wrong looking at the network instead. SQL Server answers 18456 the moment it
  # is up, so there is nothing to wait for -- give up immediately and quote what it actually said.
  case "$probe" in
    *"Login failed"*|*"Cannot open database"*)
      echo "migrate: $SERVER rejected the login for user '$USER_NAME'" >&2
      echo "$probe" >&2
      exit 1
      ;;
  esac

  if [ "$attempt" -eq 12 ]; then
    echo "migrate: $SERVER did not accept a connection after 12 attempts" >&2
    # The last error, verbatim. The generic sentence above is true of a server that never came up and
    # of half a dozen other causes; this line is the one that names which.
    echo "$probe" >&2
    exit 1
  fi
  sleep 2
done

# An EF script contains no CREATE DATABASE -- Database.Migrate() creates it in code, and generating a
# script skips that. Without this, every statement below fails with error 4060, "Cannot open database
# requested by the login", which reads like a credentials problem and is not one.
# A managed instance usually forbids CREATE DATABASE from an application login, and the database is
# provisioned by the platform instead. MIGRATION_CREATE_DATABASE=false skips this step; the script
# below is what actually matters and runs either way.
if [ "${MIGRATION_CREATE_DATABASE:-true}" = "true" ]; then
echo "migrate: ensuring database [$DATABASE] exists"
"$SQLCMD" "${BASE[@]}" -Q "IF DB_ID('$DATABASE') IS NULL CREATE DATABASE [$DATABASE];"
else
  echo "migrate: skipping CREATE DATABASE (MIGRATION_CREATE_DATABASE=false)"
fi

# -I sets QUOTED_IDENTIFIER ON, and it is REQUIRED here rather than stylistic. sqlcmd defaults it OFF,
# and this schema is full of filtered indexes -- the unique-when-not-deleted indexes on EmailHash and
# TokenHash, and the soft-delete filters on every aggregate root. SQL Server refuses to create a
# filtered index under QUOTED_IDENTIFIER OFF:
#
#   Msg 1934: CREATE INDEX failed because the following SET options have incorrect settings:
#   'QUOTED_IDENTIFIER'.
#
# Measured. Without -I the run dies at the first filtered index, which is early enough that almost
# nothing is created and late enough that the database exists.
echo "migrate: applying $SCRIPT to [$DATABASE]"
"$SQLCMD" "${BASE[@]}" -I -d "$DATABASE" -i "$SCRIPT"

echo "migrate: done"
