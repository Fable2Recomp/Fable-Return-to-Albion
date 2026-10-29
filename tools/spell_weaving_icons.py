#!/usr/bin/env python3
"""Draws Spell Weaving's ability card for the abilities book: two interlaced rings, one Will
blue and one Skill gold, on the same aged parchment Souls Combat's Stamina card uses.

    tools/spell_weaving_icons.py

Writes Fable2/mods-src/spell_weaving/art/gui/abilitytarot/spell_weaving.tex.png, which a mod's mount
converts to the `.tex` the page loads (Engine_ModImageOverlays). Our own art: nothing here is
taken from the game.
"""

import math
import os
import random
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter

WIDTH, HEIGHT = 512, 384
SCALE = 4


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def parchment():
    random.seed(11)
    paper = Image.new("RGB", (WIDTH, HEIGHT))
    px = paper.load()
    for y in range(HEIGHT):
        for x in range(WIDTH):
            dx, dy = (x - WIDTH / 2) / (WIDTH / 2), (y - HEIGHT / 2) / (HEIGHT / 2)
            edge = min(1.0, (dx * dx + dy * dy) ** 0.5)
            grain = random.uniform(-10, 10)
            base = lerp((222, 203, 160), (120, 88, 52), edge ** 2.2)
            px[x, y] = tuple(max(0, min(255, int(c + grain))) for c in base)
    paper = paper.filter(ImageFilter.GaussianBlur(0.8))
    draw = ImageDraw.Draw(paper)
    ink = (70, 46, 24)
    for inset, w in ((10, 3), (20, 1)):
        draw.rectangle((inset, inset, WIDTH - 1 - inset, HEIGHT - 1 - inset), outline=ink, width=w)
    for cx, cy in ((20, 20), (WIDTH - 21, 20), (20, HEIGHT - 21), (WIDTH - 21, HEIGHT - 21)):
        draw.ellipse((cx - 7, cy - 7, cx + 7, cy + 7), outline=ink, width=2)
    return paper.convert("RGBA")


def ring(cx, cy, radius, thickness, light, dark, edge):
    """A glossy torus seen face-on, drawn at 4x and reduced."""
    big = (WIDTH * SCALE, HEIGHT * SCALE)
    cx, cy, radius, thickness = cx * SCALE, cy * SCALE, radius * SCALE, thickness * SCALE
    mask = Image.new("L", big, 0)
    d = ImageDraw.Draw(mask)
    d.ellipse((cx - radius, cy - radius, cx + radius, cy + radius), fill=255)
    inner = radius - thickness
    d.ellipse((cx - inner, cy - inner, cx + inner, cy + inner), fill=0)

    fill = Image.new("RGB", big)
    fp = fill.load()
    x0, y0, x1, y1 = int(cx - radius), int(cy - radius), int(cx + radius) + 1, int(cy + radius) + 1
    for y in range(max(0, y0), min(big[1], y1)):
        for x in range(max(0, x0), min(big[0], x1)):
            t = min(1.0, max(0.0, ((x - x0 + y - y0) / (4 * radius) - 0.2) / 0.6))
            fp[x, y] = lerp(light, dark, t)
    rim = ImageChops.subtract(mask, mask.filter(ImageFilter.GaussianBlur(thickness * 0.35)))
    fill = Image.composite(Image.new("RGB", big, dark), fill, rim.point(lambda v: min(255, v * 2)))
    body = Image.new("RGBA", big, (0, 0, 0, 0))
    body.paste(fill, (0, 0), mask)

    grown = mask.filter(ImageFilter.MaxFilter(2 * (SCALE * 2) + 1))
    outline = Image.new("RGBA", big, edge + (255,))
    outline.putalpha(grown)

    gloss = Image.new("L", big, 0)
    ImageDraw.Draw(gloss).ellipse(
        (cx - radius * 0.75, cy - radius * 0.95, cx - radius * 0.1, cy - radius * 0.45), fill=170)
    gloss = ImageChops.multiply(gloss.filter(ImageFilter.GaussianBlur(thickness * 0.3)), mask)
    white = Image.new("RGBA", big, (255, 255, 255, 0))
    white.putalpha(gloss)

    shadow = Image.new("RGBA", big, (0, 0, 0, 0))
    shadow_mask = grown.filter(ImageFilter.GaussianBlur(thickness * 0.25)).point(lambda v: v * 0.5)
    shadow.paste((0, 0, 0, 255), (SCALE * 3, SCALE * 4), shadow_mask)

    out = Image.alpha_composite(shadow, outline)
    out = Image.alpha_composite(out, body)
    out = Image.alpha_composite(out, white)
    return out.resize((WIDTH, HEIGHT), Image.LANCZOS), mask.resize((WIDTH, HEIGHT), Image.LANCZOS)


def card():
    out = parchment()

    glow = Image.new("L", (WIDTH, HEIGHT), 0)
    g = ImageDraw.Draw(glow)
    g.ellipse((WIDTH / 2 - 190, HEIGHT / 2 - 120, WIDTH / 2 - 10, HEIGHT / 2 + 120), fill=140)
    glow = glow.filter(ImageFilter.GaussianBlur(45))
    blue = Image.new("RGBA", (WIDTH, HEIGHT), (90, 140, 255, 0))
    blue.putalpha(glow)
    out = Image.alpha_composite(out, blue)
    glow = Image.new("L", (WIDTH, HEIGHT), 0)
    g = ImageDraw.Draw(glow)
    g.ellipse((WIDTH / 2 + 10, HEIGHT / 2 - 120, WIDTH / 2 + 190, HEIGHT / 2 + 120), fill=140)
    glow = glow.filter(ImageFilter.GaussianBlur(45))
    gold = Image.new("RGBA", (WIDTH, HEIGHT), (255, 200, 80, 0))
    gold.putalpha(glow)
    out = Image.alpha_composite(out, gold)

    # Two rings interlaced: the blue passes over the gold on the left crossing and under it on
    # the right, which is what makes them read as woven rather than overlapping.
    r, th = 92, 26
    bx, gx, cy = WIDTH / 2 - 48, WIDTH / 2 + 48, HEIGHT / 2 + 4
    blue_ring, blue_mask = ring(bx, cy, r, th, (150, 200, 255), (30, 60, 170), (10, 20, 70))
    gold_ring, gold_mask = ring(gx, cy, r, th, (255, 235, 160), (170, 110, 20), (80, 50, 8))

    out = Image.alpha_composite(out, gold_ring)
    out = Image.alpha_composite(out, blue_ring)
    # Lift the gold back over the blue on the lower crossing.
    lower = Image.new("L", (WIDTH, HEIGHT), 0)
    ImageDraw.Draw(lower).rectangle((0, cy, WIDTH, HEIGHT), fill=255)
    cross = ImageChops.multiply(ImageChops.multiply(blue_mask, gold_mask), lower)
    cross = cross.filter(ImageFilter.MaxFilter(5))
    patch = gold_ring.copy()
    patch.putalpha(ImageChops.multiply(patch.getchannel("A"), cross))
    out = Image.alpha_composite(out, patch)
    return out.convert("RGB")


def slot_frame():
    """The partner slot's frame for the spell selectors: a thin glossy ring with a faint cross at
    its centre, on a transparent ground — read as "a slot with nothing in it". 128 px, like the
    will icons it sits beside."""
    size, scale = 128, 4
    big = size * scale
    ring = Image.new("L", (big, big), 0)
    d = ImageDraw.Draw(ring)
    d.ellipse((big * 0.08, big * 0.08, big * 0.92, big * 0.92), fill=255)
    d.ellipse((big * 0.20, big * 0.20, big * 0.80, big * 0.80), fill=0)
    cross = Image.new("L", (big, big), 0)
    c = ImageDraw.Draw(cross)
    w = big * 0.045
    c.rectangle((big * 0.5 - w, big * 0.36, big * 0.5 + w, big * 0.64), fill=120)
    c.rectangle((big * 0.36, big * 0.5 - w, big * 0.64, big * 0.5 + w), fill=120)

    fill = Image.new("RGB", (big, big))
    px = fill.load()
    for y in range(big):
        for x in range(big):
            t = min(1.0, max(0.0, ((x + y) / (2 * big) - 0.2) / 0.6))
            px[x, y] = lerp((235, 225, 200), (120, 100, 70), t)
    body = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    body.paste(fill, (0, 0), ring)
    grown = ring.filter(ImageFilter.MaxFilter(2 * (big // 64) + 1))
    outline = Image.new("RGBA", (big, big), (40, 28, 14, 255))
    outline.putalpha(grown)
    gloss = Image.new("L", (big, big), 0)
    ImageDraw.Draw(gloss).ellipse((big * 0.18, big * 0.10, big * 0.55, big * 0.38), fill=150)
    gloss = ImageChops.multiply(gloss.filter(ImageFilter.GaussianBlur(big * 0.03)), ring)
    white = Image.new("RGBA", (big, big), (255, 255, 255, 0))
    white.putalpha(gloss)
    plus = Image.new("RGBA", (big, big), (235, 225, 200, 0))
    plus.putalpha(cross)
    out = Image.alpha_composite(outline, body)
    out = Image.alpha_composite(out, white)
    out = Image.alpha_composite(out, plus)
    return out.resize((size, size), Image.LANCZOS)


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    tarot = os.path.join(root, "Fable2", "mods-src", "spell_weaving", "art", "gui", "abilitytarot")
    os.makedirs(tarot, exist_ok=True)
    path = os.path.join(tarot, "spell_weaving.tex.png")
    card().save(path)
    print(path)
    hud = os.path.join(root, "Fable2", "mods-src", "spell_weaving", "art", "gui", "hud")
    os.makedirs(hud, exist_ok=True)
    path = os.path.join(hud, "spell_weaving_slot.tex.png")
    slot_frame().save(path)
    print(path)


if __name__ == "__main__":
    sys.exit(main())
