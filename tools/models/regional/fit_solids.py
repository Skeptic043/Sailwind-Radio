"""Fits collision boxes inside a regional device's body for regional.json "solids".

The body is rasterized from the front (X-Y) and the side (Z-Y). Rows with the same covered spans form one
band, and each band yields one box per front span and side span, shrunk to lie inside the silhouette at every
row of the band. Controls are left out, so placed items rest on the cabinet itself.
Usage: python -I fit_solids.py <devices.json> <kind> <scale> [preview.png] [tolerance] [below]
`below` (design cm) fits only the body under that height, for a cabinet whose top keeps hand-authored solids.
Prints the boxes in design cm (x, y, z, +z toward the front) for regional.json.
"""
import json
import sys
from PIL import Image, ImageDraw

STEP = .0025      # metres per raster cell
TOLERANCE = .03   # a span edge moving further than this starts a new band, so a slope becomes steps this wide
MIN_BAND = .005   # thinner bands merge into the band below
GAP = .02         # covered spans closer than this join


def raster(model, axis):
    lo = model["bounds"]["min"]
    hi = model["bounds"]["max"]
    width = int((hi[axis] - lo[axis]) / STEP) + 3
    height = int((hi[1] - lo[1]) / STEP) + 3
    image = Image.new("1", (width, height), 0)
    draw = ImageDraw.Draw(image)
    for part in model["parts"]:
        if part["name"] != "body":
            continue
        v, t = part["vertices"], part["triangles"]
        for i in range(0, len(t), 3):
            draw.polygon([((v[t[i + k] * 3 + axis] - lo[axis]) / STEP + 1, (v[t[i + k] * 3 + 1] - lo[1]) / STEP + 1)
                          for k in range(3)], fill=1)
    pixels = image.load()
    rows = []
    for y in range(height):
        spans, start = [], None
        for x in range(width + 1):
            filled = x < width and pixels[x, y]
            if filled and start is None:
                start = x
            elif not filled and start is not None:
                span = ((start - 1) * STEP + lo[axis], (x - 1) * STEP + lo[axis])
                # Grille slots and seams are not gaps an item could rest in.
                if spans and span[0] - spans[-1][1] <= GAP:
                    spans[-1] = (spans[-1][0], span[1])
                else:
                    spans.append(span)
                start = None
        rows.append(spans)
    return rows, lo[1]


def similar(a, b):
    return len(a) == len(b) and all(abs(p[0] - q[0]) <= TOLERANCE and abs(p[1] - q[1]) <= TOLERANCE for p, q in zip(a, b))


def bands(front, side, floor):
    result = []
    for y, (f, s) in enumerate(zip(front, side)):
        if not f or not s:
            continue
        # Compared with the band's first row, so a slope becomes steps rather than one band at its narrowest.
        if result and result[-1]["end"] == y - 1 and similar(result[-1]["front"][0], f) and similar(result[-1]["side"][0], s):
            band = result[-1]
        else:
            band = {"start": y, "front": [], "side": []}
            result.append(band)
        band["end"] = y
        band["front"].append(f)
        band["side"].append(s)
    merged = []
    for band in result:
        if merged and (band["end"] - band["start"] + 1) * STEP < MIN_BAND and len(band["front"][0]) == len(merged[-1]["front"][0]) \
                and len(band["side"][0]) == len(merged[-1]["side"][0]):
            merged[-1]["end"] = band["end"]
            merged[-1]["front"] += band["front"]
            merged[-1]["side"] += band["side"]
        else:
            merged.append(band)
    boxes = []
    for band in merged:
        y0, y1 = floor + (band["start"] - 1) * STEP, floor + band["end"] * STEP
        def inner(rows):
            return [(max(r[i][0] for r in rows), min(r[i][1] for r in rows)) for i in range(len(rows[0]))]
        for fx in inner(band["front"]):
            for sz in inner(band["side"]):
                if fx[1] - fx[0] > .01 and sz[1] - sz[0] > .01:
                    boxes.append((fx[0], y0, sz[0], fx[1], y1, sz[1]))
    return boxes


def main():
    data = json.load(open(sys.argv[1], encoding="utf-8"))
    global TOLERANCE
    kind, scale = int(sys.argv[2]), float(sys.argv[3])
    if len(sys.argv) > 5:
        TOLERANCE = float(sys.argv[5])
    below = float(sys.argv[6]) * scale / 100 if len(sys.argv) > 6 else float("inf")
    model = next(m for m in data["models"] if m["kind"] == kind)
    front, floor = raster(model, 0)
    side, _ = raster(model, 2)
    # A partly covered raster cell counts as covered, so a box can overshoot the body by a cell. Radio rejects
    # solids outside the model bounds.
    lo, hi = model["bounds"]["min"], model["bounds"]["max"]
    boxes = [(max(b[0], lo[0]), max(b[1], lo[1]), max(b[2], lo[2]), min(b[3], hi[0]), min(b[4], hi[1]), min(b[5], hi[2]))
             for b in bands(front, side, floor) if b[1] < below]
    boxes = [b[:4] + (min(b[4], below), b[5]) for b in boxes]
    f = 100 / scale
    # Unity's front is -z. Design z runs toward the front.
    design = [[round(b[0] * f, 1), round(b[1] * f, 1), round(-b[5] * f, 1), round(b[3] * f, 1), round(b[4] * f, 1), round(-b[2] * f, 1)]
              for b in boxes]
    print(json.dumps(design))
    if len(sys.argv) > 4:
        S, W, H = 400, 480, 440
        sheet = Image.new("RGB", (W * 2, H), "white")
        for col, axis in enumerate((0, 2)):
            image = Image.new("RGB", (W, H), "white")
            draw = ImageDraw.Draw(image)
            point = lambda u, y: (W / 2 + u * S, H - 20 - y * S)
            for part in model["parts"]:
                v, t = part["vertices"], part["triangles"]
                for i in range(0, len(t), 3):
                    draw.polygon([point(v[t[i + k] * 3 + axis], v[t[i + k] * 3 + 1]) for k in range(3)],
                                 fill=(160, 160, 160) if part["name"] == "body" else (90, 90, 200))
            for b in boxes:
                lo, hi = (b[0], b[2]), (b[3], b[5])
                draw.rectangle([point(lo[col], b[4]), point(hi[col], b[1])], outline=(0, 150, 0))
            draw.text((5, 5), f"kind {kind} {'front' if axis == 0 else 'side'} {len(boxes)} boxes", fill=(0, 0, 0))
            sheet.paste(image, (col * W, 0))
        sheet.save(sys.argv[4])


if __name__ == "__main__":
    main()
