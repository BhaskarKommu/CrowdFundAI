#!/usr/bin/env bash
# Restore SQL Server database from .bak
# Usage:
#   export SQLCMDSERVER=localhost,1433 SQLCMDUSER=sa SQLCMDPASSWORD='...' 
#   export DB_NAME=CrowdFundAI BAK_PATH=/path/to/file.bak
#   ./scripts/restore-sql.sh

set -euo pipefail
: "${SQLCMDSERVER:=localhost,1433}"
: "${SQLCMDUSER:=sa}"
: "${DB_NAME:=CrowdFundAI}"
if [[ -z "${SQLCMDPASSWORD:-}" || -z "${BAK_PATH:-}" ]]; then
  echo "SQLCMDPASSWORD and BAK_PATH are required" >&2
  exit 1
fi

sqlcmd -S "$SQLCMDSERVER" -U "$SQLCMDUSER" -P "$SQLCMDPASSWORD" -C -Q \
  "ALTER DATABASE [$DB_NAME] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
   RESTORE DATABASE [$DB_NAME] FROM DISK = N'$BAK_PATH' WITH REPLACE;
   ALTER DATABASE [$DB_NAME] SET MULTI_USER;"

echo "Restored $DB_NAME from $BAK_PATH"
