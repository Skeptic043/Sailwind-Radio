"""Render the original Radio model as a 256x256 Thunderstore icon.

Run with Blender in background mode after opening assets/authoring/devices.blend.
"""

from pathlib import Path
import struct

import bpy
from mathutils import Vector


root = Path(__file__).resolve().parents[2]
scene = bpy.context.scene
radio_offset = Vector((-.79, -.37, 0))

for obj in bpy.data.objects:
    if obj.get("preview") and obj.get("kind", 0) != 0:
        obj.hide_render = True

camera = scene.camera
camera.location = radio_offset + Vector((.38, -1.1, .55))
camera.rotation_euler = (
    radio_offset + Vector((0, -.05, .20)) - camera.location
).to_track_quat("-Z", "Y").to_euler()
camera.data.type = "ORTHO"
camera.data.ortho_scale = .84

scene.render.engine = "CYCLES"
scene.cycles.samples = 64
scene.render.resolution_x = 256
scene.render.resolution_y = 256
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = "PNG"
scene.render.image_settings.color_mode = "RGBA"
scene.render.filepath = str(root / "icon.png")
bpy.ops.render.render(write_still=True)

# Blender's PNG metadata can include the local authoring path. Keep only the
# PNG image chunks so the release icon carries no machine-specific metadata.
icon = root / "icon.png"
source = icon.read_bytes()
signature = b"\x89PNG\r\n\x1a\n"
if not source.startswith(signature):
    raise RuntimeError("Blender did not render a PNG icon")
output = bytearray(signature)
offset = len(signature)
kept = {b"IHDR", b"PLTE", b"tRNS", b"IDAT", b"IEND"}
while offset < len(source):
    length = struct.unpack_from(">I", source, offset)[0]
    chunk_type = source[offset + 4 : offset + 8]
    end = offset + 12 + length
    if end > len(source):
        raise RuntimeError("Truncated PNG icon chunk")
    if chunk_type in kept:
        output.extend(source[offset:end])
    offset = end
icon.write_bytes(output)
