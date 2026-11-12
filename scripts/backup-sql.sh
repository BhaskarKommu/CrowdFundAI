#!/usr/bin/env bash
# Native SQL Server full backup. Run on a host that can reach SQL Server with BACKUP rights.
# Usage:
#   export SQLCMDSERVER=localhost,1433
#   export SQLCMDUSER=sa
#   export SQLCMDPASSWORD='Your_strong_Password123'
#   export DB_NAME=CrowdFundAI
#   export BACKUP_DIR=/var/backups/crowdfund
#   ./scripts/backup-sql.sh

set -euo pipefail
: "${SQLCMDSERVER:=localhost,1433}"
: "${SQLCMDUSER:=sa}"
: "${DB_NAME:=CrowdFundAI}"
: "${BACKUP_DIR:=./App_Data/backups}"
if [[ -z "${SQLCMDPASSWORD:-}" ]]; then
  echo "SQLCMDPASSWORD is required" >&2
  exit 1
fi

mkdir -p "$BACKUP_DIR"
STAMP=$(date -u +%Y%m%d_%H%M%S)
BAK_PATH="$BACKUP_DIR/${DB_NAME}_${STAMP}.bak"

# Note: path must be visible to the SQL Server process (container volume or host path).
sqlcmd -S "$SQLCMDSERVER" -U "$SQLCMDUSER" -P "$SQLCMDPASSWORD" -C -Q \
  "BACKUP DATABASE [$DB_NAME] TO DISK = N'$BAK_PATH' WITH INIT, COMPRESSION, STATS = 10"

echo "Created $BAK_PATH"
