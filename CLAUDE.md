# CLAUDE.md – SiteTwin3D

Ipari telephelyek digitális ikre: 360°-os panorámabejárás, alaprajz és (később) 3D modell egy közös térben, adatbázis-rekordokhoz kötve.

**A részletes, kötelező érvényű terv: [`docs/SPECIFICATION.md`](docs/SPECIFICATION.md).** Munka előtt olvasd el a releváns fejezetet; ha ez a fájl és a specifikáció ellentmond egymásnak, a specifikáció az irányadó, és jelezd az ellentmondást.

## Jelölések a specifikációban
- **[DÖNTÖTT]** – kötelező; eltérni csak a felhasználóval egyeztetve lehet.
- **[FELTÉTELEZÉS]** – ezzel haladj, amíg nincs más döntés.
- **[NYITOTT]** – ne köss le végleges megoldást; válassz könnyen cserélhetőt, és írd le a választást.

## Technológia
- **Frontend:** Angular (standalone komponensek, signalok), CesiumJS ≥ 1.139 (panoráma, 3D), OpenLayers + proj4 (2D, rajzolás).
- **Backend:** .NET 10, ASP.NET Core Minimal API, EF Core + Npgsql + NetTopologySuite.
- **Adatbázis:** PostgreSQL + PostGIS + ltree.
- **Objektumtár:** SeaweedFS (S3-kompatibilis); a backend csak S3 API-t használ.
- **Telepítés:** Docker Compose (on-prem és felhő).
- **Külső rendszer:** MapManager (2D térképi adatok, GeoServer) – lásd lent.

## Repó felépítése
```
backend/     ← .NET API (src/Api, tests/Api.Tests; Backend.slnx)
frontend/    ← Angular kliens (zoneless; a Cesium lusta betöltésű, viewer3d/)
converter/   ← modellkonverter (Python: IFC → szegmentált GLB, mesh → GLB, LAS/LAZ/E57 → 3D Tiles), belső szolgáltatás
db/sql/      ← migrációk által futtatott SQL (triggerek, alaptípusok); beágyazott erőforrás, `SqlScripts.Load`
deploy/      ← docker-compose.yml, .env.example, Caddyfile, backup.sh / restore.sh; edge/ = közös belépési pont (nginx, név szerinti TLS-továbbítás) a MapManagerrel közös szerverhez
docs/        ← SPECIFICATION.md, MAPMANAGER_EXTENSIONS.md, OPERATIONS.md (üzemeltetés), DEPLOY.md (új szerver: Ubuntu 24.04), USER_GUIDE.md (felhasználói kézikönyv, képei: docs/images/)
```
- Objektumtár: S3-kompatibilis; alapból **SeaweedFS** (a MinIO nem ad ki szabad konténerképet). A backend csak S3-at beszél, a tár cserélhető.
- Felületi szövegek: `frontend/src/app/core/i18n/messages.hu.ts` (`inject(MESSAGES)`); a terméknév csak itt. Az API hibakódjainak (`code`) magyar szövege is itt van (`errors`).
- **UI-könyvtár: Angular 21 + PrimeNG `21.1.10` pontosan rögzítve** (a MapManagerrel azonos). A PrimeNG 22-től kereskedelmi „PrimeUI License”, licenckulccsal – **ne frissíts** 22-re egyeztetés nélkül. Ugyanezért a **PrimeIcons `7.0.0` pontosan rögzítve** (MIT); a 8.x is PrimeUI-licences – ne frissítsd. PrimeNG 21 sajátosságok: `p-sortIcon`, `p-cellEditor` (camelCase), a Tree csomópontsablonja `pTemplate="default"`, a fa áthúzásához `TreeDragDropService` kell, a Splitter `<ng-template #panel>`-lel.
- Központi kijelölés: `core/selection/selection.service.ts` – a fa, a grid, a térkép, a panoráma, a 3D és az adatlap ezt olvassa és írja; adatváltozás után `selection.changed()`. A `focusNode()` akkor is odaviszi a nézeteket (alaprajz, 3D, panoráma), ha az elem már ki volt jelölve (pl. infopontra kattintás); a 3D a máshol kijelölt elemhez repül, a saját kattintására nem.
- A `node_type.attr_schema` `json` (nem `jsonb`), mert a mezők sorrendje az űrlap és a grid oszlopsorrendje. A szervezet saját mezői a közös alaptípusokhoz: `node_type_extension` (szintén `json`); a rekordok ellenőrzése, a grid és az import mindig az összevont sémát kéri (`NodeTypeService.SchemaOfAsync`), **ne** a `type.AttrSchema`-t közvetlenül. Felület: `features/types/` (Típusok párbeszéd, szervezeti admin).
- Objektumtár: a böngésző aláírt URL-lel, **azonos originen** éri el: a Caddy a `/objects/*` útvonalat (= a vödör neve) változatlan Host fejléccel adja tovább, így az S3-aláírás érvényes és CORS sem kell. A `/media/` foglalt (Angular betűtípusok). A `Storage:PublicUrl` = a felhasználók által megnyitott cím.
- Panorámák (FR-P8): feldolgozási sor = a `panorama.privacy_status` (pending → review → rendering → approved | failed), háttérfeladat: `PanoramaWorker`. Arcfelismerés helyben: UltraFace RFB-640 ONNX (`backend/models/`, MIT), SkiaSharp (MIT) – ImageSharpot ne használj (licenc). A régiók képi szögekben (yaw 0 = képközép) vannak, a tájolástól függetlenül. A rendszámkeresés még nincs kész.
- Horgonyok (FR-A): az 1. fázisban `point` és `panorama_point` (infopont: képi yaw/pitch + közelítő hely az alaprajzon; alaprajz nélkül a panoráma helye, `attrs.position_source = panorama_fallback`). M6 óta `region`/`box` is (doboz: középpont = `position` + `z_rel_m`, méretek méterben, tájolás északtól; a 3D-ben osztályozó térfogattal kiemelve, kattintáskor a legkisebb tartalmazó doboz nyer). M7 óta `model_feature` is (a stabil `element_id`, a modell `attrs.element_id_property` tulajdonsága szerint; egy elem egy rekordé). A sokszöghasáb még nincs kész.
- Tártakarítás (M9): `Services/Storage/StorageJanitor` – az adatbázis által hivatkozott kulcsok (panoráma eredeti/megjelenítési kulcs, modellcsomag mappája) kivételével minden `org/` alatti fájl árva a türelmi idő után. Új fájltípusnál a hivatkozást itt is fel kell venni, különben a takarítás törli!
- Helyszíni fa: a funkcionális levelek (berendezések) a helyük alatt is megjelennek (`TreePanel.addPlaced`, típus `placed`; csoport `folder`); a PrimeNG fa a sablont a csomópont `type` mezője szerint választja, ezért minden típusnak kell `pTemplate`. A fa `droppableNodes`-a mindig igaz (különben a PrimeNG nem iratkozik fel a húzásra), a módot a `drop()` ellenőrzi.
- Adatok fa (`TreePanel`, nézet `data`; a `selection.aspect` ilyenkor `location`): a helyszíni fa + panorámák (`pano:<id>`) és modellek (`model:<id>`) a `node_id`-juk szerint. Kattintás: `selection.focusPanorama` / `focusModel` (a középső fül a `selection.centerView`); a csomópont-kijelölés nem változik. A GLB-modell egycsempés tilesetjének gömbje szándékosan bő (500 m), a ráugrás a betöltött tartalom gömbjét használja (`_model`, típusdefiníción kívül; Cesium-frissítés után ellenőrizni).
- Funkcionális nézőpont (M8): a csomópont helye `located_in` (egy, explicit) vagy a legkisebb tartalmazó helyszíni doboz (`Services/Locations`); a kijelölt csomópont részfáját a `SelectionService.members`/`highlightIds` tölti be egy helyen, ezt használja a 3D-nézet és az adatlap.
- Illesztés 3D-ben: `core/geo/snap.ts` (helyi sík valós méterben, csúcs > él), forrás: `GET /sites/{id}/floors/{levelId}/geometry` (rajzi rétegek + MapManager WFS). A Cesiumban a modellelem kiválasztásához `drillPick` kell (a dobozok osztályozó térfogata eltakarja), és a `pickPosition`-t előtte kell hívni.
- Időbeli relevancia: véglegesített csomópont (`node.finalized_at`) leíró adatainak és horgonyainak minden változása indoklással és érvényességi nappal a `change_entry`-be kerül – központilag az `AppDbContext.SaveChangesAsync`-ban (`Data/AppDbContext.History.cs`). Ezért horgonyt/csomópontot **ne** módosíts vagy törölj `ExecuteUpdate`/`ExecuteDelete`-tel (megkerülné); kaszkádoló törlés előtt `RemoveFinalizedAnchorsAsync`. A felületen a 428-at a `changeReasonInterceptor` kezeli (indoklás bekérése, ismétlés).
- Modellátalakítás (spec 9.2): az API csak feladatot ad a `converter` szolgáltatásnak (`ModelConversionWorker`, `Converter:Url`); a konverter S3-ból olvas és oda ír. Új formátumnál a felismerés két helyen van: `ModelFormats.Detect` (backend) és `detectFormat` (`models-dialog.ts`). Licenc: IfcOpenShell LGPL – csak változtatás nélkül, külön programként.
- Pontjelek: `core/markers/marker-icons.ts` (SVG data URI-k, saját rajz) – a térkép (OpenLayers `Icon`), a panoráma és a 3D (Cesium billboard) ugyanazt használja; új jelet itt és a jelmagyarázatban (`features/map/map-legend.ts`) kell felvenni.
- Egy elem – egy megjelenés: elemenként egy pont, panorámánként egy infopont (egyedi index; az `AnchorService.CreateAsync` újra lerakáskor áthelyez), egyetlen hely (`PlaceView`: modellelem/doboz → pont → infopont). A nézetek kattintásai `selection.focusNode(id, true)`-t hívnak (grafikus nézet: a panoráma is követ), a fa és a grid `focusNode(id)`-t.
- Dobozimport (`Services/Anchors/BoxImportService`, felület: `features/import/box-import-dialog.ts`, a táblázat és a 3D eszköztárán): a vetületátváltás PostGIS `ST_Transform`-mal egy lekérdezésben, az elforgatás konvergenciával korrigálva; a rekord sorai felülírják a dobozait.
- 3D doboz szerkesztése: a módosítás piszkozat (`ModelViewer.boxDirty`), mentés csak a doboz elhagyásakor (`stopEditing`, másik doboz `edit`-je, más csomópont kijelölése, a nézet megszűnése) – így véglegesített rekordnál egy indoklás egy szerkesztésre.
- Horgonyok áthelyezése csak munkamenetben: `core/anchors/anchor-edit.service.ts` (indítás, függő módosítások, Mentés/Elvetés). Új húzható horgonyfelületnél a húzást itt kell „stage”-elni, nem azonnal menteni.
- Bejárás: a szomszédság kézi kapcsolat (`panorama_link`, mindkét irányban); az automatikus összekötés csak javaslat (legközelebbiek `geography`-távolsággal), utána kézzel javítható.
- Rajzi rétegek: szerkesztés EPSG:3857-ben, tárolás WGS84-ben (`Services/Drawing/WebMercator.cs`, pontos képlet); egy rekordhoz itt egy geometria tartozik.
- Cesium-panoráma tájolása: `IMAGE_HEADING_OFFSET = -90` (mérés: `frontend/src/app/features/panorama/README.md`; Cesium-frissítés után ellenőrizni).
- A szintvágás a Cesium típusdefiníciókból hiányzó `Cesium3DTileset.clippingPlanesOriginMatrix` getterre épül (`viewer3d/model-viewer.ts`); Cesium-frissítés után ellenőrizni. Egy `ClassificationPrimitive` csak egyszínű példányokat fogad: színenként külön primitív. A tileset `clippingPlanes`-ét **ne** cseréld (a régi megsemmisül, miközben a csempék még rajzolnak vele → „reading '_target'” renderhiba): tilesetenként egy gyűjtemény, csak a `modelMatrix` és az `enabled` változik.
- Külső WMS más vetületben: `wmsParams.CRS` (pl. EPSG:23700) → a térkép abban kéri, OpenLayers újravetít (`core/geo/projections.ts`, proj4 – új vetületnél ott kell regisztrálni). A proxy (`Services/Maps/WmsProxy`) a kép helyett jött WMS-hibaüzenetet `maps.wms_error`-ként adja vissza.
- MapManager-kötés: a WFS-t a backend hívja (`Services/Maps/MapManagerClient`), a CQL-t szerveroldalon, a réteg mezőlistájával ellenőrizve építi; a kliens nem kap nyers WFS-proxyt. A token konfigurációból: `MapManager:Tokens:<organization.mapmanager_token_ref>`.

## Megszeghetetlen szabályok

**Adatok és tárolás**
- Bináris tartalom (panoráma, modell) **soha** nem kerül az adatbázisba, és nem megy át a backenden: objektumtár + rövid lejáratú aláírt URL. Egyetlen kivétel a WMS/WFS-proxy.
- Minden geometria **WGS84-ben (EPSG:4326)** tárolódik. 2D megjelenítés EPSG:3857, 3D WGS84/ECEF. Más vetületű adatot importkor kell átváltani.
- Metrikus értéket (méret, távolság, terület, tűrés méterben) **soha** ne számolj EPSG:3857-koordinátákból – a Web Mercator nem távolságtartó. Használj `geography`-t vagy a pont körüli ENU-rendszert.
- Magasság: `level_id` + `z_rel_m` (a szint padlójától); abszolút magasság = `site.base_height_m + level.elevation_m + z_rel_m`.
- A horgony (`anchor`) mindig a rekordhoz (`node`) tartozik; a megjelenítési azonosítók csak a horgonyban vannak. 3D Tiles esetén stabil, metaadatban tárolt `element_id`-t használj, **soha** a csempénkénti feature-sorszámot.
- 3D modell: a backend csak a `tileset.json`-t adja ki (átírva, aláírt csempe-URL-ekkel); a csempék és a GLB bájtjai soha nem mennek át rajta. A dobozok a rekordhoz tartoznak, nem a modellhez: modell törlése vagy cseréje után is megmaradnak.

**Telephelyi jogosultság (spec 3.2)**
- Szervezeti admin: minden telephely. Telephelyi hozzárendelés nélkül: a szervezet minden telephelye a felhasználó szerepköre szerint. Hozzárendeléssel: csak azok a telephelyek, telephelyenkénti szereppel; a Megtekintő soha nem szerkeszt.
- Olvasás: a globális EF-szűrő a látható telephelyekre is szűr (más telephely adata 404). Írás: az `AppDbContext.SaveChanges` minden módosított sor telephelyét ellenőrzi (403 `site.read_only`); `ExecuteDelete`/`ExecuteUpdate` megkerüli – ott a szolgáltatás hívja a `CurrentUser.RequireEditableSite`-ot.
- Felületen az üzemmódok (`/prepare`, `/assign`, `/view`) és a `Permissions` szolgáltatás döntik el, mely eszközök töltődnek be; a szerkesztő párbeszédek `@defer`-ben vannak (megtekintésben le sem töltődnek).

**Több szervezet**
- Minden szervezethez tartozó adatot a globális EF Core szűrő (`HasQueryFilter`) szűr a bejelentkezett felhasználó szervezetére. Új táblánál ezt **mindig** állítsd be, és írj tesztet arra, hogy más szervezet adata nem érhető el (404).
- Megvalósítás: a szervezeti táblák `IOrganizationOwned`-ok, saját (denormalizált) `organization_id`-vel; regisztráld őket az `AppDbContext.OrganizationOwned<T>`-vel (teszt ellenőrzi), a hivatkozásokat pedig az összetett `References<,>` kulccsal (`(x_id, organization_id)` → `(id, organization_id)`), így az adatbázis sem enged más szervezetre mutatni.

**MapManager-határ**
- A MapManager **külső rendszer**. A SiteTwin3D-ben **ne** valósíts meg alaprajzfájl-feltöltést, raszterfeldolgozást vagy GeoServer-kezelést.
- A MapManager adatbázisához **ne** kapcsolódj közvetlenül, a szerkesztési végpontjait **ne** használd.
- Kommunikáció csak a SiteTwin3D backend WMS/WFS-proxyján át, a szervezet szolgáltatástokenjével. A kliens soha nem éri el közvetlenül a MapManagert vagy a GeoServert.
- Jogosultság, rajzolás és a saját adatok teljesen a SiteTwin3D-ben vannak.

**Elnevezés**
- A termék neve (SiteTwin3D) **csak** konfigurációban és felületi szövegekben szerepel. Kódban, névterekben, adatbázis-sémában és API-útvonalakban semleges nevet használj (lehetséges átnevezés miatt).

## Konvenciók
- Kódbeli azonosítók, megjegyzések, commitüzenetek: **angolul**. Felületi szövegek: **magyarul**, erőforrásfájlból (i18n).
- API: `/api/v1/...`, REST/JSON, geometria GeoJSON-ként (EPSG:4326); a szerkesztési tranzakciók EPSG:3857-ben fogadnak.
- Végpontok funkcióterületenként (`Endpoints/<Terület>/`), `TypedResults`-szal; üzleti logika a `Services/` alatt – a MapManager kódszerkezetét követve.
- Hibák: a szolgáltatás `AppException`-t dob; a válasz problem details gépi `code` mezővel (pl. `node.cycle`), a magyar szöveget a felület adja. Adatbázis-kényszerek hibáit a `ProblemExceptionHandler` fordítja.
- PATCH: `PatchBody` különbözteti meg a „nem küldte” és a „null” esetet (pl. `parentId: null` = gyökérbe).
- Adatbázis: snake_case nevek (`UseSnakeCaseNamingConvention`); SQL-változás (`db/sql`) új migrációt igényel.
- A Cesium renderciklusa az Angular változásfigyelésén kívül fut (`NgZone.runOutsideAngular` vagy zoneless); a Cesiumból csak a kijelölés változása kerül vissza az Angular állapotba.
- Megjelenés és közös komponensek (grid, belépés, fejléc) a MapManager felületéből jönnek – egységes arculat.
- A rajzolt elemek tárolása a MapManager mintáját követi (`draw_layer` / `draw_record` / `draw_geometry`, verziós szerkesztési tranzakció).

## Tesztelés
- Minden mérföldkőhöz automatikus tesztek; az elfogadási feltételek a specifikáció 12. fejezetében.
- Adatbázis-integrációs tesztek valós PostgreSQL-lel (Testcontainers), különösen: a `path` triggerek (beszúrás, többszintű áthelyezés, ciklus elutasítása), a szervezeti szűrő, a horgonyok CHECK-kényszerei.
- Mintaadat seed-szkriptként: kitalált szennyvíztisztító telep (2 épület, 3 szint, 1 technológiai vonal, néhány panoráma) – `deploy/dev/seed.mjs`, csak a nyilvános API-n át (bármely telepítésre), `--reset`-tel újra létrehozható. A szintetikus panorámák: `deploy/dev/seed/`.

## Ütemezés
- 1. fázis (panorámaalapú MVP): M0–M5. 2. fázis (3D): M6–M9. Részletek: specifikáció 12. fejezet.
- Az M3 előfeltétele a MapManager szolgáltatástokenes hozzáférése; amíg nincs kész, helyi GeoServerrel vagy minta-WMS-sel haladj.

## Fejlesztői parancsok
A konténerek Podmannal futnak (`/var/run/docker.sock` → Podman), a `docker-compose` ezt használja.

```bash
# Teljes stack helyben (https://localhost:8543; DB: 127.0.0.1:5433, S3: 127.0.0.1:8333)
cd deploy && cp .env.example .env   # jelszavak kitöltése
docker-compose -f docker-compose.yml -f docker-compose.local.yml up -d --build

# Backend (http://localhost:5032; a DB a fenti compose-ból, a migráció indításkor lefut)
# Egyszer: a compose DB jelszava felhasználói titokként (a .env POSTGRES_PASSWORD-je)
cd backend/src/Api && dotnet user-secrets set "ConnectionStrings:Database" "Host=localhost;Port=5433;Database=appdb;Username=postgres;Password=<jelszó>"
cd backend && dotnet run --project src/Api
cd backend && dotnet test                     # Testcontainers: valós PostGIS-konténert indít
cd backend/src/Api && dotnet ef migrations add <Név> -o Data/Migrations
# Első belépés: szervezet = "platform", felhasználó = PLATFORM_ADMIN_USER (.env), első belépéskor jelszócsere

# M3 fejlesztés MapManager nélkül: helyi GeoServer EOV-ban tárolt minta-alaprajzzal (ceg:alaprajz, szint = 'F0'|'F1'),
# a demo szervezet "MapManager"-eként (deploy/docker-compose.dev-geoserver.yml, deploy/dev/geoserver-sample.sh)
cd deploy && docker-compose -f docker-compose.yml -f docker-compose.local.yml -f docker-compose.dev-geoserver.yml up -d && ./dev/geoserver-sample.sh
# A Podman VM 6 GB memóriájába a webkép buildje a GeoServer mellett nem fér el: build előtt `stop geoserver`.

# M6 mintamodell: a minta-alaprajz épülete GLB-ként (feltöltés: Előkészítés → 3D → feltöltés ikon; elhelyezés: a telephely pontja, 0 m, 0°)
node deploy/dev/sample-model.mjs sample-building.glb
# Mintatelep (szervezeti admin; a GeoServer-minta alaprajzával): https://localhost:8543 → „Minta szennyvíztisztító telep”
SEED_PASSWORD=… node deploy/dev/seed.mjs --org demo --user admin --floorplan-layer ceg:alaprajz [--reset]
# M7: szegmentált változat (EXT_mesh_features + EXT_structural_metadata, elemazonosító tulajdonság: element_id)
node deploy/dev/sample-model.mjs --segmented

# Mentés / visszaállítás helyben (Podman): docs/OPERATIONS.md
cd deploy && COMPOSE="docker-compose -f docker-compose.yml -f docker-compose.local.yml" ENGINE=podman ./backup.sh

# Frontend (http://localhost:4200; /api és /health → localhost:5032, proxy.conf.mjs)
# A futó compose stack API-jához: API_URL=https://localhost:8543 npm start
cd frontend && npm start
cd frontend && npx ng test --watch=false
# Konverter tesztjei (generált IFC, OBJ, LAS, E57 mintákon), a képben:
podman build -f converter/Dockerfile -t twin-converter . && podman run --rm twin-converter python -m tests.test_convert
# A pye57 C++ fordítása sok memóriát kér: a Podman VM-en a konvertert külön (nem párhuzamosan) építsd.
```
