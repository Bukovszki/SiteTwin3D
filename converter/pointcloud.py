"""Point clouds (LAS, LAZ, E57) → 3D Tiles: an octree of .pnts tiles, each refining its parent (ADD), so a large scan
streams by level of detail (NFR-2).

With the source CRS (e.g. EPSG:23700, EOV) the points are taken to ECEF (EPSG:4978) and the tileset is georeferenced;
without it the coordinates stay local (metres, z up) and the API places the tileset like a GLB. Very large clouds are
thinned uniformly to MAX_POINTS (env CONVERTER_MAX_POINTS) – the limit is reported.
"""

from __future__ import annotations

import json
import os
import struct

import numpy as np
from messages import msg

MAX_POINTS = int(os.environ.get("CONVERTER_MAX_POINTS", "30000000"))
POINTS_PER_TILE = 60000
MAX_DEPTH = 14
CHUNK = 2_000_000
EXTENSIONS = (".las", ".laz", ".e57")


def _read_las(path: str, keep: float, rng: np.random.Generator):
    import laspy

    xyz, rgb = [], []
    with laspy.open(path) as reader:
        has_rgb = "red" in reader.header.point_format.dimension_names
        for chunk in reader.chunk_iterator(CHUNK):
            mask = rng.random(len(chunk)) < keep if keep < 1 else slice(None)
            xyz.append(np.stack([np.asarray(chunk.x)[mask], np.asarray(chunk.y)[mask], np.asarray(chunk.z)[mask]], axis=1))
            if has_rgb:
                colours = np.stack([np.asarray(chunk.red)[mask], np.asarray(chunk.green)[mask], np.asarray(chunk.blue)[mask]], axis=1)
                # 16-bit colours (the LAS norm) or 8-bit ones written into 16-bit fields.
                rgb.append((colours >> 8 if colours.max(initial=0) > 255 else colours).astype(np.uint8))
    return np.concatenate(xyz), (np.concatenate(rgb) if rgb else None)


def _count_las(path: str) -> int:
    import laspy

    with laspy.open(path) as reader:
        return reader.header.point_count


def _read_e57(path: str, keep: float, rng: np.random.Generator):
    import pye57

    e57 = pye57.E57(path)
    xyz, rgb = [], []
    for scan in range(e57.scan_count):
        data = e57.read_scan(scan, colors=True, ignore_missing_fields=True)  # scan pose applied
        points = np.stack([data["cartesianX"], data["cartesianY"], data["cartesianZ"]], axis=1)
        mask = rng.random(len(points)) < keep if keep < 1 else slice(None)
        xyz.append(points[mask])
        if "colorRed" in data:
            rgb.append(np.stack([data["colorRed"], data["colorGreen"], data["colorBlue"]], axis=1)[mask].astype(np.uint8))
    return np.concatenate(xyz), (np.concatenate(rgb) if len(rgb) == len(xyz) and rgb else None)


def _count_e57(path: str) -> int:
    import pye57

    e57 = pye57.E57(path)
    return sum(e57.get_header(i).point_count for i in range(e57.scan_count))


def _write_pnts(path: str, xyz: np.ndarray, rgb: np.ndarray | None) -> None:
    """3D Tiles .pnts: positions relative to RTC_CENTER as float32 (centimetres stay exact), RGB as uint8."""
    centre = (xyz.min(axis=0) + xyz.max(axis=0)) / 2
    positions = (xyz - centre).astype(np.float32).tobytes()
    table = {"POINTS_LENGTH": int(len(xyz)), "RTC_CENTER": [float(c) for c in centre], "POSITION": {"byteOffset": 0}}
    binary = positions
    if rgb is not None:
        table["RGB"] = {"byteOffset": len(binary)}
        binary += rgb.astype(np.uint8).tobytes()
    text = json.dumps(table, separators=(",", ":")).encode()
    text += b" " * ((8 - (28 + len(text)) % 8) % 8)
    binary += b"\0" * ((8 - len(binary) % 8) % 8)
    with open(path, "wb") as out:
        out.write(struct.pack("<4sIIIIII", b"pnts", 1, 28 + len(text) + len(binary), len(text), len(binary), 0, 0))
        out.write(text + binary)


def _tiles(xyz: np.ndarray, rgb: np.ndarray | None, out_dir: str, rng: np.random.Generator) -> dict:
    """Octree with refine ADD: a node keeps an even sample of its points, the rest goes to its eight children."""
    os.makedirs(os.path.join(out_dir, "tiles"), exist_ok=True)
    counter = [0]

    def node(index: np.ndarray, lo: np.ndarray, hi: np.ndarray, depth: int) -> dict:
        half = (hi - lo) / 2
        centre = lo + half
        box = [*map(float, centre), float(half[0]), 0, 0, 0, float(half[1]), 0, 0, 0, float(half[2])]
        leaf = len(index) <= POINTS_PER_TILE or depth >= MAX_DEPTH
        if leaf:
            here, rest = index, np.empty(0, dtype=index.dtype)
        else:
            chosen = rng.permutation(len(index))
            here, rest = index[chosen[:POINTS_PER_TILE]], index[chosen[POINTS_PER_TILE:]]
        name = f"tiles/{counter[0]}.pnts"
        counter[0] += 1
        _write_pnts(os.path.join(out_dir, name), xyz[here], rgb[here] if rgb is not None else None)
        # The sample spacing of this tile: its edge over the cube root of its point count.
        spacing = float(np.max(hi - lo)) / max(1.0, len(here) ** (1 / 3))
        tile = {"boundingVolume": {"box": box}, "geometricError": 0 if leaf else spacing * 2, "refine": "ADD", "content": {"uri": name}}
        if not leaf:
            octant = ((xyz[rest] >= centre) * np.array([1, 2, 4])).sum(axis=1)
            children = []
            for o in range(8):
                part = rest[octant == o]
                if len(part) == 0:
                    continue
                sub_lo = np.where(np.array([o & 1, o & 2, o & 4]) > 0, centre, lo)
                children.append(node(part, sub_lo, sub_lo + half, depth + 1))
            tile["children"] = children
        return tile

    lo, hi = xyz.min(axis=0), xyz.max(axis=0)
    size = np.maximum(hi - lo, 0.01)
    root = node(np.arange(len(xyz)), lo, lo + size, 0)
    tileset = {"asset": {"version": "1.0", "generator": "converter"}, "geometricError": root["geometricError"] * 2 or 1, "root": root}
    with open(os.path.join(out_dir, "tileset.json"), "w") as out:
        json.dump(tileset, out, separators=(",", ":"))
    return {"tiles": counter[0]}


def convert(path: str, out_dir: str, srs: str | None) -> dict:
    e57 = path.lower().endswith(".e57")
    total = _count_e57(path) if e57 else _count_las(path)
    if total == 0:
        raise ValueError(msg("points_empty"))
    keep = min(1.0, MAX_POINTS / total)
    rng = np.random.default_rng(42)
    xyz, rgb = (_read_e57 if e57 else _read_las)(path, keep, rng)

    warnings = []
    if keep < 1:
        warnings.append(msg("points_thinned", total, len(xyz)))
    georeferenced = bool(srs)
    if georeferenced:
        from pyproj import Transformer

        try:
            transformer = Transformer.from_crs(srs, "EPSG:4978", always_xy=True)
        except Exception as e:
            raise ValueError(msg("unknown_srs", srs)) from e
        x, y, z = transformer.transform(xyz[:, 0], xyz[:, 1], xyz[:, 2])
        xyz = np.stack([x, y, z], axis=1)
        if not np.isfinite(xyz).all():
            raise ValueError(msg("points_outside_srs", srs))
    tiles = _tiles(xyz, rgb, out_dir, rng)
    return {"kind": "3dtiles", "segmented": False, "points": int(len(xyz)), "sourcePoints": int(total), "tiles": tiles["tiles"],
            "georeferenced": georeferenced, "placement": None, "warnings": warnings}
