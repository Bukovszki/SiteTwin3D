"""Mesh formats (OBJ with its MTL and textures, FBX, DAE, STL, PLY, 3DS …) → GLB with the Assimp command line tool.

Not segmented: these formats have no stable element ids; highlighting works with boxes (spec 9.1). Units are taken
as metres and y as up (the common convention of these formats); the API asks for the placement as for any GLB.
"""

from __future__ import annotations

import subprocess

EXTENSIONS = (".obj", ".fbx", ".dae", ".stl", ".ply", ".3ds", ".gltf", ".x3d", ".off")


def convert(path: str, out_path: str) -> dict:
    result = subprocess.run(["assimp", "export", path, out_path, "-fglb2"], capture_output=True, text=True, timeout=3600)
    if result.returncode != 0:
        message = (result.stderr or result.stdout).strip().splitlines()
        raise ValueError(f"The model could not be read: {message[-1] if message else 'unknown error'}")
    warnings = [line.strip() for line in result.stdout.splitlines() if "warn" in line.lower()][:5]
    return {"kind": "gltf", "segmented": False, "placement": None, "warnings": warnings}
