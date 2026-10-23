#!/usr/bin/env bash
# Development only: publishes a sample floorplan stored in EOV (EPSG:23700) on the local GeoServer, the way MapManager
# would (workspace "ceg", layer "alaprajz", levels selected with the "szint" field), and links the demo organization to
# it as its "MapManager". Run from deploy/ after: docker compose ... -f docker-compose.dev-geoserver.yml up -d
#   ./dev/geoserver-sample.sh [organization code] [site longitude] [site latitude]
set -euo pipefail
ORG=${1:-demo}
LON=${2:-19.08}
LAT=${3:-47.43}
GS=http://localhost:8080/geoserver/rest
GS_AUTH=admin:${GEOSERVER_ADMIN_PASSWORD:-geoserver}
set -a; source .env; set +a
PSQL=(podman exec -i -e PGPASSWORD="$POSTGRES_PASSWORD" twin-db-1 psql -v ON_ERROR_STOP=1 -U "${POSTGRES_USER:-postgres}" -h localhost)

echo "• waiting for GeoServer"
until curl -sf -u "$GS_AUTH" "$GS/about/version.json" >/dev/null; do sleep 3; done

echo "• sample database devgis (EOV floorplan around $LON, $LAT)"
"${PSQL[@]}" -d postgres -tc "SELECT 1 FROM pg_database WHERE datname = 'devgis'" | grep -q 1 || "${PSQL[@]}" -d postgres -c "CREATE DATABASE devgis"
"${PSQL[@]}" -d devgis <<SQL
CREATE EXTENSION IF NOT EXISTS postgis;
DROP TABLE IF EXISTS alaprajz;
CREATE TABLE alaprajz (id serial PRIMARY KEY, szint text NOT NULL, nev text NOT NULL, tipus text NOT NULL, geom geometry(Polygon, 23700) NOT NULL);
-- A 40 m × 24 m building in EOV metres (the projected system the floorplan was drawn in), centred on the site point;
-- ground floor: hall + 2 rooms, first floor: 4 rooms. Walls 0.3 m.
WITH o AS (SELECT ST_X(p) AS x, ST_Y(p) AS y FROM (SELECT ST_Transform(ST_SetSRID(ST_MakePoint($LON, $LAT), 4326), 23700) AS p) t),
r(szint, nev, tipus, x0, y0, x1, y1) AS (VALUES
  ('F0', 'Épület',        'kontur',   -20, -12, 20, 12),
  ('F0', 'Szivattyútér',  'helyiseg', -19.7, -11.7, 4.85, 11.7),
  ('F0', 'Kapcsolótér',   'helyiseg', 5.15, 0.15, 19.7, 11.7),
  ('F0', 'Raktár',        'helyiseg', 5.15, -11.7, 19.7, -0.15),
  ('F1', 'Épület',        'kontur',   -20, -12, 20, 12),
  ('F1', 'Iroda',         'helyiseg', -19.7, 0.15, -0.15, 11.7),
  ('F1', 'Labor',         'helyiseg', 0.15, 0.15, 19.7, 11.7),
  ('F1', 'Öltöző',        'helyiseg', -19.7, -11.7, -0.15, -0.15),
  ('F1', 'Vezérlőterem',  'helyiseg', 0.15, -11.7, 19.7, -0.15))
INSERT INTO alaprajz (szint, nev, tipus, geom)
SELECT szint, nev, tipus, ST_MakeEnvelope(o.x + x0, o.y + y0, o.x + x1, o.y + y1, 23700) FROM r, o;
SQL

echo "• GeoServer workspace ceg, store, layer ceg:alaprajz (native EPSG:23700)"
curl -s -u "$GS_AUTH" -o /dev/null -X POST -H 'Content-Type: application/json' "$GS/workspaces" -d '{"workspace":{"name":"ceg"}}' || true
curl -s -u "$GS_AUTH" -o /dev/null -X POST -H 'Content-Type: application/json' "$GS/workspaces/ceg/datastores" -d "{
  \"dataStore\": {\"name\": \"devgis\", \"connectionParameters\": {\"entry\": [
    {\"@key\":\"dbtype\",\"\$\":\"postgis\"}, {\"@key\":\"host\",\"\$\":\"db\"}, {\"@key\":\"port\",\"\$\":\"5432\"},
    {\"@key\":\"database\",\"\$\":\"devgis\"}, {\"@key\":\"user\",\"\$\":\"${POSTGRES_USER:-postgres}\"},
    {\"@key\":\"passwd\",\"\$\":\"$POSTGRES_PASSWORD\"}, {\"@key\":\"schema\",\"\$\":\"public\"}]}}}" || true
curl -s -u "$GS_AUTH" -o /dev/null -X DELETE "$GS/layers/ceg:alaprajz?recurse=true" || true
curl -s -u "$GS_AUTH" -X POST -H 'Content-Type: application/json' "$GS/workspaces/ceg/datastores/devgis/featuretypes" -d '{
  "featureType": {"name": "alaprajz", "nativeName": "alaprajz", "title": "Minta alaprajz (EOV)", "srs": "EPSG:23700",
                  "projectionPolicy": "FORCE_DECLARED"}}' -w "  featuretype: %{http_code}\n"

echo "• organization '$ORG' → local GeoServer as its MapManager (token reference: dev)"
"${PSQL[@]}" -d appdb -c "UPDATE organization SET mapmanager_url = 'http://geoserver:8080', mapmanager_token_ref = 'dev' WHERE code = '$ORG'"
echo "done: layer ceg:alaprajz, levels: szint = 'F0' | 'F1'"
