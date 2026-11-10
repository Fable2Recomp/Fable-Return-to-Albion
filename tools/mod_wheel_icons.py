#!/usr/bin/env python3
"""Draws the bundled mods' icons for the controller's mod wheel.

    tools/mod_wheel_icons.py [mods_dir [core_dir]]   (defaults: Fable2/mods-src, Godot/assets/ui/wheel)

A plain symbol on a transparent ground: a fill shading from a light top to a darker bottom, a dark
outline and a soft shadow, drawn at 4x and reduced so the edges are clean at 128 px. Written to each
mod's art/gui/wheel/, where its input.json's "wheel_icon" names it.

Our own art: nothing here is taken from the game.
"""

import math
import os
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter

SIZE = 128
SCALE = 4
BIG = SIZE * SCALE
GOLD = ((255, 236, 176), (196, 142, 62))
BLUE = ((150, 214, 255), (32, 104, 214))
ORANGE = ((255, 206, 128), (226, 104, 26))


def px(value):
    return int(round(value * SCALE))


def blank():
    return Image.new("L", (BIG, BIG), 0)


def filled(mask, colours):
    """The mask filled with a top-to-bottom gradient, as RGBA."""
    top, bottom = colours
    ramp = Image.new("RGB", (1, BIG))
    for y in range(BIG):
        t = y / (BIG - 1)
        ramp.putpixel((0, y), tuple(int(a + (b - a) * t) for a, b in zip(top, bottom)))
    art = ramp.resize((BIG, BIG)).convert("RGBA")
    art.putalpha(mask)
    return art


def outlined(mask):
    """The outline and shadow that stand under a symbol."""
    grown = mask.filter(ImageFilter.MaxFilter(px(1.5) * 2 + 1))
    edge = Image.new("RGBA", (BIG, BIG), (30, 20, 10, 255))
    edge.putalpha(grown)
    soft = ImageChops.offset(grown, px(1), px(2)).filter(ImageFilter.GaussianBlur(px(2)))
    shadow = Image.new("RGBA", (BIG, BIG), (0, 0, 0, 255))
    shadow.putalpha(soft.point(lambda v: int(v * 0.55)))
    return Image.alpha_composite(shadow, edge)


def finished(layers):
    """Layers of (mask, colours), bottom first, each with its own outline; reduced to SIZE."""
    art = Image.new("RGBA", (BIG, BIG), (0, 0, 0, 0))
    for mask, colours in layers:
        art = Image.alpha_composite(art, outlined(mask))
        art = Image.alpha_composite(art, filled(mask, colours))
    return art.resize((SIZE, SIZE), Image.LANCZOS)


def box(draw, x0, y0, x1, y1, radius, fill=255):
    draw.rounded_rectangle((px(x0), px(y0), px(x1), px(y1)), radius=px(radius), fill=fill)


def disk():
    """A floppy disk: quicksave."""
    mask = blank()
    draw = ImageDraw.Draw(mask)
    box(draw, 22, 22, 106, 106, 7)
    draw.polygon([(px(92), px(18)), (px(110), px(18)), (px(110), px(36))], fill=0)
    box(draw, 42, 26, 80, 50, 2, 0)
    box(draw, 66, 30, 75, 46, 1)
    box(draw, 36, 66, 92, 102, 2, 0)
    return finished([(mask, GOLD)])


def reload_arrow():
    """A circular arrow: quickload."""
    mask = blank()
    draw = ImageDraw.Draw(mask)
    centre, outer, inner = 64, 42, 27
    draw.ellipse((px(centre - outer), px(centre - outer), px(centre + outer), px(centre + outer)), fill=255)
    draw.ellipse((px(centre - inner), px(centre - inner), px(centre + inner), px(centre + inner)), fill=0)
    draw.polygon([(px(64), px(64)), (px(116), px(14)), (px(124), px(62))], fill=0)
    angle = math.radians(-44)
    mid = (outer + inner) / 2
    tip_x, tip_y = centre + mid * math.cos(angle), centre + mid * math.sin(angle)
    draw.polygon([(px(tip_x - 25), px(tip_y - 19)), (px(tip_x + 21), px(tip_y - 25)), (px(tip_x + 11), px(tip_y + 22))], fill=255)
    return finished([(mask, GOLD)])


def skateboard():
    """A skateboard from the side."""
    mask = blank()
    draw = ImageDraw.Draw(mask)
    draw.line([(px(12), px(50)), (px(28), px(64)), (px(100), px(64)), (px(116), px(50))], fill=255, width=px(11), joint="curve")
    for x in (12, 116):
        draw.ellipse((px(x - 5.5), px(44.5), px(x + 5.5), px(55.5)), fill=255)
    for x in (40, 88):
        box(draw, x - 5, 66, x + 5, 78, 1)
        draw.ellipse((px(x - 13), px(72), px(x + 13), px(98)), fill=255)
        draw.ellipse((px(x - 4), px(81), px(x + 4), px(89)), fill=0)
    return finished([(mask, GOLD)])


def compass():
    """A round map with a north needle."""
    mask = blank()
    draw = ImageDraw.Draw(mask)
    draw.ellipse((px(20), px(20), px(108), px(108)), fill=255)
    draw.ellipse((px(30), px(30), px(98), px(98)), fill=0)
    needle = blank()
    pen = ImageDraw.Draw(needle)
    pen.polygon([(px(64), px(32)), (px(77), px(64)), (px(64), px(96)), (px(51), px(64))], fill=255)
    return finished([(mask, GOLD), (needle, ORANGE)])


def portals():
    """A blue portal and an orange one."""
    def ring(cx):
        mask = blank()
        draw = ImageDraw.Draw(mask)
        draw.ellipse((px(cx - 25), px(18), px(cx + 25), px(110)), fill=255)
        draw.ellipse((px(cx - 13), px(32), px(cx + 13), px(96)), fill=0)
        return mask
    return finished([(ring(42), BLUE), (ring(86), ORANGE)])


def satchel():
    """A satchel with a strap and a buckle."""
    strap = blank()
    draw = ImageDraw.Draw(strap)
    draw.arc((px(36), px(10), px(92), px(70)), 180, 360, fill=255, width=px(8))
    bag = blank()
    draw = ImageDraw.Draw(bag)
    box(draw, 20, 42, 108, 112, 13)
    flap = blank()
    draw = ImageDraw.Draw(flap)
    box(draw, 16, 38, 112, 76, 11)
    buckle = blank()
    draw = ImageDraw.Draw(buckle)
    box(draw, 55, 62, 73, 86, 3)
    draw.rectangle((px(61), px(69), px(67), px(79)), fill=0)
    dark = ((168, 112, 60), (104, 62, 30))
    return finished([(strap, dark), (bag, dark), (flap, GOLD), (buckle, ORANGE)])


def camera():
    """A camera: photo mode, the core's entry on the wheel's top wedge."""
    body = blank()
    draw = ImageDraw.Draw(body)
    box(draw, 14, 38, 114, 104, 10)
    draw.polygon([(px(42), px(40)), (px(50), px(24)), (px(78), px(24)), (px(86), px(40))], fill=255)
    box(draw, 22, 30, 36, 40, 2)
    centre_x, centre_y, outer, inner = 64, 71, 26, 18
    draw.ellipse((px(centre_x - outer), px(centre_y - outer), px(centre_x + outer), px(centre_y + outer)), fill=0)
    lens = blank()
    lens_draw = ImageDraw.Draw(lens)
    lens_draw.ellipse((px(centre_x - inner), px(centre_y - inner), px(centre_x + inner), px(centre_y + inner)), fill=255)
    lens_draw.ellipse((px(centre_x - 9), px(centre_y - 13), px(centre_x - 1), px(centre_y - 5)), fill=0)
    flash = blank()
    ImageDraw.Draw(flash).ellipse((px(92), px(46), px(104), px(58)), fill=255)
    return finished([(body, GOLD), (lens, BLUE), (flash, ORANGE)])


# The core's entries, written into the Godot project (res://assets/ui/wheel/).
CORE_ICONS = [
    ("photo_mode", camera),
]

ICONS = [
    ("quicksave", "quicksave", disk),
    ("quicksave", "quickload", reload_arrow),
    ("skate", "skateboard", skateboard),
    ("minimap", "map", compass),
    ("portal_gun", "portal", portals),
    ("satchel", "satchel", satchel),
]


def main():
    mods = sys.argv[1] if len(sys.argv) > 1 else "Fable2/mods-src"
    core = sys.argv[2] if len(sys.argv) > 2 else "Godot/assets/ui/wheel"
    os.makedirs(core, exist_ok=True)
    for name, draw in CORE_ICONS:
        draw().save(os.path.join(core, name + ".png"))
        print(os.path.join(core, name + ".png"))
    for mod, name, draw in ICONS:
        folder = os.path.join(mods, mod, "art", "gui", "wheel")
        os.makedirs(folder, exist_ok=True)
        draw().save(os.path.join(folder, name + ".png"))
        print(os.path.join(mod, "art", "gui", "wheel", name + ".png"))


if __name__ == "__main__":
    main()
