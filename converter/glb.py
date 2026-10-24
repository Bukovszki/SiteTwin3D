"""GLB writer for the converted models (glTF 2.0, y-up, metres).

A primitive is one colour; with element indices per vertex the GLB is segmented: EXT_mesh_features gives every
vertex its element, EXT_structural_metadata holds one property table (e.g. element_id = IFC GlobalId, name, class),
the same layout as deploy/dev/sample-model.mjs and what the API's element check reads (spec 9.1).
"""

from __future__ import annotations

import json
import struct
from dataclasses import dataclass, field

import numpy as np

ARRAY_BUFFER = 34962
ELEMENT_ARRAY_BUFFER = 34963


@dataclass
class Primitive:
    positions: np.ndarray  # (n, 3) float32, glTF axes
    normals: np.ndarray | None  # (n, 3) float32
    indices: np.ndarray  # (m,) uint32
    color: tuple[float, float, float, float]
    elements: np.ndarray | None = None  # (n,) element index per vertex


@dataclass
class _Builder:
    gltf: dict = field(default_factory=dict)
    chunks: list[bytes] = field(default_factory=list)
    offset: int = 0

    def view(self, data: bytes, target: int | None = None) -> int:
        entry = {"buffer": 0, "byteOffset": self.offset, "byteLength": len(data)}
        if target:
            entry["target"] = target
        self.gltf["bufferViews"].append(entry)
        self.chunks.append(data)
        self.offset += len(data)
        pad = (4 - self.offset % 4) % 4
        if pad:
            self.chunks.append(b"\0" * pad)
            self.offset += pad
        return len(self.gltf["bufferViews"]) - 1

    def accessor(self, data: np.ndarray, component: int, kind: str, target: int, bounds: bool = False) -> int:
        entry = {"bufferView": self.view(np.ascontiguousarray(data).tobytes(), target), "componentType": component,
                 "count": int(data.shape[0]), "type": kind}
        if bounds:
            entry["min"] = [float(v) for v in data.min(axis=0)]
            entry["max"] = [float(v) for v in data.max(axis=0)]
        self.gltf["accessors"].append(entry)
        return len(self.gltf["accessors"]) - 1


def write_glb(path: str, primitives: list[Primitive], properties: dict[str, list[str]] | None = None, name: str = "model") -> None:
    """Writes the primitives into one mesh; with `properties` (columns of equal length) the model is segmented."""
    b = _Builder()
    b.gltf = {"asset": {"version": "2.0", "generator": "converter"}, "scene": 0, "scenes": [{"nodes": [0]}],
              "nodes": [{"mesh": 0, "name": name}], "meshes": [{"primitives": []}], "materials": [], "accessors": [],
              "bufferViews": [], "buffers": []}
    count = len(next(iter(properties.values()))) if properties else 0
    for p in primitives:
        if p.positions.shape[0] == 0:
            continue
        attributes = {"POSITION": b.accessor(p.positions.astype(np.float32), 5126, "VEC3", ARRAY_BUFFER, bounds=True)}
        if p.normals is not None:
            attributes["NORMAL"] = b.accessor(p.normals.astype(np.float32), 5126, "VEC3", ARRAY_BUFFER)
        indices = b.accessor(p.indices.astype(np.uint32), 5125, "SCALAR", ELEMENT_ARRAY_BUFFER)
        b.gltf["materials"].append({"pbrMetallicRoughness": {"baseColorFactor": list(p.color), "metallicFactor": 0, "roughnessFactor": 0.9},
                                    **({"alphaMode": "BLEND"} if p.color[3] < 0.99 else {}), "doubleSided": True})
        primitive = {"attributes": attributes, "indices": indices, "material": len(b.gltf["materials"]) - 1}
        if properties and p.elements is not None:
            attributes["_FEATURE_ID_0"] = b.accessor(p.elements.astype(np.float32), 5126, "SCALAR", ARRAY_BUFFER)
            primitive["extensions"] = {"EXT_mesh_features": {"featureIds": [{"featureCount": count, "attribute": 0, "propertyTable": 0}]}}
        b.gltf["meshes"][0]["primitives"].append(primitive)

    if properties:
        def strings(values: list[str]) -> dict:
            encoded = [v.encode("utf-8") for v in values]
            offsets = np.zeros(len(encoded) + 1, dtype=np.uint32)
            offsets[1:] = np.cumsum([len(e) for e in encoded])
            return {"values": b.view(b"".join(encoded)), "stringOffsets": b.view(offsets.tobytes())}

        b.gltf["extensionsUsed"] = ["EXT_mesh_features", "EXT_structural_metadata"]
        b.gltf["extensions"] = {"EXT_structural_metadata": {
            "schema": {"id": "converted", "classes": {"element": {"properties": {k: {"type": "STRING"} for k in properties}}}},
            "propertyTables": [{"class": "element", "count": count, "properties": {k: strings(v) for k, v in properties.items()}}],
        }}

    binary = b"".join(b.chunks)
    b.gltf["buffers"].append({"byteLength": len(binary)})
    text = json.dumps(b.gltf, separators=(",", ":")).encode()
    text += b" " * ((4 - len(text) % 4) % 4)
    with open(path, "wb") as out:
        out.write(struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(text) + 8 + len(binary)))
        out.write(struct.pack("<II", len(text), 0x4E4F534A) + text)
        out.write(struct.pack("<II", len(binary), 0x004E4942) + binary)
