// Development only: writes a sample GLB of the building of the sample floorplan (geoserver-sample.sh): 40 m × 24 m,
// ground floor 4.5 m (pump hall, switch room, store), first floor 3.5 m (4 rooms), walls 0.3 m, slabs, roof and the
// equipment of the demo data (pumps, blowers, sludge pump, pipe). Origin: the site point on the ground-floor level,
// glTF axes: +X east, +Y up, −Z north – upload it with placement (site point, height 0 m, heading 0°).
// --segmented: every element gets a feature id (EXT_mesh_features) and a stable "element_id" in a property table
// (EXT_structural_metadata), like a BIM export (spec 9.1) – element id property of the model: element_id.
//   node dev/sample-model.mjs [out.glb] [--segmented]
import { writeFileSync } from 'node:fs';

const segmented = process.argv.includes('--segmented');
const out = process.argv.slice(2).find((a) => !a.startsWith('--')) ?? (segmented ? 'sample-building-bim.glb' : 'sample-building.glb');

/** Axis-aligned box in ENU metres: [x0, x1] east, [y0, y1] north, [z0, z1] up; with its element id and name. */
const materials = {
  wall: { color: [0.86, 0.85, 0.82, 1], boxes: [] },
  slab: { color: [0.62, 0.62, 0.64, 1], boxes: [] },
  roof: { color: [0.55, 0.3, 0.25, 1], boxes: [] },
  pump: { color: [0.15, 0.4, 0.8, 1], boxes: [] },
  blower: { color: [0.9, 0.5, 0.1, 1], boxes: [] },
  pipe: { color: [0.45, 0.47, 0.5, 1], boxes: [] },
  furniture: { color: [0.55, 0.42, 0.3, 1], boxes: [] },
};
const elements = [];
function add(material, x0, x1, y0, y1, z0, z1, id, name) {
  const n = elements.length;
  elements.push({ id: id ?? `${material.toUpperCase()}-${String(n + 1).padStart(3, '0')}`, name: name ?? material });
  materials[material].boxes.push([x0, x1, y0, y1, z0, z1, n]);
}

/** Outer walls of a storey. */
function shell(z0, z1) {
  add('wall', -20, 20, 11.7, 12, z0, z1);
  add('wall', -20, 20, -12, -11.7, z0, z1);
  add('wall', -20, -19.7, -11.7, 11.7, z0, z1);
  add('wall', 19.7, 20, -11.7, 11.7, z0, z1);
}
/** A wall along y at x (with door gaps [y0, y1]). */
function wallY(x, y0, y1, z0, z1, doors = []) {
  let from = y0;
  for (const [d0, d1] of doors) {
    add('wall', x - 0.15, x + 0.15, from, d0, z0, z1);
    add('wall', x - 0.15, x + 0.15, d0, d1, z0 + 2.2, z1); // lintel
    from = d1;
  }
  add('wall', x - 0.15, x + 0.15, from, y1, z0, z1);
}
function wallX(y, x0, x1, z0, z1, doors = []) {
  let from = x0;
  for (const [d0, d1] of doors) {
    add('wall', from, d0, y - 0.15, y + 0.15, z0, z1);
    add('wall', d0, d1, y - 0.15, y + 0.15, z0 + 2.2, z1);
    from = d1;
  }
  add('wall', from, x1, y - 0.15, y + 0.15, z0, z1);
}

// Ground floor (level F0, 0 … 4.5 m): pump hall | switch room (north) / store (south).
add('slab', -20, 20, -12, 12, -0.3, 0);
shell(0, 4.2);
wallY(5, -11.7, 11.7, 0, 4.2, [[-6, -4.8], [5, 6.2]]);
wallX(0, 5.15, 19.7, 0, 4.2, [[11, 12.2]]);
// First floor (level F1, 4.5 … 8 m): office, lab, changing room, control room.
add('slab', -20, 20, -12, 12, 4.2, 4.5);
shell(4.5, 8);
wallY(0, -11.7, 11.7, 4.5, 8, [[-6, -5], [5, 6]]);
wallX(0, -19.7, 19.7, 4.5, 8, [[-10, -9], [9, 10]]);
add('roof', -20.3, 20.3, -12.3, 12.3, 8, 8.3);

// Equipment of the demo data on the ground floor (pump hall).
add('pump', 0.9, 2.1, -4.1, -3.3, 0, 1.0, 'EQ-P01', 'Befolyó szivattyú 1');
add('pump', 1.25, 1.75, -3.95, -3.45, 1.0, 1.6, 'EQ-P01-M', 'Befolyó szivattyú 1 motor');
add('pump', -3.6, -2.4, -4.1, -3.3, 0, 1.0, 'EQ-P02', 'Befolyó szivattyú 2');
add('pump', -3.25, -2.75, -3.95, -3.45, 1.0, 1.6, 'EQ-P02-M', 'Befolyó szivattyú 2 motor');
add('pipe', -3.0, 1.5, -3.83, -3.57, 1.75, 2.0); // common pressure pipe
add('pipe', 1.37, 1.63, -3.83, -3.57, 1.6, 1.75);
add('pipe', -2.87, -2.63, -3.83, -3.57, 1.6, 1.75);
add('pipe', 1.5, 4.85, -3.83, -3.57, 1.75, 2.0);
add('blower', -13, -11, 5, 6.5, 0, 1.8, 'EQ-B01', 'Légfúvó 1');
add('blower', -9, -7, 5, 6.5, 0, 1.8, 'EQ-B02', 'Légfúvó 2');
add('pipe', -12.15, -11.85, 6.5, 11.7, 1.4, 1.7);
add('pipe', -8.15, -7.85, 6.5, 11.7, 1.4, 1.7);
add('pump', -15, -13.8, -8.6, -7.4, 0, 1.3, 'EQ-M01', 'Iszapszivattyú');
add('blower', -18.5, -16.5, -2, 0, 0, 2.2, 'EQ-S01', 'Szitaberendezés');
// First floor furniture.
add('furniture', -15, -11, 6, 7, 4.5, 5.25);
add('furniture', 6, 14, -6, -5, 4.5, 5.4); // control desk
add('furniture', 4, 16, 9, 10.5, 4.5, 5.4); // lab bench

// ── GLB writer ────────────────────────────────────────────────────────────────
/** 24 vertices (flat normals) per box; glTF coordinates: (east, up, −north); the element index per vertex. */
function boxGeometry(boxes) {
  const pos = [];
  const nor = [];
  const fid = [];
  const idx = [];
  for (const [x0, x1, y0, y1, z0, z1, element] of boxes) {
    const g = (x, y, z) => [x, z, -y];
    const faces = [
      [[1, 0, 0], [[x1, y0, z0], [x1, y1, z0], [x1, y1, z1], [x1, y0, z1]]],
      [[-1, 0, 0], [[x0, y1, z0], [x0, y0, z0], [x0, y0, z1], [x0, y1, z1]]],
      [[0, 1, 0], [[x1, y1, z0], [x0, y1, z0], [x0, y1, z1], [x1, y1, z1]]],
      [[0, -1, 0], [[x0, y0, z0], [x1, y0, z0], [x1, y0, z1], [x0, y0, z1]]],
      [[0, 0, 1], [[x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1]]],
      [[0, 0, -1], [[x0, y1, z0], [x1, y1, z0], [x1, y0, z0], [x0, y0, z0]]],
    ];
    for (const [n, corners] of faces) {
      const base = pos.length / 3;
      for (const c of corners) {
        pos.push(...g(...c));
        nor.push(...g(...n));
        fid.push(element);
      }
      idx.push(base, base + 1, base + 2, base, base + 2, base + 3);
    }
  }
  return { pos: new Float32Array(pos), nor: new Float32Array(nor), fid: new Float32Array(fid), idx: new Uint32Array(idx) };
}

const gltf = { asset: { version: '2.0', generator: 'sample-model.mjs' }, scenes: [{ nodes: [0] }], scene: 0, nodes: [{ mesh: 0, name: 'Gépház' }],
  meshes: [{ primitives: [] }], materials: [], accessors: [], bufferViews: [], buffers: [] };
const chunks = [];
let offset = 0;
function view(typed, target) {
  const bytes = Buffer.from(typed.buffer, typed.byteOffset, typed.byteLength);
  gltf.bufferViews.push({ buffer: 0, byteOffset: offset, byteLength: bytes.length, ...(target ? { target } : {}) });
  chunks.push(bytes);
  offset += bytes.length;
  const pad = (4 - (offset % 4)) % 4;
  if (pad) {
    chunks.push(Buffer.alloc(pad));
    offset += pad;
  }
  return gltf.bufferViews.length - 1;
}
for (const [name, m] of Object.entries(materials)) {
  const { pos, nor, fid, idx } = boxGeometry(m.boxes);
  const min = [Infinity, Infinity, Infinity];
  const max = [-Infinity, -Infinity, -Infinity];
  for (let i = 0; i < pos.length; i++) {
    min[i % 3] = Math.min(min[i % 3], pos[i]);
    max[i % 3] = Math.max(max[i % 3], pos[i]);
  }
  const count = pos.length / 3;
  gltf.accessors.push({ bufferView: view(pos, 34962), componentType: 5126, count, type: 'VEC3', min, max });
  gltf.accessors.push({ bufferView: view(nor, 34962), componentType: 5126, count, type: 'VEC3' });
  gltf.accessors.push({ bufferView: view(idx, 34963), componentType: 5125, count: idx.length, type: 'SCALAR' });
  gltf.materials.push({ name, pbrMetallicRoughness: { baseColorFactor: m.color, metallicFactor: 0, roughnessFactor: 0.9 } });
  const a = gltf.accessors.length;
  const primitive = { attributes: { POSITION: a - 3, NORMAL: a - 2 }, indices: a - 1, material: gltf.materials.length - 1 };
  if (segmented) {
    gltf.accessors.push({ bufferView: view(fid, 34962), componentType: 5126, count, type: 'SCALAR' });
    primitive.attributes._FEATURE_ID_0 = gltf.accessors.length - 1;
    primitive.extensions = { EXT_mesh_features: { featureIds: [{ featureCount: elements.length, attribute: 0, propertyTable: 0 }] } };
  }
  gltf.meshes[0].primitives.push(primitive);
}
if (segmented) {
  // One property table: element_id (stable, e.g. an IFC GlobalId in real exports) and name, as UTF-8 strings.
  const strings = (list) => {
    const bytes = list.map((t) => Buffer.from(t, 'utf8'));
    const offsets = new Uint32Array(bytes.length + 1);
    bytes.forEach((b, i) => (offsets[i + 1] = offsets[i] + b.length));
    return { values: view(Buffer.concat(bytes)), stringOffsets: view(offsets) };
  };
  gltf.extensionsUsed = ['EXT_mesh_features', 'EXT_structural_metadata'];
  gltf.extensions = {
    EXT_structural_metadata: {
      schema: { id: 'sample', classes: { element: { properties: { element_id: { type: 'STRING' }, name: { type: 'STRING' } } } } },
      propertyTables: [{
        class: 'element',
        count: elements.length,
        properties: { element_id: strings(elements.map((e) => e.id)), name: strings(elements.map((e) => e.name)) },
      }],
    },
  };
}
const bin = Buffer.concat(chunks);
gltf.buffers.push({ byteLength: bin.length });
let json = Buffer.from(JSON.stringify(gltf));
json = Buffer.concat([json, Buffer.alloc((4 - (json.length % 4)) % 4, 0x20)]);
const header = Buffer.alloc(12);
header.writeUInt32LE(0x46546c67, 0); // glTF
header.writeUInt32LE(2, 4);
header.writeUInt32LE(12 + 8 + json.length + 8 + bin.length, 8);
const chunk = (type, data) => {
  const h = Buffer.alloc(8);
  h.writeUInt32LE(data.length, 0);
  h.writeUInt32LE(type, 4);
  return Buffer.concat([h, data]);
};
writeFileSync(out, Buffer.concat([header, chunk(0x4e4f534a, json), chunk(0x004e4942, bin)]));
console.log(`${out}: ${elements.length} elements${segmented ? ' (segmented)' : ''}, ${(12 + 16 + json.length + bin.length) / 1024 | 0} KiB`);
