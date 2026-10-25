"""Model converter (spec 9.2): an internal HTTP service of the compose stack, called only by the API.

POST /convert {"format": "ifc" | "mesh" | "pointcloud" | "zip", "main": key, "keys": [keys of the upload],
               "outputPrefix": prefix, "srs": "EPSG:23700" | null}
  → 200 {"kind": "gltf" | "3dtiles", "key": output key, …result of the converter;
         a ZIP also "format" (the model found in it) and "main" (its path in the ZIP)}
  → 422 {"error": message} when the file cannot be converted.
GET /health → 200.

Files come from and go to the object store directly (S3); one conversion at a time (memory), the API queues them.
"""

from __future__ import annotations

import json
import logging
import mimetypes
import os
import shutil
import tempfile
import traceback
from http.server import BaseHTTPRequestHandler, HTTPServer

import boto3
from botocore.config import Config

import archive
import ifc
import mesh
import pointcloud

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("converter")

BUCKET = os.environ.get("S3_BUCKET", "objects")
s3 = boto3.client(
    "s3",
    endpoint_url=os.environ.get("S3_ENDPOINT", "http://storage:8333"),
    aws_access_key_id=os.environ.get("S3_ACCESS_KEY"),
    aws_secret_access_key=os.environ.get("S3_SECRET_KEY"),
    region_name=os.environ.get("S3_REGION", "us-east-1"),
    config=Config(s3={"addressing_style": "path"}, request_checksum_calculation="when_required",
                  response_checksum_validation="when_required"),
)
CONTENT_TYPES = {".glb": "model/gltf-binary", ".json": "application/json", ".pnts": "application/octet-stream"}


def run(job: dict) -> dict:
    main, prefix, kind = job["main"], job["outputPrefix"], job["format"]
    if not prefix.endswith("/") or ".." in prefix or not main.startswith(prefix.rsplit("/", 2)[0]):
        raise ValueError("Invalid job.")
    work = tempfile.mkdtemp(prefix="convert-")
    try:
        source_root = os.path.join(work, "src")
        # The upload's folder structure is kept: an OBJ finds its MTL and textures by relative paths.
        base = main.rsplit("/", 1)[0] + "/"
        for key in job.get("keys") or [main]:
            if not key.startswith(base):
                continue
            target = os.path.join(source_root, key[len(base):])
            os.makedirs(os.path.dirname(target), exist_ok=True)
            s3.download_file(BUCKET, key, target)
        source = os.path.join(source_root, main[len(base):])
        out = os.path.join(work, "out")
        os.makedirs(out)
        if kind == "zip":
            # Unpacked with its folders (textures by relative paths); the model in it decides what follows.
            unpacked = os.path.join(work, "unpacked")
            os.makedirs(unpacked)
            archive.extract(source, unpacked)
            os.remove(source)
            inner, model = archive.find_model(unpacked)
            result = convert_one(inner, model, out, prefix, job.get("srs"), unpacked)
            result["format"] = inner
            result["main"] = os.path.relpath(model, unpacked).replace(os.sep, "/")
        else:
            result = convert_one(kind, source, out, prefix, job.get("srs"), source_root)
        for folder, _, files in os.walk(out):
            for name in files:
                path = os.path.join(folder, name)
                key = prefix + os.path.relpath(path, out).replace(os.sep, "/")
                content_type = CONTENT_TYPES.get(os.path.splitext(name)[1]) or mimetypes.guess_type(name)[0] or "application/octet-stream"
                s3.upload_file(path, BUCKET, key, ExtraArgs={"ContentType": content_type})
        return result
    finally:
        shutil.rmtree(work, ignore_errors=True)


def convert_one(kind: str, source: str, out: str, prefix: str, srs: str | None, upload_root: str) -> dict:
    """Converts one model into `out`; the result names its output key under `prefix`."""
    if kind == "ifc":
        result = ifc.convert(source, os.path.join(out, "model.glb"))
        result["key"] = prefix + "model.glb"
    elif kind == "mesh":
        result = mesh.convert(source, os.path.join(out, "model.glb"), upload_root)
        result["key"] = prefix + "model.glb"
    elif kind == "pointcloud":
        result = pointcloud.convert(source, out, srs)
        result["key"] = prefix + "tileset.json"
    elif kind == "glb":
        # A GLB in a ZIP: shown as it is (placed like any GLB).
        shutil.copyfile(source, os.path.join(out, "model.glb"))
        result = {"kind": "gltf", "segmented": False, "placement": None, "warnings": [], "key": prefix + "model.glb"}
    elif kind == "3dtiles":
        # A ready 3D Tiles package in a ZIP: its folder as it is (georeferenced by its root transform).
        shutil.copytree(os.path.dirname(source), out, dirs_exist_ok=True)
        result = {"kind": "3dtiles", "segmented": False, "georeferenced": True, "placement": None, "warnings": [],
                  "key": prefix + "tileset.json"}
    else:
        raise ValueError(f"Unknown format: {kind}")
    return result


class Handler(BaseHTTPRequestHandler):
    def _send(self, status: int, body: dict) -> None:
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _body(self) -> bytes:
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            data = b""
            while True:
                size = int(self.rfile.readline().strip(), 16)
                if size == 0:
                    self.rfile.readline()
                    return data
                data += self.rfile.read(size)
                self.rfile.readline()
        return self.rfile.read(int(self.headers.get("Content-Length", "0")))

    def do_GET(self) -> None:  # noqa: N802
        self._send(200 if self.path == "/health" else 404, {"status": "ok"} if self.path == "/health" else {"error": "not found"})

    def do_POST(self) -> None:  # noqa: N802
        if self.path != "/convert":
            self._send(404, {"error": "not found"})
            return
        try:
            job = json.loads(self._body())
            log.info("convert %s %s", job.get("format"), job.get("main"))
            result = run(job)
            log.info("done %s → %s", job.get("main"), result.get("key"))
            self._send(200, result)
        except ValueError as e:
            log.warning("cannot convert: %s", e)
            self._send(422, {"error": str(e)})
        except Exception as e:  # noqa: BLE001 – reported to the API, logged here
            log.error("conversion failed: %s\n%s", e, traceback.format_exc())
            self._send(422, {"error": f"Conversion failed: {e}"})

    def log_message(self, format: str, *args) -> None:  # noqa: A002 – requests are logged in do_POST
        pass


if __name__ == "__main__":
    port = int(os.environ.get("PORT", "8090"))
    log.info("converter listening on %d", port)
    HTTPServer(("0.0.0.0", port), Handler).serve_forever()
