// Sample data (CLAUDE.md "Tesztelés"): a fictitious wastewater treatment plant on a new site, built through the public
// API only, so it runs against any deployment. Two buildings, three levels, one process line across buildings and
// levels, rooms and equipment with boxes, the segmented sample model with linked elements, four approved panoramas
// with walk-through links and an info point, a drawing layer, and a finalized line with one recorded change.
//
//   node dev/seed.mjs [--url https://localhost:8543] [--org demo] [--user admin] [--password …] [--reset]
//                     [--floorplan-layer ceg:alaprajz] [--lon 19.08 --lat 47.43]
//
// --reset deletes the sample site (by name) first; without it an existing sample site stops the script.
// --floorplan-layer: the MapManager layer of the floorplans (levels by szint = 'F0' | 'F1'), e.g. the dev GeoServer
// sample (geoserver-sample.sh); without it the levels get no floorplan.
// The password also comes from SEED_PASSWORD. Self-signed certificates are accepted for localhost.
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildGlb } from './sample-model.mjs';

const args = process.argv.slice(2);
const option = (name, fallback) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 && args[i + 1] && !args[i + 1].startsWith('--') ? args[i + 1] : fallback;
};
const url = option('url', 'https://localhost:8543').replace(/\/$/, '');
const organization = option('org', 'demo');
const username = option('user', 'admin');
const password = option('password', process.env.SEED_PASSWORD);
const reset = args.includes('--reset');
const floorplanLayer = option('floorplan-layer', null);
const LON = Number(option('lon', '19.08'));
const LAT = Number(option('lat', '47.43'));
const SITE_NAME = 'Minta szennyvíztisztító telep';
const BASE_HEIGHT = 105;
const here = dirname(fileURLToPath(import.meta.url));

if (!password) {
  console.error('Password: --password … or SEED_PASSWORD (an organization admin of the target organization).');
  process.exit(1);
}
if (/^https:\/\/(localhost|127\.0\.0\.1)/.test(url)) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

// ── HTTP ────────────────────────────────────────────────────────────────────
let cookie = '';
async function api(method, path, body, headers = {}) {
  const response = await fetch(`${url}/api/v1${path}`, {
    method,
    headers: { 'Content-Type': 'application/json', Cookie: cookie, ...headers },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const setCookie = response.headers.getSetCookie?.() ?? [];
  if (setCookie.length) cookie = setCookie.map((c) => c.split(';')[0]).join('; ');
  const text = await response.text();
  const data = text ? JSON.parse(text) : null;
  if (!response.ok) throw new Error(`${method} ${path} → ${response.status} ${data?.code ?? ''} ${data?.detail ?? data?.title ?? ''}`.trim());
  return data;
}
const step = (text) => console.log(`• ${text}`);

// ── Geometry: metres east / north of the site point → WGS84 (local plane, spec 7.3), and EPSG:3857 for drawing ──
const A = 6378137;
const E2 = (1 / 298.257223563) * (2 - 1 / 298.257223563);
const RAD = Math.PI / 180;
const w = Math.sqrt(1 - E2 * Math.sin(LAT * RAD) ** 2);
const M_PER_LON = (A / w) * Math.cos(LAT * RAD) * RAD;
const M_PER_LAT = ((A * (1 - E2)) / w ** 3) * RAD;
const lonLat = (e, n) => [LON + e / M_PER_LON, LAT + n / M_PER_LAT];
const point = (e, n) => ({ type: 'Point', coordinates: lonLat(e, n) });
const mercator = ([lon, lat]) => [lon * RAD * A, Math.log(Math.tan(Math.PI / 4 + (lat * RAD) / 2)) * A];
const daysAgo = (days) => new Date(Date.now() - days * 86400000).toISOString().slice(0, 10);

// ── The plant ───────────────────────────────────────────────────────────────
/** Boxes in metres: [x0, x1] east, [y0, y1] north, [z0, z1] above the level floor (as in sample-model.mjs). */
const box = (level, x0, x1, y0, y1, z0, z1) => ({
  kind: 'region', regionShape: 'box', sizeX: x1 - x0, sizeY: y1 - y0, sizeZ: z1 - z0, headingDeg: 0, levelId: level,
  zRelM: (z0 + z1) / 2, position: point((x0 + x1) / 2, (y0 + y1) / 2),
});

try {
  step(`sign-in: ${organization}/${username} at ${url}`);
  const me = await api('POST', '/auth/login', { organization, username, password });
  if (me.mustChangePassword) throw new Error('The user must change the password first (sign in once in the browser).');
  if (me.role !== 'OrgAdmin') throw new Error('An organization admin is needed (sites are created by admins).');

  const existing = (await api('GET', '/sites')).find((s) => s.name === SITE_NAME);
  if (existing && !reset) throw new Error(`"${SITE_NAME}" already exists (id ${existing.id}); run with --reset to recreate it.`);
  if (existing) {
    step(`--reset: deleting site ${existing.id} (its files are removed by the storage cleanup later)`);
    await api('DELETE', `/sites/${existing.id}`);
  }

  step('site');
  const site = await api('POST', '/sites', { name: SITE_NAME, refPoint: point(0, 0), baseHeightM: BASE_HEIGHT });
  const S = site.id;
  const types = Object.fromEntries((await api('GET', '/node-types')).map((t) => [t.code, t.id]));
  const node = async (parentId, type, name, code = null, attrs = undefined) =>
    (await api('POST', '/nodes', { siteId: S, parentId, typeId: types[type], name, code, attrs })).id;

  step('location tree: 2 buildings, 3 levels, rooms, outdoor basin');
  const machineHouse = await node(null, 'building', 'Gépház', 'G');
  const f0 = await node(machineHouse, 'level', 'Gépház földszint', 'F0', { elevation_m: 0, height_m: 4.5 });
  const f1 = await node(machineHouse, 'level', 'Gépház emelet', 'F1', { elevation_m: 4.5, height_m: 3.5 });
  const blowerHouse = await node(null, 'building', 'Fúvóház', 'F');
  const b0 = await node(blowerHouse, 'level', 'Fúvóház földszint', 'B0', { elevation_m: 0, height_m: 5 });
  const rooms = {
    hall: await node(f0, 'room', 'Szivattyútér', 'G-F0-01'),
    switchRoom: await node(f0, 'room', 'Kapcsolótér', 'G-F0-02'),
    store: await node(f0, 'room', 'Raktár', 'G-F0-03'),
    office: await node(f1, 'room', 'Iroda', 'G-F1-01'),
    lab: await node(f1, 'room', 'Labor', 'G-F1-02'),
    changing: await node(f1, 'room', 'Öltöző', 'G-F1-03'),
    control: await node(f1, 'room', 'Vezérlőterem', 'G-F1-04'),
    blowerRoom: await node(b0, 'room', 'Fúvótér', 'F-B0-01'),
  };
  const basin = await node(null, 'outdoor_area', 'Eleveniszapos medence', 'M1');
  const roomBoxes = [
    [rooms.hall, f0, -19.7, 4.85, -11.7, 11.7, 0, 4.2],
    [rooms.switchRoom, f0, 5.15, 19.7, 0.15, 11.7, 0, 4.2],
    [rooms.store, f0, 5.15, 19.7, -11.7, -0.15, 0, 4.2],
    [rooms.office, f1, -19.7, -0.15, 0.15, 11.7, 0, 3.5],
    [rooms.lab, f1, 0.15, 19.7, 0.15, 11.7, 0, 3.5],
    [rooms.changing, f1, -19.7, -0.15, -11.7, -0.15, 0, 3.5],
    [rooms.control, f1, 0.15, 19.7, -11.7, -0.15, 0, 3.5],
    [rooms.blowerRoom, b0, 30, 44, -6, 6, 0, 5],
    [basin, null, -50, -26, -8, 8, -4, 0.5],
  ];
  for (const [id, level, ...b] of roomBoxes) await api('POST', `/nodes/${id}/anchors`, box(level, ...b));

  step('functional tree: process line across both buildings and both levels of the machine house');
  const equipment = (parentId, name, code, manufacturer, model, power, year) =>
    node(parentId, 'equipment', name, code, { manufacturer, model, power_kw: power, ...(year ? { install_year: year } : {}) });
  const line = await node(null, 'process_line', 'Előkezelő vonal', '=L01');
  const eq = {
    screen: await equipment(line, 'Szitaberendezés', '=S01', 'Huber', 'ROTAMAT Ro1', 2.2, 2018),
    p1: await equipment(line, 'Befolyó szivattyú 1', '=P01', 'Grundfos', 'SE1.80', 7.5, 2015),
    p2: await equipment(line, 'Befolyó szivattyú 2', '=P02', 'Grundfos', 'SE1.80', 7.5, 2015),
    b1: await equipment(line, 'Légfúvó 1', '=B01', 'Aerzen', 'Delta Hybrid', 55, 2019),
    b2: await equipment(line, 'Légfúvó 2', '=B02', 'Aerzen', 'Delta Hybrid', 55, 2019),
    b3: await equipment(line, 'Légfúvó 3', '=B03', 'Aerzen', 'Delta Hybrid', 75, 2023),
    cabinet: await equipment(line, 'Vonalvezérlő szekrény', '=C01', 'Rittal', 'VX25', 1.5, 2019),
    sludge: await equipment(null, 'Iszapszivattyú', '=M01', 'Flygt', 'NP 3127', 4.7),
    mixer1: await equipment(null, 'Keverő 1', '=K01', 'Flygt', '4320', 3.1, 2012),
    mixer2: await equipment(null, 'Keverő 2', '=K02', 'Flygt', '4320', 3.1, 2012),
  };
  const equipmentBoxes = [
    [eq.screen, f0, -18.6, -16.4, -2.1, 0.1, 0, 2.3],
    [eq.p1, f0, 0.75, 2.25, -4.25, -3.15, 0, 1.8],
    [eq.b1, f0, -13.1, -10.9, 4.9, 6.6, 0, 1.9],
    [eq.b2, f0, -9.1, -6.9, 4.9, 6.6, 0, 1.9],
    [eq.b3, b0, 35, 38, -1.5, 1.5, 0, 2.4],
    [eq.cabinet, f1, 5.9, 14.1, -6.1, -4.9, 0, 1.0],
    [eq.sludge, f0, -15.1, -13.7, -8.7, -7.3, 0, 1.4],
    [eq.mixer1, null, -42, -40, -1, 1, -3.5, -1.5],
    [eq.mixer2, null, -34, -32, -1, 1, -3.5, -1.5],
  ];
  for (const [id, level, ...b] of equipmentBoxes) await api('POST', `/nodes/${id}/anchors`, box(level, ...b));

  step('3D model: the segmented sample building, its elements linked to the equipment by stable id');
  const glb = buildGlb(true);
  const ticket = await api('POST', '/uploads/model', { siteId: S, files: [{ path: 'minta-gephaz.glb', size: glb.length }] });
  await put(ticket.files[0].url, glb, ticket.files[0].contentType);
  const model = await api('POST', `/sites/${S}/models`, {
    prefix: ticket.prefix, name: 'Gépház (minta BIM)', nodeId: machineHouse, elementIdProperty: 'element_id',
    placement: { lon: LON, lat: LAT, heightM: 0, headingDeg: 0 },
  });
  const elements = [
    [eq.p1, 'EQ-P01', 1.5, -3.7, 0.5], [eq.p2, 'EQ-P02', -3.0, -3.7, 0.5], [eq.b1, 'EQ-B01', -12, 5.75, 0.9],
    [eq.b2, 'EQ-B02', -8, 5.75, 0.9], [eq.sludge, 'EQ-M01', -14.4, -8, 0.65], [eq.screen, 'EQ-S01', -17.5, -1, 1.1],
  ];
  for (const [id, featureRef, e, n, z] of elements)
    await api('POST', `/nodes/${id}/anchors`, { kind: 'model_feature', modelAssetId: model.id, featureRef, levelId: f0, zRelM: z, position: point(e, n) });

  step('panoramas: 4 on the machine-house ground floor (upload, privacy check, approval)');
  const panoramaSpots = [
    ['pano-szivattyuter-a.jpg', -12, 0],
    ['pano-szivattyuter-b.jpg', -4, -7],
    ['pano-kapcsoloter.jpg', 12, 6],
    ['pano-raktar.jpg', 12, -6],
  ];
  const panoramas = [];
  for (const [file, e, n] of panoramaSpots) {
    const jpeg = readFileSync(join(here, 'seed', file));
    const upload = await api('POST', '/uploads/panorama', { siteId: S, fileName: file, size: jpeg.length });
    await put(upload.url, jpeg, upload.contentType);
    const p = await api('POST', '/panoramas', {
      uploadKey: upload.key, sha256: createHash('sha256').update(jpeg).digest('hex'), nodeId: f0, levelId: f0, zRelM: 1.6,
      headingDeg: 0, position: point(e, n),
    });
    panoramas.push(p.id);
  }
  for (const id of panoramas) {
    await waitFor(id, ['review', 'approved']);
    if ((await api('GET', `/panoramas/${id}`)).privacyStatus === 'review') await api('POST', `/panoramas/${id}/approve`, {});
  }
  for (const id of panoramas) await waitFor(id, ['approved']);
  const links = await api('POST', `/sites/${S}/panorama-links/auto`, { levelId: f0, maxDistanceM: 20 });
  // Info point of pump 1 on the second panorama: image angles towards the pump (heading 0 = image centre to north).
  const [pe, pn] = [1.5 - -4, -3.7 - -7];
  await api('POST', `/nodes/${eq.p1}/anchors`, {
    kind: 'panorama_point', panoramaId: panoramas[1], yawDeg: Math.round((Math.atan2(pe, pn) / RAD) * 10) / 10,
    pitchDeg: Math.round((Math.atan2(1.0 - 1.6, Math.hypot(pe, pn)) / RAD) * 10) / 10, levelId: f0, zRelM: 1.0, position: point(1.5, -3.7),
    positionSource: 'manual',
  });
  step(`  ${panoramas.length} approved, ${links} walk-through link(s), 1 info point`);

  step('drawing layer: escape route on the ground floor');
  const layer = await api('POST', '/draw-layers', {
    siteId: S, levelId: f0, name: 'Menekülési útvonal', geometryType: 'MultiLineString', style: { color: '#16a34a' },
    fields: [{ name: 'nev', type: 'text', length: 60, alias: 'Név' }],
  });
  await api('POST', `/draw-layers/${layer.id}/transaction`, {
    inserts: [{
      geometry: { type: 'LineString', coordinates: [[-15, 3], [-2, 3], [-2, -10], [-2, -11.85]].map(([e, n]) => mercator(lonLat(e, n))) },
      attrs: { nev: 'Szivattyútér → déli kijárat' },
    }],
  });

  if (floorplanLayer) {
    step(`floorplans: ${floorplanLayer} (szint = 'F0' | 'F1')`);
    for (const [level, code] of [[f0, 'F0'], [f1, 'F1']])
      await api('POST', `/levels/${level}/floorplans`, { source: 'mapmanager', name: 'Építészet', layer: floorplanLayer, cqlFilter: `szint = '${code}'` });
  }
  const osm = 'https://ows.terrestris.de/osm/service';
  if (!(await api('GET', `/map-layers?siteId=${S}`)).some((l) => l.externalUrl === osm)) {
    step('background: OpenStreetMap WMS (terrestris)');
    await api('POST', '/map-layers', { siteId: S, source: 'external_wms', name: 'OpenStreetMap (terrestris)', layer: 'OSM-WMS', externalUrl: osm });
  }

  step('history: the line is finalized (valid since 2024-01-01), pump 2 got a stronger motor later');
  await api('PUT', `/nodes/${eq.screen}/location`, { locationId: rooms.hall });
  await api('POST', `/nodes/${line}/finalize`, { subtree: true, validFrom: '2024-01-01' });
  await api('PATCH', `/nodes/${eq.p2}`, { attrs: { manufacturer: 'Grundfos', model: 'SE1.110', power_kw: 11, install_year: 2015 } }, {
    'X-Change-Reason': encodeURIComponent('Motorcsere nagyobb teljesítményre'),
    'X-Change-Valid-From': daysAgo(60),
  });

  console.log(`\ndone: site "${SITE_NAME}" (id ${S}) – open ${url}, choose the site, Gépház földszint, 3D tab, Funkció › Előkezelő vonal`);
} catch (e) {
  console.error(`\nfailed: ${e.message}`);
  process.exit(1);
}

/** Signed PUT straight to the object store (the API never sees the bytes). */
async function put(target, data, contentType) {
  const response = await fetch(target, { method: 'PUT', headers: { 'Content-Type': contentType }, body: data });
  if (!response.ok) throw new Error(`upload → ${response.status}`);
}

/** Waits for the panorama processing (background worker) to reach one of the states. */
async function waitFor(id, states, seconds = 180) {
  for (let i = 0; i < seconds; i++) {
    const p = await api('GET', `/panoramas/${id}`);
    if (states.includes(p.privacyStatus)) return p;
    if (p.privacyStatus === 'failed') throw new Error(`panorama ${id} processing failed: ${p.processingError}`);
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error(`panorama ${id}: still ${states.join('/')} not reached`);
}
