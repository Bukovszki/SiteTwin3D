#!/usr/bin/env bash
# Backup (spec M9): database dump + incremental copy of the object store, run from deploy/ on the server (e.g. cron).
#   ./backup.sh
# Database: pg_dump custom format, the newest BACKUP_KEEP dumps are kept (BACKUP_DIR/db).
# Objects: rclone COPY to BACKUP_DIR/objects – new files are added, nothing is ever deleted there, so every kept dump
# finds the files it refers to. (Files deleted since then are cleaned up again by the API after a restore.)
# Podman: COMPOSE="docker-compose" ENGINE=podman ./backup.sh ; local stack: add -f docker-compose.local.yml to COMPOSE.
set -euo pipefail
cd "$(dirname "$0")"
set -a; source .env; set +a
COMPOSE=${COMPOSE:-docker compose}
ENGINE=${ENGINE:-docker}
PROJECT=${COMPOSE_PROJECT_NAME:-twin}
DIR=${BACKUP_DIR:-./backups}
KEEP=${BACKUP_KEEP:-14}
RCLONE_IMAGE=${RCLONE_IMAGE:-docker.io/rclone/rclone:1.68.2}
stamp=$(date -u +%Y%m%d-%H%M%S)

mkdir -p "$DIR/db" "$DIR/objects"
target=$(cd "$DIR" && pwd)

echo "• database → $DIR/db/appdb-$stamp.dump"
$COMPOSE exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" db \
  pg_dump -h localhost -U "${POSTGRES_USER:-postgres}" -Fc appdb > "$target/db/appdb-$stamp.dump.part"
mv "$target/db/appdb-$stamp.dump.part" "$target/db/appdb-$stamp.dump"

echo "• object store → $DIR/objects (incremental)"
# The keys go in as environment variables, not on the command line (ps).
export RCLONE_CONFIG_STORE_TYPE=s3 RCLONE_CONFIG_STORE_PROVIDER=Other RCLONE_CONFIG_STORE_ENDPOINT=http://storage:8333 \
  RCLONE_CONFIG_STORE_ACCESS_KEY_ID="$STORAGE_ACCESS_KEY" RCLONE_CONFIG_STORE_SECRET_ACCESS_KEY="$STORAGE_SECRET_KEY"
$ENGINE run --rm --network "${PROJECT}_backend" -v "$target/objects:/backup:z" \
  -e RCLONE_CONFIG_STORE_TYPE -e RCLONE_CONFIG_STORE_PROVIDER -e RCLONE_CONFIG_STORE_ENDPOINT \
  -e RCLONE_CONFIG_STORE_ACCESS_KEY_ID -e RCLONE_CONFIG_STORE_SECRET_ACCESS_KEY \
  "$RCLONE_IMAGE" copy "store:${STORAGE_BUCKET:-objects}" /backup --transfers 8 --stats-one-line --stats 0

echo "• keeping the newest $KEEP dumps"
ls -1t "$target"/db/appdb-*.dump | tail -n +$((KEEP + 1)) | while read -r old; do rm -- "$old"; done
echo "done: $(du -sh "$target/db/appdb-$stamp.dump" | cut -f1) dump, $(du -sh "$target/objects" | cut -f1) objects"
