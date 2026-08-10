#!/usr/bin/env bash
#
# Takes a backup, proves SQL Server can read it back, prunes old ones, sleeps, repeats.
#
# THE MECHANISM IS NOT THE POLICY. How often and how long to keep are decisions this repository has no
# business making, so they are BACKUP_INTERVAL_HOURS and BACKUP_RETENTION_DAYS and nothing here defaults
# to a number anybody should trust without thinking. What is not a decision is whether a backup happens
# at all, or whether anyone ever checks that it is readable, and those are what this closes.
#
# It exists for the case where SQL Server runs in the bundled container. If you are on a managed
# instance -- which docs/deployment.md recommends -- the platform does this better, with point-in-time
# recovery this cannot offer, and you should not run this service at all.

set -euo pipefail

: "${MSSQL_SA_PASSWORD:?MSSQL_SA_PASSWORD is required}"

SERVER="${BACKUP_SERVER:-sqlserver}"
DATABASE="${BACKUP_DATABASE:-BuildCv}"
DIRECTORY="${BACKUP_DIRECTORY:-/backups}"
INTERVAL_HOURS="${BACKUP_INTERVAL_HOURS:-24}"
RETENTION_DAYS="${BACKUP_RETENTION_DAYS:-14}"
# A FAILURE MUST NOT COST A WHOLE INTERVAL. The first version slept the full BACKUP_INTERVAL_HOURS after
# a failed backup, so a transient problem on a 24-hour schedule meant no backup for a day -- and the
# most likely transient problem is the one measured here: the database not existing yet on a fresh
# deployment. Compose now orders this after the migrator, and this is the belt to that braces.
RETRY_MINUTES="${BACKUP_RETRY_MINUTES:-10}"
USER_NAME="${BACKUP_USER:-sa}"
PASSWORD="${BACKUP_PASSWORD:-$MSSQL_SA_PASSWORD}"

SQLCMD=/opt/mssql-tools18/bin/sqlcmd
# -b so a failed backup is a failed command. Without it sqlcmd exits 0 on a failed batch and this loop
# would report success forever while writing nothing -- the failure mode that makes a backup schedule
# worse than none, because somebody stops worrying.
readonly BASE=("-S" "$SERVER" "-U" "$USER_NAME" "-P" "$PASSWORD" "-C" "-b" "-l" "5")

# NOT created here: this container does not write the file. `BACKUP DATABASE ... TO DISK` writes on the
# SERVER, and $DIRECTORY is a path in the sqlserver container's filesystem. sqlcmd only sends the
# statement -- which is why the directory is mounted there and not here.

echo "backup: every ${INTERVAL_HOURS}h, keeping ${RETENTION_DAYS} days, into ${DIRECTORY}"
echo "backup: REMINDER -- a dump is half a recovery. The encryption key ring lives in configuration,"
echo "backup: not in this file, and a restore without it returns every row and no readable one."

while true; do
  stamp=$(date -u +%Y%m%dT%H%M%SZ)
  target="${DIRECTORY}/${DATABASE}-${stamp}.bak"

  # CHECKSUM makes SQL Server compute page checksums as it writes, which is what gives VERIFYONLY below
  # something to check. COMPRESSION because these are mostly text and it costs almost nothing.
  if ! failure=$("$SQLCMD" "${BASE[@]}" -Q \
      "BACKUP DATABASE [$DATABASE] TO DISK='$target' WITH FORMAT, INIT, COMPRESSION, CHECKSUM;" 2>&1); then
    printf '%s\n' "$failure" >&2
    echo "backup: FAILED to write $target -- retrying in ${RETRY_MINUTES}m" >&2

    # Error 5 is the one somebody will hit first, and its message names a device rather than a
    # permission. The bind mount belongs to the host user; SQL Server runs as uid 10001. Saying the
    # command is worth more than saying the error.
    if printf '%s' "$failure" | grep -q "Operating system error 5"; then
      echo "backup: the directory mounted at $DIRECTORY is not writable by SQL Server (uid 10001)." >&2
      echo "backup: fix it with Docker rather than host sudo, which may not be available:" >&2
      echo "backup:   docker run --rm -v \"\$(pwd)/backups:/b\" alpine chown 10001:10001 /b" >&2
    fi
    sleep "$(( RETRY_MINUTES * 60 ))"
    continue
  fi

  # A BACKUP NOBODY CAN READ IS NOT A BACKUP, and the only moment it is cheap to find out is now.
  # VERIFYONLY re-reads the file and checks the checksums; it does not prove the data is correct, but it
  # does prove the file is not truncated or corrupt, which is the failure that otherwise waits until the
  # day somebody needs it.
  if ! "$SQLCMD" "${BASE[@]}" -Q "RESTORE VERIFYONLY FROM DISK='$target' WITH CHECKSUM;"; then
    echo "backup: WROTE BUT COULD NOT VERIFY $target -- keeping it and reporting, rather than deleting" >&2
  else
    echo "backup: $target verified"
  fi

  # Pruned AFTER a successful write, never before: a prune that runs first turns a backup failure into a
  # retention window that quietly shrinks to nothing.
  find "$DIRECTORY" -name "${DATABASE}-*.bak" -type f -mtime "+${RETENTION_DAYS}" -print -delete \
    | sed 's/^/backup: pruned /' || true

  sleep "$(( INTERVAL_HOURS * 3600 ))"
done
