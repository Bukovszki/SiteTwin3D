"""Textures into the GLB: Assimp writes a mesh's external textures as file references (uri), which the browser cannot
load from the object store. Each referenced image is put into the GLB's binary chunk instead: found next to the model
by its relative path, else anywhere in the upload by its file name (FBX often keeps the author's absolute paths);
formats a browser cannot show (TGA, BMP, TIFF, DDS …) become PNG. A missing image becomes a neutral grey pixel and a
warning, so the model still shows.
"""

from __future__ import annotations

import base64
import io
import json
import os
import struct
from urllib.parse import unquote

from PIL import Image

GLB_MAGIC = 0x46546C67
JSON_CHUNK = 0x4E4F534A
BIN_CHUNK = 0x004E4942
WEB_FORMATS = {".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg"}


def _grey_pixel() -> bytes:
    out = io.BytesIO()
    Image.new("RGB", (1, 1), (170, 170, 170)).save(out, "PNG")
    return out.getvalue()


GREY_PIXEL = _grey_pixel()


def read_glb(path: str) -> tuple[dict, bytes]:
    with open(path, "rb") as f:
        data = f.read()
    magic, _, _ = struct.unpack_from("<III", data, 0)
    if magic != GLB_MAGIC:
        raise ValueError("Not a GLB file.")
    offset, gltf, binary = 12, None, b""
    while offset < len(data):
        length, kind = struct.unpack_from("<II", data, offset)
        chunk = data[offset + 8 : offset + 8 + length]
        if kind == JSON_CHUNK:
            gltf = json.loads(chunk)
        elif kind == BIN_CHUNK:
            binary = chunk
        offset += 8 + length
    return gltf, binary


def write_glb(path: str, gltf: dict, binary: bytes) -> None:
    text = json.dumps(gltf, separators=(",", ":")).encode()
    text += b" " * ((4 - len(text) % 4) % 4)
    binary += b"\0" * ((4 - len(binary) % 4) % 4)
    total = 12 + 8 + len(text) + (8 + len(binary) if binary else 0)
    with open(path, "wb") as f:
        f.write(struct.pack("<III", GLB_MAGIC, 2, total))
        f.write(struct.pack("<II", len(text), JSON_CHUNK) + text)
        if binary:
            f.write(struct.pack("<II", len(binary), BIN_CHUNK) + binary)


def _index(root: str) -> dict[str, str]:
    """Every file under the upload by its lower-case name (the first one wins)."""
    found: dict[str, str] = {}
    for directory, _, files in os.walk(root):
        for name in files:
            found.setdefault(name.lower(), os.path.join(directory, name))
    return found


def _resolve(uri: str, model_dir: str, index: dict[str, str]) -> str | None:
    reference = unquote(uri).replace("\\", "/")
    candidate = os.path.normpath(os.path.join(model_dir, reference))
    if not os.path.isabs(reference) and os.path.isfile(candidate):
        return candidate
    return index.get(os.path.basename(reference).lower())


def _web_image(path: str) -> tuple[bytes, str]:
    ext = os.path.splitext(path)[1].lower()
    with open(path, "rb") as f:
        data = f.read()
    if ext in WEB_FORMATS:
        return data, WEB_FORMATS[ext]
    with Image.open(io.BytesIO(data)) as image:
        out = io.BytesIO()
        image.convert("RGBA" if image.mode in ("RGBA", "LA", "P") else "RGB").save(out, "PNG")
        return out.getvalue(), "image/png"


def embed(glb_path: str, model_dir: str, upload_root: str) -> list[str]:
    """Embeds the GLB's external images; returns warnings (missing or unreadable images)."""
    gltf, binary = read_glb(glb_path)
    images = gltf.get("images") or []
    if not any("uri" in image for image in images):
        return []
    index = _index(upload_root)
    data = bytearray(binary)
    views = gltf.setdefault("bufferViews", [])
    missing: list[str] = []
    for image in images:
        uri = image.get("uri")
        if uri is None:
            continue
        if uri.startswith("data:"):
            header, _, payload = uri.partition(",")
            content, mime = base64.b64decode(payload), header[5:].split(";")[0] or "image/png"
        else:
            path = _resolve(uri, model_dir, index)
            try:
                if path is None:
                    raise FileNotFoundError(uri)
                content, mime = _web_image(path)
            except Exception:  # noqa: BLE001 – a missing or broken texture must not stop the model
                missing.append(os.path.basename(unquote(uri).replace("\\", "/")))
                content, mime = GREY_PIXEL, "image/png"
        data += b"\0" * ((4 - len(data) % 4) % 4)
        views.append({"buffer": 0, "byteOffset": len(data), "byteLength": len(content)})
        data += content
        image.pop("uri", None)
        image["bufferView"] = len(views) - 1
        image["mimeType"] = mime
    buffers = gltf.setdefault("buffers", [{}])
    buffers[0].pop("uri", None)
    buffers[0]["byteLength"] = len(data)
    write_glb(glb_path, gltf, bytes(data))
    if not missing:
        return []
    shown = ", ".join(sorted(set(missing))[:5])
    return [f"Missing texture(s), shown grey: {shown}" + (" …" if len(set(missing)) > 5 else "")]
