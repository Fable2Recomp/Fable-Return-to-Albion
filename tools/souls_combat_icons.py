#!/usr/bin/env python3
"""Draws Souls Combat's two HUD orb icons, in the style of Fable 2's health heart, and the
Stamina ability's card for the abilities book.

    tools/souls_combat_icons.py [out_dir]   (default: mods-src/souls_combat/art/gui/hud)

A glossy symbol on a transparent ground, as the heart is: a saturated fill shading from a light
upper left to a dark lower right, a dark outline, a white specular highlight and a soft shadow.
Drawn at 4x and reduced, so the edges are clean at 128 px. Written as `<name>.tex.png`, which a
mod's mount converts to the `<name>.tex` the HUD loads (Engine_ModImageOverlays).

Our own art: nothing here is taken from the game.
"""

import math
import os
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter

SIZE = 128
SCALE = 4
BIG = SIZE * SCALE


def drop_shape():
    """A teardrop, point up — mana."""
    points = []
    for i in range(240):
        t = 2 * math.pi * i / 240
        x = math.sin(t) * math.sin(t / 2) ** 1.15
        y = -math.cos(t)
        points.append((BIG * (0.5 + 0.36 * x), BIG * (0.55 + 0.36 * y)))
    return points


def bolt_shape():
    """A lightning bolt — stamina."""
    outline = [(0.58, 0.08), (0.24, 0.55), (0.47, 0.55), (0.36, 0.92), (0.78, 0.40),
               (0.54, 0.40), (0.70, 0.08)]
    return [(BIG * x, BIG * y) for x, y in outline]


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def icon(shape, light, dark, edge):
    mask = Image.new("L", (BIG, BIG), 0)
    ImageDraw.Draw(mask).polygon(shape, fill=255)

    # The fill: light at the upper left, dark at the lower right.
    fill = Image.new("RGB", (BIG, BIG))
    pixels = fill.load()
    for y in range(BIG):
        for x in range(BIG):
            t = min(1.0, max(0.0, ((x + y) / (2 * BIG) - 0.2) / 0.6))
            pixels[x, y] = lerp(light, dark, t)

    # Shade toward the rim, so the symbol reads as rounded.
    rim = ImageChops.subtract(mask, mask.filter(ImageFilter.GaussianBlur(BIG * 0.05)))
    shade = Image.new("RGB", (BIG, BIG), dark)
    fill = Image.composite(shade, fill, rim.point(lambda v: min(255, v * 2)))

    body = Image.new("RGBA", (BIG, BIG), (0, 0, 0, 0))
    body.paste(fill, (0, 0), mask)

    # The outline: the shape grown a little, under the body.
    grown = mask.filter(ImageFilter.MaxFilter(2 * (BIG // 64) + 1))
    outline = Image.new("RGBA", (BIG, BIG), edge + (255,))
    outline.putalpha(grown)

    # The specular highlight: a soft white ellipse at the upper left, inside the shape.
    gloss = Image.new("L", (BIG, BIG), 0)
    ImageDraw.Draw(gloss).ellipse(
        (BIG * 0.30, BIG * 0.22, BIG * 0.52, BIG * 0.44), fill=170)
    gloss = ImageChops.multiply(gloss.filter(ImageFilter.GaussianBlur(BIG * 0.03)), mask)
    white = Image.new("RGBA", (BIG, BIG), (255, 255, 255, 0))
    white.putalpha(gloss)

    # The shadow: the outline's silhouette, blurred and dropped down-right.
    shadow = Image.new("RGBA", (BIG, BIG), (0, 0, 0, 0))
    shadow_mask = grown.filter(ImageFilter.GaussianBlur(BIG * 0.025)).point(lambda v: v * 0.55)
    shadow.paste((0, 0, 0, 255), (BIG // 40, BIG // 32), shadow_mask)

    out = Image.alpha_composite(shadow, outline)
    out = Image.alpha_composite(out, body)
    out = Image.alpha_composite(out, white)
    return out.resize((SIZE, SIZE), Image.LANCZOS)


def card():
    """The Stamina ability's still for the abilities book's preview frame: aged parchment, an
    inked double border, and the stamina bolt glowing at its centre."""
    import random
    width, height = 512, 384
    random.seed(7)
    paper = Image.new("RGB", (width, height))
    px = paper.load()
    for y in range(height):
        for x in range(width):
            dx, dy = (x - width / 2) / (width / 2), (y - height / 2) / (height / 2)
            edge = min(1.0, (dx * dx + dy * dy) ** 0.5)
            grain = random.uniform(-10, 10)
            t = edge ** 2.2
            base = lerp((222, 203, 160), (120, 88, 52), t)
            px[x, y] = tuple(max(0, min(255, int(c + grain))) for c in base)
    paper = paper.filter(ImageFilter.GaussianBlur(0.8))

    draw = ImageDraw.Draw(paper)
    ink = (70, 46, 24)
    for inset, w in ((10, 3), (20, 1)):
        draw.rectangle((inset, inset, width - 1 - inset, height - 1 - inset), outline=ink, width=w)
    for cx, cy in ((20, 20), (width - 21, 20), (20, height - 21), (width - 21, height - 21)):
        draw.ellipse((cx - 7, cy - 7, cx + 7, cy + 7), outline=ink, width=2)

    out = paper.convert("RGBA")
    glow = Image.new("L", (width, height), 0)
    ImageDraw.Draw(glow).ellipse((width / 2 - 120, height / 2 - 120, width / 2 + 120, height / 2 + 120), fill=150)
    glow = glow.filter(ImageFilter.GaussianBlur(40))
    green = Image.new("RGBA", (width, height), (120, 230, 90, 0))
    green.putalpha(glow)
    out = Image.alpha_composite(out, green)

    bolt = icon(bolt_shape(), (190, 255, 130), (26, 120, 30), (10, 48, 12)).resize((280, 280), Image.LANCZOS)
    out.alpha_composite(bolt, ((width - 280) // 2, (height - 280) // 2 + 6))
    return out.convert("RGB")


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        root, "mods-src", "souls_combat", "art", "gui", "hud")
    os.makedirs(out_dir, exist_ok=True)

    icons = {
        "souls_combat_mana": icon(drop_shape(), (150, 215, 255), (12, 52, 160), (6, 18, 64)),
        "souls_combat_stamina": icon(bolt_shape(), (190, 255, 130), (26, 120, 30), (10, 48, 12)),
    }
    for name, image in icons.items():
        path = os.path.join(out_dir, name + ".tex.png")
        image.save(path)
        print("wrote", path)

    tarot = os.path.join(root, "mods-src", "souls_combat", "art", "gui", "abilitytarot")
    os.makedirs(tarot, exist_ok=True)
    path = os.path.join(tarot, "souls_combat_stamina.tex.png")
    card().save(path)
    print("wrote", path)


if __name__ == "__main__":
    main()
