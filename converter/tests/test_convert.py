"""Converter tests on generated samples (run in the image: python -m tests.test_convert).

IFC: a wall and a pump, georeferenced in EOV → segmented GLB with GlobalIds, placement computed.
OBJ with MTL → GLB. LAS (100 000 coloured points) → octree of .pnts tiles, local and in EOV → ECEF.
"""

from __future__ import annotations

import json
import math
import os
import struct
import tempfile

import numpy as np

import ifc
import mesh
import pointcloud


def read_glb(path: str) -> tuple[dict, bytes]:
    data = open(path, "rb").read()
    magic, _, _ = struct.unpack_from("<III", data)
    assert magic == 0x46546C67, "not a GLB"
    length, _ = struct.unpack_from("<II", data, 12)
    gltf = json.loads(data[20:20 + length])
    blength, _ = struct.unpack_from("<II", data, 20 + length)
    return gltf, data[28 + length:28 + length + blength]


def property_values(gltf: dict, binary: bytes, name: str) -> list[str]:
    table = gltf["extensions"]["EXT_structural_metadata"]["propertyTables"][0]
    p = table["properties"][name]
    view = lambda i: binary[gltf["bufferViews"][i].get("byteOffset", 0):][:gltf["bufferViews"][i]["byteLength"]]
    values, offsets = view(p["values"]), np.frombuffer(view(p["stringOffsets"]), dtype=np.uint32)
    return [values[offsets[i]:offsets[i + 1]].decode() for i in range(table["count"])]


def sample_ifc(path: str) -> dict:
    import ifcopenshell.api as api
    import ifcopenshell.api.aggregate  # noqa: F401 – the API modules are loaded one by one
    import ifcopenshell.api.context  # noqa: F401
    import ifcopenshell.api.geometry  # noqa: F401
    import ifcopenshell.api.georeference  # noqa: F401
    import ifcopenshell.api.project  # noqa: F401
    import ifcopenshell.api.root  # noqa: F401
    import ifcopenshell.api.spatial  # noqa: F401
    import ifcopenshell.api.unit  # noqa: F401

    model = api.project.create_file(version="IFC4")
    project = api.root.create_entity(model, ifc_class="IfcProject", name="Minta")
    api.unit.assign_unit(model)
    context = api.context.add_context(model, context_type="Model")
    body = api.context.add_context(model, context_type="Model", context_identifier="Body", target_view="MODEL_VIEW", parent=context)
    site = api.root.create_entity(model, ifc_class="IfcSite", name="Telep")
    building = api.root.create_entity(model, ifc_class="IfcBuilding", name="Gépház")
    storey = api.root.create_entity(model, ifc_class="IfcBuildingStorey", name="Földszint")
    api.aggregate.assign_object(model, relating_object=project, products=[site])
    api.aggregate.assign_object(model, relating_object=site, products=[building])
    api.aggregate.assign_object(model, relating_object=building, products=[storey])
    ids = {}
    for ifc_class, name, size, at in (("IfcWall", "Fal 1", (5, 0.3, 3), (0, 0)), ("IfcPump", "Befolyó szivattyú 1", (1.2, 0.8, 1.0), (2, 2))):
        product = api.root.create_entity(model, ifc_class=ifc_class, name=name)
        rep = api.geometry.add_wall_representation(model, context=body, length=size[0], height=size[2], thickness=size[1])
        api.geometry.assign_representation(model, product=product, representation=rep)
        matrix = np.eye(4)
        matrix[:2, 3] = at
        api.geometry.edit_object_placement(model, product=product, matrix=matrix)
        api.spatial.assign_container(model, relating_structure=storey, products=[product])
        ids[name] = product.GlobalId
    api.georeference.add_georeferencing(model)
    angle = math.radians(30)
    api.georeference.edit_georeferencing(model, projected_crs={"Name": "EPSG:23700"}, coordinate_operation={
        "Eastings": 650000.0, "Northings": 240000.0, "OrthogonalHeight": 105.0,
        "XAxisAbscissa": math.cos(angle), "XAxisOrdinate": math.sin(angle), "Scale": 1.0})
    model.write(path)
    return ids


def test_ifc(work: str) -> None:
    ids = sample_ifc(os.path.join(work, "minta.ifc"))
    result = ifc.convert(os.path.join(work, "minta.ifc"), os.path.join(work, "ifc.glb"))
    gltf, binary = read_glb(os.path.join(work, "ifc.glb"))
    assert result["segmented"] and result["elements"] == 2, result
    assert sorted(property_values(gltf, binary, "element_id")) == sorted(ids.values())
    assert set(property_values(gltf, binary, "ifc_class")) == {"IfcWall", "IfcPump"}
    assert "Befolyó szivattyú 1" in property_values(gltf, binary, "name")
    placement = result["placement"]
    assert placement and placement["crs"] == "EPSG:23700"
    assert 18 < placement["lon"] < 23 and 45 < placement["lat"] < 49, placement
    assert abs(placement["headingDeg"] - 330) < 1e-6 and placement["absoluteHeightM"] == 105.0, placement
    print("ifc ok:", result["elements"], "elements,", placement)


def test_obj(work: str) -> None:
    obj = os.path.join(work, "cube.obj")
    with open(os.path.join(work, "cube.mtl"), "w") as m:
        m.write("newmtl blue\nKd 0.1 0.3 0.9\n")
    with open(obj, "w") as f:
        f.write("mtllib cube.mtl\nusemtl blue\n" + "".join(f"v {x} {y} {z}\n" for x in (0, 1) for y in (0, 1) for z in (0, 1)))
        f.write("f 1 3 4 2\nf 5 6 8 7\nf 1 2 6 5\nf 3 7 8 4\nf 1 5 7 3\nf 2 4 8 6\n")
    result = mesh.convert(obj, os.path.join(work, "cube.glb"))
    gltf, _ = read_glb(os.path.join(work, "cube.glb"))
    colours = [m["pbrMetallicRoughness"].get("baseColorFactor") for m in gltf["materials"]]
    assert result["kind"] == "gltf" and gltf["meshes"], result
    assert any(c and abs(c[2] - 0.9) < 1e-3 for c in colours), colours  # the MTL colour is kept
    assert not gltf.get("extensionsRequired"), gltf.get("extensionsRequired")
    print("obj ok: MTL colour kept")


def test_pointcloud(work: str) -> None:
    import laspy

    rng = np.random.default_rng(1)
    n = 100_000
    header = laspy.LasHeader(point_format=2, version="1.2")
    header.scales = [0.001, 0.001, 0.001]
    header.offsets = [650000, 240000, 100]
    las = laspy.LasData(header)
    las.x = 650000 + rng.random(n) * 40
    las.y = 240000 + rng.random(n) * 24
    las.z = 100 + rng.random(n) * 8
    las.red = las.green = las.blue = (rng.random(n) * 65535).astype(np.uint16)
    path = os.path.join(work, "scan.las")
    las.write(path)
    for srs in (None, "EPSG:23700"):
        out = os.path.join(work, f"tiles-{srs is not None}")
        result = pointcloud.convert(path, out, srs)
        tileset = json.load(open(os.path.join(out, "tileset.json")))
        tiles = [tileset["root"]]
        count = 0
        while tiles:
            tile = tiles.pop()
            data = open(os.path.join(out, tile["content"]["uri"]), "rb").read()
            magic, version, length, json_len, bin_len, _, _ = struct.unpack_from("<4sIIIIII", data)
            assert magic == b"pnts" and length == len(data) and (28 + json_len) % 8 == 0
            count += json.loads(data[28:28 + json_len])["POINTS_LENGTH"]
            tiles.extend(tile.get("children", []))
        assert count == n and result["points"] == n, (count, result)
        centre = tileset["root"]["boundingVolume"]["box"][:3]
        if srs:
            assert 6.3e6 < math.dist(centre, (0, 0, 0)) < 6.4e6, centre  # on the earth's surface (ECEF)
        print(f"pointcloud ok ({srs or 'local'}): {result['tiles']} tiles, {count} points, root centre {[round(c) for c in centre]}")


def test_e57(work: str) -> None:
    import pye57

    rng = np.random.default_rng(2)
    n = 20_000
    path = os.path.join(work, "scan.e57")
    writer = pye57.E57(path, mode="w")
    writer.write_scan_raw({"cartesianX": rng.random(n) * 10, "cartesianY": rng.random(n) * 6, "cartesianZ": rng.random(n) * 3,
                           "colorRed": np.full(n, 200, dtype=np.uint8), "colorGreen": np.full(n, 50, dtype=np.uint8),
                           "colorBlue": np.full(n, 50, dtype=np.uint8)})
    writer.close()
    result = pointcloud.convert(path, os.path.join(work, "e57"), None)
    assert result["points"] == n and not result["georeferenced"], result
    print(f"e57 ok: {result['points']} points in {result['tiles']} tile(s)")


if __name__ == "__main__":
    with tempfile.TemporaryDirectory() as work:
        test_ifc(work)
        test_obj(work)
        test_pointcloud(work)
        test_e57(work)
    print("all converter tests passed")
