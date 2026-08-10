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

: "${MSSQL_SA_PASSWORD:?MSSQL_SA_PASSWORD is required}"

SERVER="${MIGRATION_SERVER:-sqlserver}"
DATABASE="${MIGRATION_DATABASE:-BuildCv}"
SCRIPT="${MIGRATION_SCRIPT:-/migrations/BuildCv.sql}"
SQLCMD=/opt/mssql-tools18/bin/sqlcmd

# -C trusts the server certificate, which is self-signed inside the compose network.
# -b is LOAD-BEARING: without it sqlcmd exits 0 on a failed batch, so a migration that died half way
#    would still satisfy `service_completed_successfully` and the API would start against a schema
#    with some of its tables. Measured -- the first attempt here stopped after one table and would
#    have reported success.
readonly BASE=("-S" "$SERVER" "-U" "sa" "-P" "$MSSQL_SA_PASSWORD" "-C" "-b")

# Compose already gates this on the server's healthcheck, but a server that answers SELECT 1 can
# still refuse the next connection while it finishes recovery, and that would fail the whole `up`.
for attempt in $(seq 1 30); do
  if "$SQLCMD" "${BASE[@]}" -Q "SELECT 1" >/dev/null 2>&1; then
    break
  fi
  if [ "$attempt" -eq 30 ]; then
    echo "migrate: $SERVER did not accept a connection after 30 attempts" >&2
    exit 1
  fi
  sleep 2
done

# An EF script contains no CREATE DATABASE -- Database.Migrate() creates it in code, and generating a
# script skips that. Without this, every statement below fails with error 4060, "Cannot open database
# requested by the login", which reads like a credentials problem and is not one.
echo "migrate: ensuring database [$DATABASE] exists"
"$SQLCMD" "${BASE[@]}" -Q "IF DB_ID('$DATABASE') IS NULL CREATE DATABASE [$DATABASE];"

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
