"""IFC → segmented GLB (spec 9.1, 9.2): every product with geometry becomes an element of the model, selectable by its
IFC GlobalId (property element_id), with its name and IFC class. Colours from the IFC styles, else by class.

Placement: if the IFC is georeferenced (IFC4 IfcMapConversion with an EPSG target CRS), the origin, height and
heading are computed; otherwise the API asks for them, as for any GLB. Far-away coordinates are moved to a local
origin (float32 vertices would lose centimetres) and the shift goes into the placement.
"""

from __future__ import annotations

import math
import multiprocessing

import numpy as np

import ifcopenshell
import ifcopenshell.geom

from glb import Primitive, write_glb
from messages import msg

SKIPPED = ("IfcOpeningElement", "IfcSpace", "IfcVirtualElement", "IfcAnnotation", "IfcGrid")
# Without an IFC style: a colour by class, so walls, slabs and equipment are told apart.
CLASS_COLOURS = {
    "IfcWall": (0.86, 0.85, 0.82, 1), "IfcWallStandardCase": (0.86, 0.85, 0.82, 1), "IfcSlab": (0.62, 0.62, 0.64, 1),
    "IfcRoof": (0.55, 0.3, 0.25, 1), "IfcColumn": (0.7, 0.7, 0.7, 1), "IfcBeam": (0.7, 0.7, 0.7, 1),
    "IfcWindow": (0.6, 0.8, 0.95, 0.5), "IfcDoor": (0.55, 0.42, 0.3, 1), "IfcStair": (0.7, 0.7, 0.68, 1),
    "IfcRailing": (0.4, 0.4, 0.4, 1), "IfcPipeSegment": (0.45, 0.47, 0.5, 1), "IfcPipeFitting": (0.45, 0.47, 0.5, 1),
    "IfcDuctSegment": (0.75, 0.75, 0.78, 1), "IfcPump": (0.15, 0.4, 0.8, 1), "IfcValve": (0.8, 0.2, 0.2, 1),
    "IfcTank": (0.3, 0.6, 0.5, 1), "IfcFlowTerminal": (0.6, 0.6, 0.3, 1),
}
DEFAULT_COLOUR = (0.75, 0.75, 0.75, 1)
FAR = 1000.0


def _settings() -> ifcopenshell.geom.settings:
    settings = ifcopenshell.geom.settings()
    for key in ("use-world-coords", "USE_WORLD_COORDS"):
        try:
            settings.set(key, True)
            break
        except Exception:  # option names differ between IfcOpenShell versions
            continue
    return settings


def _colour(material, ifc_class: str) -> tuple[float, float, float, float]:
    try:
        diffuse = material.diffuse
        rgb = (diffuse.r(), diffuse.g(), diffuse.b()) if hasattr(diffuse, "r") else tuple(diffuse)[:3]
        transparency = getattr(material, "transparency", 0.0) or 0.0
        if any(math.isnan(c) for c in rgb):
            raise ValueError
        alpha = 1.0 - (0.0 if math.isnan(transparency) else transparency)
        return (round(rgb[0], 3), round(rgb[1], 3), round(rgb[2], 3), round(max(0.2, alpha), 3))
    except Exception:
        return CLASS_COLOURS.get(ifc_class, DEFAULT_COLOUR)


def _georeference(model, offset: np.ndarray) -> dict | None:
    """Placement of the local point `offset` (IFC coordinates) from IfcMapConversion, or None."""
    try:
        conversions = model.by_type("IfcMapConversion")
    except RuntimeError:  # IFC2X3 has no such entity
        return None
    if not conversions:
        return None
    mc = conversions[0]
    crs = getattr(mc.TargetCRS, "Name", None) or ""
    if not crs.upper().startswith("EPSG:"):
        return None
    from pyproj import Transformer

    scale = mc.Scale or 1.0
    angle = math.atan2(mc.XAxisOrdinate or 0.0, mc.XAxisAbscissa or 1.0)  # x axis, counter-clockwise from grid east
    x, y = float(offset[0]), float(offset[1])
    east = mc.Eastings + scale * (x * math.cos(angle) - y * math.sin(angle))
    north = mc.Northings + scale * (x * math.sin(angle) + y * math.cos(angle))
    lon, lat = Transformer.from_crs(crs, "EPSG:4326", always_xy=True).transform(east, north)
    # IFC +y (= glTF −z, the model's forward) points `angle` counter-clockwise from grid north: heading = −angle.
    return {"lon": lon, "lat": lat, "absoluteHeightM": (mc.OrthogonalHeight or 0.0) + float(offset[2]),
            "headingDeg": (-math.degrees(angle)) % 360, "crs": crs}


def convert(path: str, out_path: str) -> dict:
    model = ifcopenshell.open(path)
    iterator = ifcopenshell.geom.iterator(_settings(), model, multiprocessing.cpu_count(), exclude=SKIPPED)
    if not iterator.initialize():
        raise ValueError(msg("ifc_no_geometry"))

    ids, names, classes = [], [], []
    groups: dict[tuple, dict[str, list]] = {}
    while True:
        shape = iterator.get()
        product = model.by_id(shape.id)
        geometry = shape.geometry
        verts = np.asarray(geometry.verts, dtype=np.float64).reshape(-1, 3)
        faces = np.asarray(geometry.faces, dtype=np.int64).reshape(-1, 3)
        if len(faces):
            element = len(ids)
            ids.append(product.GlobalId)
            names.append(product.Name or "")
            classes.append(product.is_a())
            normals = np.asarray(geometry.normals, dtype=np.float64).reshape(-1, 3) if len(geometry.normals) else None
            material_ids = np.asarray(geometry.material_ids, dtype=np.int64) if len(geometry.material_ids) else np.full(len(faces), -1)
            materials = list(geometry.materials)
            for material_id in np.unique(material_ids):
                colour = _colour(materials[material_id], product.is_a()) if 0 <= material_id < len(materials) else CLASS_COLOURS.get(product.is_a(), DEFAULT_COLOUR)
                # Unwelded per face group: each primitive keeps its own vertex range.
                face_group = faces[material_ids == material_id]
                used, remap = np.unique(face_group, return_inverse=True)
                group = groups.setdefault(colour, {"pos": [], "nor": [], "idx": [], "el": [], "count": 0})
                group["pos"].append(verts[used])
                group["nor"].append(normals[used] if normals is not None and len(normals) == len(verts) else None)
                group["idx"].append(remap.reshape(-1, 3) + group["count"])
                group["el"].append(np.full(len(used), element))
                group["count"] += len(used)
        if not iterator.next():
            break
    if not ids:
        raise ValueError(msg("ifc_no_elements"))

    # A local origin for far-away coordinates; the shift goes into the placement.
    all_points = np.concatenate([p for g in groups.values() for p in g["pos"]])
    lo, hi = all_points.min(axis=0), all_points.max(axis=0)
    centre = (lo + hi) / 2
    offset = np.array([centre[0], centre[1], 0.0]) if np.abs(centre[:2]).max() > FAR else np.zeros(3)

    primitives = []
    for colour, g in groups.items():
        pos = np.concatenate(g["pos"]) - offset
        has_normals = all(n is not None for n in g["nor"])
        nor = np.concatenate(g["nor"]) if has_normals else None
        # IFC is z-up, glTF y-up: (x, y, z) → (x, z, −y); the model's north (IFC +y) becomes glTF −z.
        to_gltf = lambda a: np.stack([a[:, 0], a[:, 2], -a[:, 1]], axis=1)
        primitives.append(Primitive(to_gltf(pos), to_gltf(nor) if nor is not None else None, np.concatenate(g["idx"]).reshape(-1), colour,
                                    np.concatenate(g["el"])))
    write_glb(out_path, primitives, {"element_id": ids, "name": names, "ifc_class": classes}, name="IFC")

    warnings = []
    placement = _georeference(model, offset)
    if placement is None and offset.any():
        warnings.append(msg("ifc_far_origin"))
    return {"kind": "gltf", "segmented": True, "elementIdProperty": "element_id", "elements": len(ids),
            "schema": model.schema, "placement": placement, "warnings": warnings,
            "size": [float(v) for v in (hi - lo)]}
