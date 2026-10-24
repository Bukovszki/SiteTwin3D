# Üzemeltetési kézikönyv

Ez a leírás a rendszer egy szerveres, Docker Compose alapú telepítésének üzemeltetéséhez készült (on-prem vagy felhő). Minden parancs a `deploy/` mappából futtatandó.

## 1. Felépítés

| Szolgáltatás | Feladat | Kívülről elérhető |
|---|---|---|
| `web` (Caddy) | HTTPS, a felület kiszolgálása, továbbítás az API-hoz és az objektumtárhoz | igen (80, 443) |
| `api` (.NET) | REST API, háttérfeladatok (panoráma-feldolgozás, tártakarítás) | nem |
| `db` (PostgreSQL + PostGIS) | adatbázis (`pgdata` kötet) | nem |
| `storage` (SeaweedFS, S3) | panorámák, 3D modellek (`storage` kötet) | csak aláírt URL-lel, a `web`-en át |

A bináris tartalom (panoráma, modell) soha nem kerül az adatbázisba; a böngésző rövid lejáratú aláírt URL-lel éri el az objektumtárat. Ezért **a mentéshez az adatbázist és az objektumtárat is menteni kell** (3. fejezet).

## 2. Telepítés és frissítés

```bash
cp .env.example .env        # jelszavak, DOMAIN, PUBLIC_URL, tárkulcsok kitöltése
docker compose up -d --build
docker compose ps           # minden szolgáltatás "healthy"
curl https://<domain>/health
```

Első belépés: szervezet = `platform`, felhasználó = `PLATFORM_ADMIN_USER`, jelszó = `PLATFORM_ADMIN_PASSWORD` (az első belépéskor cserélni kell).

**Frissítés:** előbb mentés (3.1), utána:

```bash
git pull
docker compose up -d --build
```

Az API induláskor lefuttatja az adatbázis-migrációkat. Ha az új verzió hibás, a mentésből vissza lehet állni (3.3), majd a korábbi verzióval újraépíteni.

## 3. Mentés és visszaállítás

### 3.1 Mentés

```bash
./backup.sh
```

- **Adatbázis:** teljes `pg_dump` egyedi formátumban: `BACKUP_DIR/db/appdb-ÉÉÉÉHHNN-ÓÓPPMM.dump`. A legújabb `BACKUP_KEEP` darab marad meg (alapból 14).
- **Objektumtár:** növekményes másolat a `BACKUP_DIR/objects` mappába (rclone). Csak hozzáad, **soha nem töröl**, így minden megőrzött adatbázismentés megtalálja a hozzá tartozó fájlokat. A mappa idővel nő; ha túl nagy, egy friss teljes mentés után üríthető (a régebbi adatbázismentésekhez tartozó fájlok ezzel elvesznek).
- Futás közben is menthető, leállás nem kell. Előbb az adatbázis, utána a fájlok mentődnek, így a fájlmásolat mindig tartalmazza az adatbázismentés által hivatkozott fájlokat.
- A `BACKUP_DIR` (alapból `deploy/backups/`) **ugyanazon a szerveren** van: rendszeresen vigye át egy másik gépre vagy tárhelyre (pl. `rsync`, `restic`, felhős tár).

Napi mentés cronnal (pl. hajnali 2-kor):

```cron
0 2 * * * cd /opt/app/deploy && ./backup.sh >> backups/backup.log 2>&1
```

Podman esetén: `COMPOSE="docker-compose" ENGINE=podman ./backup.sh`.

### 3.2 Mit nem ment a szkript

- `.env` (jelszavak, kulcsok) – külön, biztonságos helyen őrizze.
- `dpkeys` kötet (a bejelentkezési sütik kulcsai) – elvesztésekor csak újra be kell lépni.
- Caddy tanúsítványai – automatikusan újra igénylődnek.

### 3.3 Visszaállítás

```bash
./restore.sh backups/db/appdb-20261024-020000.dump
```

- **Adatbázis:** az API leáll, az adatbázis teljesen lecserélődik a mentésre.
- **Fájlok:** a fájlok visszamásolódnak (törlés nélkül), majd az API újraindul, és szükség esetén lefuttatja az újabb migrációkat.
- **Felhasználók:** a visszaállítás után újra be kell lépniük.
- **Mentés óta feltöltött fájlok:** árvává válnak, és a takarítás a türelmi idő után törli őket (4. fejezet).

Megerősítés nélküli futtatáshoz (pl. próba-visszaállítás egy tesztszerveren): `--yes`. **Javasolt negyedévente próba-visszaállítást végezni egy külön gépen.**

## 4. Tártakarítás (árva fájlok)

Az API óránként ellenőrzi az objektumtárat (`CLEANUP_INTERVAL_HOURS`, alapból 6 óra), és összeveti az adatbázissal. Két dolgot takarít:

- **Árva fájlok:** olyan fájlok, amelyekre egyetlen rekord sem hivatkozik, és régebbiek a türelmi időnél (`CLEANUP_GRACE_HOURS`, alapból 24 óra). Ilyen például a félbehagyott feltöltés, egy törölt elem ottmaradt fájlja vagy a visszaállítás után feleslegessé vált fájl. A türelmi idő azért kell, mert a feltöltés két lépésben történik: előbb a fájl, utána a rekord.
- **Lejárt eredeti panorámák:** ha a szervezetnél be van állítva az eredeti panorámák megőrzési ideje, a lejárt eredetik törlődnek. A kitakart megjelenítési változat megmarad.

Csak jelentés, törlés nélkül: `CLEANUP_DRY_RUN=true` (ajánlott egy frissítés utáni első napokra).

Kézi futtatás platformadminként (alapból csak jelentés):

```bash
# a böngészőben platformadminként belépve, vagy a munkamenet sütijével:
curl -X POST https://<domain>/api/v1/platform/storage/cleanup -H 'Content-Type: application/json' -d '{"dryRun": true}' -b 'app.auth=…'
```

A válasz tartalma:
- az átnézett és a hivatkozott fájlok száma;
- az árvák száma és összmérete, valamint néhány minta-kulcs;
- a türelmi időn belüli („fiatal”) hivatkozatlan fájlok száma;
- a lejárt eredetik és a ténylegesen törölt fájlok száma.

## 5. Naplók

Az API naplója JSON (soronként egy objektum), a konténernapló szolgáltatásonként 3 × 20 MB-ra korlátozott.

```bash
docker compose logs -f api                       # élő napló
docker compose logs api | grep '"LogLevel":"Error"'
docker compose logs api | jq -r 'select(.Scopes[]?.RequestId == "0HN…") | .Message'
```

- **Kérésnapló:** minden API-kérésről egy sor: módszer, útvonal (lekérdezési paraméterek nélkül), státusz, időtartam, felhasználó- és szervezetazonosító. Az 5xx hibák `Error`, a 401/403/429 válaszok `Warning` szinten jelennek meg.
- **Kérésazonosító:** minden válasz `X-Request-Id` fejlécet kap, és ugyanez szerepel a kérés összes naplósorában. Hibabejelentéskor ezt kérje a felhasználótól (a böngésző fejlesztői eszközeiben, a Hálózat fülön látható).
- **Biztonsági események:** a belépések, a zárolások és a platformadmin létrehozása külön sort kapnak. Az adminisztratív műveletek (felhasználók, szervezetek, jogosultságok) a kérésnaplóban követhetők: ki (felhasználóazonosító), mikor, melyik végpontot hívta.
- **Háttérfeladatok:** a panoráma-feldolgozás és a tártakarítás eredménye is naplózódik (pl. „Storage cleanup: … orphan(s) … deleted”).
- **Megőrzés:** központi naplógyűjtőbe (pl. Loki, ELK) a Docker naplómeghajtóján át továbbítható; a JSON formátum ehhez készült.

## 6. Felügyelet

| Mit | Hogyan |
|---|---|
| Élő-e | `GET /health` → `Healthy` (az adatbázis-kapcsolatot is ellenőrzi) |
| Konténerek | `docker compose ps` (a `db`, `api`, `storage` saját egészség-ellenőrzéssel) |
| Lemezhely | `docker system df -v` – a `pgdata` és a `storage` kötet, valamint a `BACKUP_DIR` növekedése |
| Hibák | `Error` szintű naplósorok száma (5. fejezet) |
| Mentés | a `backup.log` utolsó sora (`done: …`) és a legújabb `.dump` dátuma |

## 7. Gyakori teendők

- **Elfelejtett admin jelszó (szervezet):** a platformadmin nem lát szervezeti adatot. Ha egy szervezetnek nincs belépni tudó adminja, az adatbázisban kell egy felhasználót ideiglenes jelszóval ellátni (a fejlesztőcsapat segítségével), vagy egy másik szervezeti adminnak kell visszaállítania.
- **Betelt lemez:**
  - régi mentések áthelyezése (3.1);
  - `docker image prune` a régi lemezképekre;
  - az árvák ellenőrzése a takarítás jelentésével (4.).
- **MapManager nem érhető el:** a panorámák, a 3D és a saját adatok működnek. Az alaprajz-háttér, a MapManagerhez kötött adatlapok és a 3D-illesztés alaprajzi geometriája ilyenkor hiányos; a felület ezt jelzi.
