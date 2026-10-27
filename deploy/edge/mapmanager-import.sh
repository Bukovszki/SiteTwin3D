#!/usr/bin/env bash
# MapManager move, step 2 – on the NEW server: loads the package of mapmanager-export.sh into a MapManager installed
# per docs/DEPLOY.md 12.3 (repository cloned, .env filled in with the NEW domain).
#   MM_DIR=/opt/mapmanager/deploy ./mapmanager-import.sh ~/mapmanager-migration-<stamp>.tar
# Overwrites the new MapManager's database and GeoServer data with the old ones.
set -euo pipefail

PACKAGE=${1:?Használat: ./mapmanager-import.sh <mapmanager-migration-….tar>}
PACKAGE=$(realpath "$PACKAGE")
MM_DIR=${MM_DIR:-/opt/mapmanager/deploy}
WORK=${WORK:-$HOME/mapmanager-migration}
cd "$MM_DIR"
[[ -f .env ]] || { echo "Nincs .env a $MM_DIR mappában: előbb a docs/DEPLOY.md 12.3 szerint töltsd ki."; exit 1; }

echo "== 1/8 A csomag ellenőrzése és kibontása"
if [[ -f "$PACKAGE.sha256" ]]; then (cd "$(dirname "$PACKAGE")" && sha256sum -c "$(basename "$PACKAGE").sha256"); fi
mkdir -p "$WORK"
tar -C "$WORK" -xf "$PACKAGE"
STAMP=$(basename "$PACKAGE" .tar)
BACKUP="$WORK/${STAMP#mapmanager-migration-}"
for f in gisdb.dump geoserver_data.tar.gz uploads.tar.gz dpkeys.tar.gz mapmanager.env; do
  [[ -s "$BACKUP/$f" ]] || { echo "Hiányzik a csomagból: $f"; exit 1; }
done
cat "$BACKUP/versions.txt" 2>/dev/null || true
echo "Szabad hely:"; { df -h "$WORK" /var/lib/docker 2>/dev/null || true; } | sed 's/^/  /'

echo "== 2/8 Jelszavak átvétele a régi .env-ből"
# The GeoServer data directory holds the old GeoServer admin password and the stores' database password: the new
# .env must carry the same ones (the API uses them for the GeoServer REST API and sets the store user's password).
cp .env ".env.before-migration-$(date +%Y%m%d-%H%M%S)"
old() { { grep -E "^$1=" "$BACKUP/mapmanager.env" || true; } | tail -n 1 | cut -d= -f2-; }
for key in GEOSERVER_ADMIN_USER GEOSERVER_ADMIN_PASSWORD GEOSERVER_DB_PASSWORD GEOSERVER_IMAGE; do
  value=$(old "$key")
  [[ -n "$value" ]] || continue
  if grep -qE "^$key=" .env; then
    # Values may hold any character: written by awk, not sed.
    awk -v k="$key" -v v="$value" 'BEGIN { FS = OFS = "=" } $1 == k { print k "=" v; next } { print }' .env > .env.tmp && mv .env.tmp .env
  else
    echo "$key=$value" >> .env
  fi
  echo "  $key: a régi értéket kapta"
done
chmod 600 .env
if [[ "$(old POSTGRES_USER)" != "$({ grep -E '^POSTGRES_USER=' .env || true; } | tail -n 1 | cut -d= -f2-)" ]]; then
  echo "  Figyelem: a POSTGRES_USER eltér a régitől; a visszaállítás a mostani felhasználóval fut."
fi
# Read, not sourced: a password may hold characters the shell would interpret.
env_value() { { grep -E "^$1=" .env || true; } | tail -n 1 | cut -d= -f2-; }
PGUSER=$(env_value POSTGRES_USER); PGUSER=${PGUSER:-postgres}
POSTGRES_PASSWORD=$(env_value POSTGRES_PASSWORD)
DOMAIN=$(env_value DOMAIN)

echo "== 3/8 GeoServer Proxy Base URL (a régi szerver címe ne maradjon benne)"
PROXY=$(tar -xzf "$BACKUP/geoserver_data.tar.gz" -O ./global.xml 2>/dev/null | grep -o '<proxyBaseUrl>[^<]*' | sed 's/<proxyBaseUrl>//' || true)
if [[ -n "$PROXY" ]]; then
  echo "  A régi GeoServerben be van állítva: $PROXY"
  echo "  A végén írd át a GeoServer felületén (Settings → Global → Proxy Base URL) az új címre, vagy töröld."
else
  echo "  Nincs beállítva – rendben."
fi

echo "== 4/8 Konténerek és kötetek létrehozása, adatbázis indítása"
docker compose up --no-start
docker compose stop api web geoserver 2>/dev/null || true
docker compose up -d db
# A fresh database volume: the image initialises with a temporary server, then restarts it – a single "ready" answer
# may come from the temporary one. Wait for the container's health check, then for 10 s of steady answers.
DB=$(docker compose ps -q db)
for _ in $(seq 1 90); do
  [[ "$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$DB" 2>/dev/null)" == healthy ]] && break
  sleep 2
done
STEADY=0
for _ in $(seq 1 90); do
  if docker compose exec -T db pg_isready -h localhost -U "$PGUSER" -d gisdb >/dev/null 2>&1; then
    STEADY=$((STEADY + 1)); [[ $STEADY -ge 5 ]] && break
  else
    STEADY=0
  fi
  sleep 2
done
[[ $STEADY -ge 5 ]] || { echo "Az adatbázis 3 perc alatt sem állt fel:"; docker compose logs --tail 30 db; exit 1; }
echo "  Az adatbázis fut."

echo "== 5/8 A GeoServer adatbázis-szerepkörei (a pg_dump nem menti a szerepköröket)"
ROLES_SQL='DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '"'mapmanager_olvaso'"') THEN
    CREATE ROLE mapmanager_olvaso NOLOGIN;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '"'mapmanager_geoserver'"') THEN
    CREATE ROLE mapmanager_geoserver LOGIN IN ROLE mapmanager_olvaso;
  END IF;
END $$;'
for attempt in 1 2 3 4 5; do
  if echo "$ROLES_SQL" | docker compose exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" db \
      psql -h localhost -U "$PGUSER" -d gisdb -v ON_ERROR_STOP=1 -q; then
    echo "  Szerepkörök rendben."; break
  fi
  [[ $attempt -lt 5 ]] || { echo "A szerepkörök létrehozása nem sikerült."; exit 1; }
  echo "  Újrapróbálás 5 s múlva…"; sleep 5
done

echo "== 6/8 Adatbázis visszaállítása (néhány perc; ~1 GB az offline alaptérkép miatt)"
set +e
docker compose exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" db \
  pg_restore -h localhost -U "$PGUSER" -d gisdb --clean --if-exists < "$BACKUP/gisdb.dump" 2> "$WORK/pg_restore.log"
RESTORE=$?
set -e
ERRORS=$(grep -c '^pg_restore: error' "$WORK/pg_restore.log" || true)
echo "  pg_restore kilépési kód: $RESTORE, hibasorok: $ERRORS (napló: $WORK/pg_restore.log)"
if [[ "$ERRORS" -gt 0 ]]; then
  grep '^pg_restore: error' "$WORK/pg_restore.log" | head -n 10 | sed 's/^/    /'
  echo "  (A már létező kiterjesztésekről szóló hibák ártalmatlanok; más hibánál nézd meg a naplót.)"
fi

echo "== 7/8 GeoServer adatkönyvtár, feltöltések, süti-kulcsok"
for v in geoserver_data uploads dpkeys; do
  docker run --rm -v "mapmanager_$v:/v" -v "$BACKUP:/b:ro" alpine \
    sh -c "rm -rf /v/* /v/.[!.]* 2>/dev/null; tar xzf /b/$v.tar.gz -C /v"
  echo "  $v visszatöltve"
done

echo "== 8/8 Indítás (az API beállítja a GeoServer felhasználó jelszavát a .env-ből)"
docker compose up -d
STARTED=""
for _ in $(seq 1 36); do
  if docker compose logs --since 5m api 2>&1 | grep -q "Application started"; then STARTED=1; break; fi
  sleep 5
done
docker compose ps
if [[ -z "$STARTED" ]]; then
  echo "Az API 3 perc alatt nem indult el – napló:"; docker compose logs --tail 40 api; exit 1
fi
docker compose logs --since 5m api 2>&1 | grep -i "GeoServer olvasó felhasználó" | tail -n 1 || true

cat <<EOF

Kész. Ellenőrizd:
  1. https://${DOMAIN:-<új cím>} – belépés a régi felhasználókkal (mindenkinek újra be kell lépnie).
  2. Egy réteg megnyitása a térképen; a GeoServer naplójában nincs adatbázis-kapcsolati hiba:
       docker compose logs --since 10m geoserver | grep -iE "error|exception" | head
  3. Ha a 3. lépés Proxy Base URL-t jelzett: írd át az új címre.
  4. SiteTwin3D: ha a MapManager címe megváltozott, Platform → Szervezetek → a szervezet MapManager címe;
     a szolgáltatástoken a régi marad (az adatbázissal jött), ha új kell: deploy/api.env, majd az api újraindítása.
  5. Ha minden rendben: a kibontott mentés és a csomag törlése (jelszavak vannak benne):
       rm -rf "$WORK" "$PACKAGE" "$PACKAGE.sha256"
EOF
