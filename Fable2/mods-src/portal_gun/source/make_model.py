#!/usr/bin/env python3
"""Writes the Portal Gun's mesh (portal_gun.glb) and textures (portal_gun.tex.png and its maps).

The gun is built round the Master Flintlock Rifle's own axes, because that rifle is its template and
gives it its grip points: +Y is the muzzle, the right hand holds at y -0.09 and the left at y 0.27, both
close to the line x 0, z 0. The body sits above that line with two grips under it, so the hands hold it
rather than sink into it. Everything below is written with +Z up, but the rifle's up is -X (its muzzle
is at x -0.07), so vertex() turns it. glTF is Y-up and the model file is Z-up, a glTF (x, y, z) landing at
model (x, -z, y), so the model's (x, y, z) is written as (x, z, -y).

Material 0 is everything solid. Material 1 is what glows, which godot/portal_gun.gd colours.

    make_model.py <folder for the .glb> <folder for the .tex.png files>

Needs Pillow.
"""
import json, math, random, struct, sys
from PIL import Image, ImageDraw, ImageFilter

AXIS = 0.15  # the barrel's height above the grip line
TAU = math.tau

# Where each part reads in the texture: (left, top, right, bottom).
SHELL = (0.0, 0.0, 0.5, 0.5)
CHIN = (0.5, 0.0, 1.0, 0.5)
METAL = (0.0, 0.5, 0.5, 0.75)
LENS = (0.5, 0.5, 0.75, 0.75)
# Flat cells along the bottom, read through their middles.
CELLS = {"white": (238, 237, 232), "inner": (34, 35, 40), "black": (30, 31, 35), "steel": (70, 72, 78),
         "rubber": (20, 20, 22), "rim": (200, 232, 255), "glow": (150, 215, 255), "vent": (112, 116, 122)}
NAMES = list(CELLS)

# (vertices, indices) for each material.
meshes = [([], []), ([], [])]


def cell(name):
    return ((NAMES.index(name) + 0.5) / len(NAMES), 0.875)


def in_rect(rect, u, v):
    # Kept off the panel's border, or the smaller mips would bleed the next panel in.
    return (rect[0] + (rect[2] - rect[0]) * (0.02 + 0.96 * u), rect[1] + (rect[3] - rect[1]) * (0.02 + 0.96 * v))


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


def lerp(a, b, t):
    return a + (b - a) * t


def smooth(a, b, t):
    t = max(0.0, min(1.0, (t - a) / (b - a)))
    return t * t * (3 - 2 * t)


def vertex(p, n, uv, material=0):
    rows = meshes[material][0]
    # Up, here +Z, turned to the rifle's -X, then the model's (x, y, z) as glTF has it.
    rows.append(((-p[2], p[0], -p[1]), (-n[2], n[0], -n[1]), uv))
    return len(rows) - 1


def triangle(a, b, c, material=0):
    """Winds a triangle clockwise seen from the way its vertices' normals face.

    That is the game's own winding, the reverse of glTF's, and the compile keeps a file's order.
    """
    rows, indices = meshes[material]
    pa, pb, pc = rows[a], rows[b], rows[c]
    # The stored axes are a rotation of the model's, so handedness holds.
    face = cross(sub(pb[0], pa[0]), sub(pc[0], pa[0]))
    indices.extend((a, c, b) if dot(face, pa[1]) >= 0 else (a, b, c))


def surface(shape, across, along, facing, wrap=False, material=0):
    """A smooth sheet: shape(i / across, j / along) -> (point, uv). facing(point) says which way is out."""
    points = [[shape(i / across, j / along) for j in range(along + 1)] for i in range(across + 1)]

    def at(i, j):
        if wrap:
            i %= across
        else:
            i = max(0, min(across, i))
        return points[i][max(0, min(along, j))][0]

    grid = []
    for i in range(across + 1):
        row = []
        for j in range(along + 1):
            p, uv = points[i][j]
            n = cross(sub(at(i + 1, j), at(i - 1, j)), sub(at(i, j + 1), at(i, j - 1)))
            if dot(n, n) < 1e-16:
                n = facing(p)
            n = unit(n)
            if dot(n, facing(p)) < 0:
                n = (-n[0], -n[1], -n[2])
            row.append(vertex(p, n, uv, material))
        grid.append(row)
    for i in range(across):
        for j in range(along):
            a, b, c, d = grid[i][j], grid[i + 1][j], grid[i][j + 1], grid[i + 1][j + 1]
            triangle(a, b, c, material)
            triangle(b, d, c, material)
    return grid


def strip(edge_a, edge_b, uv, normal, material=0):
    """A flat band joining two runs of points of the same length, as a rim."""
    for k in range(len(edge_a) - 1):
        n = normal(edge_a[k], edge_b[k])
        a, b = vertex(edge_a[k], n, uv, material), vertex(edge_b[k], n, uv, material)
        n = normal(edge_a[k + 1], edge_b[k + 1])
        c, d = vertex(edge_a[k + 1], n, uv, material), vertex(edge_b[k + 1], n, uv, material)
        triangle(a, b, c, material)
        triangle(b, d, c, material)


def lathe(profile, segments=36, centre=(0, 0, AXIS), material=0, facets=False):
    """Bands round the barrel: (y, radius, where it reads). A cell name reads flat, METAL reads along its grain."""
    for (y0, r0, skin), (y1, r1, _) in zip(profile, profile[1:]):
        if y0 == y1 and r0 == r1:
            continue
        slope = unit((r0 - r1, y1 - y0, 0))
        for s in range(segments):
            a0, a1 = TAU * s / segments, TAU * (s + 1) / segments
            mid = (a0 + a1) / 2
            corners = []
            for y, r, a, v in ((y0, r0, a0, 0), (y0, r0, a1, 0), (y1, r1, a0, 1), (y1, r1, a1, 1)):
                c, z = math.sin(a), math.cos(a)
                aim = mid if facets else a
                n = (slope[1] * math.sin(aim), slope[0], slope[1] * math.cos(aim))
                p = (centre[0] + r * c, y, centre[2] + r * z)
                uv = cell(skin) if isinstance(skin, str) else in_rect(skin, a / TAU, 0.15 + 0.7 * v)
                corners.append(vertex(p, n, uv, material))
            triangle(corners[0], corners[1], corners[2], material)
            triangle(corners[1], corners[3], corners[2], material)


def beam(a, b, up, width, height, skin="black", material=0):
    """A squared bar from a to b, width and height each (at a, at b), with hard edges and ends."""
    forward = unit(sub(b, a))
    side = unit(cross(forward, up))
    lift = cross(side, forward)
    rings = []
    for at, w, h in ((a, width[0], height[0]), (b, width[1], height[1])):
        rings.append([add(add(at, side, sx * w), lift, sy * h) for sx, sy in ((1, 1), (-1, 1), (-1, -1), (1, -1))])
    uv = cell(skin)
    for k in range(4):
        p0, p1, p2, p3 = rings[0][k], rings[0][(k + 1) % 4], rings[1][k], rings[1][(k + 1) % 4]
        n = unit(cross(sub(p1, p0), sub(p2, p0)))
        mid = add(add(p0, p1), add(p2, p3))
        centre = add(a, b)
        if dot(n, sub((mid[0] / 4, mid[1] / 4, mid[2] / 4), (centre[0] / 2, centre[1] / 2, centre[2] / 2))) < 0:
            n = (-n[0], -n[1], -n[2])
        v = [vertex(p, n, uv, material) for p in (p0, p1, p2, p3)]
        triangle(v[0], v[1], v[2], material)
        triangle(v[1], v[3], v[2], material)
    for ring, n in ((rings[0], (-forward[0], -forward[1], -forward[2])), (rings[1], forward)):
        v = [vertex(p, n, uv, material) for p in ring]
        triangle(v[0], v[1], v[2], material)
        triangle(v[0], v[2], v[3], material)


def cylinder(a, b, radius, sides=12, skin="black", cap=True):
    forward = unit(sub(b, a))
    side = unit(cross(forward, (0.31, 0.53, 0.79)))
    lift = cross(side, forward)
    uv = cell(skin)
    for s in range(sides):
        t0, t1 = TAU * s / sides, TAU * (s + 1) / sides
        n0 = add((math.cos(t0) * side[0], math.cos(t0) * side[1], math.cos(t0) * side[2]), lift, math.sin(t0))
        n1 = add((math.cos(t1) * side[0], math.cos(t1) * side[1], math.cos(t1) * side[2]), lift, math.sin(t1))
        v = [vertex(add(a, n0, radius), n0, uv), vertex(add(a, n1, radius), n1, uv),
             vertex(add(b, n0, radius), n0, uv), vertex(add(b, n1, radius), n1, uv)]
        triangle(v[0], v[1], v[2])
        triangle(v[1], v[3], v[2])
        if cap:
            triangle(vertex(b, forward, uv), vertex(add(b, n0, radius), forward, uv),
                     vertex(add(b, n1, radius), forward, uv))


def tube(points, radius, sides=6, skin="rubber", steps=10):
    """A cable through points: a Catmull-Rom curve, carried along without twisting."""
    pts = [points[0]] + list(points) + [points[-1]]
    path = []
    for i in range(1, len(pts) - 2):
        p0, p1, p2, p3 = pts[i - 1], pts[i], pts[i + 1], pts[i + 2]
        for s in range(steps):
            t = s / steps
            path.append(tuple(0.5 * (2 * p1[k] + (p2[k] - p0[k]) * t + (2 * p0[k] - 5 * p1[k] + 4 * p2[k] - p3[k]) * t * t
                                     + (3 * p1[k] - p0[k] - 3 * p2[k] + p3[k]) * t * t * t) for k in range(3)))
    path.append(points[-1])
    side = unit(cross(sub(path[1], path[0]), (0.31, 0.53, 0.79)))
    uv = cell(skin)
    rows = []
    for i, p in enumerate(path):
        forward = unit(sub(path[min(i + 1, len(path) - 1)], path[max(i - 1, 0)]))
        side = unit(sub(side, (forward[0] * dot(side, forward), forward[1] * dot(side, forward),
                               forward[2] * dot(side, forward))))
        lift = cross(side, forward)
        row = []
        for s in range(sides):
            t = TAU * s / sides
            n = add((math.cos(t) * side[0], math.cos(t) * side[1], math.cos(t) * side[2]), lift, math.sin(t))
            row.append(vertex(add(p, n, radius), n, uv))
        rows.append(row)
    for r0, r1 in zip(rows, rows[1:]):
        for s in range(sides):
            a, b, c, d = r0[s], r0[(s + 1) % sides], r1[s], r1[(s + 1) % sides]
            triangle(a, b, c)
            triangle(b, d, c)


def around(angle, radius, y):
    """A point round the barrel: the angle from the top, toward +x."""
    return (radius * math.sin(angle), y, AXIS + radius * math.cos(angle))


# ---- The rear shell: an egg, with the barrel's arch cut from its front.

REAR, SHELL_R, SHELL_LIP = -0.45, 0.138, 0.008


def shell_radius(y):
    t = (y - REAR) / 0.30
    if t >= 1.0:
        return 1.0 - 0.55 * ((t - 1.0) ** 2) * 0.35
    return math.sqrt(max(0.0, 1.0 - (1.0 - t) ** 2.3))


def shell_front(angle):
    """How far forward the shell reaches at this angle: a hood on top, a low arch at the sides."""
    side = abs(angle) / math.pi
    hood = lerp(-0.035, -0.225, smooth(0.04, 0.42, side))
    return lerp(hood, -0.165, smooth(0.62, 0.86, side))


def shell_point(angle, s, inset=0.0):
    y = lerp(REAR + inset, shell_front(angle), 1.0 - math.cos(s * math.pi / 2))
    r = max(0.0, SHELL_R * shell_radius(y) - inset)
    return (r * math.sin(angle), y, AXIS + 1.04 * r * math.cos(angle))


def build_shell():
    def outer(u, v):
        return shell_point(TAU * (u - 0.5), v), in_rect(SHELL, u, v)

    def inner(u, v):
        return shell_point(TAU * (u - 0.5), v, SHELL_LIP), cell("inner")

    def outward(p):
        return (p[0], p[1] + 0.20, p[2] - AXIS)

    def inward(p):
        return (-p[0], -p[1] - 0.20, AXIS - p[2])

    surface(outer, 48, 24, outward, wrap=True)
    surface(inner, 32, 10, inward, wrap=True)
    a = [shell_point(TAU * (i / 96 - 0.5), 1.0) for i in range(97)]
    b = [shell_point(TAU * (i / 96 - 0.5), 1.0, SHELL_LIP) for i in range(97)]
    strip(a, b, cell("rim"), lambda p, q: unit((0, 1, 0)), material=1)


# ---- The chin: a white hull under the barrel, its side rising to a rounded peak at each prong's hub.

CHIN_R, CHIN_REACH, CHIN_MID, CHIN_HALF = 0.124, math.radians(112), 0.135, 0.178


def chin_point(v, s, inset=0.0):
    edge = abs(v)
    halfway = CHIN_HALF * math.sqrt(max(0.0, 1.0 - edge ** 2.8))
    y = CHIN_MID + 0.02 * edge + s * halfway
    # It closes toward the barrel at both ends, the back most.
    close = 0.016 * (max(0.0, -s) ** 2) * (1.0 - edge * 0.5) + 0.006 * (max(0.0, s) ** 3)
    r = CHIN_R - close - inset
    return (r * math.sin(v * CHIN_REACH), y, AXIS - r * math.cos(v * CHIN_REACH))


def build_chin():
    def outer(u, v):
        return chin_point(2 * u - 1, 2 * v - 1), in_rect(CHIN, u, v)

    def inner(u, v):
        return chin_point(2 * u - 1, 2 * v - 1, 0.008), cell("inner")

    def outward(p):
        return (p[0], 0, p[2] - AXIS)

    def inward(p):
        return (-p[0], 0, AXIS - p[2])

    surface(outer, 32, 24, outward)
    surface(inner, 16, 10, inward)
    run = [(2 * i / 60 - 1, 1) for i in range(61)] + [(1 - 2 * i / 60, -1) for i in range(61)]
    a = [chin_point(v, s) for v, s in run]
    b = [chin_point(v, s, 0.008) for v, s in run]

    def rim(p, q):
        return unit((p[0] * 0.2, (p[1] - CHIN_MID) * 3, (AXIS - p[2]) * -0.2))

    strip(a, b, cell("white"), rim)


# ---- The barrel, what shows in the arch, and the two grips.

def build_barrel():
    # The plate the shell closes on.
    lathe([(-0.235, 0.0, "inner"), (-0.235, 0.118, "inner"), (-0.20, 0.118, "black"), (-0.185, 0.09, "black"),
           (-0.135, 0.086, METAL), (-0.128, 0.058, "black"), (-0.128, 0.0, "black")])
    # The glass over the glowing tube is a cage of rods, since nothing here can be see-through.
    lathe([(-0.13, 0.034, "glow"), (0.052, 0.034, "glow")], segments=20, material=1)
    for k in range(5):
        angle = TAU * k / 5 + 0.31
        beam(around(angle, 0.063, -0.13), around(angle, 0.063, 0.055), (0, 0, 1) if abs(math.sin(angle)) > 0.5
             else (1, 0, 0), (0.0042, 0.0042), (0.0042, 0.0042), "steel")
    lathe([(-0.052, 0.036, "steel"), (-0.052, 0.070, "steel"), (-0.036, 0.070, "black"), (-0.036, 0.036, "black")],
          segments=24)
    # Front of the glass, the sleeve, then the heavy collars and the mouth.
    lathe([(0.05, 0.0, "black"), (0.05, 0.078, "black"), (0.058, 0.090, "steel"), (0.075, 0.090, "steel"),
           (0.08, 0.082, METAL), (0.165, 0.082, METAL), (0.168, 0.092, "black")])
    lathe([(0.168, 0.092, "black"), (0.178, 0.112, "black"), (0.255, 0.112, METAL), (0.266, 0.092, "black")],
          segments=8, facets=True)
    lathe([(0.266, 0.092, "black"), (0.266, 0.086, METAL), (0.298, 0.086, METAL), (0.302, 0.094, "black")])
    lathe([(0.302, 0.094, "black"), (0.31, 0.103, "black"), (0.345, 0.103, METAL), (0.353, 0.092, "black")],
          segments=8, facets=True)
    lathe([(0.353, 0.092, "black"), (0.356, 0.09, METAL), (0.378, 0.086, METAL), (0.384, 0.076, "steel"),
           (0.382, 0.068, "inner"), (0.356, 0.062, "inner"), (0.352, 0.05, "inner"), (0.352, 0.0, "inner")])
    # The lens, down the mouth.
    lathe([(0.3535, 0.0, "glow"), (0.3535, 0.034, "glow")], segments=24, material=1)

    # The bar under the glass, running back into the shell.
    beam((0, -0.215, AXIS - 0.074), (0, 0.075, AXIS - 0.074), (0, 0, 1), (0.03, 0.03), (0.022, 0.022))
    # A vent box on the barrel's flank.
    beam((0.082, 0.09, AXIS), (0.082, 0.15, AXIS), (0, 0, 1), (0.008, 0.008), (0.02, 0.02), "steel")

    # The grips, where the rifle's sockets put the hands.
    for y0, y1, drop_from in ((-0.145, -0.03, AXIS - 0.096), (0.205, 0.325, AXIS - 0.118)):
        cylinder((0, y0, 0.0), (0, y1, 0.0), 0.021, 14, "rubber")
        cylinder((0, y1, 0.0), (0, y0, 0.0), 0.021, 14, "rubber")
        beam((0, y0 + 0.022, 0.0), (0, y0 + 0.022, drop_from), (0, 1, 0), (0.012, 0.012), (0.012, 0.012))
        beam((0, y1 - 0.022, 0.0), (0, y1 - 0.022, drop_from), (0, 1, 0), (0.012, 0.012), (0.012, 0.012))
        for rib in range(3):
            y = lerp(y0 + 0.02, y1 - 0.02, rib / 2)
            cylinder((0, y - 0.002, 0.0), (0, y + 0.002, 0.0), 0.0222, 14, "steel", cap=False)


# ---- The three prongs, with their hubs and the cables back to the shell.

def prong(angle, hub_r, hub_y, knee_r, knee_y, wrist_r, wrist_y, tip_r, tip_y, tab_angle, tab_r, tab_y, sway):
    radial = (math.sin(angle), 0, math.cos(angle))
    across = (math.cos(angle), 0, -math.sin(angle))
    hub = around(angle, hub_r, hub_y)
    knee = around(angle, knee_r, knee_y)
    wrist = around(angle, wrist_r, wrist_y)
    tip = around(angle, tip_r, tip_y)

    # The hub stands on the body.
    cylinder(around(angle, hub_r - 0.012, hub_y), around(angle, hub_r + 0.010, hub_y), 0.0235, 14, "black")
    cylinder(around(angle, hub_r + 0.010, hub_y), around(angle, hub_r + 0.018, hub_y), 0.013, 10, "steel")
    # The link, doubled at the root as a fork round the hub.
    root = around(angle, hub_r + 0.016, hub_y + 0.004)
    for side in (-1, 1):
        a = add(root, across, side * 0.0085)
        b = add(lerp3(root, knee, 0.42), across, side * 0.0052)
        beam(a, b, across, (0.0034, 0.0034), (0.0052, 0.0044))
    beam(lerp3(root, knee, 0.36), knee, across, (0.0072, 0.0052), (0.0050, 0.0058))
    # The spring and bracket beside the fork.
    beam(add(lerp3(root, knee, 0.10), across, 0.019), add(lerp3(root, knee, 0.36), across, -0.019), radial,
         (0.0022, 0.0022), (0.0028, 0.0028), "steel")
    beam(add(lerp3(root, knee, 0.10), across, -0.019), add(lerp3(root, knee, 0.36), across, 0.019), radial,
         (0.0022, 0.0022), (0.0028, 0.0028), "steel")
    # The knee's pin, and the arm out to the blade.
    cylinder(add(knee, across, -0.0115), add(knee, across, 0.0115), 0.0092, 10, "steel")
    cylinder(add(knee, across, 0.0115), add(knee, across, -0.0115), 0.0092, 10, "steel")
    beam(knee, wrist, across, (0.0052, 0.0058), (0.0046, 0.0052))
    # The blade: broad and flat where it leaves the arm, drawn to a point.
    cylinder(add(wrist, across, -0.0105), add(wrist, across, 0.0105), 0.0072, 10, "steel")
    cylinder(add(wrist, across, 0.0105), add(wrist, across, -0.0105), 0.0072, 10, "steel")
    shoulder = lerp3(wrist, tip, 0.26)
    beam(wrist, shoulder, radial, (0.0090, 0.0225), (0.0050, 0.0072))
    beam(shoulder, tip, radial, (0.0225, 0.0030), (0.0072, 0.0020))

    # The cable leaves the hub, bows out, and goes in at the shell's tab.
    tab = around(tab_angle, tab_r, tab_y)
    out = (math.sin(tab_angle), 0, math.cos(tab_angle))
    tube([add(hub, radial, 0.012),
          add(around(angle, hub_r + 0.04, hub_y - 0.03), across, sway * 0.016),
          add(around(lerp(angle, tab_angle, 0.4), hub_r + 0.040, lerp(hub_y, tab_y, 0.4)), across, sway * 0.022),
          add(around(lerp(angle, tab_angle, 0.8), tab_r + 0.024, lerp(hub_y, tab_y, 0.82)), out, 0.0),
          tab], 0.0052)
    beam(add(tab, out, 0.002), (tab[0] - out[0] * 0.012, tab[1] - 0.034, tab[2] - out[2] * 0.012),
         across, (0.012, 0.015), (0.0085, 0.010), "white")
    cylinder(add(tab, (0, 1, 0), -0.002), add(tab, (0, 1, 0), 0.008), 0.0072, 8, "steel")


def lerp3(a, b, t):
    return (lerp(a[0], b[0], t), lerp(a[1], b[1], t), lerp(a[2], b[2], t))


def build_prongs():
    prong(0.0, 0.112, 0.215, 0.200, 0.280, 0.222, 0.455, 0.172, 0.605, 0.20, 0.128, -0.052, 1)
    for sign in (-1, 1):
        prong(sign * math.radians(121), 0.124, 0.125, 0.222, 0.225, 0.246, 0.435, 0.196, 0.590,
              sign * math.radians(101), 0.140, -0.188, -sign)


def build_details():
    # The vent on the shell's crown.
    centre = shell_point(0.0, 0.62)

    def vent(u, v):
        a = TAU * u
        r = 0.026 * math.sin(v * math.pi / 2)
        lift = 0.0085 * math.cos(v * math.pi / 2) - 0.002
        return (centre[0] + r * math.sin(a), centre[1] + 1.6 * r * math.cos(a), centre[2] + lift), cell("vent")

    surface(vent, 20, 6, lambda p: (0, 0.1 * (p[1] - centre[1]), 1), wrap=True)


build_shell()
build_chin()
build_barrel()
build_prongs()
build_details()


def write_glb(path):
    def pack(fmt, rows):
        return b"".join(struct.pack(fmt, *row) for row in rows)

    blobs, views, accessors, primitives, offset = [], [], [], [], 0

    def blob(data, kind, component, count, bounds=None):
        nonlocal offset
        views.append({"buffer": 0, "byteOffset": offset, "byteLength": len(data)})
        accessor = {"bufferView": len(views) - 1, "componentType": component, "count": count, "type": kind}
        if bounds:
            accessor.update(bounds)
        accessors.append(accessor)
        blobs.append(data + b"\0" * (-len(data) % 4))
        offset += (len(data) + 3) & ~3
        return len(accessors) - 1

    for material, (rows, indices) in enumerate(meshes):
        p = [r[0] for r in rows]
        attributes = {
            "POSITION": blob(pack("<3f", p), "VEC3", 5126, len(p),
                             {"min": [min(q[i] for q in p) for i in range(3)],
                              "max": [max(q[i] for q in p) for i in range(3)]}),
            "NORMAL": blob(pack("<3f", [r[1] for r in rows]), "VEC3", 5126, len(p)),
            "TEXCOORD_0": blob(pack("<2f", [r[2] for r in rows]), "VEC2", 5126, len(p))}
        primitives.append({"attributes": attributes, "material": material,
                           "indices": blob(b"".join(struct.pack("<H", i) for i in indices), "SCALAR", 5123,
                                           len(indices))})
    data = b"".join(blobs)
    doc = {"asset": {"version": "2.0", "generator": "portal_gun make_model.py"}, "scene": 0,
           "scenes": [{"nodes": [0]}], "nodes": [{"mesh": 0, "name": "PortalGun"}],
           "meshes": [{"primitives": primitives}],
           "materials": [{"name": "PortalGun"}, {"name": "PortalGunGlow"}], "accessors": accessors,
           "bufferViews": views, "buffers": [{"byteLength": len(data)}]}
    text = json.dumps(doc, separators=(",", ":")).encode()
    text += b" " * (-len(text) % 4)
    with open(path, "wb") as out:
        out.write(struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(text) + 8 + len(data)))
        out.write(struct.pack("<I4s", len(text), b"JSON") + text)
        out.write(struct.pack("<I4s", len(data), b"BIN\0") + data)


# ---- The textures.

SIZE = 1024


def box(rect):
    return (int(rect[0] * SIZE), int(rect[1] * SIZE), int(rect[2] * SIZE), int(rect[3] * SIZE))


def speckle(size, seed, low, high, blur):
    rng = random.Random(seed)
    image = Image.new("L", size)
    image.putdata([rng.randint(low, high) for _ in range(size[0] * size[1])])
    return image.filter(ImageFilter.GaussianBlur(blur))


def white_part(rect, seed, paint):
    """A panel of painted white plastic: its wear, then what paint() marks on it."""
    x0, y0, x1, y1 = box(rect)
    w, h = x1 - x0, y1 - y0
    rng = random.Random(seed)
    # Soft mottling, read as the slight unevenness of a moulded part.
    mottle = speckle((w // 4, h // 4), seed, 0, 255, 3).resize((w, h), Image.BICUBIC)
    base = Image.new("RGB", (w, h))
    px = []
    for value in mottle.tobytes():
        shade = 233 + (value - 128) * 0.14
        px.append((int(shade), int(shade * 0.994), int(shade * 0.968)))
    base.putdata(px)
    draw = ImageDraw.Draw(base, "RGBA")
    paint(draw, w, h, rng)
    # Fine scuffs.
    for _ in range(60):
        x, y = rng.randint(0, w), rng.randint(0, h)
        angle = rng.uniform(0, math.tau)
        length = rng.uniform(6, 34)
        draw.line((x, y, x + math.cos(angle) * length, y + math.sin(angle) * length),
                  fill=(90, 90, 92, rng.randint(14, 42)), width=1)
    for _ in range(18):
        x, y, r = rng.randint(0, w), rng.randint(0, h), rng.uniform(1, 3.5)
        draw.ellipse((x - r, y - r, x + r, y + r), fill=(70, 70, 72, rng.randint(16, 46)))
    return base.filter(ImageFilter.GaussianBlur(0.45))


def paint_shell(draw, w, h, rng):
    line = (58, 60, 66, 235)
    # Wear gathers toward the open edge.
    for row in range(int(h * 0.72), h):
        t = (row - h * 0.72) / (h * 0.28)
        draw.line((0, row, w, row), fill=(52, 50, 48, int(t * t * 120)))
    # The two halves meet down the crown and round the underside.
    draw.line((w * 0.5, h * 0.06, w * 0.5, h * 0.60), fill=line, width=2)
    draw.line((0, h * 0.62, w, h * 0.62), fill=(70, 72, 78, 120), width=1)
    # The rear cap's seam, and the screws along it.
    draw.line((0, h * 0.36, w, h * 0.36), fill=line, width=3)
    for k in range(6):
        x, y = w * (k + 0.5) / 6, h * 0.39
        draw.ellipse((x - 5, y - 5, x + 5, y + 5), fill=(120, 122, 126, 255), outline=(50, 50, 55, 255))
        draw.line((x - 3, y, x + 3, y), fill=(40, 40, 44, 255), width=1)
    # A hatch and its stencil on each flank.
    for centre in (0.24, 0.76):
        x, y = w * centre, h * 0.73
        draw.rounded_rectangle((x - 40, y - 26, x + 40, y + 26), 8, outline=(62, 64, 70, 225), width=2)
        for k in range(4):
            draw.rectangle((x - 28, y - 14 + k * 8, x + 28, y - 10 + k * 8), fill=(70, 72, 78, 215))
    # The cooling slots, either side of the crown.
    for side in (-1, 1):
        for k in range(5):
            x = w * (0.5 + side * (0.075 + k * 0.016))
            draw.rounded_rectangle((x - 3, h * 0.46, x + 3, h * 0.56), 3, fill=(36, 37, 42, 240))


def paint_chin(draw, w, h, rng):
    line = (58, 60, 66, 225)
    # The chin's two halves join under the barrel.
    draw.line((w * 0.5, 0, w * 0.5, h), fill=line, width=2)
    # Wear along the open edges, which is where a hand and a holster meet it.
    for col in range(w):
        edge = abs(col - w * 0.5) / (w * 0.5)
        if edge > 0.72:
            t = (edge - 0.72) / 0.28
            draw.line((col, 0, col, h), fill=(52, 50, 48, int(t * t * 90)))
    # Panel lines swept toward the front, and a plain stamped ring on each side.
    draw.arc((w * 0.05, h * 0.2, w * 0.95, h * 1.4), 200, 340, fill=(70, 72, 78, 170), width=2)
    for centre in (0.17, 0.83):
        x, y = w * centre, h * 0.26
        draw.ellipse((x - 19, y - 19, x + 19, y + 19), outline=(52, 54, 60, 230), width=4)
        draw.ellipse((x - 7, y - 7, x + 7, y + 7), fill=(52, 54, 60, 230))


def write_textures(folder):
    diffuse = Image.new("RGB", (SIZE, SIZE), (30, 31, 35))
    shell = white_part(SHELL, 5, paint_shell)
    chin = white_part(CHIN, 9, paint_chin)
    diffuse.paste(shell, box(SHELL)[:2])
    diffuse.paste(chin, box(CHIN)[:2])

    # Machined black: dark bands round the barrel, with the sheen of its grain.
    x0, y0, x1, y1 = box(METAL)
    grain = speckle((x1 - x0, 1), 3, 0, 255, 0).resize((x1 - x0, y1 - y0), Image.NEAREST)
    metal = Image.new("RGB", (x1 - x0, y1 - y0))
    px = []
    for i, value in enumerate(grain.tobytes()):
        row = i // (x1 - x0)
        v = 30 + (value - 128) * 0.03 + 8 * math.sin(row / (y1 - y0) * math.pi)
        px.append((int(v), int(v * 1.03), int(v * 1.12)))
    metal.putdata(px)
    diffuse.paste(metal, (x0, y0))

    x0, y0, x1, y1 = box(LENS)
    ImageDraw.Draw(diffuse).rectangle((x0, y0, x1, y1), fill=(24, 26, 32))

    # The flat cells.
    draw = ImageDraw.Draw(diffuse)
    width = SIZE / len(NAMES)
    for k, name in enumerate(NAMES):
        draw.rectangle((k * width, SIZE * 0.75, (k + 1) * width, SIZE), fill=CELLS[name])
    diffuse.save(folder + "/portal_gun.tex.png")

    # Specular: colour in the RGB, the tightness of the highlight in the alpha.
    spec = Image.new("RGBA", (SIZE, SIZE), (64, 66, 72, 110))
    d = ImageDraw.Draw(spec)
    for rect in (SHELL, CHIN):
        d.rectangle(box(rect), fill=(150, 150, 150, 190))
    d.rectangle(box(METAL), fill=(90, 92, 100, 130))
    for k, name in enumerate(NAMES):
        value = {"white": (150, 150, 150, 190), "steel": (150, 152, 160, 170), "rubber": (24, 24, 24, 50),
                 "inner": (20, 20, 22, 40)}.get(name, (64, 66, 72, 110))
        d.rectangle((k * width, SIZE * 0.75, (k + 1) * width, SIZE), fill=value)
    # Dark marks don't shine like the plastic does.
    for rect, part in ((SHELL, shell), (CHIN, chin)):
        x0, y0, x1, y1 = box(rect)
        mask = part.convert("L").point(lambda v: 255 if v < 150 else 0).filter(ImageFilter.GaussianBlur(0.6))
        spec.paste((30, 30, 32, 60), (x0, y0), mask)
    spec.save(folder + "/portal_gun_spec.tex.png")

    # What glows when nothing has coloured it.
    glow = Image.new("RGB", (SIZE, SIZE), (0, 0, 0))
    d = ImageDraw.Draw(glow)
    for name, colour in (("rim", (70, 150, 255)), ("glow", (120, 200, 255))):
        k = NAMES.index(name)
        d.rectangle((k * width, SIZE * 0.75, (k + 1) * width, SIZE), fill=colour)
    glow.save(folder + "/portal_gun_glow.tex.png")


write_glb(sys.argv[1] + "/portal_gun.glb")
write_textures(sys.argv[2])
counts = [(len(m[0]), len(m[1]) // 3) for m in meshes]
print(sum(c[0] for c in counts), "vertices,", sum(c[1] for c in counts), "triangles", counts)
