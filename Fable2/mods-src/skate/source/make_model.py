#!/usr/bin/env python3
"""Writes the skateboard's mesh (skateboard.glb) and textures (skateboard.tex.png and its specular map).

The model's axes: +Y is the nose, +Z is up, and the top of the deck's flat middle is at z 0, where the hero's
feet are. The wheels' undersides are at z -WHEEL_BOTTOM, which RIDE_HEIGHT in mod/init.lua matches. glTF is
Y-up and the model file is Z-up, a glTF (x, y, z) landing at model (x, -z, y), so the model's (x, y, z) is
written as (x, z, -y). Triangles are wound clockwise, the game's own winding and the reverse of glTF's,
because the compile keeps a file's order.

    make_model.py <folder for the .glb> <folder for the .tex.png files>

Needs Pillow.
"""
import json, math, random, struct, sys
from PIL import Image, ImageDraw, ImageFilter, ImageFont

TAU = math.tau

HALF_LENGTH, HALF_WIDTH, THICK = 0.525, 0.125, 0.018
KICK_FROM, KICK_RISE = 0.66, 0.085      # where along the half length the kick starts, and how high it ends
CONCAVE = 0.012                         # how much the deck's edges rise
ROUND_FROM = 0.74                       # where along the half length the outline starts to round off
TRUCK_AT = 0.295                        # along the deck, from its middle
AXLE_DROP = 0.066                       # the axle below the deck's top
WHEEL_R, WHEEL_IN, WHEEL_OUT = 0.044, 0.070, 0.122
WHEEL_BOTTOM = AXLE_DROP + WHEEL_R

# Where each part reads in the texture: (left, top, right, bottom).
GRIP = (0.0, 0.0, 0.25, 1.0)
GRAPHIC = (0.25, 0.0, 0.5, 1.0)
PLY = (0.5, 0.0, 0.75, 0.25)
CELLS = {"steel": (168, 170, 176), "dark_steel": (70, 72, 78), "bushing": (190, 120, 36),
         "wheel": (238, 232, 212), "wheel_side": (214, 206, 182), "bearing": (40, 40, 44),
         "bolt": (196, 198, 202), "black": (24, 24, 26)}
NAMES = list(CELLS)

rows, indices = [], []


def cell(name):
    return (0.5 + 0.5 * (NAMES.index(name) + 0.5) / len(NAMES), 0.625)


def in_rect(rect, u, v):
    # Kept off the panel's border, or the smaller mips would bleed the next panel in.
    return (rect[0] + (rect[2] - rect[0]) * (0.02 + 0.96 * u), rect[1] + (rect[3] - rect[1]) * (0.01 + 0.98 * v))


def sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def add(a, b, s=1.0):
    return (a[0] + b[0] * s, a[1] + b[1] * s, a[2] + b[2] * s)


def dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def unit(a):
    length = math.sqrt(dot(a, a)) or 1.0
    return (a[0] / length, a[1] / length, a[2] / length)


def vertex(p, n, uv):
    rows.append(((p[0], p[2], -p[1]), (n[0], n[2], -n[1]), uv))
    return len(rows) - 1


def triangle(a, b, c):
    """Winds a triangle clockwise seen from the way its vertices' normals face."""
    pa, pb, pc = rows[a], rows[b], rows[c]
    face = cross(sub(pb[0], pa[0]), sub(pc[0], pa[0]))
    indices.extend((a, c, b) if dot(face, pa[1]) >= 0 else (a, b, c))


def quad(a, b, c, d):
    triangle(a, b, c)
    triangle(b, d, c)


# ---- The deck: a popsicle outline, kicked up at both ends, dished across.

def half_width_at(v):
    edge = abs(v)
    if edge <= ROUND_FROM:
        return HALF_WIDTH
    t = (edge - ROUND_FROM) / (1.0 - ROUND_FROM)
    return HALF_WIDTH * math.sqrt(max(0.0, 1.0 - t ** 2.2))


def lift_at(v):
    t = max(0.0, (abs(v) - KICK_FROM) / (1.0 - KICK_FROM))
    return KICK_RISE * t ** 1.7


def deck_point(u, v, below=0.0):
    across = u * half_width_at(v)
    fade = 1.0 - 0.6 * max(0.0, (abs(v) - KICK_FROM) / (1.0 - KICK_FROM))
    z = lift_at(v) + CONCAVE * fade * u * u - below
    return (across, v * HALF_LENGTH, z)


def build_deck():
    across, along = 14, 64

    def station(j):
        # Rows along the deck, closer together at the ends, where its outline turns fastest.
        t = 2 * j / along - 1
        return 0.55 * t + 0.45 * math.sin(t * math.pi / 2)

    def sheet(below, rect, up):
        points = [[deck_point(2 * i / across - 1, station(j), below) for j in range(along + 1)]
                  for i in range(across + 1)]
        grid = []
        for i in range(across + 1):
            row = []
            for j in range(along + 1):
                a, b = points[min(i + 1, across)][j], points[max(i - 1, 0)][j]
                c, d = points[i][min(j + 1, along)], points[i][max(j - 1, 0)]
                n = cross(sub(a, b), sub(c, d))
                if dot(n, n) < 1e-14:
                    n = (0, 0, 1)
                n = unit(n)
                if n[2] * up < 0:
                    n = (-n[0], -n[1], -n[2])
                row.append(vertex(points[i][j], n, in_rect(rect, i / across, (station(j) + 1) / 2)))
            grid.append(row)
        for i in range(across):
            for j in range(along):
                quad(grid[i][j], grid[i + 1][j], grid[i][j + 1], grid[i + 1][j + 1])

    sheet(0.0, GRIP, 1)
    sheet(THICK, GRAPHIC, -1)

    # The edge, all the way round, with the plies showing.
    run = [(1, station(j)) for j in range(along + 1)] + [(-1, station(along - j)) for j in range(1, along + 1)]
    for k in range(len(run) - 1):
        (u0, v0), (u1, v1) = run[k], run[k + 1]
        top0, top1 = deck_point(u0, v0), deck_point(u1, v1)
        low0, low1 = deck_point(u0, v0, THICK), deck_point(u1, v1, THICK)
        step = unit(sub(top1, top0))
        n = unit((step[1], -step[0], 0))
        if dot(n, (top0[0], top0[1], 0)) < 0:
            n = (-n[0], -n[1], -n[2])
        s0, s1 = k / 12.0, (k + 1) / 12.0
        a = vertex(top0, n, in_rect(PLY, s0 % 1.0 if s1 % 1.0 > s0 % 1.0 else 0.0, 0.0))
        b = vertex(top1, n, in_rect(PLY, s1 % 1.0 if s1 % 1.0 > s0 % 1.0 else 1.0, 0.0))
        c = vertex(low0, n, in_rect(PLY, s0 % 1.0 if s1 % 1.0 > s0 % 1.0 else 0.0, 1.0))
        d = vertex(low1, n, in_rect(PLY, s1 % 1.0 if s1 % 1.0 > s0 % 1.0 else 1.0, 1.0))
        quad(a, b, c, d)


# ---- The trucks and wheels.

def lathe_x(profile, centre, sides=20):
    """Bands round an axle across the board: (x from centre, radius, cell)."""
    for (x0, r0, name), (x1, r1, _) in zip(profile, profile[1:]):
        if x0 == x1 and r0 == r1:
            continue
        slope = unit((x1 - x0, r1 - r0, 0))
        for s in range(sides):
            a0, a1 = TAU * s / sides, TAU * (s + 1) / sides
            quad_ids = []
            for x, r, a in ((x0, r0, a0), (x0, r0, a1), (x1, r1, a0), (x1, r1, a1)):
                ring = (math.cos(a), math.sin(a))
                # Perpendicular to the band's slope, away from the axle.
                n = (-slope[1], slope[0] * ring[0], slope[0] * ring[1])
                if abs(slope[0]) < 1e-5:
                    n = (-1.0 if r1 > r0 else 1.0, 0.0, 0.0)
                    n = (n[0] if x0 >= 0 else n[0], 0.0, 0.0)
                p = (centre[0] + x, centre[1] + r * ring[0], centre[2] + r * ring[1])
                quad_ids.append(vertex(p, n, cell(name)))
            quad(*quad_ids)


def box(centre, half, name):
    for axis in range(3):
        for sign in (-1, 1):
            n = [0.0, 0.0, 0.0]
            n[axis] = sign
            u, v = (axis + 1) % 3, (axis + 2) % 3
            ids = []
            for du, dv in ((-1, -1), (1, -1), (-1, 1), (1, 1)):
                p = list(centre)
                p[axis] += sign * half[axis]
                p[u] += du * half[u]
                p[v] += dv * half[v]
                ids.append(vertex(tuple(p), tuple(n), cell(name)))
            quad(*ids)


def peg(x, y, z0, z1, radius, name, sides=10):
    for s in range(sides):
        a0, a1 = TAU * s / sides, TAU * (s + 1) / sides
        n0 = (math.cos(a0), math.sin(a0), 0.0)
        n1 = (math.cos(a1), math.sin(a1), 0.0)
        v = [vertex((x + n0[0] * radius, y + n0[1] * radius, z0), n0, cell(name)),
             vertex((x + n1[0] * radius, y + n1[1] * radius, z0), n1, cell(name)),
             vertex((x + n0[0] * radius, y + n0[1] * radius, z1), n0, cell(name)),
             vertex((x + n1[0] * radius, y + n1[1] * radius, z1), n1, cell(name))]
        quad(*v)
        up = (0.0, 0.0, 1.0)
        triangle(vertex((x, y, z1), up, cell(name)), vertex(add((x, y, z1), n0, radius), up, cell(name)),
                 vertex(add((x, y, z1), n1, radius), up, cell(name)))


def build_truck(sign):
    y = sign * TRUCK_AT
    under = -THICK
    axle = (0.0, y, -AXLE_DROP)
    # The baseplate, and the bolts through it, whose heads show on the grip.
    box((0.0, y, under - 0.004), (0.031, 0.044, 0.004), "dark_steel")
    for bx in (-0.021, 0.021):
        for by in (-0.033, 0.033):
            peg(bx, y + by, under - 0.011, under + 0.0, 0.0034, "bolt", 6)
            peg(bx, y + by, -0.0012, 0.0012, 0.0052, "bolt", 8)
    # The kingpin and its bushings, leaning in toward the deck's middle.
    box((0.0, y - sign * 0.010, under - 0.015), (0.015, 0.016, 0.007), "bushing")
    peg(0.0, y - sign * 0.010, under - 0.030, under - 0.006, 0.0058, "steel", 8)
    # The hanger, wide at the axle and narrowing to its pivot.
    lathe_x([(-0.070, 0.0, "steel"), (-0.070, 0.0095, "steel"), (-0.030, 0.0125, "steel"), (0.030, 0.0125, "steel"),
             (0.070, 0.0095, "steel"), (0.070, 0.0, "steel")], axle, 12)
    box((0.0, y - sign * 0.004, -AXLE_DROP + 0.016), (0.022, 0.016, 0.018), "steel")
    box((0.0, y + sign * 0.018, under - 0.016), (0.009, 0.024, 0.008), "dark_steel")
    # The axle, the wheels and their nuts.
    lathe_x([(-WHEEL_OUT - 0.008, 0.0, "steel"), (-WHEEL_OUT - 0.008, 0.0036, "steel"), (WHEEL_OUT + 0.008, 0.0036, "steel"),
             (WHEEL_OUT + 0.008, 0.0, "steel")], axle, 8)
    for side in (-1, 1):
        lathe_x(sorted_wheel(side), axle, 22)
        x = side * (WHEEL_OUT + 0.002)
        lathe_x([(x, 0.0, "dark_steel"), (x, 0.0072, "dark_steel"), (x + side * 0.006, 0.0072, "dark_steel"),
                 (x + side * 0.006, 0.0, "dark_steel")], axle, 6)


def sorted_wheel(side):
    def at(x):
        return side * x

    return [(at(WHEEL_IN), 0.0, "bearing"), (at(WHEEL_IN), 0.016, "wheel_side"), (at(WHEEL_IN), 0.034, "wheel_side"),
            (at(WHEEL_IN + 0.006), WHEEL_R - 0.002, "wheel"), (at(WHEEL_IN + 0.012), WHEEL_R, "wheel"),
            (at(WHEEL_OUT - 0.012), WHEEL_R, "wheel"), (at(WHEEL_OUT - 0.006), WHEEL_R - 0.002, "wheel"),
            (at(WHEEL_OUT), 0.034, "wheel_side"), (at(WHEEL_OUT), 0.018, "bearing"), (at(WHEEL_OUT), 0.0, "bearing")]


build_deck()
build_truck(1)
build_truck(-1)


def write_glb(path):
    def pack(fmt, items):
        return b"".join(struct.pack(fmt, *item) for item in items)

    p = [r[0] for r in rows]
    blobs = [pack("<3f", p), pack("<3f", [r[1] for r in rows]), pack("<2f", [r[2] for r in rows]),
             b"".join(struct.pack("<H", i) for i in indices)]
    views, accessors, offset = [], [], 0
    kinds = [("VEC3", 5126, len(p)), ("VEC3", 5126, len(p)), ("VEC2", 5126, len(p)), ("SCALAR", 5123, len(indices))]
    for n, blob in enumerate(blobs):
        views.append({"buffer": 0, "byteOffset": offset, "byteLength": len(blob)})
        accessor = {"bufferView": n, "componentType": kinds[n][1], "count": kinds[n][2], "type": kinds[n][0]}
        if n == 0:
            accessor["min"] = [min(q[i] for q in p) for i in range(3)]
            accessor["max"] = [max(q[i] for q in p) for i in range(3)]
        accessors.append(accessor)
        offset += (len(blob) + 3) & ~3
    data = b"".join(blob + b"\0" * (-len(blob) % 4) for blob in blobs)
    doc = {"asset": {"version": "2.0", "generator": "skate make_model.py"}, "scene": 0,
           "scenes": [{"nodes": [0]}], "nodes": [{"mesh": 0, "name": "Skateboard"}],
           "meshes": [{"primitives": [{"attributes": {"POSITION": 0, "NORMAL": 1, "TEXCOORD_0": 2},
                                       "indices": 3, "material": 0}]}],
           "materials": [{"name": "Skateboard"}], "accessors": accessors, "bufferViews": views,
           "buffers": [{"byteLength": len(data)}]}
    text = json.dumps(doc, separators=(",", ":")).encode()
    text += b" " * (-len(text) % 4)
    with open(path, "wb") as out:
        out.write(struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(text) + 8 + len(data)))
        out.write(struct.pack("<I4s", len(text), b"JSON") + text)
        out.write(struct.pack("<I4s", len(data), b"BIN\0") + data)


# ---- The textures.

SIZE = 1024


def pixels(rect):
    return (int(rect[0] * SIZE), int(rect[1] * SIZE), int(rect[2] * SIZE), int(rect[3] * SIZE))


def speckle(size, seed, low, high):
    rng = random.Random(seed)
    image = Image.new("L", size)
    image.frombytes(bytes(rng.randint(low, high) for _ in range(size[0] * size[1])))
    return image


def paint_grip():
    """Griptape: black grit, worn paler where the feet go, cut clean at the bolts."""
    x0, y0, x1, y1 = pixels(GRIP)
    w, h = x1 - x0, y1 - y0
    grit = speckle((w, h), 3, 22, 62)
    wear = Image.new("L", (w, h), 0)
    draw = ImageDraw.Draw(wear)
    for centre in (0.30, 0.70):
        draw.ellipse((w * 0.14, h * (centre - 0.12), w * 0.86, h * (centre + 0.12)), fill=26)
    wear = wear.filter(ImageFilter.GaussianBlur(26))
    out = Image.merge("L", [Image.eval(grit, lambda v: v)])
    out = Image.eval(out, lambda v: v)
    combined = Image.new("L", (w, h))
    combined.frombytes(bytes(min(255, a + b) for a, b in zip(out.tobytes(), wear.tobytes())))
    return Image.merge("RGB", (combined, combined, combined))


def paint_graphic():
    """The underside: a deep stained red with a gold crest, scraped pale where the board meets rails."""
    x0, y0, x1, y1 = pixels(GRAPHIC)
    w, h = x1 - x0, y1 - y0
    image = Image.new("RGB", (w, h), (120, 30, 28))
    draw = ImageDraw.Draw(image, "RGBA")
    # Darker toward the ends.
    for row in range(h):
        t = abs(row / h - 0.5) * 2
        draw.line((0, row, w, row), fill=(20, 4, 4, int(t * t * 150)))
    gold = (226, 176, 72, 255)
    # A band down each side, and a crest in the middle.
    for x in (w * 0.09, w * 0.91):
        draw.line((x, h * 0.05, x, h * 0.95), fill=gold, width=5)
    centre = (w * 0.5, h * 0.5)
    for radius, width in ((86, 7), (70, 3)):
        draw.ellipse((centre[0] - radius, centre[1] - radius, centre[0] + radius, centre[1] + radius),
                     outline=gold, width=width)
    # A crown over a sword, the sort of thing Bowerstone would paint on a shield.
    cx, cy = centre
    draw.polygon([(cx - 40, cy - 12), (cx - 46, cy - 46), (cx - 22, cy - 28), (cx, cy - 54), (cx + 22, cy - 28),
                  (cx + 46, cy - 46), (cx + 40, cy - 12)], fill=gold)
    draw.rectangle((cx - 5, cy - 8, cx + 5, cy + 56), fill=gold)
    draw.rectangle((cx - 24, cy + 2, cx + 24, cy + 9), fill=gold)
    try:
        font = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSerif-Bold.ttf", 38)
    except OSError:
        font = ImageFont.load_default()
    for text, at in (("ALBION", 0.345), ("ALBION", 0.655)):
        box_ = draw.textbbox((0, 0), text, font=font)
        tile = Image.new("RGBA", (box_[2] - box_[0] + 8, box_[3] - box_[1] + 12), (0, 0, 0, 0))
        ImageDraw.Draw(tile).text((4 - box_[0], 4 - box_[1]), text, font=font, fill=gold)
        tile = tile.rotate(90 if at < 0.5 else -90, expand=True)
        image.paste(tile, (int(cx - tile.width / 2), int(h * at - tile.height / 2)), tile)
    # Scrapes, from grinding: long and mostly along the board, deepest over the trucks' gap.
    rng = random.Random(11)
    for _ in range(230):
        x = rng.gauss(w * 0.5, w * 0.26)
        y = rng.uniform(h * 0.12, h * 0.88)
        length = rng.uniform(10, 90)
        slant = rng.uniform(-0.25, 0.25)
        draw.line((x, y, x + slant * length, y + length), fill=(214, 186, 150, rng.randint(26, 110)),
                  width=rng.choice((1, 1, 2)))
    return image.filter(ImageFilter.GaussianBlur(0.4))


def paint_ply():
    """Seven plies of maple, seen edge on, one of them dyed."""
    x0, y0, x1, y1 = pixels(PLY)
    w, h = x1 - x0, y1 - y0
    image = Image.new("RGB", (w, h))
    draw = ImageDraw.Draw(image)
    colours = [(216, 180, 128), (190, 150, 96), (222, 188, 136), (168, 48, 40), (222, 188, 136), (190, 150, 96),
               (216, 180, 128)]
    for k, colour in enumerate(colours):
        draw.rectangle((0, h * k / 7, w, h * (k + 1) / 7), fill=colour)
    return image


def write_textures(folder):
    diffuse = Image.new("RGB", (SIZE, SIZE), (24, 24, 26))
    diffuse.paste(paint_grip(), pixels(GRIP)[:2])
    diffuse.paste(paint_graphic(), pixels(GRAPHIC)[:2])
    diffuse.paste(paint_ply(), pixels(PLY)[:2])
    draw = ImageDraw.Draw(diffuse)
    width = SIZE * 0.5 / len(NAMES)
    for k, name in enumerate(NAMES):
        draw.rectangle((SIZE * 0.5 + k * width, SIZE * 0.5, SIZE * 0.5 + (k + 1) * width, SIZE * 0.75), fill=CELLS[name])
    diffuse.save(folder + "/skateboard.tex.png")

    # Specular: colour in the RGB, the tightness of the highlight in the alpha. Grit doesn't shine.
    spec = Image.new("RGBA", (SIZE, SIZE), (10, 10, 10, 30))
    d = ImageDraw.Draw(spec)
    d.rectangle(pixels(GRAPHIC), fill=(96, 92, 88, 150))
    d.rectangle(pixels(PLY), fill=(50, 46, 40, 90))
    shine = {"steel": (170, 172, 178, 190), "dark_steel": (110, 112, 118, 170), "bolt": (180, 182, 188, 200),
             "bushing": (40, 36, 30, 80), "wheel": (70, 68, 62, 110), "wheel_side": (70, 68, 62, 110),
             "bearing": (120, 120, 126, 170), "black": (20, 20, 20, 40)}
    for k, name in enumerate(NAMES):
        d.rectangle((SIZE * 0.5 + k * width, SIZE * 0.5, SIZE * 0.5 + (k + 1) * width, SIZE * 0.75), fill=shine[name])
    spec.save(folder + "/skateboard_spec.tex.png")


write_glb(sys.argv[1] + "/skateboard.glb")
write_textures(sys.argv[2])
print(len(rows), "vertices,", len(indices) // 3, "triangles; wheels' undersides at z", round(-WHEEL_BOTTOM, 3))
