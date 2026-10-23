# MapManager-bővítések a SiteTwin3D integrációhoz

**Verzió:** 0.1 (vázlat) · **Dátum:** 2026-10-22
**Kinek szól:** a MapManager repójában dolgozó fejlesztőnek / ügynöknek.
**Előzmény:** a SiteTwin3D (ipari telephelyek panoráma- és 3D-alapú digitális ikre) a 2D térképi adatokat – alaprajzokat, háttérrétegeket, leíró adatokat – a MapManagerből veszi. A SiteTwin3D önálló alkalmazás saját felhasználókkal; a MapManagerrel csak a GeoServer-proxyn át kommunikál.

> Ez a dokumentum a MapManager README-je alapján készült. A meglévő megoldásokra vonatkozó állításokat (táblanevek, szolgáltatások, házirendek) a megvalósítás elején a kódban ellenőrizni kell; eltérés esetén a kód az irányadó, és a tervet hozzá kell igazítani.

## Jelölések
- **[DÖNTÖTT]** – kötelező; eltérni csak egyeztetve lehet.
- **[FELTÉTELEZÉS]** – ezzel haladj, amíg nincs más döntés.
- **[NYITOTT]** – döntésre vár; válassz könnyen cserélhető megoldást, és írd le a választást.

## Áttekintés és sorrend

| # | Bővítés | Prioritás | Miért |
|---|---|---|---|
| A | Szolgáltatástokenes hozzáférés a proxyhoz | **1. – elsőként** | A SiteTwin3D M3 mérföldköve erre épül |
| B | Raszteres adatforrás kezelése (PDF/kép georeferálása) | 2. | Raszteres alaprajzok publikálása |

A két bővítés független egymástól. Mindkettő a meglévő felépítést követi: Minimal API funkcióterületenként, üzleti logika a `Services/` alatt, EF Core-migráció, magyar táblanevek, verziószám a README szabálya szerint (új funkció → középső szám nő).

### Mi NEM feladat
- SiteTwin3D-specifikus logika (telephelyek, szintek, horgonyok) – az a SiteTwin3D-ben van.
- A SiteTwin3D felhasználóinak kezelése – a SiteTwin3D-nek saját felhasználókezelése van; a MapManager egy szolgáltatásfiókot (tokent) lát.
- A szerkesztési végpontok tokenes elérése – a SiteTwin3D nem szerkeszt MapManager-adatot.

---

## A) Szolgáltatástokenes hozzáférés [DÖNTÖTT]

### A.1 Cél
Egy külső rendszer (a SiteTwin3D backendje) szerver–szerver kapcsolatban, sütis belépés nélkül, **csak olvasásra** érje el egy cég publikált rétegeit a meglévő GeoServer-proxyn át. A MapManager marad a rétegjogosultság egyetlen kapuőre.

### A.2 Hatókör
A token **kizárólag** ezekhez ad hozzáférést:

| Útvonal | Mire |
|---|---|
| `GET /geoserver/wms` | `GetMap`, `GetFeatureInfo`, `GetLegendGraphic` – alaprajzok és háttérrétegek |
| `GET /geoserver/wfs` | `GetFeature`, `DescribeFeatureType` – leíró adatok (adatlap, adattábla) |
| `GET /api/publications` és `GET /api/publications/{id}` | a választható rétegek listája a SiteTwin3D konfigurációs felületéhez |
| `GET /api/publications/{id}/fields` | a réteg mezőlistája (a SiteTwin3D adattáblájához) |

Minden más végpont tokennel **403**. Az `OwsRequestValidator` meglévő szabályai (engedett műveletek, csak a cég rétegei, tiltott paraméterek) **változatlanul** érvényesek tokenes kérésre is.

### A.3 Adatmodell
```
szolgaltatas_token (
  id            bigint,
  ceg_id        → ceg,                 -- a token egy céghez tartozik
  nev           text,                  -- pl. 'SiteTwin3D éles'
  elotag        text,                  -- a token első 8 karaktere, azonosításhoz (listában, naplóban)
  token_hash    text,                  -- SHA-256 kivonat; maga a token nincs tárolva
  hatokor       text[],                -- pl. {'wms','wfs','publications'} [FELTÉTELEZÉS: alapból mind a három]
  letrehozva    timestamptz,
  letrehozo_id  → felhasznalo,
  lejarat       timestamptz NULL,      -- NULL: nem jár le
  utolso_hasznalat timestamptz NULL,
  visszavonva   timestamptz NULL
)
-- egyedi index: token_hash; a cégszűrő (HasQueryFilter) erre a táblára is vonatkozzon
```

### A.4 Token formátuma és ellenőrzése
- Véletlen, legalább 256 bites token, URL-biztos kódolással, felismerhető előtaggal (pl. `mmst_…`), hogy titokkeresők és naplószűrők felismerjék.
- A token **csak létrehozáskor jelenik meg egyszer**; utána csak az előtagja látszik.
- Kérésben: `Authorization: Bearer <token>` fejléc. URL-paraméterben **nem** fogadható el (naplókba, előzményekbe kerülne).
- Ellenőrzés: a kapott token SHA-256 kivonatával keresés; elutasítás (401), ha nincs ilyen, visszavont, lejárt, vagy a cég le van tiltva. A tokent és a fejlécet **soha** ne naplózd, csak az előtagot.
- Az ellenőrzés eredménye legfeljebb 30 mp-ig gyorsítótárazható (a biztonsági bélyeg mintájára), mert a térkép sok kérést küld; visszavonás így legfeljebb 30 mp alatt hat.
- `utolso_hasznalat` frissítése ritkítva (pl. legfeljebb percenként egyszer).

### A.5 Bekötés a jogosultsági rendszerbe
- Új hitelesítési séma (pl. `ServiceToken`) a meglévő sütis séma mellé. Sikeres ellenőrzéskor olyan principal jön létre, amely **ugyanazt a cégazonosítót** hordozza, mint egy céges felhasználó, egy külön szereppel (pl. `Szolgaltatas`) és a hatókörökkel.
- Így a meglévő **cégszűrő** és a **`LayerAccess`** (a cég rétegei) változtatás nélkül működik a tokenes kérésekre is.
- Házirend: a `Szolgaltatas` szerep csak az A.2 táblázat végpontjain engedett (explicit engedélylista), a hatókör szerint. A `FallbackPolicy` miatt minden más végpont alapból elutasítja.
- A proxy továbbításkor az `Authorization` fejlécet **nem** adja tovább a GeoServernek (mint a sütit sem).

### A.6 Kezelés (cégadmin)
| Metódus | Útvonal | Leírás |
|---|---|---|
| GET | `/api/service-tokens` | a cég tokenjei (név, előtag, hatókör, létrehozva, lejárat, utolsó használat, állapot) |
| POST | `/api/service-tokens` | új token (`name`, `scopes`, opcionálisan `expiresAt`); a válasz egyszer tartalmazza a teljes tokent |
| DELETE | `/api/service-tokens/{id}` | visszavonás (azonnal, legfeljebb a gyorsítótár idejéig él még) |

- Házirend: `Admin` (cégadmin). A platform-adminisztrátor nem lát tokent.
- Felület: a Felhasználók fül mellett „Szolgáltatás-hozzáférés” szakasz: lista, létrehozás (a token egyszeri megjelenítése másolás gombbal és figyelmeztetéssel), visszavonás.

### A.7 Naplózás
- Elutasított tokenes kérés: cég, token-előtag, kérés, ok (a meglévő proxynapló mintájára).
- Sikeres kérésekről elég az `utolso_hasznalat`; részletes hozzáférési napló [NYITOTT].

### A.8 Elfogadási feltételek
1. Érvényes tokennel a cég publikált rétegére küldött WMS `GetMap` és WFS `GetFeature` sikeres; a válasz azonos a sütis kérésével.
2. Más cég rétegére, tiltott paraméterrel (`SLD_BODY` stb.) vagy tiltott művelettel (`GetCapabilities`, WFS-T) küldött kérés ugyanúgy elutasítva, mint sütivel.
3. Tokennel hívott nem engedett végpont (pl. `/api/uploads`, szerkesztés) 403.
4. Visszavont, lejárt vagy letiltott cégű token 401 (legfeljebb a gyorsítótár idejével késve).
5. Az adatbázisban nincs nyílt token; naplóban sem fejléc, sem teljes token nem jelenik meg.
6. Integrációs tesztek a fentiekre.

### A.9 Szerződés a SiteTwin3D felé (tájékoztató)
```
GET {MapManager}/geoserver/wms?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap
    &LAYERS=ceg_alaprajz:gyar_f2&CRS=EPSG:3857&BBOX=...&WIDTH=...&HEIGHT=...
    &FORMAT=image/png&TRANSPARENT=true&CQL_FILTER="szint"='F2'
Authorization: Bearer mmst_...
```
A SiteTwin3D a saját felhasználói jogosultságának ellenőrzése után, a saját backendjén át küldi ezeket; a böngésző soha nem kapja meg a tokent.

---

## B) Raszteres adatforrás kezelése [DÖNTÖTT]

### B.1 Cél
Raszteres alaprajzok és térképek (PDF, kép) feltöltése, **georeferálása** illesztőpontokkal, és publikálása GeoServer-raszterrétegként – ugyanabban a publikálási folyamatban, mint a vektoros (SHP) feltöltések.

### B.2 Bemenetek
- **PDF:** oldal kiválasztása, raszterizálás választható felbontással (pl. 150–300 DPI) [FELTÉTELEZÉS: GDAL PDF-meghajtó vagy pdftoppm].
- **Kép:** PNG, JPEG, TIFF.
- **GeoTIFF:** ha már georeferált (érvényes vetülettel), a georeferálási lépés kimarad.

### B.3 Georeferálás
- **Illesztőpontok:** pontpárok kép pixel ↔ térképi koordináta. A térképi pont kattintással a térképen (EPSG:3857), vagy beírva bármely vetületben (pl. EOV, EPSG:23700).
  - **2 pont:** hasonlósági transzformáció (eltolás, forgatás, egységes méretarány) – a tipikus alaprajzi eset.
  - **3+ pont:** affin transzformáció; a felület pontonként mutatja a maradék eltérést (méterben), hogy a hibás pont kiszűrhető legyen.
- **Célvetület:** a feltöltés tárolási vetülete az illesztőpontok vetülete (pl. EOV) – a MapManager „eredeti vetületben tárolás” elvének megfelelően; a GeoServer a kért vetületre (pl. EPSG:3857) vetít.
- **Felület:** osztott nézet – bal oldalt a kép, jobb oldalt a térkép; pontpárok felvétele, mozgatása, törlése; élő átlátszó előnézet a térképen.
- Később módosítható: az illesztőpontok szerkesztése újrafeldolgozást indít, a publikáció neve és a hivatkozások megmaradnak.

### B.4 Feldolgozás
- GDAL-lal: illesztőpontok → transzformáció → **Cloud Optimized GeoTIFF**, áttekintő szintekkel (overviews), tömörítéssel (fényképszerű képnél JPEG, vonalas rajznál DEFLATE/LZW) és átlátszósággal a forgatás miatti üres sarkokban (alfa csatorna vagy nodata).
- **Háttérfeladatként** fusson, állapotlekérdezéssel (sorban áll / fut / kész / hiba); a nagy PDF-ek feldolgozása hosszú lehet. [FELTÉTELEZÉS] Kezdetben egy egyszerű, adatbázisban nyilvántartott feladatsor elég.
- A GDAL futtatása: az API konténerébe telepítve, vagy külön feldolgozó konténerben [NYITOTT – a telepítési képtől függ].

### B.5 Adatmodell
- A `feltoltes` tábla bővítése egy **típus** mezővel: `vektor` (meglévő) | `raszter`. [FELTÉTELEZÉS] Ez egyszerűbb, mint külön tábla, és a publikálás, cégszűrő, törlés változatlanul használható.
- Raszternél az `adatstruktura` tartalma: eredeti fájl és oldal, méret (px), sávok, felbontás (m/px), SRID, illesztőpontok, transzformáció típusa és maradék eltérései, a COG elérési útja, feldolgozási állapot.
- A `rekord` és `geometria` táblát raszter nem használja. [NYITOTT] A raszter befoglaló sokszöge felkerülhet a `geometria` táblába, hogy kereséshez és áttekintéshez használható legyen.

### B.6 Tárolás és GeoServer
- A GeoServernek fájlként kell olvasnia a GeoTIFF-et, ezért **közös kötet** kell az API (vagy a feldolgozó) és a GeoServer konténer között (pl. `raster_data`, a GeoServerben csak olvasható csatolással). A `deploy/docker-compose.yml`-t és a mentési szkriptet ehhez igazítani kell.
- Publikálás a GeoServer REST API-ján: **GeoTIFF coverage store + réteg** a cég munkaterületén, alapértelmezett raszterstílussal; a meglévő `publikacio` sor, munkaterület-ellenőrzés, méretarány-tartomány és visszavonás ugyanúgy működjön.
- A proxy (`OwsRequestValidator`) raszterrétegre is a cég rétegeit engedje; a `GetFeatureInfo` raszteren pixelértéket ad – ez elfogadható.
- Törlés: a publikációk visszavonása után a coverage store, a COG és az eredeti fájl is törlődik.

### B.7 Ami nem kell most
- Offline csomag raszterhez (mobil) – később.
- Raszterszerkesztés, mozaikolás több képből – később, ha igény lesz rá.

### B.8 Elfogadási feltételek
1. Egy EOV-ban georeferált PDF-alaprajz (2 illesztőponttal) feltöltés és publikálás után WMS-en, EPSG:3857-ben kérve a helyén, a forgatásnak megfelelően jelenik meg, a sarkok átlátszók.
2. 3 illesztőpontnál a felület kiírja a pontonkénti eltérést; egy szándékosan hibás pont nagy eltéréssel látszik.
3. Már georeferált GeoTIFF illesztőpontok nélkül publikálható.
4. Az illesztőpontok módosítása után a réteg újra feldolgozódik, a publikáció neve nem változik.
5. Nagy (pl. 50+ MB) PDF feldolgozása a háttérben fut, a felület mutatja az állapotot, a kérés nem fut időtúllépésbe.
6. A feltöltés törlése mindent eltávolít (GeoServer, fájlok, adatbázis).
7. Tokenes (A) kéréssel a raszterréteg ugyanúgy elérhető.

---

## Nyitott kérdések
1. Részletes hozzáférési napló kell-e a tokenes kérésekről (A.7)?
2. GDAL helye: API-konténer vagy külön feldolgozó konténer (B.4)?
3. A raszter befoglaló sokszöge kerüljön-e a `geometria` táblába (B.5)?
4. Kell-e tokenenként rétegszintű szűkítés (a token csak a cég bizonyos rétegeit lássa), vagy elég a cégszintű hozzáférés? [FELTÉTELEZÉS] Elég a cégszintű.
