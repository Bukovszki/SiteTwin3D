"""Mesh formats (OBJ with its MTL and textures, FBX, DAE, STL, PLY, 3DS …) → GLB with the Assimp command line tool.

Not segmented: these formats have no stable element ids; highlighting works with boxes (spec 9.1). Units are taken
as metres and y as up (the common convention of these formats); the API asks for the placement as for any GLB.
"""

from __future__ import annotations

import os
import subprocess

import textures

EXTENSIONS = (".obj", ".fbx", ".dae", ".stl", ".ply", ".3ds", ".gltf", ".x3d", ".off")


def convert(path: str, out_path: str, upload_root: str | None = None) -> dict:
    """`upload_root`: the folder of the whole upload (a ZIP's), where textures are looked for by name."""
    result = subprocess.run(["assimp", "export", path, out_path, "-fglb2"], capture_output=True, text=True, timeout=3600)
    if result.returncode != 0:
        message = (result.stderr or result.stdout).strip().splitlines()
        raise ValueError(f"The model could not be read: {message[-1] if message else 'unknown error'}")
    warnings = [line.strip() for line in result.stdout.splitlines() if "warn" in line.lower()][:5]
    # External textures into the GLB (the browser cannot follow file references into the object store).
    warnings += textures.embed(out_path, os.path.dirname(path), upload_root or os.path.dirname(path))
    size = _extent(out_path)
    if size > LARGE_MODEL_M:
        warnings.append(f"The model is {size:,.0f} m across: if it was exported in centimetres or millimetres, export it in metres.")
    return {"kind": "gltf", "segmented": False, "placement": None, "warnings": warnings}


# A plant model larger than this is most likely in centimetres or millimetres (read as metres).
LARGE_MODEL_M = 3000


def _extent(glb_path: str) -> float:
    """The largest extent of the vertex positions (their accessors' bounds; node transforms not applied)."""
    gltf, _ = textures.read_glb(glb_path)
    lows, highs = [], []
    for mesh in gltf.get("meshes", []):
        for primitive in mesh.get("primitives", []):
            accessor = gltf["accessors"][primitive["attributes"]["POSITION"]]
            if "min" in accessor and "max" in accessor:
                lows.append(accessor["min"])
                highs.append(accessor["max"])
    if not lows:
        return 0.0
    return max(max(h[i] for h in highs) - min(l[i] for l in lows) for i in range(3))
