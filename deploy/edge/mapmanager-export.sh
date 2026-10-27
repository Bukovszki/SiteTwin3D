#!/usr/bin/env bash
# MapManager move, step 1 – on the OLD server: stops editing, takes a full backup with MapManager's own backup.sh
# (database dump + geoserver_data, uploads, dpkeys volumes) and packs it with the .env into one file to copy over.
#   MM_DIR=/opt/mapmanager/deploy ./mapmanager-export.sh
# The old MapManager stays stopped (api, web, geoserver), so nothing changes after the backup; to start it again:
#   cd $MM_DIR && docker compose up -d
set -euo pipefail

MM_DIR=${MM_DIR:-/opt/mapmanager/deploy}
OUT=${OUT:-$HOME}
cd "$MM_DIR"
[[ -f .env && -x backup.sh ]] || { echo "Nem MapManager deploy mappa: $MM_DIR (nincs .env vagy backup.sh)"; exit 1; }

echo "== 1/4 A régi MapManager leállítása (az adatbázis fut tovább a mentéshez)"
docker compose stop api web geoserver

echo "== 2/4 Mentés a MapManager saját backup.sh-jával"
# A separate folder and a high KEEP: the regular backups are not pruned by this run.
KEEP=1000 ./backup.sh ./migration
BACKUP=$(find ./migration -mindepth 1 -maxdepth 1 -type d -name '[0-9]*-[0-9]*' | sort | tail -n 1)
[[ -s "$BACKUP/gisdb.dump" ]] || { echo "A mentés nem sikerült: hiányzik a $BACKUP/gisdb.dump"; exit 1; }

echo "== 3/4 Beállítások és verziók a mentés mellé"
# The new server needs the same GeoServer and database passwords (they are inside the GeoServer data directory).
cp .env "$BACKUP/mapmanager.env"
chmod 600 "$BACKUP/mapmanager.env"
{
  echo "MapManager commit: $(git -C .. rev-parse --short HEAD 2>/dev/null || echo ismeretlen)"
  echo "Képek:"
  docker compose images 2>/dev/null | sed 's/^/  /'
} > "$BACKUP/versions.txt"

echo "== 4/4 Egy fájlba csomagolás"
NAME="mapmanager-migration-$(basename "$BACKUP").tar"
tar -C "$(dirname "$BACKUP")" -cf "$OUT/$NAME" "$(basename "$BACKUP")"
chmod 600 "$OUT/$NAME"
(cd "$OUT" && sha256sum "$NAME" > "$NAME.sha256")

ls -lh "$OUT/$NAME"
cat "$BACKUP/versions.txt"
cat <<EOF

Kész. A csomag: $OUT/$NAME  (ellenőrzőösszeg: $OUT/$NAME.sha256)
Jelszavakat tartalmaz (.env) – csak titkosított csatornán (scp) másold, és a végén töröld.

Következő lépés – másolás az új szerverre, pl. a saját gépedről:
  scp regi-szerver:$OUT/$NAME regi-szerver:$OUT/$NAME.sha256 .
  scp $NAME $NAME.sha256 deploy@uj-szerver:~/

A régi MapManager leállítva marad. Ha mégis kell:  cd $MM_DIR && docker compose up -d
EOF
