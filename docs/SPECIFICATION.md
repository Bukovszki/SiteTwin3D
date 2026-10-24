# SiteTwin3D – Igényspecifikáció és megvalósítási terv

**Verzió:** 0.5 (vázlat) · **Dátum:** 2026-10-22 · **Státusz:** egyeztetés alatt

> **Jelölések a dokumentumban**
> - **[DÖNTÖTT]** – elfogadott döntés, ettől csak egyeztetés után lehet eltérni.
> - **[FELTÉTELEZÉS]** – ésszerű alapértelmezés, amíg nincs más döntés; a megvalósítás ezzel haladhat.
> - **[NYITOTT]** – döntésre vár; a megvalósítás ezt a részt ne kösse le véglegesen.

---

## 1. Cél és hatókör

### 1.1 Cél
Webes alkalmazás ipari telephelyek és létesítmények 3D-s megjelenítésére **kívülről és belülről**, amely a 3D modellt, a 2D alaprajzot és a 360°-os panorámaképekből álló **virtuális bejárást** egy közös, georeferált térben kapcsolja össze.

Szlogen: *SiteTwin3D – telephelyek digitális ikre, kívülről és belülről.*

### 1.2 Célfelhasználók
Ipari létesítmények üzemeltetői, karbantartói, facility managerei, mérnökei (pl. szennyvíztisztító telepek, gyártóüzemek, energetikai létesítmények).

### 1.3 Hatókörön belül (MVP)
- Rugalmas, egytáblás hierarchia a telephely elemeinek leírására (helyszíni és funkcionális nézőpont).
- 3D modell megjelenítése CesiumJS-ben, kül- és beltéri nézettel, szintenkénti szűréssel.
- Vektoros alaprajz megjelenítése szintenként, a 3D-vel szinkronban.
- 360°-os panorámák elhelyezése és bejárása, kapcsolódva a hierarchia elemeihez.
- Elemkiválasztás (rámutatás) a 3D-ben, a hierarchiában és az alaprajzon, kölcsönös kiemeléssel.
- Pont illesztése (snap) a vektoros geometriára.

### 1.3b Fázisok [DÖNTÖTT]
- **1. fázis – panorámaalapú MVP:** hierarchia, műszaki adatok és grid, vektoros alaprajz, panorámák és bejárás, infopontok, három üzemmód. A Cesium ebben a fázisban a telephely áttekintő térképét és a panorámapontok térbeli elhelyezését adja; 3D mesh-modell nélkül is teljes értékűen használható.
- **2. fázis – 3D modell:** mesh/3D Tiles betöltése, bedobozolás, szegmentált modellelemek, infopont-pozíciók pontosítása a mesh-sel, illesztés.
- Indoklás: a panoráma gyorsan, kevés előfeldolgozással ad használható eredményt; a mesh-modellek mérete és optimalizálása nagyobb kockázat, ezért később jön. Az adatmodell (horgonyok) mindkét fázist változtatás nélkül kiszolgálja.

### 1.4 Hatókörön kívül (MVP-ben nem)
- Valós idejű IoT/szenzoradatok, prediktív karbantartás.
- CMMS-funkciók (munkalapok, karbantartási ütemezés).
- BIM/IFC szerkesztés; a modellek az alkalmazáson kívül készülnek, az alkalmazás importál és megjelenít.
- Mobil natív alkalmazás (a webes felület legyen használható tableten).

### 1.5 Üzemmódok [DÖNTÖTT]
Az alkalmazás három üzemmódban működik. Az üzemmód egyben felületi munkaterület és jogosultsági kapu is.

| # | Üzemmód | Cél | Ki használhatja |
|---|---|---|---|
| 1 | **Előkészítés** | Adatok (műszaki adatok, hierarchia, 3D modell, alaprajz) betöltése és összerendelése; panorámák feltöltése, térbe helyezése, a tér bejárhatóvá tétele. | Adminisztrátor, Szerkesztő |
| 2 | **Összerendelés** | Adatbázis-elemek (pl. berendezések) hozzárendelése infoponthoz a panorámán, kijelölt 3D modellelemhez, lehatárolt térrészhez – vagy ezek közül többhöz egyszerre. | Adminisztrátor, Szerkesztő |
| 3 | **Megtekintés** | Bejárás, adatbázis-böngészés, alaprajz és 3D nézet; összerendelési és szerkesztési lehetőség nélkül. | Mindenki |

**Közös elv minden üzemmódban:** az adatbázis-grid, a fanézet, az alaprajz, a 3D nézet és a panorámanéző **kölcsönösen szinkronban** vannak: bármelyikben kiválasztott elem a többiben is kijelölődik, és ha az elemnek van alaprajzi megjelenése, az alaprajzon is megjelenik.

---

## 2. Fogalomtár

| Fogalom | Jelentés |
|---|---|
| **Telephely (site)** | A legfelső helyszíni egység; referenciaponttal (alapértelmezett nézet) és alapmagassággal rendelkezik. Egy telepítésen belül tetszőleges számú telephely lehet, nagy földrajzi területen szétszórva. |
| **Szint (level)** | Az a csomópont, amelyhez az alaprajz, a panorámák és a horgonyok magassága viszonyul; magassága a telephely alapmagasságához képest adott. |
| **Csomópont (node)** | A hierarchia bármely eleme (épület, szint, helyiség, technológiai vonal, berendezés stb.). |
| **Csomóponttípus (node type)** | A csomópont fajtája; bővíthető lista, nem fix szintek. |
| **Nézőpont (aspect)** | A hierarchia szempontja: `location` (hol van) vagy `function` (mit csinál, melyik rendszer része). Bővíthető. |
| **Kapcsolat (relation)** | Fák közötti, nem hierarchikus összefüggés (pl. berendezés *located_in* helyiség). |
| **Panoráma** | 360°-os (equirectangular) kép ismert pozícióval és iránnyal. |
| **Tárolási koordináta-rendszer** | WGS84 földrajzi koordináták (EPSG:4326); minden geometria ebben tárolódik. |
| **Relatív magasság (`z_rel_m`)** | Magasság méterben a szint padlójától; az abszolút magasság a telephely alapmagasságából, a szint magasságából és ebből adódik. |
| **Műszaki adat** | A csomópont leíró adatai (pl. gyártó, típus, teljesítmény), JSONB-ben tárolva, a csomóponttípus JSON-sémája szerint. |
| **Horgony (anchor)** | Egy csomópont térbeli megjelenése. Egy csomóponthoz több horgony tartozhat. Fajtái: modellelem, panorámapont (infopont), lehatárolt térrész, térbeli pont. |
| **Infopont** | Panorámaképen elhelyezett jelölő, amely egy csomóponthoz tartozik (a horgony egyik fajtája). |
| **Lehatárolt térrész** | A mesh egy részét körülzáró térfogat, amely a rekorddal való kapcsolatot hordozza; a benne lévő mesh-rész maga az objektum. Alapértelmezett alakja a **doboz** (középpont, méret, függőleges tengely körüli elforgatás), kiegészítő alakja a **sokszöghasáb** (alapsokszög alsó és felső magassággal). Bármilyen modellen működik, BIM-en, fotogrammetrián és szkennelt mesh-en is. |

---

## 3. Szervezetek és szerepkörök [DÖNTÖTT]

A SiteTwin3D **saját, önálló felhasználó- és jogosultságkezeléssel** működik, független a MapManager felhasználóitól (6.3).

### 3.1 Több szervezet
- Egy telepítés **több szervezetet** szolgál ki (felhős és on-prem működésre egyaránt). A modell a **MapManager cégmodelljét követi**: közös adatbázis, az adatok szervezetenként **logikailag** különülnek el (`organization_id`), egy felhasználó egy szervezethez tartozik.
- Szervezetet a **platform-adminisztrátor** hoz létre az első szervezeti adminnal; a szervezet felhasználóit a szervezet adminja kezeli. A platform-adminisztrátor a szervezetek adataihoz nem fér hozzá.
- **Szervezeti szűrő:** az EF Core globális szűrője (`HasQueryFilter`) minden szervezethez tartozó táblát a bejelentkezett felhasználó szervezetére szűr, így egy elfelejtett feltétel sem vezet más szervezet adatához (más szervezet adatára 404). Új sor mentésekor a szervezet automatikusan kitöltődik.
- **MapManager-kapcsolat szervezetenként:** minden SiteTwin3D-szervezethez egy MapManager-cég és annak szolgáltatástokenje rendelhető (6.3). Így a szervezetek csak a saját MapManager-rétegeiket érik el.

### 3.2 Szerepkörök
| Szerepkör | Jogosultság |
|---|---|
| **Platform-adminisztrátor** | Szervezetek létrehozása, letiltása, a szervezeti adminok kezelése, MapManager-kapcsolat beállítása szervezetenként. A szervezetek adataihoz nem fér hozzá. |
| **Szervezeti admin** | A szervezet felhasználói, telephelyei, csomóponttípusai, rajzi rétegei; minden szerkesztés a szervezeten belül. |
| **Szerkesztő** | Hierarchia, panorámák, horgonyok, rajzolt elemek, modellek szerkesztése a hozzárendelt telephelyeken. |
| **Megtekintő** | Csak olvasás a hozzárendelt telephelyeken. |

Szerkesztő és megtekintő jog telephely szintjén rendelhető (`user_site_role`).

### 3.3 Hitelesítés
- A MapManager megoldását követi [FELTÉTELEZÉS]: szervezetazonosító + felhasználónév + jelszó, HttpOnly, `SameSite=Strict` süti, PBKDF2 jelszó-kivonat, ideiglenes jelszó első belépéskor cserével, zárolás ismételt hibás próbálkozás után, biztonsági bélyeg a munkamenetek visszavonásához.
- Alapból minden végpont bejelentkezést kér (`FallbackPolicy`).

---

## 4. Funkcionális követelmények

### 4.1 Hierarchia (FR-H)
- **FR-H1** Csomópont létrehozása, szerkesztése, törlése, áthelyezése másik szülő alá (részfával együtt).
- **FR-H2** A csomópont típusa a `node_type` listából választható; új típus adminisztrátorként felvehető kódmódosítás nélkül.
- **FR-H3** Egy telephelyen belül több gyökér, több fa lehet (pl. egy helyszíni és egy funkcionális fa).
- **FR-H4** A testvérek sorrendje kézzel állítható (`sort_order`).
- **FR-H5** A csomópont műszaki adatai JSONB-ben tárolódnak (`attrs`). A csomóponttípus opcionális JSON-sémát (`attr_schema`) tartalmazhat, amely alapján a backend validál, a felület pedig űrlapot és grid-oszlopokat generál. Új adatmező felvételéhez nem kell kódot módosítani.
- **FR-H6** Csomópontok között típusos kapcsolat hozható létre (`located_in`, `connected_to`, `feeds` stb.).
- **FR-H7** Fanézet a felületen nézőpontonként; keresés név és kód alapján; morzsamenü (breadcrumb).
- **FR-H8** Áthelyezésnél ciklus nem jöhet létre (adatbázis szinten kikényszerítve).

### 4.2 3D megjelenítés (FR-3D)
- **FR-3D1** Telephely 3D modelljének megjelenítése a georeferencia szerint a Cesium földgömbön.
- **FR-3D2** Kültéri nézet: a teljes telephely körbejárható.
- **FR-3D3** Beltéri nézet: szint kiválasztásakor a felette lévő szintek elrejthetők vagy levághatók (clipping).
- **FR-3D4** Elemszintű kiválasztás: kattintásra az elem azonosítója és tulajdonságai megjelennek, és a hierarchiában is kijelölődik.
- **FR-3D5** Kijelölt csomópont (vagy részfa, pl. egy technológiai vonal összes eleme) kiemelése a 3D-ben.
- **FR-3D6** Kamera ráugrás egy csomópontra (fly-to).

### 4.3 Alaprajz (FR-AR)
- **FR-AR1** Szintenkénti alaprajz megjelenítése **WMS-rétegként** [DÖNTÖTT]. Elsődleges forrás a **MapManager** (6.3): vektoros alaprajz (SHP-feltöltésből) vagy georeferált raszteres alaprajz (PDF-ből vagy képből, a MapManager raszterkezelő bővítésével), GeoServeren publikálva. Másodlagos forrás bármely külső WMS. A SiteTwin3D maga nem tárol és nem dolgoz fel alaprajzfájlokat. A forrás **bármely vetületben** lehet (pl. EOV). Egy szinthez több alaprajzréteg is tartozhat (pl. építészeti és gépészeti). **2. fázis / opcionális:** vektoros alaprajz (falak, nyílászárók, helyiségpoligonok), amely a snaphez és a pontosabb elemelhelyezéshez kell.
- **FR-AR1b** Az alaprajzon a horgonnyal rendelkező elemek jelölőként jelennek meg; a jelölő kattintásra kijelöli a rekordot, Előkészítés és Összerendelés üzemmódban áthúzható.
- **FR-AR2** Az alaprajz és a 3D nézet szinkronban: kijelölés, aktuális szint, kamera pozíciója az alaprajzon.
- **FR-AR3** Panorámapontok megjelenítése az alaprajzon; kattintásra a panoráma megnyílik.
- **FR-AR4** Az alaprajz **külön 2D nézetben, OpenLayersben** jelenik meg [DÖNTÖTT]; a Cesium nézettel (panoráma, 3D) a központi kijelölési szolgáltatás tartja szinkronban.
- **FR-AR5** Egyetlen, folyamatosan nagyítható 2D térkép a régiótól a szint alaprajzáig: telephelyek áttekintése nagy területen, átjárás telephelyek között, WMS/WMTS háttértérképekkel (pl. ortofotó, közműtérkép), az épületek és panorámapontok jelölésével.
- **FR-AR6** 2D rajzolás és szerkesztés az alaprajzon (jelölők áthelyezése, dobozok alapjának és sokszögeknek a rajzolása) az OpenLayers beépített illesztésével (Snap) a meglévő elemekre. A rajzolás **a SiteTwin3D-ben, a MapManagertől függetlenül** történik [DÖNTÖTT]: a SiteTwin3D saját OpenLayers-felülete rajzol, és a saját adatbázisába ment; a MapManager szerkesztési szolgáltatása nincs használatban. Az alaprajz (WMS) csak háttérként szolgál.
- **FR-AR7** Rajzolt kiegészítő elemek: az alaprajzra tetszőleges **rajzi rétegek** vehetők fel (pl. zónák, útvonalak, menekülési utak, megjegyzések, a WMS-alaprajzról hiányzó elemek). Egy réteg egy geometriatípust tartalmaz, saját mezőleírással (leíró adatok); az elemek szerkeszthetők, törölhetők. Egy rajzolt elem opcionálisan egy rekordhoz (`node`) köthető. Tárolás a SiteTwin3D adatbázisában, **a MapManager adatszerkezetével azonos mintára** (8.6).

**Megvalósítási megjegyzések (OpenLayers):**
- **Vetület:** az OpenLayers nézet **Web Mercatorban (EPSG:3857)** fut (7. fejezet). A tárolt WGS84 adatok vetítését az OpenLayers beépítetten végzi.
- **WMS-alaprajz:** EPSG:3857-ben kérjük le; a MapManager GeoServere a tárolási vetületből (pl. EOV) maga vetít át. Külső WMS-nél, ha a szerver csak más vetületet ad, az OpenLayers raszter-újravetítéssel jeleníti meg, a proj4-ben regisztrált definícióval (CPU-igényesebb, a feliratok kissé elmosódhatnak).
- **Szintváltás:** szintenként konfigurálható a `floorplan` táblában (8.2): külön réteg szintenként, vagy egy rétegen `CQL_FILTER` (pl. `"szint" = 'F2'`); a MapManager proxyja a CQL-szűrést kérésenként engedi.
- **Elemre kattintás a WMS-en:** `GetFeatureInfo` ugyanazon a proxyn át; ha a kapott elem (`geometria_id`) egy SiteTwin3D-rekordhoz kötött (`node.attrs.mapmanager_ref`), a rekord kijelölődik.
- Minden WMS-kérés a SiteTwin3D backendjén át megy (6.3); a kliens soha nem éri el közvetlenül a MapManagert vagy a GeoServert.

### 4.4 Panorámák és virtuális bejárás (FR-P)
- **FR-P1** Panoráma feltöltése (equirectangular JPEG); pozíció megadása az alaprajzon (tárolva WGS84-ben), szint, kameramagasság a padlótól (`z_rel_m`) és tájolás.
- **FR-P2** A panoráma a hierarchia bármely szintjén lévő csomóponthoz kapcsolható (pl. telephelyhez kültéri felvételként, épülethez, szinthez, helyiséghez vagy berendezéshez). Nincs „csak levélhez” megkötés; ha egy csomópont később gyermeket kap, a panoráma kapcsolata érvényes marad.
- **FR-P3** Egy csomópont alatti összes panoráma lekérdezhető (részfa szerint).
- **FR-P4** Bejárás: a panorámában a szomszédos panorámapontok navigációs jelölőként jelennek meg.
- **FR-P5** Panoráma megnyitásakor a 3D nézet és az alaprajz is a panoráma helyére áll.
- **FR-P6** Tájolás-kalibráció: a panoráma északi iránya (`heading_deg`) egy, a panorámán és az alaprajzon is azonosítható pont (pl. ajtó, sarok) egymáshoz igazításával állítható be. Pontatlan tájolás mellett az infopontok és a navigációs jelölők rossz irányba mutatnak, ezért kalibrálatlan panoráma infopont-elhelyezésre figyelmeztetést ad.
- **FR-P7** Az alaprajzon a megnyitott panoráma nézési iránya és látószöge megjelenik (látókúp).
- **FR-P8** **Személyes adatok kitakarása (GDPR):**
  - Feltöltés után háttérfeladat keresi a kitakarandó területeket. **Alapból csak arcokat** keres. A **rendszámkeresés telephelyenként kapcsolható be** (pl. kültéri, parkolós felvételekhez), és csak **felismert járművek körvonalán belül** keres, így a berendezések azonosító- és típustáblái, a falakon és gépeken lévő feliratok nem jelölődnek meg tévesen.
  - **Emberi jóváhagyás:** a szerkesztő Előkészítés üzemmódban látja a javasolt kitakarásokat, a téveseket visszavonhatja, és kézzel további területeket takarhat ki. A panoráma csak jóváhagyás után válik láthatóvá a Megtekintő szerepkörnek.
  - Az equirectangular kép torzítása miatt a felismerés a kép kockatérkép-lapjain (vagy átfedő, perspektivikus kivágásokon) fut, az eredmény visszavetül a panorámára.
  - A kitakarás a **megjelenítési változatba** égetődik bele (elmosás). Az eredeti, kitakaratlan kép kezelése szervezetenként állítható: (a) a jóváhagyás után törlődik, vagy (b) korlátozott hozzáférésű tárolóban marad, megőrzési idővel. [FELTÉTELEZÉS] Alapértelmezés: (a).
  - Megvalósítás [FELTÉTELEZÉS]: nyílt forrású, helyben futó felismerő modellek (pl. ONNX-formátumú arc- és járműdetektor), háttérfeladatként; a képek nem kerülnek külső szolgáltatáshoz.
  - A technikai kitakarás mellett a szervezeti teendők (munkavállalók tájékoztatása, adatvédelmi hatásvizsgálat) az üzemeltető felelőssége; ezeket a kézikönyvben jelezni kell.

**Megvalósítási megjegyzések a panorámanézőhöz (Cesium beépített panoráma):**
- A panoráma `EquirectangularPanorama` primitívként kerül a jelenetbe; a `transform` a panoráma WGS84 pozíciójából, abszolút magasságából (7. fejezet) és tájolásából (`heading_deg`) számolt mátrix (`Transforms.headingPitchRollToFixedFrame`), a kamera a panoráma középpontjába áll, és csak forgatható.
- A navigációs jelölők és az infopontok saját megvalósításúak (billboard/label a panoráma középpontjából az adott irányba, a panoráma sugarán belül); a beépített osztály ilyen jelölőket nem ad.
- Kattintás a panorámán: `camera.getPickRay()` irányvektorát a panoráma `transform`-jának inverzével helyi rendszerbe kell váltani, ebből adódik a yaw/pitch.
- **Kockázat – képméret:** a nagy felbontású panorámák (pl. 8K × 4K és felette) meghaladhatják a WebGL maximális textúraméretét egyes eszközökön. Megoldás: feltöltéskor a szerver a megjelenítéshez csökkentett felbontású változatot és/vagy kockatérkép-lapokat (`CubeMapPanorama`) állít elő; az eredeti fájl megmarad. Az M3-ban tesztelni kell a célzott böngészőkön és tableteken.
- A funkció a CesiumJS-ben viszonylag új (1.139, 2026. március), ezért a megvalósítás elején egy rövid prototípussal ellenőrizni kell a jelölőket, a kattintást és a teljesítményt.
- [NYITOTT, ideiglenes megoldással] A szomszédsági kapcsolatok automatikusan (távolság alapján) vagy kézzel jöjjenek létre? Megvalósítás (2026-10-23): kézi kapcsolatként tárolva, egy automatikus javaslattal (legközelebbiek szintenként, valós távolság alapján), amely utána kézzel javítható – bármelyik irányba könnyen átállítható.

### 4.5 Rámutatás és illesztés (FR-S)
- **FR-S1** Felületi pont lekérése a 3D-ben (`scene.pickPosition`).
- **FR-S2** Illesztés (snap) a vektoros geometria csúcsaira és éleire, beállítható tűréssel. 2D-ben az OpenLayers Snap interakciója végzi, képernyőpixelben megadott tűréssel; 3D-ben az illesztés a pont körüli ENU-rendszerben, valós méterben történik.
- **FR-S3** Az illesztés a panoráma- és elemelhelyezésnél használható.
- [FELTÉTELEZÉS] Távolság- és területmérés az MVP után.

### 4.6 Adatbázis-grid (FR-G)
- **FR-G1** Táblázatos nézet a csomópontokról, típus szerint szűrve; az oszlopok a csomóponttípus JSON-sémájából generálódnak.
- **FR-G2** Szűrés, rendezés, keresés a műszaki adatokra is (JSONB mezők).
- **FR-G3** Sor kiválasztása kijelöli az elemet a fában, az alaprajzon, a 3D-ben és – ha van infopontja – a panorámán; fordítva is.
- **FR-G4** Jelzi, hogy egy elemnek van-e már horgonya, és milyen fajtájú (pl. ikonnal), így látszik, mi van még összerendeletlenül.
- **FR-G5** Előkészítés üzemmódban a műszaki adatok a gridben szerkeszthetők; megtekintés üzemmódban csak olvashatók.

### 4.7 Összerendelés (FR-A)
- **FR-A1** Folyamat: elem kiválasztása az adatbázis-gridben vagy a fában → horgony létrehozása a 3D nézetben vagy a panorámán.
- **FR-A2** Horgonyfajták:
  - **modellelem**: kattintás egy 3D Tiles elemre (feature ID);
  - **panorámapont (infopont)**: kattintás a panorámaképen (irány yaw/pitch). A panoráma csak irányt ad, ezért a térbeli pozíciót (`position`) **a felhasználó jelöli meg közelítőleg az alaprajzon** [DÖNTÖTT]: az infopont elhelyezése után egy kattintás az alaprajzon, ahol a berendezés látható. Nincs automatikus átszámítás a panorámáról. Ha a szinthez nincs alaprajz, a pozíció ideiglenesen a panoráma helye. A pozíció forrását (`attrs.position_source`: `manual` | `panorama_fallback`) tárolni kell.
    - *Jövőbeli lehetőségek (nem része a tervnek):* sugár és vektoros alaprajz metszése, háromszögelés két panorámából, sugár és 3D mesh metszése (`ray_floorplan` | `triangulated` | `ray_mesh`);
  - **lehatárolt térrész („bedobozolás”)**: a mesh-elem köré doboz kerül, amely a rekorddal való kapcsolatot hordozza, a dobozba eső mesh-rész pedig maga az objektum. A doboz a 3D-ben lerakható (illesztéssel), fogantyúkkal méretezhető és a függőleges tengely körül forgatható. Kiegészítő alakként sokszöghasáb is rajzolható (alapsokszög az alaprajzon vagy a 3D-ben, alsó és felső magassággal). A 3D-ben osztályozó térfogattal (classification) emelődik ki, így a dobozba eső mesh-felület színeződik;
  - **térbeli pont**: pont elhelyezése a 3D-ben, illesztéssel (FR-S2).
- **FR-A3** Egy csomóponthoz több horgony is tartozhat, akár különböző fajtájúak (pl. infopont két panorámán és egy modellelem). Szabálytalan vagy vonalas elemeknél (pl. csővezeték) egy rekordhoz több doboz is rendelhető.
- **FR-A3b** Egymást átfedő vagy egymásba ágyazott dobozoknál kattintáskor a kattintott pontot tartalmazó **legkisebb térfogatú** doboz rekordja választódik ki.
- **FR-A4** Minden horgonynak van pozíciója (WGS84, szint, relatív magasság), így a csomópont az alaprajzon akkor is megjelenik, ha nincs saját geometriája.
- **FR-A5** Horgony törlése és áthelyezése; a csomópont törlésekor a horgonyai is törlődnek.
- **FR-A6** Megtekintés üzemmódban az infopontok és kiemelt elemek kattinthatók, és megnyitják a műszaki adatlapot.

### 4.8 Import (FR-I)
- **FR-I1** 3D modell feltöltése (glTF/GLB vagy kész 3D Tiles csomag); a modell telephelyhez kötődik; a georeferált 3D Tiles a saját elhelyezését hordozza, egy georeferencia nélküli glTF-hez pedig elhelyezést (WGS84 pont, magasság, tájolás) kell megadni (`model_asset.attrs`).
- **FR-I2** Vektoros alaprajz importja szintenként.
- **FR-I4** Térbeli adatok importja bármely vetületből: a forrás vetületét (SRID) meg kell adni vagy a fájlból kell kiolvasni; a backend importkor WGS84-re transzformál (PostGIS `ST_Transform`, GDAL), a forrás SRID-je metaadatként megmarad.
- **FR-I3** Műszaki adatok tömeges importja táblázatból (CSV/XLSX) csomóponttípusonként, a JSON-séma szerinti oszlop-hozzárendeléssel és hibajelentéssel.
- [NYITOTT] Milyen forrásformátumokból érkeznek az adatok? (IFC, DWG/DXF, pontfelhő, fotogrammetria, GeoJSON?)

---

## 5. Nem funkcionális követelmények

| Kód | Követelmény |
|---|---|
| **NFR-1** | Böngésző: Chrome, Edge, Firefox aktuális verziói; WebGL2 szükséges. |
| **NFR-2** | Nagy modellnél is folyamatos megjelenítés: streamelés és LOD (3D Tiles). |
| **NFR-3** | Bináris tartalmak (modellek, panorámák) nem mennek át a .NET backenden; kiszolgálásuk közvetlenül az objektumtárból történik, rövid lejáratú aláírt URL-lel. |
| **NFR-4** | Felület nyelve: magyar, a szövegek erőforrásfájlban, később bővíthetően (i18n). |
| **NFR-5** | A termék neve csak konfigurációban és felületi szövegekben szerepel; kódban, névterekben, adatbázis-sémában, API-útvonalakban semleges belső név használandó (lehetséges későbbi átnevezés miatt). |
| **NFR-6** | Telepíthetőség: **Docker Compose** alapú fejlesztői és éles környezet [DÖNTÖTT], így on-prem és felhős üzemeltetésben egyaránt futtatható. A MapManager telepítési mintáját követi (PostGIS, API, Caddy HTTPS-sel, `deploy/` mappa `.env.example`-lel és mentési szkripttel); a MapManagerrel közös szerveren és Docker-hálózaton is futhat. |
| **NFR-8** | **Egységes megjelenés a MapManagerrel** [DÖNTÖTT]: a felület arculata (színek, tipográfia, fejléc, ikonok, elrendezési minták) és a közös komponensek (pl. adattábla/grid, belépő képernyő, felhasználókezelés) a MapManager Angular-felületéből jönnek. A megvalósításhoz a MapManager repójához olvasási hozzáférés kell. [FELTÉTELEZÉS] Első lépésben átvétel másolással; ha mindkét termék tartósan közös komponenseket használ, később közös Angular-könyvtárba szervezhetők. |
| **NFR-9** | **Adatvédelem (GDPR) a panorámákon** [DÖNTÖTT]: lásd FR-P8. |
| **NFR-7** | Hitelesítés: **saját felhasználókezelés** [DÖNTÖTT], a MapManagertől független. [NYITOTT] később szervezeti SSO (pl. Entra ID / OIDC) igény szerint. |

---

## 6. Architektúra [DÖNTÖTT]

Háromrétegű felépítés: **CesiumJS frontend – .NET backend – PostgreSQL (PostGIS) adattárolás**, kiegészítve egy S3-kompatibilis objektumtárral.

```
┌───────────────────────────────────────────────────────┐
│  SiteTwin3D frontend (Angular)                        │
│  - Cesium: panoráma, 3D   - OpenLayers: 2D, rajzolás  │
│  - fanézet, grid, adatlap                             │
└──────────┬──────────────────────────┬─────────────────┘
           │ REST/JSON + WMS          │ HTTPS (aláírt URL)
┌──────────▼───────────────┐    ┌─────▼────────────────────┐
│ SiteTwin3D backend       │    │ Objektumtár (S3)         │
│ (.NET 10, ASP.NET Core)  │───▶│ - 3D Tiles, glTF         │
│ - hierarchia, horgonyok  │    │ - panorámák              │
│ - saját jogosultság      │    └──────────────────────────┘
│ - WMS-proxy ─────────────┼──────────┐ szolgáltatástoken
└──────────┬───────────────┘          │ (csak olvasás)
           │ EF Core + Npgsql   ┌─────▼──────────────────────────┐
┌──────────▼───────────────┐    │ MapManager (külső rendszer)    │
│ PostgreSQL + PostGIS     │    │ - API-proxy (/geoserver/wms)   │
│ + ltree (SiteTwin3D DB)  │    │ - GeoServer: alaprajzok,       │
│ - node, anchor, panorama │    │   térképi rétegek (vektor,     │
│ - floorplan (hivatkozás) │    │   raszter)                     │
└──────────────────────────┘    │ - saját PostGIS, felhasználók  │
                                └────────────────────────────────┘
```

### 6.3 Integráció a MapManagerrel [DÖNTÖTT]

**Feladatmegosztás**
- **MapManager:** a 2D térképi adatok gazdája. Alaprajzok és térképi rétegek feltöltése, feldolgozása, tárolása és GeoServeren való publikálása: vektoros (SHP) és – a tervezett bővítéssel – raszteres (georeferált PDF/kép) forrásból.
- **SiteTwin3D:** a térképi szolgáltatások **fogyasztója**. Nem tárol és nem dolgoz fel alaprajzfájlokat; a szintekhez csak a MapManager publikációira (`munkaterület:réteg`) és a szűrésükre hivatkozik.

**Független működés**
- **Jogosultság:** a SiteTwin3D saját felhasználó- és jogosultságkezeléssel működik (3. fejezet, NFR-7); a MapManager felhasználóit nem használja és nem ismeri.
- **Rajzolás:** a SiteTwin3D saját OpenLayers-felülete rajzol, és a saját adatbázisába ment (FR-AR6). A MapManager szerkesztési végpontjai (`/api/publications/{id}/edit/*`) nincsenek használatban.
- **Adatbázis:** a két rendszer adatbázisa külön van; nincs közös tábla és nincs közvetlen adatbázis-hozzáférés egymás adataihoz.

**Hozzáférés a térképi szolgáltatásokhoz**
- A SiteTwin3D kliens a WMS-kéréseket **a SiteTwin3D backendjéhez** küldi (`/api/v1/maps/wms`). A backend:
  1. ellenőrzi a SiteTwin3D-felhasználó jogát az adott telephelyhez, illetve hogy a kért réteg szerepel-e a telephely `floorplan` vagy háttérréteg-konfigurációjában;
  2. továbbítja a kérést a MapManager proxyjához (`/geoserver/wms`) egy **szolgáltatástokennel**;
  3. a választ változatlanul visszaadja.
- Engedélyezett kérések: WMS `GetMap`, `GetFeatureInfo`, `GetLegendGraphic` (a MapManager proxyjának szabályai szerint, pl. `CQL_FILTER` engedett, `SLD_BODY` tiltott).
- A MapManager marad a rétegjogosultság egyetlen kapuőre: a szolgáltatásfiók csak a hozzá rendelt cég publikált rétegeit éri el, csak olvasásra.
- Konfiguráció a SiteTwin3D-ben **szervezetenként** (`organization.mapmanager_url`, `mapmanager_token_ref`): minden SiteTwin3D-szervezet a saját MapManager-cégének tokenjével fér hozzá a térképi rétegekhez. A token titokkezelőben (vagy titkosított beállításban) van, az adatbázisban csak a hivatkozása.

**Kapcsolat a rekordok között (opcionális)**
- Ha egy SiteTwin3D-rekord (pl. berendezés) a MapManager valamelyik rétegén is szerepel (pl. közműtérkép pontjaként), a `node.attrs.mapmanager_ref` (`{ "layer": "munkaterület:réteg", "featureId": "réteg.geometria_id" }`) köti össze őket. A WMS-en kattintva (`GetFeatureInfo`) a hozzá tartozó SiteTwin3D-rekord kijelölődik.

**Leíró adatok a MapManagerből [DÖNTÖTT: élő hivatkozás a kiindulás]**
- A SiteTwin3D-rekordok (pl. berendezések) leíró adatai **a MapManager rétegeiből köthetők**; ez a legegyszerűbb kiindulás, mert az adatok feltöltése, tárolása és karbantartása már a MapManagerben megoldott.
- **Működés:**
  - Egy MapManager-réteg kiválasztásakor a SiteTwin3D az elemekhez **vázrekordokat** hoz létre a `node` táblában: típus, név (egy választott mezőből), szülő (pl. telephely vagy szint), valamint `node.attrs.mapmanager_ref` (`{ "layer": "munkaterület:réteg", "featureId": "réteg.geometria_id" }`). A műszaki adatok **nem másolódnak át**.
  - Az adatlap és a grid a kapcsolt elem attribútumait **kérésenként olvassa** a MapManagerből (WFS `GetFeature` `featureID`-vel, illetve lapozva és `CQL_FILTER`-rel a gridhez), a WMS-proxyval azonos úton (szolgáltatástoken).
  - A WMS-en kattintva (`GetFeatureInfo`) a `featureId` alapján a SiteTwin3D-rekord kijelölődik; fordítva, a SiteTwin3D-rekord kijelölésekor a MapManager-réteg eleme kiemelhető.
  - A horgonyok (infopont, doboz stb.) és a hierarchia a vázrekordokhoz kötődnek, tehát a SiteTwin3D saját adatai.
- **Következmények:**
  - Az adat egy helyen él, mindig friss; a SiteTwin3D-ben **csak olvasható**, módosítás a MapManagerben történik.
  - A MapManager elérhetetlensége esetén az adatlap üres marad (hibajelzéssel), a hierarchia, a panorámák és a horgonyok használhatók.
  - A MapManagerben törölt elem vázrekordja „árva” lesz: a szinkronizálás jelzi, törlés csak megerősítéssel.
  - A grid MapManager-rétegből származó rekordoknál a réteg mezőit mutatja (a MapManager mezőlistája szerint), a SiteTwin3D-ben felvitt rekordoknál a `node_type.attr_schema` szerintieket.
- **Később választható:** importálás (az attribútumok a `node.attrs`-ba másolódnak, igény szerinti újraszinkronizálással), ha a függetlenség vagy a teljesítmény fontosabb lesz. A kapcsolat mindkét módnál a `mapmanager_ref`, így az átállás nem igényel adatmodell-változást.
- A WMS-proxy (`/api/v1/maps/wms`) mellé ehhez egy **WFS-proxy** is kell (`/api/v1/maps/wfs`, csak `GetFeature` és `DescribeFeatureType`), ugyanazokkal az ellenőrzésekkel.

**Szükséges MapManager-bővítések** (a MapManager projektben valósulnak meg, a SiteTwin3D ezekre épít):
1. **Raszteres adatforrás kezelése:** PDF/kép feltöltése, georeferálás illesztőpontokkal (bármely vetületben), GeoTIFF előállítása, publikálás GeoServer-raszterrétegként.
2. **Szolgáltatástokenes hozzáférés a proxyhoz:** céghez rendelt, csak olvasási jogú API-kulcs a `/geoserver/wms` és `/geoserver/wfs` útvonalhoz, a jelenlegi sütis belépés mellett; a token visszavonható, a használat naplózott.
3. [NYITOTT] **Publikációk listázása tokennel:** a SiteTwin3D konfigurációs felületén a választható rétegek listájához (pl. `GET /api/publications` tokennel, csak olvasásra).

### 6.1 Technológiai javaslat
| Réteg | Technológia | Megjegyzés |
|---|---|---|
| Frontend | **Angular** [DÖNTÖTT], TypeScript, CesiumJS | Grid és megjelenés: a MapManager felületéből átvéve (NFR-8) |
| Alaprajz, 2D térkép (kliens) | **OpenLayers** [DÖNTÖTT], proj4 | 2D rajzolás és szerkesztés (Draw, Modify, Snap), WMS/WMTS rétegek megjelenítése |
| 2D térképszolgáltatás | **MapManager + GeoServer** [DÖNTÖTT], külső rendszer | Alaprajzok és térképi rétegek tárolása, feldolgozása, publikálása (6.3) |
| Panorámanéző | **CesiumJS beépített panorámája** (`EquirectangularPanorama`, igény esetén `CubeMapPanorama`), CesiumJS ≥ 1.139 [DÖNTÖTT] | Nincs külön panorámakönyvtár; a 2. fázisban a panoráma és a 3D modell ugyanabban a jelenetben, egymáshoz igazítva jeleníthető meg |
| Backend | .NET 10 (LTS), ASP.NET Core Web API, EF Core, Npgsql, NetTopologySuite | Npgsql támogatja az `ltree` és a PostGIS típusokat |
| Adatbázis | PostgreSQL 16+ PostGIS és ltree kiterjesztéssel | |
| Objektumtár | **SeaweedFS** (fejlesztés, on-prem) [DÖNTÖTT] / bármely S3-kompatibilis | A backend csak S3 API-t használ, a tár cserélhető (pl. felhőben Amazon S3) |
| Konténerizálás | Docker Compose | |

### 6.2 Tárolási döntés [DÖNTÖTT]
- **Képek és modellek az objektumtárban**, az adatbázisban csak metaadat és hivatkozás (`storage_key`), valamint hash.
- Indoklás: az adatbázis nem hízik feleslegesen, a mentés egyszerűbb, a kiszolgálás cache-elhető és CDN mögé tehető, a .NET nem továbbít bájtokat.
- Az árva fájlok kezelésére háttérfeladat szükséges (az objektumtár és az adatbázis összevetése).

---

## 7. Koordináta-rendszerek [DÖNTÖTT]

| Szerep | Rendszer | Megjegyzés |
|---|---|---|
| **Tárolás** (PostGIS) | WGS84, **EPSG:4326** (hosszúság, szélesség) | Egységes rendszer bármely régióra; több telephely nagy területen, egymáshoz képest a helyükön |
| **2D megjelenítés** (OpenLayers) | Web Mercator, **EPSG:3857** | Szögtartó (konform): az alaprajzok szögben nem torzulnak; a 4326 ↔ 3857 átváltás az OpenLayersben beépített |
| **3D és panoráma** (Cesium) | WGS84 / ECEF | A Cesium natív rendszere |
| **Források** (alaprajz, WMS, import) | **Bármilyen** (pl. EOV, EPSG:23700) | Importkor a backend WGS84-re transzformál; WMS-nél lehetőleg EPSG:3857-ben kérünk, különben OpenLayers raszter-újravetítés |

### 7.1 Magasság
- A telephelynek van **alapmagassága** (`site.base_height_m`, ellipszoidi magasság méterben).
- A szint (level) csomópont magassága a telephely alapmagasságához képest: `attrs.elevation_m`, `attrs.height_m`.
- Panorámák és horgonyok: **`level_id`** (melyik szinten vannak) és **`z_rel_m`** (magasság a szint padlójától). Kültéri elemeknél a `level_id` üres, a `z_rel_m` a telephely alapmagasságához viszonyul.
- **Abszolút magasság (Cesium):** `base_height_m + level.elevation_m + z_rel_m`.
- Indoklás: a felhasználók szint- és padlómagasságban gondolkodnak; ha egy szint magassága módosul, minden rajta lévő elem együtt mozog. A `level_id` azért is kell, mert a funkcionális fában lévő elemek (pl. technológiai vonal berendezései) szülője nem szint, így a hierarchiából nem olvasható ki, melyik alaprajzon jelenjenek meg.

### 7.2 Irányok
- Minden irány (`heading_deg`) a **földrajzi északtól** mért szög, az óramutató járásával megegyezően.
- Web Mercatorban a térkép „fel” iránya mindenhol a földrajzi észak, ezért a panorámák tájolásánál nincs vetületi (meridiánkonvergencia) korrekció. (EOV-ban lenne.)

### 7.3 Méretek és távolságok – fontos buktató
- A Web Mercator **nem távolságtartó**: Magyarország szélességén (~47,5°) egy EPSG:3857-egység kb. 0,68 valós méter, és ez az arány a szélességgel változik.
- Ezért metrikus értéket (doboz mérete, magasság, mérés, illesztési tűrés méterben) **soha nem szabad EPSG:3857-koordinátákból számolni**:
  - a méretek és magasságok valós méterben, külön mezőkben tárolódnak;
  - távolság- és területszámítás PostGIS `geography` típussal, vagy a pont körüli ENU-rendszerben (`Cesium.Transforms.eastNorthUpToFixedFrame`);
  - a 2D illesztési tűrés képernyőpixelben adott.
- **Cesium-pontosság:** a dobozok és panorámák modellmátrixa a saját középpontjukban számolt ENU-rendszerből készül, így nincs lebegőpontos pontatlanság a nagy ECEF-értékek miatt.

---

## 8. Adatmodell

### 8.1 Elvek [DÖNTÖTT]
- **Egytáblás hierarchia**: minden elem a `node` táblában; a fajtát a `type_id` adja, nem a tábla.
- **Több fa** ugyanabban a táblában, a nézőpontot a csomóponttípus `aspect` mezője jelöli.
- A fák közötti összefüggéseket a `node_relation` tábla írja le; így a vonalas létesítmények (pl. szennyvíztisztító technológiai sor), amelyek több épületen és szinten átívelnek, a funkcionális fában maradnak, a helyszínhez pedig kapcsolattal kötődnek.
- `parent_id` az egyszerű szerkesztéshez, `path` (ltree) a gyors részfa- és ős-lekérdezésekhez. A `path` címkéi azonosítókból állnak, nem nevekből.

### 8.2 Séma (kiindulási DDL)

```sql
CREATE EXTENSION IF NOT EXISTS ltree;
CREATE EXTENSION IF NOT EXISTS postgis;

-- Minden geometria WGS84-ben (EPSG:4326); magasság külön mezőkben, a szinthez viszonyítva (7. fejezet)

-- Szervezetek és felhasználók (3. fejezet; a MapManager cégmodelljének mintájára)
CREATE TABLE organization (
    id                     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code                   text NOT NULL UNIQUE,     -- belépési szervezetazonosító
    name                   text NOT NULL,
    active                 boolean NOT NULL DEFAULT true,
    mapmanager_url         text,                     -- MapManager alap-URL
    mapmanager_token_ref   text,                     -- a szolgáltatástoken HIVATKOZÁSA a titokkezelőben, nem maga a token
    created_at             timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE app_user (
    id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    organization_id   bigint REFERENCES organization(id),  -- NULL: platform-adminisztrátor
    username          text NOT NULL,
    username_lower    text NOT NULL,
    display_name      text NOT NULL,
    password_hash     text NOT NULL,
    role              text NOT NULL,          -- 'PlatformAdmin' | 'OrgAdmin' | 'Editor' | 'Viewer'
    active            boolean NOT NULL DEFAULT true,
    must_change_password boolean NOT NULL DEFAULT true,
    failed_logins     int NOT NULL DEFAULT 0,
    locked_until      timestamptz,
    security_stamp    text NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    last_login        timestamptz,
    UNIQUE (organization_id, username_lower)
);

CREATE TABLE user_site_role (
    user_id  bigint NOT NULL REFERENCES app_user(id) ON DELETE CASCADE,
    site_id  bigint NOT NULL REFERENCES site(id) ON DELETE CASCADE,
    role     text   NOT NULL,                 -- 'Editor' | 'Viewer'
    PRIMARY KEY (user_id, site_id)
);

CREATE TABLE site (
    id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    organization_id bigint NOT NULL REFERENCES organization(id),
    name           text NOT NULL,
    ref_point      geometry(Point, 4326) NOT NULL,      -- referenciapont (alapértelmezett nézet)
    base_height_m  double precision NOT NULL DEFAULT 0, -- alapmagasság (ellipszoidi), ehhez viszonyulnak a szintek
    extent         geometry(Polygon, 4326),             -- telephely határa (áttekintő térkép)
    attrs          jsonb NOT NULL DEFAULT '{}'
);
CREATE INDEX site_extent ON site USING gist (extent);
CREATE INDEX site_org    ON site (organization_id);
-- A telephely alatti táblák (node, panorama, anchor, draw_* stb.) a site_id-n át tartoznak szervezethez.
-- A globális szervezeti szűrőhöz ezekben is legyen organization_id (denormalizálva, triggerrel vagy
-- mentéskor kitöltve), vagy a szűrő a site-on át csatlakozzon [a megvalósítás döntése, tesztelve].
-- A node_type szervezetenként bővíthető: organization_id NULL = közös alaptípus.

CREATE TABLE node_type (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    organization_id bigint REFERENCES organization(id),  -- NULL: közös alaptípus
    code            text NOT NULL,       -- 'building', 'level', 'room', 'process_line', 'equipment' ...
    name            text NOT NULL,
    aspect          text NOT NULL,       -- 'location' | 'function'
    is_level        boolean NOT NULL DEFAULT false,  -- szint típus (alaprajz, panoráma, horgony magassága ehhez)
    attr_schema     json,                -- JSON Schema a műszaki adatokhoz; json (nem jsonb), mert a mezők sorrendje
                                         -- az űrlap és a grid oszlopsorrendje, a jsonb pedig átrendezi a kulcsokat
    UNIQUE NULLS NOT DISTINCT (organization_id, code)
);
-- Megvalósítás: text kulcs helyett bigint + szervezetenként egyedi code, mert a szervezetenként bővíthető
-- típusok kódjai ütköznének (2026-10-23).

CREATE TABLE node (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    site_id     bigint NOT NULL REFERENCES site(id) ON DELETE CASCADE,
    parent_id   bigint REFERENCES node(id),
    path        ltree NOT NULL,       -- triggerrel karbantartva
    type_id     bigint NOT NULL REFERENCES node_type(id),
    code        text,                 -- szakmai jelölés (pl. KKS / IEC 81346)
    name        text NOT NULL,
    sort_order  int  NOT NULL DEFAULT 0,
    geom        geometry(Geometry, 4326),  -- saját 2D geometria, ha van (pl. helyiségpoligon)
    attrs       jsonb NOT NULL DEFAULT '{}'  -- műszaki adatok, a node_type.attr_schema szerint
);
CREATE INDEX node_path_gist ON node USING gist (path);
CREATE INDEX node_parent    ON node (parent_id);
CREATE INDEX node_site      ON node (site_id);
CREATE INDEX node_geom      ON node USING gist (geom);
CREATE INDEX node_attrs     ON node USING gin (attrs jsonb_path_ops);

CREATE TABLE node_relation (
    from_id  bigint NOT NULL REFERENCES node(id) ON DELETE CASCADE,
    to_id    bigint NOT NULL REFERENCES node(id) ON DELETE CASCADE,
    kind     text   NOT NULL,         -- 'located_in', 'connected_to', 'feeds' ...
    PRIMARY KEY (from_id, to_id, kind)
);

CREATE TABLE panorama (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    node_id      bigint NOT NULL REFERENCES node(id),
    storage_key  text   NOT NULL,
    sha256       text   NOT NULL,
    position     geometry(Point, 4326) NOT NULL,
    level_id     bigint REFERENCES node(id),      -- szint (NULL: kültér)
    z_rel_m      real NOT NULL DEFAULT 1.6,       -- kameramagasság a szint padlójától
    heading_deg  real,                            -- a kép közepének iránya a földrajzi északtól
    captured_at  timestamptz,
    privacy_status text NOT NULL DEFAULT 'pending', -- 'pending' | 'review' | 'approved' (FR-P8)
    privacy_regions jsonb NOT NULL DEFAULT '[]',    -- javasolt és kézi kitakarások (yaw/pitch tartományok, forrás, elfogadva)
    display_key  text,                             -- a kitakart megjelenítési változat kulcsa az objektumtárban
    original_retained_until timestamptz            -- NULL: az eredeti törölve
);
CREATE INDEX panorama_node     ON panorama (node_id);
CREATE INDEX panorama_level    ON panorama (level_id);
CREATE INDEX panorama_position ON panorama USING gist (position);

CREATE TABLE panorama_link (               -- bejárási szomszédság
    from_id  bigint NOT NULL REFERENCES panorama(id) ON DELETE CASCADE,
    to_id    bigint NOT NULL REFERENCES panorama(id) ON DELETE CASCADE,
    PRIMARY KEY (from_id, to_id)
);

CREATE TABLE model_asset (                 -- 3D modellek és tile-csomagok
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    site_id      bigint NOT NULL REFERENCES site(id) ON DELETE CASCADE,
    node_id      bigint REFERENCES node(id),  -- pl. épülethez kötve
    kind         text NOT NULL,               -- 'gltf' | '3dtiles'
    segmented    boolean NOT NULL DEFAULT false, -- vannak-e kijelölhető elemek (feature ID-k)
    storage_key  text NOT NULL,               -- glTF fájl vagy tileset.json kulcsa
    attrs        jsonb NOT NULL DEFAULT '{}'
);

-- Szintenkénti alaprajzforrások: csak HIVATKOZÁS WMS-rétegekre, fájlt nem tárol
-- (egy szinthez több réteg is tartozhat, pl. építészet, gépészet)
CREATE TABLE floorplan (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    level_id        bigint NOT NULL REFERENCES node(id) ON DELETE CASCADE,
    source          text NOT NULL,            -- 'mapmanager' | 'external_wms'
    name            text NOT NULL,            -- megjelenített név, pl. 'Építészet'
    layer           text NOT NULL,            -- WMS LAYERS, MapManagernél 'munkaterület:réteg'
    cql_filter      text,                     -- szintválasztás egy közös rétegen, pl. "szint" = 'F2'
    wms_params      jsonb NOT NULL DEFAULT '{}', -- egyéb paraméterek (STYLES, ELEVATION stb.)
    external_url    text,                     -- csak 'external_wms' esetén
    sort_order      int NOT NULL DEFAULT 0,
    opacity         real NOT NULL DEFAULT 1,
    attrs           jsonb NOT NULL DEFAULT '{}',
    CONSTRAINT floorplan_source CHECK (
        source = 'mapmanager' OR (source = 'external_wms' AND external_url IS NOT NULL)
    )
);
CREATE INDEX floorplan_level ON floorplan (level_id);

-- Telephely- vagy telepítésszintű háttérrétegek (ortofotó, közmű stb.), ugyanígy hivatkozásként
CREATE TABLE map_layer (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    site_id         bigint REFERENCES site(id) ON DELETE CASCADE,  -- NULL: minden telephelyen
    source          text NOT NULL,            -- 'mapmanager' | 'external_wms'
    name            text NOT NULL,
    layer           text NOT NULL,
    wms_params      jsonb NOT NULL DEFAULT '{}',
    external_url    text,
    sort_order      int NOT NULL DEFAULT 0,
    visible_default boolean NOT NULL DEFAULT true
);

-- Horgonyok: egy csomópont térbeli megjelenései (több is lehet, eltérő fajtával)
CREATE TABLE anchor (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    node_id         bigint NOT NULL REFERENCES node(id) ON DELETE CASCADE,
    kind            text   NOT NULL,      -- 'model_feature' | 'panorama_point' | 'region' | 'point'
    -- model_feature
    model_asset_id  bigint REFERENCES model_asset(id) ON DELETE CASCADE,
    feature_ref     text,                 -- STABIL elemazonosító a modell metaadatából (pl. IFC GlobalId), NEM a csempénkénti feature-sorszám
    -- panorama_point (infopont)
    panorama_id     bigint REFERENCES panorama(id) ON DELETE CASCADE,
    yaw_deg         real,
    pitch_deg       real,
    -- region (lehatárolt térrész)
    region_shape    text,                 -- 'box' (alapértelmezett) | 'prism'
    -- box: középpont = position + z_rel_m; méretek VALÓS méterben; heading a földrajzi északtól
    size_x          real,
    size_y          real,
    size_z          real,
    heading_deg     real,
    -- prism: alapsokszög + magasságtartomány (a szint padlójától, méterben)
    footprint       geometry(Polygon, 4326),
    z_min           real,
    z_max           real,
    -- minden fajtánál: megjelenítési pozíció (alaprajz, címke; doboznál a középpont)
    position        geometry(Point, 4326) NOT NULL,
    level_id        bigint REFERENCES node(id),   -- szint (NULL: kültér)
    z_rel_m         real NOT NULL DEFAULT 0,      -- magasság a szint padlójától
    attrs           jsonb NOT NULL DEFAULT '{}',
    CONSTRAINT anchor_kind_fields CHECK (
        (kind = 'model_feature'  AND model_asset_id IS NOT NULL AND feature_ref IS NOT NULL) OR
        (kind = 'panorama_point' AND panorama_id IS NOT NULL AND yaw_deg IS NOT NULL AND pitch_deg IS NOT NULL) OR
        -- IS NOT NULL kell: NULL-lal való összehasonlítás NULL, és a NULL-ra kiértékelődő CHECK átengedne
        (kind = 'region' AND region_shape = 'box'
                         AND size_x IS NOT NULL AND size_y IS NOT NULL AND size_z IS NOT NULL
                         AND size_x > 0 AND size_y > 0 AND size_z > 0 AND heading_deg IS NOT NULL) OR
        (kind = 'region' AND region_shape = 'prism'
                         AND footprint IS NOT NULL AND z_min IS NOT NULL AND z_max IS NOT NULL AND z_max > z_min) OR
        (kind = 'point')
    )
);
CREATE INDEX anchor_node     ON anchor (node_id);
CREATE INDEX anchor_feature  ON anchor (model_asset_id, feature_ref);
CREATE INDEX anchor_panorama ON anchor (panorama_id);
CREATE INDEX anchor_position ON anchor USING gist (position);
CREATE INDEX anchor_level    ON anchor (level_id);
```

> **A kapcsolat iránya [DÖNTÖTT]:** a horgony mindig az adatbázis-rekordhoz (`node`) tartozik. Ugyanaz a rekord több, különböző fajtájú horgonnyal is megjelenhet (pl. infopont egy panorámán és modellelem a 3D-ben). A megjelenítési oldal azonosítói csak a horgonyban szerepelnek, a `node` rekord nem hivatkozik rájuk.
>
> A horgonytábla oldja fel az „infopont vagy modellelem” kétértelműséget: egy csomópont bármelyik, vagy egyszerre több fajta horgonnyal is rendelkezhet. A `position` mindig kitöltött, így minden horgonnyal rendelkező elem megjeleníthető az alaprajzon.

### 8.3 A `path` karbantartása (trigger)

```sql
CREATE OR REPLACE FUNCTION node_set_path() RETURNS trigger AS $$
DECLARE
    parent_path ltree;
BEGIN
    IF NEW.parent_id IS NULL THEN
        NEW.path := NEW.id::text::ltree;
    ELSE
        SELECT path INTO parent_path FROM node WHERE id = NEW.parent_id;
        IF parent_path IS NULL THEN
            RAISE EXCEPTION 'Parent node % not found', NEW.parent_id;
        END IF;
        IF TG_OP = 'UPDATE' AND parent_path <@ OLD.path THEN
            RAISE EXCEPTION 'Cycle: node % cannot be moved under its own subtree', NEW.id;
        END IF;
        NEW.path := parent_path || NEW.id::text;
    END IF;
    RETURN NEW;
END $$ LANGUAGE plpgsql;

CREATE TRIGGER node_path_insert
    BEFORE INSERT ON node
    FOR EACH ROW EXECUTE FUNCTION node_set_path();

-- Csak akkor fut, ha a szülő változik (a leszármazottak path-frissítése nem indítja újra)
CREATE TRIGGER node_path_move
    BEFORE UPDATE OF parent_id ON node
    FOR EACH ROW EXECUTE FUNCTION node_set_path();

CREATE OR REPLACE FUNCTION node_move_subtree() RETURNS trigger AS $$
BEGIN
    IF NEW.path IS DISTINCT FROM OLD.path THEN
        UPDATE node
           SET path = NEW.path || subpath(path, nlevel(OLD.path))
         WHERE path <@ OLD.path AND id <> NEW.id;
    END IF;
    RETURN NULL;
END $$ LANGUAGE plpgsql;

CREATE TRIGGER node_path_cascade
    AFTER UPDATE OF parent_id ON node
    FOR EACH ROW EXECUTE FUNCTION node_move_subtree();
```

> Megjegyzés a megvalósításhoz: a triggereket integrációs teszttel ellenőrizni kell (beszúrás, áthelyezés több szinttel, ciklus elutasítása). A szülő és a gyermek ugyanahhoz a telephelyhez kell tartozzon; ezt is ellenőrizni kell (trigger vagy alkalmazásréteg).

### 8.4 Tipikus lekérdezések

```sql
-- Egy épület teljes részfája, sorrendben
SELECT * FROM node WHERE path <@ '1.14' ORDER BY path, sort_order;

-- Morzsamenü: egy csomópont összes őse
SELECT * FROM node WHERE path @> '1.14.203.517' ORDER BY nlevel(path);

-- Egy csomópont alatti összes panoráma, bármilyen mélyen
SELECT p.* FROM panorama p
JOIN node n ON n.id = p.node_id
WHERE n.path <@ '1.14';

-- Technológiai vonal berendezései és a helyiség, ahol vannak
SELECT e.name AS equipment, r.name AS room
FROM node e
JOIN node_relation rel ON rel.from_id = e.id AND rel.kind = 'located_in'
JOIN node r ON r.id = rel.to_id
WHERE e.path <@ '2.40';
```

### 8.5 Rendezés
- Testvérek közti szakmai sorrend: `sort_order`.
- Fa szerinti bejárás: `path`.
- A helyszíni és a funkcionális fa rendezése egymástól független; így a technológiai sorrend (pl. a tisztítási folyamat lépései) és az épület/szint sorrend egyszerre érvényesülhet.

### 8.6 Rajzolt kiegészítő elemek [DÖNTÖTT]

**Elv:** a rajzolt elemek tárolása a **MapManager adatszerkezetét követi** (feltöltés → rekord → geometria), hogy később a MapManagerbe vihetők vagy GeoServeren publikálhatók legyenek egyszerű leképezéssel:

| MapManager | SiteTwin3D | Szerep |
|---|---|---|
| `feltoltes` (`adatstruktura jsonb`) | `draw_layer` (`structure jsonb`) | réteg és mezőleírása |
| `rekord` (`attributumok jsonb`, `verzio`) | `draw_record` (`attrs jsonb`, `version`) | leíró adatok |
| `geometria` (1:N a rekordhoz) | `draw_geometry` (1:N a rekordhoz) | térbeli adatok külön táblában |

**Eltérés a MapManagertől:** a geometria a SiteTwin3D tárolási rendszerében, **WGS84-ben (EPSG:4326)** van (7. fejezet), nem a forrás eredeti vetületében. Rajzolás EPSG:3857-ben történik, mentéskor a backend 4326-ra vetít.

**Elhatárolás a horgonyoktól:** a horgony (`anchor`) egy rekord térbeli *megjelenése* (infopont, doboz stb.), a rajzolt elem önálló rajzi tartalom. Ha egy rajzolt elem egy rekord alaprajzi kontúrja, a `draw_record.node_id` köti a rekordhoz.

```sql
CREATE TABLE draw_layer (
    id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    site_id        bigint NOT NULL REFERENCES site(id) ON DELETE CASCADE,
    level_id       bigint REFERENCES node(id) ON DELETE CASCADE,  -- NULL: telephelyszintű (kültéri) réteg
    name           text NOT NULL,
    geometry_type  text NOT NULL,          -- 'Point' | 'MultiLineString' | 'MultiPolygon' (rétegenként egy)
    structure      jsonb NOT NULL DEFAULT '{}',  -- mezőleírás: név, típus, hossz, tizedes, álnév (a MapManager adatstruktura mintájára)
    style          jsonb NOT NULL DEFAULT '{}',  -- egyszerű megjelenés (szín, vonalvastagság, ikon)
    sort_order     int NOT NULL DEFAULT 0,
    created_at     timestamptz NOT NULL DEFAULT now(),
    created_by     bigint
);
CREATE INDEX draw_layer_site  ON draw_layer (site_id);
CREATE INDEX draw_layer_level ON draw_layer (level_id);

CREATE TABLE draw_record (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    layer_id     bigint NOT NULL REFERENCES draw_layer(id) ON DELETE CASCADE,
    seq          int NOT NULL,                  -- sorszám a rétegen belül
    attrs        jsonb NOT NULL DEFAULT '{}',   -- leíró adatok a structure szerint
    node_id      bigint REFERENCES node(id) ON DELETE SET NULL,  -- opcionális kötés rekordhoz
    version      int NOT NULL DEFAULT 1,        -- optimista zárolás
    modified_at  timestamptz NOT NULL DEFAULT now(),
    modified_by  bigint
);
CREATE INDEX draw_record_layer ON draw_record (layer_id);
CREATE INDEX draw_record_node  ON draw_record (node_id);

CREATE TABLE draw_geometry (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    record_id  bigint NOT NULL REFERENCES draw_record(id) ON DELETE CASCADE,
    layer_id   bigint NOT NULL REFERENCES draw_layer(id) ON DELETE CASCADE,  -- mint a MapManagerben: rétegenkénti szűréshez és indexhez
    geom       geometry(Geometry, 4326) NOT NULL
);
CREATE INDEX draw_geometry_record ON draw_geometry (record_id);
CREATE INDEX draw_geometry_geom   ON draw_geometry USING gist (geom);
CREATE INDEX draw_geometry_layer  ON draw_geometry (layer_id);
```

**Szerkesztés (a MapManager szerkesztési mintájára):**
- Lekérés: a réteg elemei befoglaló téglalap szerint, GeoJSON-ként EPSG:3857-ben, rekordverzióval.
- Mentés egy tranzakcióban: `inserts`, `updates`, `deletes` (GeoJSON, EPSG:3857). Ellenőrzés a `structure` alapján (ismeretlen mező, típus, hossz) és a geometriára (rétegtípus, `ST_IsValid`; egyrészes elem többrészes rétegen `ST_Multi`). Minden hiba egyszerre jön vissza (400), ilyenkor semmi nem mentődik.
- Ütközés: módosítás és törlés csak egyező `version` mellett, különben 409 az érintett azonosítókkal; sikeres mentéskor a verzió nő.
- A törölt geometria rekordja is törlődik, ha nincs más geometriája.

**Megjelenítés:** kis adatmennyiségre tervezve, a kliens OpenLayers vektorrétegként tölti az API-ból (befoglaló téglalap szerint); GeoServer nem kell hozzá. [FELTÉTELEZÉS] Ha egy réteg később nagy lesz, vagy a MapManager-ügyfeleknek is látniuk kell, a rétegtől függetlenül átvihető a MapManagerbe.

---

## 9. 3D adatok

### 9.1 Formátum [DÖNTÖTT, a részletek NYITOTTAK]
- Elsődleges formátum: **3D Tiles 1.1** glTF-tartalommal, **elemszintű azonosítókkal** (`EXT_mesh_features`) és metaadatokkal (`EXT_structural_metadata`).
- Kis modellekhez (egy-két épület) elfogadható egyetlen glTF/GLB is.
- Tömörítés: geometriára Draco vagy meshopt, textúrákra KTX2/Basis.
- A 3D elem és a `node` közötti kapcsolatot a `model_feature` fajtájú horgony adja (`anchor.feature_ref`); kiválasztáskor ezen keresztül történik a hierarchiabeli kijelölés.
- **Stabil elemazonosító [DÖNTÖTT]:** a 3D Tiles belső feature ID-je csempénkénti sorszám, amely csempénként és részletességi szintenként eltér, és a tileset újragenerálásakor megváltozhat. Ezért minden modellelemnek egy **stabil azonosító tulajdonságot** kell hordoznia a metaadatban (`EXT_structural_metadata`), pl. `element_id` (BIM-forrásnál az IFC GlobalId). A tulajdonság neve modellenként a `model_asset.attrs.element_id_property` mezőben adható meg. A horgony ezt az értéket tárolja.
- Kiválasztás: kijelölt feature → `getProperty(element_id_property)` → horgony → `node`.
- Kiemelés: `Cesium3DTileStyle` feltétel az `element_id` értékére, így az elem minden csempében és részletességi szinten egyszerre emelődik ki, akkor is, ha több csempére esik szét.
- A feldolgozási láncnak (9.2) biztosítania kell, hogy az `element_id` a forrásból a tilesetbe változatlanul átkerüljön; ezt importkor ellenőrizni kell (hiányzó vagy duplikált azonosítók jelentése).
- **Szegmentált és nem szegmentált modellek:** BIM-ből készült modellekben az elemek külön kijelölhetők (`segmented = true`). Fotogrammetriai vagy szkennelt mesh-nél nincsenek külön elemek; ott a `region` fajtájú horgony („bedobozolás”) és a Cesium osztályozó térfogata (classification) adja a kiemelést.
- **Bedobozolás [DÖNTÖTT]:** a doboz hordozza a rekorddal való kapcsolatot, a dobozba eső mesh-rész az objektum. Előnye, hogy bármilyen modellen működik, és a tileset újragenerálása vagy cseréje (pl. újabb felmérés) után is érvényes marad. Megvalósítás: `ClassificationPrimitive` + `BoxGeometry`, a doboz középpontjában számolt ENU-rendszerből és az elforgatásból képzett modellmátrixszal; a méretek valós méterben adottak (7.3).

### 9.2 Feldolgozási folyamat [NYITOTT]
Forrás (pl. IFC, DWG/DXF, fotogrammetria) → glTF (elemazonosítókkal) → optimalizálás → 3D Tiles → objektumtár.
Lehetséges eszközök, amelyeket a megvalósítás elején értékelni kell: `gltf-transform` (optimalizálás, tömörítés), `3d-tiles-tools` (CesiumGS), PostGIS-ből tile-generálás (pg2b3dm), illetve kereskedelmi megoldások. A választás a forrásformátumoktól függ.

**Szerveroldali átalakítás (2026-10-24, felhasználói döntés):** külön `converter` szolgáltatás a compose-ban (`converter/`, Python), csak a belső hálózaton; az API háttérfeladata (`ModelConversionWorker`) adja neki a feladatot, a konverter az objektumtárból olvas és oda ír (a bájtok nem mennek át az API-n).
- **IFC** → szegmentált GLB (IfcOpenShell 0.9, LGPL, változtatás nélkül, külön programban): minden geometriás elem a GlobalId-jával (`element_id`), nevével és IFC-osztályával kijelölhető; színek az IFC-stílusokból, ennek hiányában osztály szerint. Georeferált IFC4-nél (IfcMapConversion, EPSG-vetület) az elhelyezés magától adódik; a távoli koordináták helyi origóra kerülnek.
- **Mesh** (OBJ + MTL + textúrák, FBX, DAE, STL, PLY, 3DS, X3D, OFF, glTF + .bin) → GLB (Assimp, BSD); nem szegmentált, elhelyezés kötelező.
- **Pontfelhő** (LAS, LAZ, E57) → 3D Tiles (`pnts` csempék oktális fában, ADD finomítás); a forrás vetületével (pl. EPSG:23700) ECEF-be vetítve georeferált, nélküle helyi koordinátás, és a kiszolgáláskor az elhelyezés kerül a gyökér-transzformáció elé. Felső korlát: `CONVERTER_MAX_POINTS` (alapból 30 millió, felette egyenletes ritkítás, jelezve).
- Állapot: `model_asset.status` = converting → ready | failed (hibaüzenettel); a 3D nézet csak a kész modelleket mutatja. A források a modell mappájában maradnak, az eredmény a `converted/` almappába kerül.
- DWG nem támogatott (zárt formátum): előbb IFC-be vagy OBJ-be kell exportálni.

**M6-ban választott, cserélhető megoldás:** a rendszer kész eredményt fogad – egyetlen GLB-t vagy explicit csempézésű 3D Tiles csomagot (mappa, gyökerében `tileset.json`, a csempék önálló fájlok: glb, b3dm, i3dm, pnts, cmpt). Az átalakítás (IFC, DWG, fotogrammetria → 3D Tiles) külső eszközzel történik (pl. `gltf-transform`, `3d-tiles-tools`); szerveroldali átalakító később ugyanebbe a feltöltési útba illeszthető.
- A fájlok egyenként, aláírt URL-lel kerülnek az objektumtárba (`org/{szervezet}/models/{guid}/…`), a bájtok nem mennek át a backenden.
- A backend csak a `tileset.json`-t szolgálja ki (`GET /models/{id}/tileset.json`), átírva: a csempék rövid lejáratú (alapból 240 perc, `Storage:TileUrlMinutes`) aláírt objektumtár-URL-t kapnak, a beágyazott `tileset.json`-ök ugyanerre a végpontra mutatnak (`?path=`). Az implicit csempézés (URI-sablonok) ezért nem támogatott.
- A GLB egycsempés 3D Tiles 1.1 tilesetként szolgálódik ki, a gyökér-transzformáció hordozza az elhelyezést (`model_asset.attrs.placement`: WGS84 pont, magasság a telephely alapszintjétől, tájolás – a glTF −Z tengelye az északtól mért irányba). Így az osztályozó térfogat (9.1) minden modellen működik.

### 9.3 Rámutatás és illesztés
- Rámutatás szegmentált modellen: `scene.pick()` → `Cesium3DTileFeature` → stabil `element_id` tulajdonság → `anchor` → `node`.
- Rámutatás dobozra: `scene.pickPosition()` → az aktuális szint dobozai közül azok, amelyek tartalmazzák a pontot (a pontot a doboz középpontjának ENU-rendszerébe váltva, a dobozzal ellentétes forgatás után tengelyirányú tartalmazási teszt) → a legkisebb térfogatú → `anchor` → `node`. Ha mindkét módszer talál elemet, a szegmentált modellelem az elsődleges [FELTÉTELEZÉS].
- Felületi pont: `scene.pickPosition()` (mélységpuffer alapján; felületre helyezéshez elég).
- Illesztés (snap): a Cesiumban nincs beépített csúcs- vagy élillesztés, ezért a **vektoros geometrián** történik:
  - kliensoldalon az aktuális szint geometriájából épített térbeli indexszel (pl. rbush), vagy
  - szerveroldalon PostGIS-szel (`ST_ClosestPoint`, `ST_DWithin`).
  - [FELTÉTELEZÉS] MVP-ben kliensoldali illesztés az aktuális szint adataival.

---

## 10. API-vázlat [FELTÉTELEZÉS]

REST/JSON, verziózott útvonal (`/api/v1`). Geometria GeoJSON-ként, WGS84-ben (EPSG:4326); magasság külön mezőkben (`levelId`, `zRelM`).

| Metódus | Útvonal | Leírás |
|---|---|---|
| GET | `/sites` | Elérhető telephelyek |
| GET | `/sites?bbox=` | Telephelyek egy térképi területen (áttekintő térkép) |
| GET | `/sites/{id}` | Telephely referenciaponttal és alapmagassággal |
| GET/POST/PATCH/DELETE | `/levels/{nodeId}/floorplans` | Szint alaprajzrétegei (hivatkozás MapManager-publikációra vagy külső WMS-re) |
| GET/POST/PATCH/DELETE | `/map-layers` | Háttérrétegek |
| GET | `/maps/wms?floorplanId=\|mapLayerId=&...` | WMS-proxy: jogosultság-ellenőrzés után továbbítás a MapManagerhez szolgáltatástokennel (6.3); csak `GetMap`, `GetFeatureInfo`, `GetLegendGraphic` |
| GET/POST/PATCH/DELETE | `/draw-layers` | Rajzi rétegek (szint vagy telephely, geometriatípus, mezőleírás, stílus) |
| GET | `/draw-layers/{id}/features?bbox=` | Rajzolt elemek GeoJSON-ként, EPSG:3857-ben, rekordverzióval |
| POST | `/draw-layers/{id}/transaction` | Új / módosított / törölt elemek egy tranzakcióban (8.6); 400 elemenkénti hibákkal, 409 verzióütközésnél |
| GET | `/sites/{id}/linked-layers/features?layer=&filter=&sort=` | Kötött MapManager-réteg gridje; a WFS-t a backend hívja, a CQL-t szerveroldalon, a réteg mezőlistájával ellenőrizve építi (nyers WFS-proxy helyett, 2026-10-23) |
| GET | `/nodes/{id}/linked-attributes` | Kötött rekord élő attribútumai a MapManagerből |
| POST | `/sites/{id}/link-layer` | Vázrekordok létrehozása egy MapManager-réteg elemeiből (réteg, típus, névmező, szülő) |
| POST | `/sites/{id}/sync-links` | Kapcsolatok ellenőrzése: új, törölt (árva) elemek jelentése |
| GET | `/maps/mapmanager/publications` | Választható MapManager-rétegek listája a konfigurációhoz (ha a MapManager tokennel engedi; 6.3/3.) |
| GET | `/sites/{id}/nodes?aspect=&root=&depth=` | Fa vagy részfa |
| GET | `/nodes/{id}` | Csomópont részletei |
| GET | `/nodes/{id}/ancestors` | Morzsamenü |
| POST | `/nodes` | Új csomópont |
| PATCH | `/nodes/{id}` | Módosítás, áthelyezés (`parentId`) |
| DELETE | `/nodes/{id}` | Törlés (részfával, megerősítéssel) |
| GET/POST/DELETE | `/nodes/{id}/relations` | Kapcsolatok |
| GET | `/nodes/{id}/panoramas?recursive=true` | Panorámák a részfában |
| GET | `/sites/{id}/floors/{nodeId}/geometry` | Szint vektoros geometriája (alaprajz és illesztés) |
| GET | `/nodes?siteId=&typeId=&filter=&sort=&page=` | Grid-lekérdezés, szűrés a műszaki adatokra is |
| GET | `/anchors/by-feature/{modelAssetId}/{featureRef}` | 3D elemből horgony és csomópont |
| GET/POST | `/nodes/{id}/anchors` | Egy csomópont horgonyai, új horgony |
| PATCH/DELETE | `/anchors/{id}` | Horgony módosítása, törlése |
| GET | `/panoramas/{id}/anchors` | Egy panoráma infopontjai |
| GET | `/sites/{id}/floors/{nodeId}/anchors` | Egy szint horgonyai az alaprajzhoz |
| POST | `/sites/{id}/import/attributes` | Műszaki adatok tömeges importja |
| POST | `/uploads/panorama` | Feltöltési URL kérése (aláírt PUT URL) |
| POST | `/panoramas` | Panoráma-metaadat rögzítése feltöltés után |
| GET | `/sites/{id}/models` | 3D modellek, aláírt letöltési URL-lel |
| GET | `/node-types` | Csomóponttípusok |

---

## 11. Frontend felépítés

- **Keretrendszer:** Angular [DÖNTÖTT], standalone komponensekkel és signal-alapú állapotkezeléssel [FELTÉTELEZÉS].
- **Cesium és Angular:** a Cesium renderciklusa az Angular változásfigyelésén kívül fusson (`NgZone.runOutsideAngular`, vagy zoneless Angular); a Cesium-eseményekből csak a kijelölés változása kerüljön vissza az Angular állapotba. Különben minden képkocka teljes változásfigyelést indít.
- **Üzemmódok:** az 1.5 fejezet szerinti három üzemmód külön útvonal (route) és munkaterület; az üzemmódváltás a jogosultság szerint engedélyezett. Az összerendelő és szerkesztő eszközök csak az 1–2. üzemmódban töltődnek be.
- **Elrendezés [FELTÉTELEZÉS]:** bal oldalon fanézet és adatbázis-grid (fülekkel), középen a 3D nézet vagy a panorámanéző, mellette az alaprajz, jobb oldalon az adatlap (műszaki adatok, horgonyok).
- **Állapotkezelés:** egy központi kijelölési szolgáltatás (aktuális telephely, üzemmód, szint, csomópont, panoráma, horgony), amelyre minden nézet feliratkozik; így a grid, a fa, az alaprajz, a 3D és a panoráma kölcsönösen szinkronban marad.
- **Modulok:** `viewer3d` (Cesium inicializálás, tilesetek, kiemelés, vágás, osztályozó térfogatok), `floorplan`, `panorama`, `tree`, `grid`, `datasheet` (JSON-sémából generált űrlap), `anchoring` (összerendelő eszközök), `snap`, `api`.
- A Cesium ion token és egyéb kulcsok konfigurációból jönnek; [NYITOTT] szükséges-e Cesium ion (alaptérkép, domborzat), vagy saját/nyílt forrású alaptérkép elég.

---

## 12. Megvalósítási terv

Minden mérföldkő végén működő, bemutatható állapot és automatikus tesztek.

### M0 – Projektváz
- Monorepo: `frontend/`, `backend/`, `db/` (migrációk), `docker-compose.yml` (PostgreSQL + PostGIS, SeaweedFS, backend, frontend).
- CI: build és tesztek futtatása.
- **Elfogadás:** `docker compose up` után a frontend egy üres Cesium-földgömböt mutat, a backend `/health` végpontja válaszol, az adatbázis-migráció lefut.

### M1 – Adatmodell és hierarchia-API
- Séma a 8. fejezet szerint, EF Core-migrációval; triggerek.
- Szervezet és felhasználó táblák, a **globális szervezeti szűrő már itt** (3.1), mert minden későbbi lekérdezés erre épül; tesztek arra, hogy más szervezet adata nem érhető el.
- CRUD végpontok, áthelyezés, kapcsolatok, csomóponttípusok.
- Integrációs tesztek valós PostgreSQL-lel (pl. Testcontainers).
- **Elfogadás:** részfa-, ős- és áthelyezési tesztek zöldek; ciklus létrehozása elutasítva.

### 1. fázis – panorámaalapú MVP

### M2 – Adatbázis-grid, fa és műszaki adatok
- Angular-alkalmazás váza, kijelölési szolgáltatás.
- Fanézet, grid a JSON-sémából generált oszlopokkal, adatlap-űrlap.
- Leíró adatok kötése MapManager-rétegből (vázrekordok + élő WFS-olvasás, 6.3) – ez az elsődleges út; mellette a műszaki adatok tömeges importja (CSV/XLSX) a MapManagerben nem szereplő rekordokhoz.
- Telephelyek kezelése (referenciapont, alapmagasság, határ); OpenLayers áttekintő térkép EPSG:3857-ben, a telephelyekkel és háttér-WMS-sel.
- **Elfogadás:** egy mintatáblázat importja után a berendezések a gridben szűrhetők és szerkeszthetők; a fában és a gridben a kijelölés szinkron.

### M3 – Alaprajz és panorámák elhelyezése
- Szintválasztó; alaprajzrétegek szintenként: hivatkozás MapManager-publikációra (réteg + opcionális CQL-szűrő) vagy külső WMS-re; WMS-proxy szolgáltatástokennel; megjelenítés szintenként.
- Előfeltétel: a MapManager szolgáltatástokenes hozzáférése (6.3/2.). Amíg nincs kész, a fejlesztés egy a MapManagerrel azonos viselkedésű helyi GeoServerrel vagy egy külső minta-WMS-sel halad.
- Panorámák feltöltése (aláírt URL), elhelyezése az alaprajzon, tájolás-kalibráció.
- GDPR-kitakarás (FR-P8): háttérfeladat az arcfelismeréshez, jóváhagyási felület, kitakart megjelenítési változat. A rendszámkeresés a 2. körben is jöhet.
- **Elfogadás:** egy EOV-ban tárolt, MapManagerben publikált minta-alaprajz a SiteTwin3D Web Mercator térképén a helyén, torzításmentesen jelenik meg, és csak az adott telephelyre jogosult SiteTwin3D-felhasználó látja; egy mintaszinten a panorámák az alaprajzon a helyükön látszanak, kattintásra megnyílnak, a látókúp az alaprajzon követi a nézési irányt.

### M4 – Bejárás és infopontok
- Szomszédos panorámák közti navigáció (jelölők a panorámán).
- Infopont elhelyezése: rekord kiválasztása a gridben → kattintás a panorámán → a közelítő hely megjelölése az alaprajzon.
- Kölcsönös kijelölés: grid ↔ fa ↔ alaprajz ↔ panoráma.
- Rajzi rétegek (8.6): réteg létrehozása mezőleírással, elemek rajzolása, szerkesztése, törlése tranzakcióban, verzióütközés-kezeléssel.
- **Elfogadás:** egy mintaszinten legalább 5 panoráma között lehet navigálni; egy berendezés a gridből kiválasztva infopontot kap, az alaprajzon megjelenik, és bármelyik nézetben rákattintva a többiben is kijelölődik.

### M5 – Üzemmódok és jogosultság
- Üzemmódváltás (Előkészítés, Összerendelés, Megtekintés), hitelesítés, szerepkörök telephely szinten.
- **Elfogadás:** megtekintő szerepkörrel csak a Megtekintés üzemmód érhető el, az összerendelő és szerkesztő eszközök nem töltődnek be; a bejárás és az adatlapok használhatók.

> **Az 1. fázis végén** az alkalmazás 3D mesh nélkül, panorámákkal, alaprajzzal és adatbázissal teljes értékűen használható.

### 2. fázis – 3D modell

### M6 – 3D modell és bedobozolás
- 3D Tiles / glTF betöltése az objektumtárból, aláírt URL-lel; feldolgozási lánc kiválasztása (9.2).
- Szintenkénti elrejtés vagy vágás.
- Bedobozolás: doboz lerakása, méretezése, forgatása; osztályozó térfogattal kiemelés; kiválasztás a legkisebb tartalmazó doboz alapján.
- **Elfogadás:** egy mintamodellen egy berendezés bedobozolva kiemelődik; a dobozra kattintva a gridben és a panorámán is kijelölődik.

### M7 – Szegmentált modellelemek és illesztés
- Stabil `element_id` alapú kiválasztás és kiemelés szegmentált (BIM) modelleken.
- Kliensoldali snap a vektoros geometriára.
- **Elfogadás:** szegmentált mintamodellen az elemre kattintva a rekord kijelölődik; a beállított tűrésen belül a pont a legközelebbi csúcsra vagy élre illeszkedik.

### M8 – Funkcionális nézőpont
- Funkcionális fa (pl. technológiai vonal), kapcsolatok a helyszíni fához.
- Vonal összes elemének kiemelése a 3D-ben, épületeken és szinteken átívelve.
- **Elfogadás:** egy minta technológiai vonal kijelölésekor minden eleme kiemelődik, és listázható, melyik helyiségben vannak.

### M9 – Üzemeltetés
- Árva fájlok takarítása, naplózás, mentési útmutató.

> Megjegyzés: a funkcionális nézőpont (M8) a hierarchia és a grid szintjén az 1. fázisban is használható; az M8 a 3D-beli, épületeken átívelő kiemelést teszi hozzá.

---

## 13. Irányelvek a megvalósító ügynöknek

- A **[NYITOTT]** pontoknál ne hozz végleges döntést; válassz könnyen cserélhető megoldást, és jelezd a választást.
- A termék neve (SiteTwin3D) **csak** konfigurációban és felületi szövegekben szerepeljen (NFR-5).
- Kódbeli azonosítók, megjegyzések és commitüzenetek angolul; a felületi szövegek magyarul, erőforrásfájlból.
- Bináris tartalom soha ne kerüljön az adatbázisba és ne menjen át a backenden (NFR-3). Kivétel: a WMS-proxy (6.3), amely a MapManager válaszát továbbítja.
- A MapManager **külső rendszer** (6.3): a SiteTwin3D-ben ne valósíts meg alaprajzfájl-feltöltést, raszterfeldolgozást vagy GeoServer-kezelést; a MapManager adatbázisához ne kapcsolódj közvetlenül; a MapManager szerkesztési végpontjait ne használd. Csak a WMS-proxyn (és ha elkészül, a publikációlistán) át kommunikálj vele.
- A jogosultságkezelés és a rajzolás teljesen a SiteTwin3D-ben van; a MapManager felhasználóit ne használd.
- Minden geometria WGS84-ben (EPSG:4326) tárolódjon; a 2D megjelenítés EPSG:3857, a 3D WGS84/ECEF. Más vetületű forrást importkor kell átváltani (7. fejezet).
- Metrikus értéket (méret, távolság, terület, tűrés méterben) soha ne számolj EPSG:3857-koordinátákból (7.3).
- Minden mérföldkőhöz automatikus teszt tartozzon; a hierarchia triggereit integrációs teszt fedje le.
- Mintaadat: egy kitalált szennyvíztisztító telep (2 épület, 3 szint, 1 technológiai vonal, néhány panoráma), seed-szkriptként.

---

## 14. Nyitott kérdések összesítve

### 14.1 Az 1. fázis indulása előtt
Mind eldőlt: több szervezet (3.1), Docker-alapú telepítés (NFR-6), grid és megjelenés a MapManagerből (NFR-8), GDPR-kitakarás (FR-P8).
- [NYITOTT, nem blokkoló] A kitakaratlan eredeti képek megőrzése: törlés jóváhagyás után (alapértelmezés) vagy korlátozott megőrzés – szervezetenként állítható.

### 14.2 Az 1. fázis közben eldönthető
5. **Panoráma-szomszédság:** automatikus (távolság alapján, kézi javítással) vagy kézi?
6. **Panorámák felvétele:** milyen kamera, milyen felbontás? A WebGL-textúraméret kockázata miatt (4.4) érdemes az M3 elején egy valós mintaképpel kipróbálni.
7. **WMS-források részletei:** mely háttérrétegek kellenek (ortofotó, közmű), és a MapManagerben a szintek külön rétegként vagy egy rétegen `szint` mezővel (CQL-szűrő) lesznek-e?
8. **Alaptérkép a 3D-ben:** Cesium ion, vagy a MapManager WMS-rétegei (ortofotó) a SiteTwin3D proxyján át Cesium-képrétegként? Az utóbbi külső szolgáltatás nélkül is működik.

### 14.3 A 2. fázisig ráér
9. **3D forrásadatok:** szegmentált (BIM/IFC) vagy fotogrammetriai/szkennelt mesh? Ettől függ a feldolgozási lánc (9.2), és hogy a modellelem- vagy a dobozhorgony lesz-e a fő összerendelési mód.
10. **Vektoros alaprajzok** a 3D-s illesztéshez: DWG/DXF-ből (MapManager-importtal) vagy kézi rajzolással?
11. **Szervezeti SSO** (Entra ID / OIDC) igény szerint.

### 14.4 MapManager-oldali kérdések
12. **Ütemezés:** a szolgáltatástokenes hozzáférés az M3 előfeltétele; a raszteres adatforrás kezelése a raszteres alaprajzoké.
13. **Publikációk listázása tokennel** (6.3/3.): kell-e, vagy a rétegneveket kézzel adjuk meg a konfigurációban?

---

## 15. Döntési napló

| Dátum | Döntés |
|---|---|
| 2026-10-22 | Terméknév: **SiteTwin3D** (munkanév; védjegyhelyzet: a „Twinsite” EU-védjeggyel való hasonlóság később tisztázandó). |
| 2026-10-22 | Háromrétegű architektúra: CesiumJS – .NET – PostgreSQL. |
| 2026-10-22 | Képek és modellek objektumtárban, az adatbázisban csak metaadat. |
| 2026-10-22 | Elsődleges 3D formátum: 3D Tiles 1.1 elemszintű metaadatokkal. |
| 2026-10-22 | Egytáblás, rugalmas hierarchia ltree-vel; több nézőpont (helyszíni, funkcionális) és fák közötti kapcsolatok. |
| 2026-10-22 | Panoráma a hierarchia bármely szintjén lévő csomóponthoz köthető, nincs „csak levélhez” megkötés. |
| 2026-10-22 | ~~Helyi ENU koordináták telephelyenkénti georeferenciával.~~ Felülírva, lásd lent. |
| 2026-10-22 | Három üzemmód: Előkészítés, Összerendelés, Megtekintés; kölcsönös kijelölés minden nézet között. |
| 2026-10-22 | Térbeli megjelenés horgonyokkal: modellelem, infopont (panorámapont), lehatárolt térrész, térbeli pont; egy elemhez több horgony is tartozhat. |
| 2026-10-22 | Műszaki adatok JSONB-ben, csomóponttípusonkénti JSON-sémával. |
| 2026-10-22 | Frontend keretrendszer: Angular. |
| 2026-10-22 | A horgony az adatbázis-rekordhoz kötődik; 3D Tiles esetén stabil, metaadatban tárolt elemazonosítóval (nem a csempénkénti feature-sorszámmal). |
| 2026-10-22 | Két fázis: először panorámaalapú MVP (alaprajz, panorámák, infopontok, grid), utána 3D mesh és bedobozolás. |
| 2026-10-22 | Infopont-pozíció: a felhasználó jelöli meg közelítőleg az alaprajzon, nincs automatikus átszámítás a panorámáról. Ezért az 1. fázisban elég a georeferált raszteres alaprajz; a vektoros alaprajz a 2. fázisban kell (snap). |
| 2026-10-22 | Panorámanéző: a CesiumJS beépített panorámatámogatása (`EquirectangularPanorama`, ≥ 1.139); nincs külön panorámakönyvtár. |
| 2026-10-22 | Alaprajz és 2D térkép: OpenLayers külön nézetben. |
| 2026-10-22 | Koordináta-rendszerek: tárolás WGS84 (EPSG:4326), 2D megjelenítés Web Mercator (EPSG:3857), 3D WGS84; a források (WMS, raszteres alaprajz, import) bármely vetületben érkezhetnek. Magasság szinthez viszonyítva (`level_id` + `z_rel_m`). Indok: alaprajz WMS-ből is jöhet, és több telephely között nagy területen kell mozogni. |
| 2026-10-22 | ~~Alaprajzforrások szintenként a `floorplan` táblában (WMS vagy raszter); a raszteres alaprajzot a backend EPSG:3857 csempékké alakítja.~~ Felülírva, lásd lent. |
| 2026-10-22 | **MapManager-integráció:** a 2D térképi adatok (vektoros és raszteres alaprajzok, háttérrétegek) a MapManagerben élnek és GeoServeren publikálódnak; a SiteTwin3D csak fogyasztó, a `floorplan` / `map_layer` táblákban hivatkozik a rétegekre. A raszteres adatforrás kezelése a MapManager bővítéseként készül. |
| 2026-10-22 | A SiteTwin3D önálló: saját jogosultságkezelés, saját rajzolás és adatbázis. A WMS-kérések a SiteTwin3D backendjén át, szolgáltatástokennel jutnak a MapManager proxyjához (MapManager-bővítés). |
| 2026-10-22 | Rajzolt kiegészítő elemek a SiteTwin3D adatbázisában, a MapManager adatszerkezetének mintájára (`draw_layer` / `draw_record` / `draw_geometry`, 1:N geometria, JSONB leíró adatok, verziós szerkesztési tranzakció); eltérés: tárolás WGS84-ben. |
| 2026-10-22 | Több szervezet a MapManager cégmodelljének mintájára (logikai elkülönítés, globális szervezeti szűrő, platform-adminisztrátor); MapManager-kapcsolat és token szervezetenként. |
| 2026-10-22 | Docker Compose alapú telepítés (on-prem és felhő); egységes megjelenés és közös komponensek (grid) a MapManager felületéből. |
| 2026-10-22 | GDPR: arcok kitakarása alapból; rendszám csak telephelyenként bekapcsolva és csak járművön belül (az azonosító táblák védelmére); emberi jóváhagyás a megjelenés előtt; helyben futó felismerés. |
| 2026-10-22 | Leíró adatok: kiindulásként a MapManager rétegeiből kötve, élő hivatkozással. A SiteTwin3D vázrekordokat tart (`mapmanager_ref`), az attribútumokat WFS-en, kérésenként olvassa; az importálás később választható. |
| 2026-10-22 | Bedobozolás: a mesh-elem köré tett, forgatható doboz hordozza a rekorddal való kapcsolatot; egy rekordhoz több doboz is tartozhat; átfedésnél a legkisebb doboz nyer. Kiegészítő alak: sokszöghasáb. |
| 2026-10-23 | Objektumtár: **SeaweedFS** (ingyenes, Apache 2.0, S3-kompatibilis) a MinIO helyett, mert a MinIO már nem ad ki szabad konténerképet. A backend csak S3 API-t használ, így a tár cserélhető. |
| 2026-10-23 | `node_type`: bigint kulcs + szervezetenként egyedi `code`; `attr_schema` `json` típusú (a mezősorrend megmarad). |
| 2026-10-23 | Horgony CHECK-kényszer: explicit `IS NOT NULL` a méretekre és a `z_max`-ra (a spec eredeti kényszere a NULL miatt átengedte a méret nélküli dobozt). |
| 2026-10-23 | Szervezeti elkülönítés: minden szervezeti táblában denormalizált `organization_id`, globális EF-szűrő, és összetett külső kulcsok (`(x_id, organization_id)`), így az adatbázis sem enged más szervezetre mutatni. Szülő és gyermek ugyanazon a telephelyen és ugyanabban a fában (nézőpont). |
| 2026-10-23 | MapManager-kötés: a WFS-t csak a backend hívja típusos végpontokon (nincs nyers `/maps/wfs` proxy); a `node.attrs.mapmanager_ref` foglalt, a kötött rekord műszaki adata csak olvasható. |
| 2026-10-23 | Felület: Angular 21 + PrimeNG 21.1.10 (MIT) rögzítve, a MapManagerrel azonosan; a PrimeNG 22-től kereskedelmi licenc. |
| 2026-10-23 | Objektumtár elérése a böngészőből azonos originen: a fordított proxy a `/objects/*` (vödörnév) útvonalat változatlan Host fejléccel továbbítja, így az aláírt URL érvényes, CORS nem kell. |
| 2026-10-23 | GDPR-kitakarás (FR-P8): helyben futó UltraFace RFB-640 ONNX arcfelismerő (MIT) átfedő, 60°-os perspektivikus kivágásokon; képfeldolgozás SkiaSharppal (MIT). A régiók képi szögekben tárolódnak (yaw 0 = képközép). Feldolgozási állapotok: pending → review → rendering → approved (failed). A kitakarás visszafordíthatatlan (a terület néhány képpontra kicsinyítve, majd visszanagyítva). A megjelenítési változat leghosszabb oldala legfeljebb 8192 px. A rendszámkeresés későbbi lépés. |
| 2026-10-23 | Szervezeti beállítás: `organization.panorama_original_retention_days` (NULL = az eredeti törlődik jóváhagyáskor, alapértelmezés). |
| 2026-10-23 | Panoráma-szomszédság (14.2/5, nyitott): kézi kapcsolat + automatikus javaslat (szintenként a legközelebbiek `geography`-távolsággal, alapból 15 m-en belül, legfeljebb 4); a javaslat nem írja felül a kézi javításokat. |
| 2026-10-23 | Infopont alaprajz nélkül: a pozíció ideiglenesen a panoráma helye (`position_source = panorama_fallback`); kézi áthelyezéskor `manual` lesz. |
| 2026-10-23 | Telephelyi jogosultság (3.2): telephelyi hozzárendelés nélkül a felhasználó a szervezet minden telephelyét a szerepköre szerint éri el; hozzárendeléssel csak a megadott telephelyeket, telephelyenkénti szereppel; a Megtekintő soha nem szerkeszt. Kikényszerítés központilag: olvasásnál a globális szűrő (404), írásnál a mentés előtti telephely-ellenőrzés (403). A változás újbóli belépés nélkül, legfeljebb 30 mp alatt érvényesül. |
| 2026-10-23 | Üzemmódok (1.5) útvonalként: `/prepare` (Előkészítés), `/assign` (Összerendelés), `/view` (Megtekintés); a szerkesztési üzemmódokba csak szerkesztési joggal lehet belépni, a szerkesztő eszközök kódja Megtekintésben le sem töltődik. |
| 2026-10-23 | 3D modell (M6): GLB vagy kész 3D Tiles csomag, fájlonként aláírt URL-lel feltöltve; a backend csak az átírt `tileset.json`-t adja ki (aláírt csempe-URL-ek). A GLB egycsempés tilesetként, az elhelyezést hordozó gyökér-transzformációval jelenik meg, hogy az osztályozó térfogat rajta is működjön. Átalakítás (9.2) egyelőre külső eszközzel. |
| 2026-10-23 | Szintenkénti vágás: kiválasztott szintnél a modell a szint mennyezete alatt 0,5 m-rel (szintmagasság: `height_m`, ennek hiányában a következő szint, végül 3 m) vízszintes síkkal le van vágva; a teljes modellnél nincs vágás. A dobozok közül a kiválasztott szintéi látszanak. |
| 2026-10-23 | Doboz (`region`/`box`) a 3D-ben: lerakás a modellfelületre kattintva (1 m-es kocka a felületen állva, a szint a kiválasztott vagy a pont magassága szerinti), fogantyúk (oldalak: méret a szemközti oldal megtartásával, teteje: magasság, kör: forgatás, közép: mozgatás) és számmezők; a magasság átírásakor az alja marad a helyén. A tartalmazási teszt és a kirajzolt osztályozó térfogat 5 cm ráhagyással dolgozik (a padlón álló doboz így a padlót is kiemeli). A sokszöghasáb később. |
| 2026-10-23 | 3D-kijelölés a panorámán: a dobozra kattintva, ha a rekordnak van infopontja, annak panorámája nyílik meg; a kijelölt rekord azonos szinten lévő dobozai a panorámában is kirajzolódnak. |
| 2026-10-23 | Szegmentált modell (M7): a `model_asset.attrs.element_id_property` megadásával szegmentált (`segmented = true`). A `model_feature` horgony a stabil azonosítót tárolja; egy modellelem egyszerre egy rekordhoz tartozhat (409 `anchor.feature_taken`). A modell törlésekor az elemhorgonyai is törlődnek (a dobozok maradnak). Kiemelés `Cesium3DTileStyle` feltétellel az azonosítóra; kattintáskor a hozzárendelt elem elsőbbséget kap a dobozokkal szemben. |
| 2026-10-23 | Elemazonosító-ellenőrzés importkor (9.1): `POST /models/{id}/element-check` – a backend a modell GLB-tartalmainak `EXT_structural_metadata` tulajdonságtábláit olvassa (szerveroldali feldolgozás, a bájtok nem jutnak a böngészőhöz), és jelenti a hiányzó, illetve egy táblán belül ismétlődő azonosítókat. Több csempében (LOD) ismétlődő azonosító szabályos, nem hiba. Legfeljebb 500 tartalom, tartalmanként 512 MB. A régi b3dm batch táblákat nem olvassa. |
| 2026-10-23 | Illesztés 3D-ben (FR-S2, 9.3): `GET /sites/{id}/floors/{levelId}/geometry` = a szint saját rajzi rétegei + a MapManager-alaprajz WFS-geometriája (a backend kéri le EPSG:3857-ben, pontos képlettel WGS84-re). Kliensoldalon a telephely pontja körüli helyi síkban (az ellipszoid görbületi sugaraival, valós méterben) a legközelebbi csúcs, ennek hiányában él a tűrésen belül (alapból 0,25 m, a 3D eszköztáron állítható, 0 = ki). Index: egyszerű lista befoglaló téglalapos szűréssel (egy szint elemszámánál elég; rbush helyett). Illeszkedik: a doboz lerakása és mozgatása, a térbeli pont lerakása. |
| 2026-10-24 | Funkcionális nézőpont (M8): egy csomópont helye elsődlegesen az explicit `located_in` kapcsolat (csomópontonként egy, `PUT /nodes/{id}/location`, csak a helyszíni fa eleme lehet). Ha nincs ilyen, a hely levezetett: a helyszíni csomópontok (helyiség, épület) dobozai közül a legkisebb, amely tartalmazza a csomópont valamelyik horgonyát, valós magassággal (szint + `z_rel_m`), így az azonos alaprajzú szintek is elkülönülnek. `GET /nodes/{id}/members` = a csomópont és teljes részfája, elemenként a helyével és a forrással (`relation` / `box`). |
| 2026-10-24 | Részfa kiemelése a 3D-ben (FR-3D5): a kijelölt csomópont minden leszármazottjának dobozai és modellelemei együtt emelődnek ki. Teljes modell nézetben (nincs kiválasztott szint) „átlátszó” nézet: a modell áttetsző, a kiemelt elemek dobozai kitöltve látszanak, így a vonal az épületeken és szinteken át követhető; kiválasztott szintnél a szokásos vágás és kiemelés. |
| 2026-10-24 | Üzemeltetés (M9): tártakarítás háttérfeladatként (alapból 6 óránként) és platformadmin-végponton (`POST /platform/storage/cleanup`, alapból csak jelentés): a hivatkozatlan fájlok 24 órás türelmi idő után törlődnek, a lejárt megőrzésű eredeti panorámák is. Naplózás: JSON konzolnapló, kérésenként egy sor (felhasználó, szervezet, státusz, időtartam), `X-Request-Id`. Mentés: `deploy/backup.sh` (pg_dump + az objektumtár növekményes, soha nem törlő rclone-másolata), `deploy/restore.sh`; útmutató: `docs/OPERATIONS.md`. |
| 2026-10-24 | Horgonyok áthelyezése csak szerkesztési munkamenetben (felhasználói kérés, véletlen elmozdítás ellen): az „Infopontok áthelyezése” gombbal indul (Előkészítés és Összerendelés módban), benne az infopont a panorámán, a horgonyjelölő az alaprajzon húzható. A húzások függő módosítások, csak a „Mentés” (elfogadás) írja őket; az „Elvetés” visszaállít. Mentetlen módosítással módváltás előtt megerősítést kér, a szerkesztési módból kilépve a függő módosítások elvesznek. Infopontra kattintáskor a nézetek (alaprajz, 3D, panoráma) akkor is odaugranak, ha a rekord már ki volt jelölve. |
| 2026-10-24 | Időbeli relevancia (felhasználói kérés, a SmartRoute mintájára): a rekord piszkozatként szabadon, előzmény nélkül szerkeszthető, amíg egy szerkesztő nem véglegesíti (rekordonként vagy részfára, `POST /nodes/{id}/finalize`, visszadátumozható kiinduló állapottal). Véglegesítés után a leíró adatok (név, kód, műszaki adatok) és a horgonyok (hely) minden változása – törlés is, a panoráma vagy a modell törlésével járó horgonytörlés is – indoklást igényel (428 `change.reason_required`; fejlécek: `X-Change-Reason`, `X-Change-Valid-From`), és a `change_entry` táblába kerül: érvényesség kezdete (nap, visszadátumozható, de nem a jövőben és nem az utolsó rögzített állapot előtt; azonos napra újabb rögzítés = javítás), ki és mikor rögzítette, indoklás, előtte/utána állapot. Kikényszerítés központilag az `AppDbContext` mentésénél. Lekérdezés: `GET /nodes/{id}/history`, `GET /nodes/{id}/state?asOf=` (az adott napon érvényes állapot). A hierarchia (szülő, hely-kapcsolat) és a rajzi elemek egyelőre nem tartoznak bele; a véglegesítés visszavonása nincs. |
| 2026-10-24 | Mintaadat (13., Tesztelés): `deploy/dev/seed.mjs` a nyilvános API-n át hoz létre egy „Minta szennyvíztisztító telep”-et: 2 épület (Gépház 2 szinttel, Fúvóház), 3 szint, helyiségek és kültéri medence dobozokkal, 1 technológiai vonal 7 elemmel két épületen és két szinten át, a szegmentált mintamodell hozzárendelt elemekkel, 4 jóváhagyott panoráma bejárással és infoponttal, menekülési útvonal rajzi rétegen, opcionálisan MapManager-alaprajz, véglegesített vonal egy rögzített változással. Telephely törlése mostantól a nem kaszkádoló hivatkozásokat (horgony- és panorámaszint, panoráma- és modellcsomópont) előbb törli. |
| 2026-10-24 | Egy elem – egy megjelenés (felhasználói döntés, az FR-A3 pontosítása): egy elemnek panorámánként legfeljebb egy infopontja és összesen egy pontja lehet (adatbázisban egyedi index; új lerakás az előzőt helyezi át); dobozból vonalas elemnél több is lehet. Az elemnek egyetlen helye van: modellelem vagy doboz → pont → infopont közelítő helye (`GET /sites/{id}/places`, `/nodes/{id}/place`); az infopontnak nincs külön alaprajzi helye, létrehozáskor az elem helyét kapja. Az alaprajzon elemenként egy jelölő, a dobozok területként (kattintásra a legkisebb tartalmazó), a 3D-ben a csak pontszerű elemek is jelölőt kapnak. A meglévő többszörösöket a migráció összevonta (a legutóbbi maradt; véglegesített rekordnál előzménnyel). Kereszt-interakció: minden kattintás fókuszkérés (`focusNode`): a fa (nézetváltással, lenyitással), az adatlap, az alaprajz (szinttel), a 3D és a panoráma (grafikus nézetből kattintva megnyílik vagy átvált) mind odaugrik, újrakattintásra is. |
| 2026-10-24 | A berendezés mindkét fában ugyanaz a rekord (felhasználói döntés): a funkcionális fában a vonala alatt, a helyszíni fában a helye szerinti helyiség (épület, szint, kültéri terület) alatt jelenik meg levélként (🔗), a hely nélküliek egy „Hely nélkül” csoportban. Az adatmodell nem változik: a helyszíni „szülő” a hely (`located_in`, ennek hiányában a legkisebb tartalmazó doboz, `GET /sites/{id}/placements`). A helyszíni fában való áthúzás a helyet állítja (megerősítéssel), a funkcionális fát nem; onnan törölni nem lehet. Kijelöléskor a fa abban a nézetben marad, ahol az elem látszik. |
| 2026-10-24 | Adatok fa (felhasználói döntés): a fa harmadik nézete a helyszíni struktúra, a csomópontok alatt a feltöltött panorámákkal és 3D modellekkel (darabszámmal), a csomóponthoz nem kötött modellek „A telephely egészéhez” csoportban. A panoráma kötelezően, a modell opcionálisan egy csomóponthoz tartozik (`panorama.node_id`, `model_asset.node_id`; a modell helye a modellek párbeszédében állítható). Kattintásra a nézet az adathoz ugrik: a panoráma megnyílik, a térkép a szintjére és helyére áll; a modellnél a 3D nézet nyílik meg és a modellre repül. Az adat áthúzással más csomóponthoz tehető (megerősítéssel). |
| 2026-10-24 | Doboz szerkesztése (felhasználói döntés): a fogantyúkkal és a panel mezőivel végzett módosítás piszkozat marad, és csak a doboz elhagyásakor mentődik (panel bezárása, másik elem kijelölése bármely nézetben, a 3D nézet elhagyása); véglegesített rekordnál ekkor kér egyszer indoklást. A panel jelzi a nem mentett módosítást, „Elvetés”-sel a tárolt doboz áll vissza. A fogantyúk ikonok: oldalak és tető – kétirányú nyíl a tengely mentén, forgatás – környíl, mozgatás – négyirányú nyíl. |
| 2026-10-24 | Befoglaló dobozok importja táblázatból (felhasználói döntés, FR-I4 része): CSV/XLSX, soronként egy doboz – rekord kódja, szint (kód vagy név), középpont a választott vetületben (alapból EOV, EPSG:23700; WGS84 vagy más méteres EPSG-kód), alsó magasság a padlótól, három méret, elforgatás a vetület hálózati északától. Átváltás PostGIS-szel, az elforgatás a meridiánkonvergenciával a valódi északra igazítva; a telephelytől 10 km-nél messzebbi középpont hiba (felcserélt koordináták). A rekord sorai felülírják a dobozait (vonalas elemnél több sor; a meglévő doboz helyben frissül, a felesleges törlődik), forrás: `import`. Minden vagy semmi, soronkénti hibalista; véglegesített rekordnál egy indoklás az egész importra. `POST /sites/{id}/import/boxes`. |
