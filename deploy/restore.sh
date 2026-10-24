#!/usr/bin/env bash
# Restore (spec M9) from backup.sh: replaces the database with a dump and copies the backed-up objects back.
#   ./restore.sh backups/db/appdb-YYYYMMDD-HHMMSS.dump [--yes]
# The API is stopped meanwhile; users sign in again afterwards. Objects are copied back without deleting anything:
# files newer than the dump become orphans and are removed by the API's cleanup after its grace period.
set -euo pipefail
cd "$(dirname "$0")"
set -a; source .env; set +a
COMPOSE=${COMPOSE:-docker compose}
ENGINE=${ENGINE:-docker}
PROJECT=${COMPOSE_PROJECT_NAME:-twin}
DIR=${BACKUP_DIR:-./backups}
RCLONE_IMAGE=${RCLONE_IMAGE:-docker.io/rclone/rclone:1.68.2}
dump=${1:?Usage: ./restore.sh <dump file> [--yes]}
[ -f "$dump" ] || { echo "No such dump: $dump" >&2; exit 1; }
if [ "${2:-}" != "--yes" ]; then
  read -r -p "The database will be REPLACED by $dump. Continue? [y/N] " answer
  [ "$answer" = "y" ] || exit 1
fi
user=${POSTGRES_USER:-postgres}
psql=(exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" db psql -v ON_ERROR_STOP=1 -h localhost -U "$user" -d postgres -q)

echo "• stopping the API"
$COMPOSE stop api
echo "• database ← $dump"
$COMPOSE "${psql[@]}" -c "DROP DATABASE IF EXISTS appdb WITH (FORCE)" -c "CREATE DATABASE appdb"
$COMPOSE exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" db pg_restore -h localhost -U "$user" -d appdb --no-owner --exit-on-error < "$dump"

echo "• object store ← $DIR/objects"
target=$(cd "$DIR" && pwd)
export RCLONE_CONFIG_STORE_TYPE=s3 RCLONE_CONFIG_STORE_PROVIDER=Other RCLONE_CONFIG_STORE_ENDPOINT=http://storage:8333 \
  RCLONE_CONFIG_STORE_ACCESS_KEY_ID="$STORAGE_ACCESS_KEY" RCLONE_CONFIG_STORE_SECRET_ACCESS_KEY="$STORAGE_SECRET_KEY"
$ENGINE run --rm --network "${PROJECT}_backend" -v "$target/objects:/backup:z" \
  -e RCLONE_CONFIG_STORE_TYPE -e RCLONE_CONFIG_STORE_PROVIDER -e RCLONE_CONFIG_STORE_ENDPOINT \
  -e RCLONE_CONFIG_STORE_ACCESS_KEY_ID -e RCLONE_CONFIG_STORE_SECRET_ACCESS_KEY \
  "$RCLONE_IMAGE" copy /backup "store:${STORAGE_BUCKET:-objects}" --transfers 8 --stats-one-line --stats 0

echo "• starting the API (it applies newer migrations, if any)"
$COMPOSE start api
echo "done"
