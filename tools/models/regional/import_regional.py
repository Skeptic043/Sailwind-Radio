"""Imports one regional Radio device from a Pocket Builder GLB into Blender and exports
its runtime model fragment.

Blender 5.2 LTS, metres. Run:
  blender --background --factory-startup --disable-autoexec --python-exit-code 1 \
    --python import_regional.py -- <device.glb> <kind> <scale> <out dir>

The GLB comes from the Pocket Builder exporter, with a `<device>.parts.json` sidecar beside
it that holds each part's ID, authored sRGB material and world transform (cm, Y up, +Z
front). Unity coordinates are X right, Y up, Z back. Blender uses X right, Y back, Z up,
so the export swaps Y/Z and reverses winding exactly as build_devices.py does.

Kinds: family * 4 + role. Families 1 Al'Ankh, 2 Aestrin, 3 Emerald; roles 0 radio,
1 small speaker, 2 speaker, 3 woofer. Kinds 0-3 remain the walnut family from
build_devices.py.
"""
import bpy
import bmesh
import hashlib
import json
import re
import sys
from pathlib import Path
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

if bpy.app.version < (5, 2, 0):
    raise RuntimeError(f"import_regional.py was validated on Blender 5.2; found {bpy.app.version_string}")
args = sys.argv[sys.argv.index("--") + 1:]
GLB, KIND, SCALE, OUT = Path(args[0]), int(args[1]), float(args[2]), Path(args[3])
SIDECAR = json.loads(GLB.with_suffix(".parts.json").read_text(encoding="utf-8"))
ROLE = KIND % 4
# The GLB is recentred on the asset's base centre; move sidecar positions into the same frame.
for _part in SIDECAR["parts"]:
    for _k in range(3):
        _part["world"][12 + _k] -= SIDECAR["pivot"][_k]

WALNUT = Path(__file__).resolve().parents[3] / "assets" / "runtime" / "devices.json"

# Speakers: part IDs come from the design generator. First match wins; the rest is body.
SPEAKER_RULES = [
    (r"-(volume|bass)-(knob|cap)$", r"control_\1"),
    (r"-(volume|bass)-index$", r"indicator_\1"),
    (r"-power-(socket|button)$", "control_power"),
]
# Radios: part IDs carry a long revision history, so radio parts are matched by name.
RADIO_KNOBS = {"Master vol — rotary control": "master", "Local vol — rotary control": "local"}
RADIO_BUTTONS = {"Power": "power", "Source": "collections", "Previous track": "previous", "Next track": "next",
                 "Pause": "playpause", "Shuffle": "shuffle"}
# Display contents the runtime draws itself, and the design-file icon marks replaced by walnut glyphs.
# The Emerald wax belt stays as body geometry; the runtime writes on its front.
RADIO_REMOVED = ("split-leaf cell", "static lettering", "hinge lines", "character wheel", "alphabet wrap",
                 "preview blank sector", "enlarged", "Title shaft", "Artist shaft", "Axle bearing")


def classify(parts):
    """Runtime part name per design part (None = removed), knob parts, icon placements and display anchors."""
    names, knobs, icons = {}, {}, []
    if ROLE != 0:
        for pid in parts:
            names[pid] = "body"
            for pattern, name in SPEAKER_RULES:
                m = re.search(pattern, pid)
                if m:
                    names[pid] = m.expand(name)
                    break
            m = re.search(r"-(volume|bass)-knob$", pid)
            if m:
                knobs[m.group(1)] = pid
            if re.search(r"-power-button$", pid):
                icons.append((pid, "power", 1))
        return names, knobs, icons, []
    for pid, p in parts.items():
        name = p["name"]
        if any(s_ in name for s_ in RADIO_REMOVED) or p["material"]["name"].startswith("Ivory engraved"):
            names[pid] = None
        elif name in RADIO_KNOBS:
            names[pid] = "control_" + RADIO_KNOBS[name]
            knobs[RADIO_KNOBS[name]] = pid
        elif name == "Movable stylus carriage":
            names[pid] = "display_carriage"
        else:
            m = re.match(r"(.+) — (push button|button)$", name)
            if m and m.group(1) in RADIO_BUTTONS:
                names[pid] = "control_" + RADIO_BUTTONS[m.group(1)]
                icons.append((pid, RADIO_BUTTONS[m.group(1)], 0))
            else:
                names[pid] = "body"
    for pid, p in parts.items():  # index marks follow their knob
        parent = p["parent"]
        if names.get(pid) == "body" and parent in parts and parts[parent]["name"] in RADIO_KNOBS and "index" in p["name"]:
            names[pid] = "indicator_" + RADIO_KNOBS[parts[parent]["name"]]
    return names, knobs, icons, display_anchors(parts)


def unity_point(cm):
    return [round(cm[0] * SCALE / 100, 6), round(cm[1] * SCALE / 100, 6), round(-cm[2] * SCALE / 100, 6)]


def unity_dir(v):
    v = Vector(v).normalized()
    return [round(v.x, 6), round(v.y, 6), round(-v.z, 6)]


def display_anchors(parts):
    """Window rectangles for the runtime displays, in metres and Unity axes. `right`, `up` and
    `normal` (out of the face) span the window; rows are title then artist."""
    out, rows = [], {}
    for p in parts.values():
        m = re.match(r"(Title|Artist) (split-leaf cell|character wheel) (\d+)", p["name"])
        if m:
            rows.setdefault((m.group(1).lower(), m.group(2)), []).append((int(m.group(3)), p))
    for (row, kind), numbered in sorted(rows.items(), key=lambda kv: (kv[0][0] != "title", kv[0][1])):
        cells = [p for _n, p in sorted(numbered, key=lambda t: t[0])]
        first, last = Vector(cells[0]["world"][12:15]), Vector(cells[-1]["world"][12:15])
        right = (last - first).normalized()
        pitch = (last - first).length / (len(cells) - 1)
        anchor = {"row": row, "cells": len(cells), "right": unity_dir(right), "pitch": round(pitch * SCALE / 100, 6),
                  "width": round(pitch * len(cells) * SCALE / 100, 6)}
        if kind == "split-leaf cell":
            w = cells[0]["world"]
            up, normal = Vector(w[4:7]), Vector(w[8:11])
            centre = (first + last) / 2 + normal * (cells[0]["size"][2] / 2)
            anchor.update(type="splitflap", height=round(cells[0]["size"][1] * SCALE / 100, 6))
        else:
            # Wheels turn about the row axis; the visible strip is the drum front behind the bezel window.
            bezel = next(p for p in parts.values() if p["name"] == row.capitalize() + " Bezel frame")
            normal = Vector(bezel["world"][8:11])
            up = Vector(bezel["world"][4:7])
            radius = cells[0]["size"][0] / 2
            centre = (first + last) / 2 + normal * radius
            # The bezel mesh's opening is 0.578 of its height; the row fills it.
            anchor.update(type="drum", height=round(bezel["size"][1] * 0.57 * SCALE / 100, 6),
                          radius=round(radius * SCALE / 100, 6))
        anchor.update(center=unity_point(centre), up=unity_dir(up), normal=unity_dir(normal))
        out.append(anchor)
    belt = next((p for p in parts.values() if "endless loop" in p["name"]), None)
    if belt:
        w = belt["world"]
        right, up, normal = Vector(w[0:3]), Vector(w[4:7]), Vector(w[8:11])
        face = Vector(w[12:15]) + normal * (belt["size"][2] / 2)
        lines = [("title", "title — static lettering"), ("artist", "Artist — static lettering")]
        for row, label in lines:
            line = next(p for p in parts.values() if p["name"] == label)
            centre = face + up * (Vector(line["world"][12:15]) - face).dot(up)
            out.append({"type": "scroll", "row": row, "center": unity_point(centre), "right": unity_dir(right),
                        "up": unity_dir(up), "normal": unity_dir(normal),
                        "width": round(belt["size"][0] * SCALE / 100, 6), "height": round(2.3 * SCALE / 100, 6)})
    return out


def to_unity(v):
    return (round(v[0], 6), round(v[2], 6), round(v[1], 6))


def srgb(hex_color):
    return [int(hex_color[i:i + 2], 16) / 255 for i in (1, 3, 5)]


# ---------------------------------------------------------------- import

bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)
bpy.context.scene.unit_settings.system = "METRIC"
bpy.context.preferences.filepaths.save_version = 0
bpy.ops.import_scene.gltf(filepath=str(GLB))

by_id = {p["id"]: p for p in SIDECAR["parts"]}
meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
# Bake the hierarchy and the approved scale into each mesh, origin at the device base centre.
for o in meshes:
    world = o.matrix_world.copy()
    o.parent = None
    o.matrix_world = Matrix.Scale(SCALE, 4) @ world
bpy.ops.object.select_all(action="DESELECT")
for o in meshes:
    o.select_set(True)
bpy.context.view_layer.objects.active = meshes[0]
bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
for o in list(bpy.context.scene.objects):
    if o.type != "MESH":
        bpy.data.objects.remove(o)

# ---------------------------------------------------------------- tag parts and materials

part_names, knob_parts, icon_places, displays = classify(by_id)
for o in list(meshes):
    if part_names.get(o.get("partId"), "body") is None:
        meshes.remove(o)
        bpy.data.objects.remove(o)

materials, material_keys = [], {}
for o in meshes:
    pid = o.get("partId")
    if pid not in by_id:
        raise RuntimeError(f"Mesh {o.name} has no sidecar part")
    o["kind"], o["part"] = KIND, part_names[pid]
    src = by_id[pid]["material"]
    key = (src["name"], src["color"], src["roughness"], src["metalness"])
    if key not in material_keys:
        material_keys[key] = len(materials)
        materials.append({"name": src["name"], "color": srgb(src["color"]) + [1.0], "metallic": src["metalness"],
                          "smoothness": round(1 - src["roughness"], 4)})
    o["material_index"] = material_keys[key]
    o.name = f"{KIND}_{o['part']}_{by_id[pid]['name']}"[:63]

# Knob pivot and axis come from the authored knob part: its centre and its local +Z.
pivots, axes = {}, {}
for knob, pid in knob_parts.items():
    w = by_id[pid]["world"]
    for name in (f"control_{knob}", f"indicator_{knob}"):
        pivots[name] = unity_point(w[12:15])
        axes[name] = unity_dir(w[8:11])
for pid, p in by_id.items():  # the stylus carriage moves along its row, so it pivots at its own centre
    if part_names.get(pid) == "display_carriage":
        pivots["display_carriage"] = unity_point(p["world"][12:15])

# ---------------------------------------------------------------- hidden-face removal

def inside(bvh, point, direction=Vector((0.577, 0.577, 0.577))):
    """Parity ray test: point is inside a closed mesh when the ray crosses it an odd number of times."""
    hits, origin = 0, point.copy()
    for _ in range(64):
        loc, _n, _i, _d = bvh.ray_cast(origin, direction)
        if loc is None:
            break
        hits += 1
        origin = loc + direction * 1e-5
    return hits % 2 == 1


# The parity test is only meaningful for closed parts, so open shells never hide faces.
trees = {}
for o in meshes:
    bm = bmesh.new()
    bm.from_mesh(o.data)
    # The GLB import splits vertices along hard edges; weld a scratch copy so a closed part reads closed.
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6)
    if all(e.is_manifold for e in bm.edges):
        bm.transform(o.matrix_world)
        trees[o] = BVHTree.FromBMesh(bm)
    bm.free()

before = 0
for o in meshes:
    o.data.calc_loop_triangles()
    before += len(o.data.loop_triangles)
removed = 0
for o in meshes:
    others = [t for x, t in trees.items() if x is not o]
    bm = bmesh.new()
    bm.from_mesh(o.data)
    doomed = []
    to_world, normal_world = o.matrix_world, o.matrix_world.to_3x3()
    for f in bm.faces:
        # Remove a face only when one other part covers every sample on a barycentric
        # grid across it. Coverage stitched from several parts can leave visible gaps.
        offset = (normal_world @ f.normal).normalized() * 2e-4
        corners = [v.co for v in f.verts]
        centre = f.calc_center_median()
        points = [c.lerp(centre, 0.005) for c in corners]
        points += [corners[i].lerp(corners[(i + 1) % len(corners)], t).lerp(centre, 0.005)
                   for i in range(len(corners)) for t in (0.25, 0.5, 0.75)]
        for i in range(1, len(corners) - 1):
            a_, b_, c_ = corners[0], corners[i], corners[i + 1]
            n = 4
            for u in range(n + 1):
                for v in range(n + 1 - u):
                    w = n - u - v
                    points.append((a_ * (u + 0.2) + b_ * (v + 0.2) + c_ * (w + 0.2)) / (n + 0.6))
        samples = [to_world @ p_ + offset for p_ in points]
        if any(all(inside(t, s_) for s_ in samples) for t in others):
            doomed.append(f)
    if doomed and len(doomed) < len(bm.faces):
        removed += len(doomed)
        bmesh.ops.delete(bm, geom=doomed, context="FACES")
        bm.to_mesh(o.data)
    bm.free()

# ---------------------------------------------------------------- export

groups, vertex_maps = {}, {}
deps = bpy.context.evaluated_depsgraph_get()
for o in meshes:
    key = (o["part"], o["material_index"])
    out = groups.setdefault(key, {"name": key[0], "material": key[1], "pivot": list(pivots.get(key[0], [0, 0, 0])),
                                  "vertices": [], "normals": [], "triangles": []})
    if key[0] in axes:
        out["axis"] = axes[key[0]]
    vmap = vertex_maps.setdefault(key, {})
    ev = o.evaluated_get(deps)
    mesh = ev.to_mesh()
    mesh.calc_loop_triangles()
    for tri in mesh.loop_triangles:
        for index in (2, 1, 0):
            loop = mesh.loops[tri.loops[index]]
            v = o.matrix_world @ mesh.vertices[loop.vertex_index].co
            n = (o.matrix_world.to_3x3() @ mesh.corner_normals[tri.loops[index]].vector).normalized()
            vertex = (to_unity(v), to_unity(n))
            if vertex not in vmap:
                vmap[vertex] = len(out["vertices"]) // 3
                out["vertices"].extend(vertex[0])
                out["normals"].extend(vertex[1])
            out["triangles"].append(vmap[vertex])
    ev.to_mesh_clear()

def surface_area(part):
    v, t = part["vertices"], part["triangles"]
    total = 0.0
    for i in range(0, len(t), 3):
        a, b, c = (Vector(v[j * 3:j * 3 + 3]) for j in t[i:i + 3])
        total += (b - a).cross(c - a).length / 2
    return total


# The native wall-placement ghost draws only the first submesh of the root mesh, so the
# largest body material (the cabinet itself) comes first, as in the walnut models.
parts = sorted(groups.values(), key=lambda p: (p["name"] != "body", -surface_area(p) if p["name"] == "body" else 0))

# ---------------------------------------------------------------- walnut glyphs on button faces

walnut = json.loads(WALNUT.read_text(encoding="utf-8"))


def walnut_glyph(kind, name):
    """Flat walnut icon triangles in cap units: (x, y) relative to the cap centre / cap radius."""
    model = next(m for m in walnut["models"] if m["kind"] == kind)
    icon = [p for p in model["parts"] if p["name"] == "icon_" + name]
    cap = [p for p in model["parts"] if p["name"] == "control_" + name]
    xs = [x for p in cap for x in p["vertices"][0::3]]
    ys = [y for p in cap for y in p["vertices"][1::3]]
    cx, cy, radius = (max(xs) + min(xs)) / 2, (max(ys) + min(ys)) / 2, (max(xs) - min(xs)) / 2
    tris = []
    for p in icon:
        v, nrm = p["vertices"], p["normals"]
        for i in range(0, len(p["triangles"]), 3):
            tri = p["triangles"][i:i + 3]
            if sum(nrm[t * 3 + 2] for t in tri) / 3 > -0.5:
                continue  # back cap and side walls of the 1 mm walnut slab
            tris.append([((v[t * 3] - cx) / radius, (v[t * 3 + 1] - cy) / radius) for t in tri])
    return tris, walnut["materials"][icon[0]["material"]]


for pid, name, source_kind in icon_places:
    p = by_id[pid]
    tris, ivory = walnut_glyph(source_kind, name)
    key = (ivory["name"], tuple(ivory["color"]), ivory["metallic"], ivory["smoothness"])
    mat_index = next((i for i, m in enumerate(materials) if (m["name"], tuple(m["color"]), m["metallic"], m["smoothness"]) == key), None)
    if mat_index is None:
        mat_index = len(materials)
        materials.append(dict(ivory))
    w = p["world"]
    u, v, n = Vector(w[0:3]), Vector(w[4:7]), Vector(w[8:11])
    face = Vector(w[12:15]) + n * (p["size"][2] / 2)
    # The glyph fills the cap's flat face: the widest profile radius at the cap's top height.
    profile = p.get("profile")
    # Rectangular keys (box or rounded) take the glyph inside their face, not edge to edge.
    ratio = 0.62 if p["type"] in ("box", "rounded") else 1.0
    if profile:
        top = max(z for _r, z in profile)
        ratio = max(r for r, z in profile if z >= top - 1e-6) / max(r for r, _z in profile)
    flat_radius = p["size"][0] / 2 * ratio
    unity_n = Vector((n.x, n.y, -n.z))
    out = {"name": "icon_" + name, "material": mat_index, "pivot": [0, 0, 0], "vertices": [], "normals": [], "triangles": []}
    for tri in tris:
        pts = []
        for gx, gy in tri:
            q = (face + (u * gx + v * gy) * flat_radius) * (SCALE / 100) + n * 5e-4
            pts.append(Vector((q.x, q.y, -q.z)))
        if (pts[1] - pts[0]).cross(pts[2] - pts[0]).dot(unity_n) < 0:
            pts[1], pts[2] = pts[2], pts[1]
        for q in pts:
            out["triangles"].append(len(out["vertices"]) // 3)
            out["vertices"].extend(round(c, 6) for c in q)
            out["normals"].extend(round(c, 6) for c in unity_n)
    parts.append(out)

if ROLE == 1:
    # Small speakers hang on walls: like the walnut satellite, the back face is the
    # native wall attachment plane at Unity z = 0 and the cabinet extends to -z.
    back = max(max(p["vertices"][2::3]) for p in parts)
    for p in parts:
        p["vertices"][2::3] = [round(z - back, 6) for z in p["vertices"][2::3]]
        if p["name"] in pivots:
            p["pivot"][2] = round(p["pivot"][2] - back, 6)
triangles = sum(len(p["triangles"]) // 3 for p in parts)

# Contact box for pointer raycasts and item physics: the cabinet body's bounds, with every face
# that carries a control pulled in behind that control so the native pointer reaches it first.
body = [v for p in parts if p["name"] == "body" for v in p["vertices"]]
contact_min = [min(body[k::3]) for k in range(3)]
contact_max = [max(body[k::3]) for k in range(3)]
for name in sorted({p["name"] for p in parts if p["name"].startswith("control_")}):
    pieces = [p for p in parts if p["name"] == name]
    icon = next((p for p in parts if p["name"] == "icon_" + name[8:]), None)
    face = pieces[0].get("axis") or (icon["normals"][:3] if icon else None)
    if face is None:
        raise RuntimeError(f"{name} has no face direction for the contact box")
    k = max(range(3), key=lambda j: abs(face[j]))
    values = [v for p in pieces for v in p["vertices"][k::3]]
    # The box face sits behind the deepest point of every control, so the whole control (and
    # its padded trigger) is what the native pointer meets first. On a sloped deck this keeps
    # the lower half of each button out of the box as well as the upper half.
    if face[k] > 0:
        contact_max[k] = min(contact_max[k], min(values) - 0.004)
    else:
        contact_min[k] = max(contact_min[k], max(values) + 0.004)
if any(contact_min[k] >= contact_max[k] - 0.02 for k in range(3)):
    raise RuntimeError(f"contact box collapsed: {contact_min} .. {contact_max}")
mins = [min(min(p["vertices"][k::3]) for p in parts) for k in range(3)]
maxs = [max(max(p["vertices"][k::3]) for p in parts) for k in range(3)]
OUT.mkdir(parents=True, exist_ok=True)
stem = GLB.stem
fragment = {"kind": KIND, "materials": materials, "parts": parts, "displays": displays,
            "bounds": {"min": [round(v, 4) for v in mins], "max": [round(v, 4) for v in maxs]},
            "contact": {"min": [round(v, 4) for v in contact_min], "max": [round(v, 4) for v in contact_max]}}
(OUT / f"{stem}.model.json").write_text(json.dumps(fragment, separators=(",", ":")), encoding="utf-8")

# Editable authoring file: one collection per runtime part.
for o in meshes:
    name = o["part"]
    coll = bpy.data.collections.get(name) or bpy.data.collections.new(name)
    if coll.name not in bpy.context.scene.collection.children:
        bpy.context.scene.collection.children.link(coll)
    for old in list(o.users_collection):
        old.objects.unlink(o)
    coll.objects.link(o)
bpy.ops.wm.save_as_mainfile(filepath=str(OUT / f"{stem}.blend"))
report = {"blender": bpy.app.version_string, "source": GLB.name,
          "source_sha256": hashlib.sha256(GLB.read_bytes()).hexdigest(), "kind": KIND, "scale": SCALE,
          "triangles_before_culling": before, "faces_removed": removed, "triangles": triangles,
          "parts": sorted({p["name"] for p in parts}), "size_m": [round(maxs[k] - mins[k], 4) for k in range(3)]}
(OUT / f"{stem}.report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print("REGIONAL_DEVICE_EXPORTED", json.dumps(report))
