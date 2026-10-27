# Telepítés új szerverre (Ubuntu 24.04, Contabo VPS)

Lépésről lépésre egy friss **Ubuntu 24.04** szerver előkészítése és a rendszer első indítása. A példa egy Contabo VPS (12 GB RAM, NVMe), de bármely Ubuntu 24.04-et futtató szerveren ugyanígy megy. A napi üzemeltetés (mentés, visszaállítás, naplók, frissítés) leírása: [OPERATIONS.md](OPERATIONS.md).

> Az operációs rendszer a **sima Ubuntu 24.04** legyen, ne a cPaneles változat: a cPanel saját webszervere elfoglalná a 80/443-as portot, és nem támogatja a Dockert.

> **Ha a MapManager is erre a szerverre kerül:** az 1–6. lépés ugyanaz; utána a [12. fejezet](#12-közös-szerver-a-mapmanagerrel) szerint folytasd (a 7–8. lépés helyett).

**Amire szükség lesz**

| Mi | Példa |
|---|---|
| a szerver nyilvános IP-címe (Contabo ügyfélpanel / visszaigazoló e-mail) | `203.0.113.10` |
| egy név a szervernek: saját domain, vagy domain nélkül az ingyenes [sslip.io](#név-domain-nélkül-sslipio) | `twin.ceg.hu` |
| e-mail-cím a tanúsítványértesítésekhez | `it@ceg.hu` |
| hozzáférés a GitHub-repóhoz | `github.com/Bukovszki/SiteTwin3D` |

A lépések sorrendje:

1. [SSH-kulcs a saját gépeden](#1-ssh-kulcs-létrehozása-a-saját-gépeden)
2. [Első belépés, felhasználó, SSH lezárása](#2-első-belépés-és-saját-felhasználó)
3. [Tűzfal, swap, időzóna](#3-tűzfal-swap-időzóna)
4. [Docker telepítése](#4-docker-telepítése)
5. [DNS](#5-dns-beállítása)
6. [A kód letöltése a szerverre (deploy key)](#6-a-kód-letöltése-github-deploy-key)
7. [Beállítások (`.env`)](#7-beállítások-env)
8. [Első indítás](#8-első-indítás)
9. [Első belépés a felületre, szervezet, MapManager](#9-első-belépés-a-felületre)
10. [Mentés ütemezése](#10-mentés-ütemezése)
11. [Frissítés később](#11-frissítés)
12. [Közös szerver a MapManagerrel](#12-közös-szerver-a-mapmanagerrel) – és az adatok átköltöztetése

---

## 1. SSH-kulcs létrehozása a saját gépeden

A szerverre jelszó helyett **kulccsal** lépünk be: a kulcspár privát fele a saját gépeden marad, a nyilvános felét tesszük a szerverre. Ezt egyszer kell megcsinálni; ugyanazzal a kulccsal több szerver is elérhető.

**macOS / Linux (Terminál):**

```bash
ssh-keygen -t ed25519 -C "nev@ceg.hu"
```

**Windows 10/11 (PowerShell)** – az OpenSSH beépített:

```powershell
ssh-keygen -t ed25519 -C "nev@ceg.hu"
```

A program megkérdezi:

1. **Hová mentse** – Enter (alapértelmezés: `~/.ssh/id_ed25519`, Windowson `C:\Users\<név>\.ssh\id_ed25519`). Ha már van ilyen fájlod, ne írd felül: adj meg új nevet, pl. `~/.ssh/id_ed25519_twin`.
2. **Jelmondat (passphrase)** – adj meg egyet. Ez védi a kulcsot, ha a géped illetéktelen kezekbe kerül. (macOS-en a kulcstartó megjegyzi; Windowson az `ssh-agent` szolgáltatás.)

Két fájl jön létre:

| Fájl | Mi ez | Kinek adható |
|---|---|---|
| `id_ed25519` | **privát kulcs** | senkinek – soha ne másold sehová, ne küldd el |
| `id_ed25519.pub` | **nyilvános kulcs** | ezt kell a szerverre tenni |

A nyilvános kulcs kiírása (egy sor, `ssh-ed25519 AAAA… nev@ceg.hu`):

```bash
cat ~/.ssh/id_ed25519.pub                          # macOS / Linux
type $env:USERPROFILE\.ssh\id_ed25519.pub          # Windows PowerShell
```

**A kulcs a szerverre:**

- **Rendeléskor / újratelepítéskor:** ha a Contabo felületén megadható SSH-kulcs, másold be ide a nyilvános kulcs sorát – így a `root` felhasználó már kulccsal is elérhető.
- **Utólag** (ha csak root-jelszót kaptál), a saját gépedről:

  ```bash
  ssh-copy-id root@203.0.113.10                                       # macOS / Linux
  ```

  ```powershell
  type $env:USERPROFILE\.ssh\id_ed25519.pub | ssh root@203.0.113.10 "mkdir -p ~/.ssh && chmod 700 ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys"
  ```

  (Ilyenkor utoljára kéri a root jelszavát.)

**Kényelmi beállítás** – a saját gépeden a `~/.ssh/config` fájlba (Windowson `C:\Users\<név>\.ssh\config`):

```
Host twin
    HostName 203.0.113.10
    User deploy
    IdentityFile ~/.ssh/id_ed25519
```

Ezután elég annyi: `ssh twin` (a `deploy` felhasználót a következő lépésben hozzuk létre).

## 2. Első belépés és saját felhasználó

```bash
ssh root@203.0.113.10
```

Az első belépéskor a gép megkérdezi, megbízol-e a szerver ujjlenyomatában – `yes`.

**Rendszerfrissítés:**

```bash
apt update && apt full-upgrade -y
reboot            # ha kernelfrissítés volt; utána lépj be újra
```

**Saját felhasználó** (`deploy`) sudo-joggal – a napi munkát ezzel végezzük, nem root-ként:

```bash
adduser deploy                      # jelszót kér: ez a sudo-jelszó lesz
usermod -aG sudo deploy
rsync --archive --chown=deploy:deploy ~/.ssh /home/deploy   # a root kulcsát megkapja a deploy is
```

**Próbáld ki egy MÁSIK terminálablakban**, mielőtt bármit lezársz:

```bash
ssh deploy@203.0.113.10
sudo whoami        # → root
```

**SSH lezárása** (csak ha a fenti belépés működött!): root-belépés és jelszavas belépés tiltása.

```bash
sudo tee /etc/ssh/sshd_config.d/01-hardening.conf > /dev/null <<'EOF'
PermitRootLogin no
PasswordAuthentication no
KbdInteractiveAuthentication no
EOF
sudo sshd -t && sudo systemctl restart ssh
sudo sshd -T | grep -E 'permitrootlogin|passwordauthentication'
#   permitrootlogin no
#   passwordauthentication no
```

> A fájlnév `01-` kezdete számít: az sshd az **első** talált értéket használja, és egyes szolgáltatói képeken van egy `50-cloud-init.conf`, amely engedélyezné a jelszót. Az utolsó parancs kimenete mutatja, mi érvényes.

A nyitva hagyott root-munkamenetből most már kiléphetsz. Innentől: `ssh twin`.

## 3. Tűzfal, swap, időzóna

**Tűzfal** – kívülről csak az SSH és a web:

```bash
sudo ufw allow OpenSSH
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw allow 443/udp      # HTTP/3
sudo ufw enable
sudo ufw status
```

> A Docker a közzétett portjait az `ufw` mellett nyitja meg. A telepítés csak a webet (80/443) teszi közzé; az adatbázis, az objektumtár, az API és a konverter csak a konténerek belső hálózatán érhető el.

**Swap** (4 GB) – tartalék, ha egy nagy modell átalakítása vagy egy build kifutna a memóriából:

```bash
sudo fallocate -l 4G /swapfile && sudo chmod 600 /swapfile
sudo mkswap /swapfile && sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
echo 'vm.swappiness=10' | sudo tee /etc/sysctl.d/99-swap.conf && sudo sysctl --system > /dev/null
free -h
```

**Időzóna** (a mentés cron-időpontjai ehhez igazodnak):

```bash
sudo timedatectl set-timezone Europe/Budapest
```

Az automatikus biztonsági frissítések (`unattended-upgrades`) Ubuntun alapból be vannak kapcsolva.

## 4. Docker telepítése

A Docker **hivatalos** csomagtárolójából (nem az Ubuntu régebbi `docker.io` csomagjából):

```bash
sudo apt-get install -y ca-certificates curl git
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "${UBUNTU_CODENAME:-$VERSION_CODENAME}") stable" \
  | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo usermod -aG docker deploy
```

Lépj ki és be újra (`exit`, majd `ssh twin`), hogy a csoporttagság érvényes legyen, majd:

```bash
docker run --rm hello-world
docker compose version
```

> A `docker` csoport tagjai gyakorlatilag root-jogúak a szerveren – csak megbízható felhasználó legyen benne.

## 5. DNS beállítása

A domain DNS-kezelőjében (ahol a domaint regisztráltad):

| Típus | Név | Érték |
|---|---|---|
| `A` | `twin` (→ `twin.ceg.hu`) | `203.0.113.10` |

Ellenőrzés a saját gépedről (néhány perc – néhány óra, mire él):

```bash
nslookup twin.ceg.hu      # → 203.0.113.10
```

A HTTPS-tanúsítványt a rendszer magától kéri (Let's Encrypt) az első indításkor – ehhez a domainnek már a szerverre kell mutatnia, és a 80-as portnak nyitva kell lennie.

### Név domain nélkül: sslip.io

Teszt- és demószerverhez nem kell domaint venni: az ingyenes **sslip.io** szolgáltatásnál minden név, amelyben benne van az IP-cím (pontok helyett kötőjellel), magától arra az IP-re mutat – regisztráció és DNS-beállítás nélkül, és HTTPS-tanúsítvány is kérhető rá. A `203.0.113.10` IP-hez:

| Alkalmazás | Név |
|---|---|
| ez a rendszer | `sitetwin3d.203-0-113-10.sslip.io` |
| MapManager | `mapmanager.203-0-113-10.sslip.io` |

Ellenőrzés: `nslookup sitetwin3d.203-0-113-10.sslip.io` → `203.0.113.10`. Később saját domainre átállni a `.env`-ben a `DOMAIN` és a `PUBLIC_URL` átírása és egy újraindítás (`docker compose up -d`).

## 6. A kód letöltése (GitHub deploy key)

Ha a repó **privát**, a szerver egy saját, csak olvasási jogú kulccsal (deploy key) éri el – így a szerveren nincs személyes GitHub-hozzáférés.

**A szerveren** (a `deploy` felhasználóval) – jelmondat nélkül, mert a szerver magától használja:

```bash
ssh-keygen -t ed25519 -f ~/.ssh/github_deploy -C "twin szerver deploy key" -N ""
cat >> ~/.ssh/config <<'EOF'
Host github.com
    IdentityFile ~/.ssh/github_deploy
    IdentitiesOnly yes
EOF
chmod 600 ~/.ssh/config
cat ~/.ssh/github_deploy.pub
```

**A GitHubon:** a repó → **Settings** → **Deploy keys** → **Add deploy key** → cím: pl. „Contabo szerver”, kulcs: a fenti `.pub` sor → az **Allow write access** maradjon kikapcsolva → **Add key**.

**Letöltés:**

```bash
sudo mkdir -p /opt/app && sudo chown deploy:deploy /opt/app
ssh -T git@github.com          # „Hi Bukovszki/SiteTwin3D! You've successfully authenticated…” – az első alkalommal: yes
git clone git@github.com:Bukovszki/SiteTwin3D.git /opt/app
```

(Nyilvános repónál a deploy key elhagyható: `git clone https://github.com/Bukovszki/SiteTwin3D.git /opt/app`.)

## 7. Beállítások (`.env`)

```bash
cd /opt/app/deploy
cp .env.example .env
chmod 600 .env
nano .env
```

Véletlen jelszavak generálása (minden értékhez újat):

```bash
openssl rand -base64 24     # jelszavak
openssl rand -hex 16        # objektumtár-kulcsok (csak betű és szám)
```

| Változó | Érték a példában | Megjegyzés |
|---|---|---|
| `DOMAIN` | `twin.ceg.hu` | a DNS-ben beállított név |
| `PUBLIC_URL` | `https://twin.ceg.hu` | amit a felhasználók megnyitnak |
| `ACME_EMAIL` | `it@ceg.hu` | a tanúsítvány lejárati értesítéseihez |
| `POSTGRES_PASSWORD` | *(generált)* | |
| `PLATFORM_ADMIN_USER` | `admin` | az első platform-adminisztrátor |
| `PLATFORM_ADMIN_PASSWORD` | *(generált)* | az első belépéskor cserélni kell |
| `STORAGE_ACCESS_KEY`, `STORAGE_SECRET_KEY` | *(generált, hex)* | az objektumtár kulcspárja |
| `CONVERTER_MEMORY` | `5g` | 12 GB RAM-hoz, ha egyedül fut a szerveren (MapManagerrel együtt: `4g`) |
| `STORAGE_MAX_VOLUMES` | lásd alább | a fájltár felső korlátja GB-ban |

**`STORAGE_MAX_VOLUMES`** – a panorámák és modellek helye nagyjából ennyi GB. Hagyj helyet a rendszernek, a Docker-képeknek, az adatbázisnak (együtt ~30 GB) és – ha a mentés ezen a lemezen marad – a fájlok mentett másolatának is. Lemezméret: `df -h /`. Példa 200 GB-os lemezre, helyben tartott mentéssel: `STORAGE_MAX_VOLUMES=80`.

A `.env` titkokat tartalmaz: ne kerüljön a gitbe (a `.gitignore` kizárja), és mentsd el biztonságos helyre (jelszókezelő) – a szerver elvesztésekor a mentések visszaállításához kell.

## 8. Első indítás

```bash
cd /opt/app/deploy
docker compose up -d --build
```

Az első build 10–20 perc (a webfelület és a konverter fordítása); a későbbiek gyorsabbak. Utána:

```bash
docker compose ps                         # minden szolgáltatás "running", a db/api/storage "healthy"
docker compose logs -f web                # a tanúsítvány megszerzése: „certificate obtained successfully”
curl https://twin.ceg.hu/health           # → Healthy
```

Ha a tanúsítvány nem jön meg: ellenőrizd, hogy a domain a szerverre mutat-e (`nslookup`), és hogy a 80-as port nyitva van-e (`sudo ufw status`); a hibát a `docker compose logs web` írja ki.

## 9. Első belépés a felületre

1. Nyisd meg: `https://twin.ceg.hu` → szervezet: `platform`, felhasználó és jelszó a `.env`-ből (`PLATFORM_ADMIN_*`) → jelszócsere.
2. **Szervezetek** → **Új szervezet**: azonosító (pl. `ceg`), név, az első szervezeti admin felhasználóneve és ideiglenes jelszava. Ha van MapManager: a **MapManager címe** és a **szolgáltatástoken hivatkozása** (pl. `ceg`).
3. **MapManager-token** (ha van): a tokent nem a felületen, hanem a szerveren kell megadni, a gitből kizárt `deploy/api.env` fájlban:

   ```bash
   cd /opt/app/deploy
   echo 'MapManager__Tokens__ceg=mmst_…' >> api.env      # a „ceg” a 2. pontban megadott hivatkozás
   chmod 600 api.env
   docker compose up -d api
   ```

4. Kilépés, majd belépés a szervezet adminjával (szervezet: `ceg`) → jelszócsere → **Telephelyek** → az első telephely. Innen a [felhasználói kézikönyv](USER_GUIDE.md) vezet tovább.

## 10. Mentés ütemezése

Napi mentés hajnali 2-kor (a `deploy` felhasználó crontabjába: `crontab -e`):

```cron
0 2 * * * cd /opt/app/deploy && ./backup.sh >> backups/backup.log 2>&1
```

A mentés **ugyanerre a szerverre** kerül (`deploy/backups/`). Vidd át rendszeresen máshová, pl. a saját gépedről:

```bash
rsync -az --delete twin:/opt/app/deploy/backups/ ~/twin-mentesek/
```

Részletek, visszaállítás, próba-visszaállítás: [OPERATIONS.md, 3. fejezet](OPERATIONS.md#3-mentés-és-visszaállítás).

## 11. Frissítés

```bash
cd /opt/app/deploy
./backup.sh                       # előbb mindig mentés
git pull
docker compose up -d --build
docker image prune -f             # a régi képek törlése (helyet szabadít fel)
```

Az API induláskor lefuttatja az adatbázis-migrációkat. Ha valami nem működik, a mentésből vissza lehet állni ([OPERATIONS.md, 3.3](OPERATIONS.md#33-visszaállítás)).

---

---

## 12. Közös szerver a MapManagerrel

A két rendszer egy szerveren, saját néven fut (pl. `sitetwin3d.203-0-113-10.sslip.io` és `mapmanager.203-0-113-10.sslip.io`). Mivel mindkettő a 80/443-as portot használná, egy kis **közös belépési pont** (nginx, `deploy/edge`) kapja meg a portokat, és a kért név alapján adja tovább a kapcsolatot a megfelelő alkalmazás saját webszerverének. A titkosított kapcsolatot nem bontja ki, így mindkét alkalmazás a saját HTTPS-tanúsítványával és beállításaival, változatlanul fut. A `mapmanager.`-rel kezdődő nevek a MapManagerhez, minden más ehhez a rendszerhez kerül.

```
internet ─80/443─▶ edge (nginx) ─┬─ sitetwin3d.… ─▶ ez a rendszer (Caddy + API + …)
                                 └─ mapmanager.… ─▶ MapManager (Caddy + API + GeoServer + …)
```

A MapManager repóját ehhez nem kell módosítani: a kiegészítő compose-fájlja ebben a repóban van (`deploy/edge/mapmanager.yml`), és a MapManager `.env`-jében egy sor kapcsolja be. Így a MapManager saját szkriptjei (`update.sh`, `backup.sh`) is ezzel futnak.

**Memória (12 GB):** ebben a rendszerben `CONVERTER_MEMORY=4g`; a MapManager GeoServere alapból 1 GB-os (`GEOSERVER_MAX_HEAP`), ez demóhoz elég.

### 12.1 A belépési pont

Az 1–6. lépés után (a kód a `/opt/app` alatt van):

```bash
cd /opt/app/deploy/edge
docker compose up -d          # létrehozza az "edge" hálózatot is
docker compose ps
```

### 12.2 Ez a rendszer

A [7. lépés](#7-beállítások-env) szerint, ezekkel az eltérésekkel a `deploy/.env`-ben:

```ini
DOMAIN=sitetwin3d.203-0-113-10.sslip.io
PUBLIC_URL=https://sitetwin3d.203-0-113-10.sslip.io
CONVERTER_MEMORY=4g
# a belépési pont mögött: a webszerver nem foglal portot, az "edge" hálózaton érhető el
COMPOSE_FILE=docker-compose.yml:edge/twin.yml
```

```bash
cd /opt/app/deploy
docker compose up -d --build
curl https://sitetwin3d.203-0-113-10.sslip.io/health      # → Healthy
```

### 12.3 MapManager

A MapManager külön repó, ezért **külön deploy key** kell hozzá (a GitHub egy kulcsot csak egy repóhoz enged):

```bash
ssh-keygen -t ed25519 -f ~/.ssh/github_mapmanager -C "twin szerver MapManager deploy key" -N ""
cat >> ~/.ssh/config <<'EOF'
Host github-mapmanager
    HostName github.com
    IdentityFile ~/.ssh/github_mapmanager
    IdentitiesOnly yes
EOF
cat ~/.ssh/github_mapmanager.pub     # → GitHub: MapManager repó → Settings → Deploy keys → Add deploy key (csak olvasás)

sudo mkdir -p /opt/mapmanager && sudo chown deploy:deploy /opt/mapmanager
git clone git@github-mapmanager:Bukovszki/MapManager.git /opt/mapmanager
cd /opt/mapmanager/deploy
cp .env.example .env && chmod 600 .env && nano .env
```

A MapManager `.env`-jében ezek az eltérések; a többi értéket a MapManager telepítési leírása (`deploy/README.md` a MapManager repójában) szerint kell kitölteni, a jelszavakat `openssl rand -base64 24`-gyel:

```ini
DOMAIN=mapmanager.203-0-113-10.sslip.io
ACME_EMAIL=it@ceg.hu
# a belépési pont mögött (a kiegészítő fájl ennek a rendszernek a repójában van)
COMPOSE_FILE=docker-compose.yml:/opt/app/deploy/edge/mapmanager.yml
```

```bash
docker compose up -d --build
docker compose logs -f api        # első indítás: migrációk, platform-admin
```

A MapManager tűzfalszkriptje (`firewall.sh`) ugyanazokat a portokat nyitja (22, 80, 443); a 3. lépés után nem kell futtatni.

### 12.4 A két rendszer összekötése

1. MapManager (`https://mapmanager.203-0-113-10.sslip.io`): platform-admin → cég létrehozása, rétegek publikálása; a szolgáltatástoken kiadása a MapManager leírása szerint.
2. Ez a rendszer (`https://sitetwin3d.203-0-113-10.sslip.io`): platform-admin → **Szervezetek** → a szervezet **MapManager címe**: `https://mapmanager.203-0-113-10.sslip.io`, **szolgáltatástoken hivatkozása**: pl. `ceg`.
3. A token a szerveren, a gitből kizárt fájlba:

   ```bash
   cd /opt/app/deploy
   echo 'MapManager__Tokens__ceg=mmst_…' >> api.env && chmod 600 api.env
   docker compose up -d api
   ```

### 12.5 Mentés, frissítés

- **Mentés:** mindkét rendszer a saját szkriptjével, két cron-sorral (`crontab -e`):

  ```cron
  0 2 * * * cd /opt/app/deploy && ./backup.sh >> backups/backup.log 2>&1
  30 2 * * * cd /opt/mapmanager/deploy && ./backup.sh >> backups/backup.log 2>&1
  ```

- **Frissítés:** ez a rendszer a [11. lépés](#11-frissítés) szerint, a MapManager a saját `update.sh`-jával. A `COMPOSE_FILE` miatt mindkettő a belépési pont mögött marad. A belépési pont ritkán változik: `cd /opt/app/deploy/edge && docker compose pull && docker compose up -d`.
- **Naplók:** a belépési pont mögött az alkalmazások naplójában az ügyfél IP-címe helyett a belépési pont belső címe látszik, mert a titkosított kapcsolatot nem bontja ki.

### 12.6 Adatok átköltöztetése egy másik szerverről

Az új szerveren előbb indítsd el üresen mindkét rendszert (12.1–12.3), utána:

**Ez a rendszer:** a régi szerveren mentés, átvitel, az újon visszaállítás.

```bash
# régi szerveren
cd <régi telepítés>/deploy && ./backup.sh
# a mentés (adatbázis + fájlok) átvitele az új szerverre, pl. a saját gépedről
rsync -az regi:<régi telepítés>/deploy/backups/ twin:/opt/app/deploy/backups/
# új szerveren
cd /opt/app/deploy && ./restore.sh backups/db/appdb-<időbélyeg>.dump
```

A visszaállítás után a felhasználóknak újra be kell lépniük. Ha a MapManager címe változott, a szervezet **MapManager címét** írd át, és a tokent tedd az `api.env`-be (12.4).

**MapManager:** két szkript ebben a repóban (`deploy/edge/`), a MapManager saját `backup.sh`-jára építve. Négy dolgot visz át: adatbázis, GeoServer adatkönyvtár (`geoserver_data`), feltöltött fájlok (`uploads`), süti-kulcsok (`dpkeys`). Mellé a régi `.env`-et is, mert a GeoServer adatkönyvtára a **régi** GeoServer-admin jelszót és az adattárak adatbázis-jelszavát (`GEOSERVER_DB_PASSWORD`) tartalmazza – az új `.env`-be ezek kerülnek át, a többi (pl. `DOMAIN`) az új marad. A `pg_dump` a szerepköröket nem menti: a GeoServer olvasó szerepkörei (`mapmanager_olvaso`, `mapmanager_geoserver`) a visszaállítás előtt jönnek létre.

Előfeltétel az új szerveren:

1. **A belépési pont fut** (12.1), és ez a rendszer mögötte van (12.2): `docker ps`-ben az `edge-edge-1` foglalja a 80/443-at, a `twin-web-1`-nek nincs nyilvános portja. Ha a belépési pont a MapManager első indításakor még nem fut, a MapManager webszervere nem kap tanúsítványt (a Let's Encrypt „Connection refused”-ot kap a 80-as porton, a böngésző `ERR_SSL_PROTOCOL_ERROR`-t mutat) – ilyenkor a belépési pont elindítása után `cd /opt/mapmanager/deploy && docker compose restart web`.
2. A MapManager a 12.3 szerint klónozva, ugyanaz vagy újabb verzió, mint a régin. A `.env`-et legegyszerűbb a csomagban lévő régiből indítani, és csak a címet átírni:
   ```bash
   cd /opt/mapmanager/deploy
   tar -xOf ~/mapmanager-migration-<időbélyeg>.tar <időbélyeg>/mapmanager.env > .env && chmod 600 .env
   nano .env    # DOMAIN=mapmanager.<új név>, ACME_EMAIL, COMPOSE_FILE=docker-compose.yml:/opt/app/deploy/edge/mapmanager.yml
   ``` Ez a repó (és benne a két szkript) mindkét szerveren legyen meg (`/opt/app`); ha a régin nincs, a `mapmanager-export.sh`-t elég odamásolni.

```bash
# 1. RÉGI szerveren – leállítja a szerkesztést (api, web, geoserver), ment, egy fájlba csomagol (a $HOME-ba)
MM_DIR=/opt/mapmanager/deploy /opt/app/deploy/edge/mapmanager-export.sh

# 2. Másolás közvetlenül a régi szerverről az újra (a csomag több GB is lehet; jelszavak vannak benne: csak SSH-n).
#    A saját gépedről, a kulcsodat továbbítva (-A); a -P folytatja, ha megszakad:
ssh-add && ssh -A deploy@uj
rsync -P 'root@regi:/root/mapmanager-migration-<időbélyeg>.tar*' ~/
#    (ha a régi szerverre más kulccsal lépsz be: a gépeden  scp -3 'root@regi:/root/mapmanager-migration-*' deploy@uj:~/ )

# 3. ÚJ szerveren – ellenőrzőösszeg, jelszavak átvétele a .env-be (a régi .env mentése: .env.before-migration-…),
#    szerepkörök, pg_restore, kötetek, indítás
MM_DIR=/opt/mapmanager/deploy /opt/app/deploy/edge/mapmanager-import.sh ~/mapmanager-migration-<időbélyeg>.tar
```

A visszatöltő szkript a végén kiírja, mit kell kézzel ellenőrizni: belépés a régi felhasználókkal, egy réteg a térképen, a GeoServer naplója; ha a régi GeoServerben **Proxy Base URL** volt beállítva, azt jelzi – írd át az új címre (GeoServer → Settings → Global). A rétegek nevei és a szolgáltatástokenek az adatbázissal jönnek, így ennek a rendszernek a réteg- és alaprajz-hivatkozásai érvényesek maradnak; ha a MapManager címe változott, a szervezet **MapManager címét** írd át (12.4). A régi MapManager leállítva marad (`docker compose up -d`-vel újraindítható); ha minden rendben, a csomagot mindkét szerverről töröld.

> Átköltöztetés előtt mindkét rendszerből készüljön friss mentés a régi szerveren, és az új szerveren egy próbabelépéssel ellenőrizd az adatokat, mielőtt a régit leállítod.

---

## Ellenőrzőlista

- [ ] SSH csak kulccsal, root-belépés tiltva (`sudo sshd -T | grep -E 'permitrootlogin|passwordauthentication'`)
- [ ] `ufw status`: csak 22, 80, 443 nyitva
- [ ] `https://<domain>/health` → `Healthy`, érvényes tanúsítvány
- [ ] platform-admin jelszava lecserélve, a `.env` elmentve jelszókezelőbe
- [ ] napi mentés cronban, a mentések rendszeresen másik gépre kerülnek
- [ ] egy próba-visszaállítás megtörtént (negyedévente ismételni)
- [ ] közös szervernél: mindkét név `https://`-sel betölt, és mindkettő a saját tanúsítványát mutatja
