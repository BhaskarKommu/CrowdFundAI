#!/usr/bin/env bash
# Restore file store from a crowdfund-backup-*.zip produced by BackupService
# Usage: ./scripts/restore-files.sh /path/to/crowdfund-backup-YYYYMMDD_HHMMSS.zip ./App_Data/uploads

set -euo pipefail
ZIP="${1:?zip path required}"
DEST="${2:-./App_Data/uploads}"
TMP=$(mktemp -d)
unzip -q "$ZIP" -d "$TMP"
mkdir -p "$DEST"
if [[ -d "$TMP/files" ]]; then
  cp -a "$TMP/files/." "$DEST/"
  echo "Restored files into $DEST"
else
  echo "No files/ folder in archive" >&2
  exit 1
fi
rm -rf "$TMP"
