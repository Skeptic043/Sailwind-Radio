"""Merges the walnut models (kinds 0-3) from assets/runtime/devices.json with the exported
regional fragments into a version 2 devices.json.

Run after build_devices.py (which writes the walnut version 1 file) and the regional Blender
imports. Re-running is idempotent: only kinds 0-3 are taken from the existing file.
Usage: python assemble_devices.py
"""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
RUNTIME = ROOT / "assets" / "runtime" / "devices.json"
FRAGMENTS = ROOT / "assets" / "authoring" / "regional"
REGISTRY = json.loads((Path(__file__).parent / "regional.json").read_text(encoding="utf-8"))


def scale_depth(fragment, factor):
    """Scales a fragment along Unity z, for a cabinet deeper or shallower than its source design. Directions along a
    surface scale with it, and normals take the inverse scale so lighting stays correct. A display anchor's centre is
    its front face, where a drum's curve starts, so drum radii keep their curvature."""
    def point(p):
        return [p[0], p[1], round(p[2] * factor, 6)]

    def direction(n, z_scale):
        x, y, z = n[0], n[1], n[2] * z_scale
        length = (x * x + y * y + z * z) ** .5 or 1
        return [round(x / length, 6), round(y / length, 6), round(z / length, 6)]

    for part in fragment["parts"]:
        part["pivot"] = point(part["pivot"])
        v, n = part["vertices"], part["normals"]
        for i in range(0, len(v), 3):
            v[i + 2] = round(v[i + 2] * factor, 6)
            n[i:i + 3] = direction(n[i:i + 3], 1 / factor)
    for box in (fragment["bounds"], fragment["contact"]):
        box["min"], box["max"] = point(box["min"]), point(box["max"])
    for display in fragment.get("displays") or []:
        display["center"] = point(display["center"])
        display["right"] = direction(display["right"], factor)
        display["up"] = direction(display["up"], factor)
        display["normal"] = direction(display["normal"], 1 / factor)


def set_display_cells(fragment, cells):
    """Fewer, wider cells in the same window, for larger letters."""
    for display in fragment.get("displays") or []:
        if "cells" in display:
            display["cells"] = cells
            display["pitch"] = round(display["width"] / cells, 6)


def material_key(m):
    return (m["name"], tuple(round(c, 6) for c in m["color"]), round(m["metallic"], 6), round(m["smoothness"], 6))


def main():
    current = json.loads(RUNTIME.read_text(encoding="utf-8"))
    models = [m for m in current["models"] if 0 <= m["kind"] <= 3]
    # Only the walnut materials survive from the existing file, so regional materials from an
    # earlier run never accumulate toward the reader's 64-material limit.
    walnut = 1 + max(p["material"] for m in models for p in m["parts"])
    materials = [dict(m) for m in current["materials"][:walnut]]
    keys = {material_key(m): i for i, m in enumerate(materials)}
    if sorted(m["kind"] for m in models) != [0, 1, 2, 3]:
        raise SystemExit("devices.json must contain walnut kinds 0-3; run build_devices.py first")
    report = []
    for device in REGISTRY["devices"]:
        fragment = json.loads((FRAGMENTS / f"{device['source']}.model.json").read_text(encoding="utf-8"))
        if fragment["kind"] != device["kind"]:
            raise SystemExit(f"{device['source']}: fragment kind {fragment['kind']} != registry kind {device['kind']}")
        if "depth" in device:
            scale_depth(fragment, device["depth"])
        if "display_cells" in device:
            set_display_cells(fragment, device["display_cells"])
        remap = []
        for m in fragment["materials"]:
            k = material_key(m)
            if k not in keys:
                keys[k] = len(materials)
                materials.append(dict(m))
            remap.append(keys[k])
        parts = []
        for p in fragment["parts"]:
            q = dict(p)
            q["material"] = remap[p["material"]]
            parts.append(q)
        model = {"kind": fragment["kind"], "parts": parts, "bounds": fragment["bounds"], "contact": fragment["contact"]}
        if fragment.get("displays"):
            model["displays"] = fragment["displays"]
        if "contact_top" in device:
            # The native item physics copies the item's own box, so it must not reach above the
            # surface placed items should rest on (design cm, scaled like the model).
            model["contact"]["max"][1] = min(model["contact"]["max"][1], round(device["contact_top"] * device["scale"] / 100, 4))
        if "solids" in device:
            # Collision boxes for placed items and the player, authored in design cm (x, y, z,
            # with +z toward the front) and scaled like the model. Unity's front is -z.
            f = device["scale"] / 100
            model["solids"] = [{"min": [round(b[0] * f, 4), round(b[1] * f, 4), round(-b[5] * f, 4)],
                                "max": [round(b[3] * f, 4), round(b[4] * f, 4), round(-b[2] * f, 4)]} for b in device["solids"]]
        models.append(model)
        report.append((device["source"], fragment["kind"], sum(len(p["triangles"]) // 3 for p in parts)))
    models.sort(key=lambda m: m["kind"])
    payload = {"version": 2, "materials": materials, "models": models}
    RUNTIME.write_text(json.dumps(payload, separators=(",", ":")), encoding="utf-8")
    print(f"{RUNTIME} version 2: {len(models)} models, {len(materials)} materials, "
          f"sha256 {hashlib.sha256(RUNTIME.read_bytes()).hexdigest()}")
    for source, kind, tris in report:
        print(f"  kind {kind:2} {source:16} {tris} triangles")


if __name__ == "__main__":
    main()
