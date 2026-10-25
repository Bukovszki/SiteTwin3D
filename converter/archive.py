"""ZIP uploads: a model with its files (e.g. an FBX with textures in folders) as one archive.

Unpacked with its folder structure (textures are referenced by relative paths), safely: no member may leave the
target folder, and the total size and the number of files are limited (a "ZIP bomb" stops early). The model is the
one main file in it, by the same rules as a plain upload (the API's ModelFormats.Detect): tileset.json, or exactly
one GLB / IFC / mesh / point cloud.
"""

from __future__ import annotations

import os
import zipfile
from messages import msg

MESH = (".obj", ".fbx", ".dae", ".stl", ".ply", ".3ds", ".gltf", ".x3d", ".off")
POINT_CLOUDS = (".las", ".laz", ".e57")
MAX_BYTES = int(os.environ.get("CONVERTER_MAX_UNPACKED_MB", "20480")) * 1024 * 1024
MAX_FILES = 20_000


def extract(path: str, target: str) -> None:
    """Unpacks the ZIP into `target`; raises ValueError for unsafe or oversized archives."""
    try:
        archive = zipfile.ZipFile(path)
    except zipfile.BadZipFile as e:
        raise ValueError(msg("zip_invalid")) from e
    with archive:
        members = [m for m in archive.infolist() if not m.is_dir()]
        if len(members) > MAX_FILES:
            raise ValueError(msg("zip_too_many_files", len(members), MAX_FILES))
        root = os.path.realpath(target)
        written = 0
        for member in members:
            name = member.filename.replace("\\", "/")
            parts = [p for p in name.split("/") if p not in ("", ".")]
            if not parts or ".." in parts or name.startswith("/") or ":" in parts[0]:
                raise ValueError(msg("zip_unsafe_path", member.filename))
            # macOS metadata, not part of the model.
            if parts[0] == "__MACOSX" or parts[-1].startswith("._"):
                continue
            destination = os.path.realpath(os.path.join(root, *parts))
            if not destination.startswith(root + os.sep):
                raise ValueError(msg("zip_unsafe_path", member.filename))
            os.makedirs(os.path.dirname(destination), exist_ok=True)
            with archive.open(member) as source, open(destination, "wb") as out:
                while chunk := source.read(1024 * 1024):
                    written += len(chunk)
                    if written > MAX_BYTES:
                        raise ValueError(msg("zip_too_large", MAX_BYTES // (1024 * 1024)))
                    out.write(chunk)


def find_model(folder: str) -> tuple[str, str]:
    """The model in an unpacked folder: (format, path). Format: 3dtiles | glb | ifc | mesh | pointcloud."""
    tilesets, mains = [], []
    for directory, _, files in os.walk(folder):
        for name in files:
            path = os.path.join(directory, name)
            ext = os.path.splitext(name)[1].lower()
            if name == "tileset.json":
                tilesets.append(path)
            elif ext in (".glb", ".ifc") or ext in MESH or ext in POINT_CLOUDS:
                mains.append(path)
    if tilesets:
        # The outermost tileset.json is the package; nested ones are its external tilesets.
        return "3dtiles", min(tilesets, key=lambda p: p.count(os.sep))
    if len(mains) != 1:
        names = ", ".join(sorted(os.path.relpath(p, folder) for p in mains)[:8])
        raise ValueError(msg("zip_model_count", names) if names else msg("zip_no_model"))
    main = mains[0]
    ext = os.path.splitext(main)[1].lower()
    return ("glb" if ext == ".glb" else "ifc" if ext == ".ifc" else "pointcloud" if ext in POINT_CLOUDS else "mesh"), main
