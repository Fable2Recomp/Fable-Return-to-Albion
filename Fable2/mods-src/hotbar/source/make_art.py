#!/usr/bin/env python3
"""Writes the bar's look, after Fable Anniversary's: touching gold rings around dark discs, a pointed
end each side and each key's number in a small capsule on top of its ring. The art goes to
art/gui/hud and the pieces that place it to ui/hud.json, in 720p units around the bottom centre.

    make_art.py <the mod's folder> <fable2.ttf>
"""
import json, math, os, sys
from PIL import Image, ImageDraw, ImageFont

SCALE = 4                               # texture pixels to a 720p unit: sharp at twice 720p
AA = 4                                  # drawn this much larger, then reduced

ORB = 44.0                              # a ring's width
RADIUS = ORB * 0.5
PITCH = ORB * 0.975                     # slot to slot: the rings just touch
SPLIT = ORB * 0.22                      # the gap after 3: 1-3 are the game's modes
MID = -46.0                             # the rings' centre, above the bottom of the screen
ICON = RADIUS * 1.4
CAPSULE = (ORB * 0.5, ORB * 0.37)
BADGE = (ORB * 0.46, ORB * 0.3)
DIGIT_BOX = (ORB * 0.75, ORB * 0.6)
DIGIT_SIZE = ORB * 0.52
TOUCH_SCALE = 1.6                       # the bar's size on a touchscreen, over the setting's

GOLD = (0.76, 0.65, 0.4)
GOLD_BRIGHT = (0.98, 0.93, 0.72)
GOLD_DARK = (0.3, 0.22, 0.1)
INSIDE = (0.17, 0.1, 0.05)
SHADOW = (0.04, 0.03, 0.02, 0.85)
LIGHT = (1.0, 0.78, 0.38, 0.22)


def rgba(colour, alpha=1.0):
    return tuple(round(c * 255) for c in colour[:3]) + (round((colour[3] if len(colour) > 3 else alpha) * 255),)


def hex_of(colour, alpha=1.0):
    return "#%02x%02x%02x%02x" % rgba(colour, alpha)


def lerp(a, b, t):
    return tuple(x + (y - x) * t for x, y in zip(a, b))


def canvas(units_x, units_y):
    return Image.new("RGBA", (round(units_x * SCALE * AA), round(units_y * SCALE * AA)), (0, 0, 0, 0))


def reduced(image):
    return image.resize((image.width // AA, image.height // AA), Image.LANCZOS)


def disc(draw, centre, radius, colour):
    draw.ellipse((centre[0] - radius, centre[1] - radius, centre[0] + radius, centre[1] + radius), fill=colour)


def ring(lit):
    """The rim as turned metal: the light runs round it, brightest high on the left and low on the right."""
    size = ORB + 2.0
    image = canvas(size, size)
    draw = ImageDraw.Draw(image)
    px = SCALE * AA
    centre = (image.width * 0.5, image.height * 0.5)
    disc(draw, centre, (RADIUS + 1.0) * px, rgba(GOLD_DARK, 0.9))
    band = RADIUS * 0.17
    outer, inner = RADIUS * px, (RADIUS - band) * px
    steps = 720
    for i in range(steps):
        a0, a1 = math.tau * i / steps, math.tau * (i + 1.5) / steps
        shine = (0.5 + 0.5 * math.cos(2.0 * a0 + math.pi * 0.5)) ** 2
        colour = lerp(lerp(GOLD_DARK, GOLD, 0.75 + 0.25 * shine), GOLD_BRIGHT, 0.8 if lit else shine * 0.8)
        draw.polygon([(centre[0] + math.cos(a0) * inner, centre[1] + math.sin(a0) * inner),
                      (centre[0] + math.cos(a0) * outer, centre[1] + math.sin(a0) * outer),
                      (centre[0] + math.cos(a1) * outer, centre[1] + math.sin(a1) * outer),
                      (centre[0] + math.cos(a1) * inner, centre[1] + math.sin(a1) * inner)], fill=rgba(colour))
    line = max(1.0, RADIUS * 0.03) * px
    disc(draw, centre, RADIUS * 0.83 * px + line * 0.5, rgba(GOLD_DARK))
    return reduced(image), size


def orb():
    size = RADIUS * 1.64
    image = canvas(size, size)
    disc(ImageDraw.Draw(image), (image.width * 0.5, image.height * 0.5), image.width * 0.5, rgba(INSIDE))
    return reduced(image), size


def glow():
    size = RADIUS * 2.48
    image = canvas(size, size)
    centre = (image.width * 0.5, image.height * 0.5)
    outer = canvas(size, size)
    disc(ImageDraw.Draw(outer), centre, image.width * 0.5, rgba(LIGHT[:3], LIGHT[3] * 0.6))
    inner = canvas(size, size)
    disc(ImageDraw.Draw(inner), centre, RADIUS * 1.12 * SCALE * AA, rgba(LIGHT))
    return reduced(Image.alpha_composite(Image.alpha_composite(image, outer), inner)), size


def end(side):
    """The point at each end of the bar; side -1 points left, 1 right."""
    width, height = ORB * 0.26, ORB * 0.3
    image = canvas(width, height)
    draw = ImageDraw.Draw(image)
    tip_x, back_x = (0, image.width) if side < 0 else (image.width, 0)
    mid = image.height * 0.5
    draw.polygon([(tip_x, mid), (back_x, 0), (back_x, mid)], fill=rgba(GOLD_BRIGHT))
    draw.polygon([(tip_x, mid), (back_x, mid), (back_x, image.height)], fill=rgba(GOLD))
    return reduced(image), (width, height)


def capsule(fill):
    """White rim or filled body: a piece tints the rim gold, bright when lit. A picture stretches to its piece."""
    width, height = CAPSULE
    image = canvas(width, height)
    draw = ImageDraw.Draw(image)
    box = (0, 0, image.width - 1, image.height - 1)
    radius = image.height * 0.5
    rim = max(1.0, height * 0.08) * SCALE * AA
    if fill:
        draw.rounded_rectangle(box, radius, fill=rgba(INSIDE))
    else:
        draw.rounded_rectangle(box, radius, outline=(255, 255, 255, 255), width=round(rim))
    return reduced(image)


def digit(text, face):
    """The Fable II face's superscript figures set large: its ordinary 1 and 2 have decorative cuts
    that read as damage this small. White, so a piece tints it; its dark edge stays dark. Centred by
    the ink drawn: the box the font states for a superscript is the full figure's, so placing by it
    leaves the figure high in its capsule."""
    image = canvas(*DIGIT_BOX)
    px = SCALE * AA
    font = ImageFont.truetype(face, round(DIGIT_SIZE * px), layout_engine=ImageFont.Layout.RAQM)
    stroke = round(max(2.0, DIGIT_SIZE / 8.0) * 0.5 * px)
    drawn = Image.new("RGBA", (image.width * 2, image.height * 2), (0, 0, 0, 0))
    ImageDraw.Draw(drawn).text((image.width * 0.5, image.height * 0.5), text, font=font, features=["sups"],
                               fill=(255, 255, 255, 255), stroke_width=stroke, stroke_fill=rgba(SHADOW))
    ink = drawn.crop(drawn.getchannel("A").getbbox())
    image.alpha_composite(ink, (round((image.width - ink.width) * 0.5), round((image.height - ink.height) * 0.5)))
    return reduced(image)


def blank():
    return Image.new("RGBA", (4, 4), (0, 0, 0, 0))


def round2(value):
    return round(value, 2)


def piece(pid, kind, centre, box=None, **more):
    entry = {"id": pid, "kind": kind, "anchor": [0.5, 1]}
    if box is not None:
        entry["offset"] = [round2(centre[0] - box[0] * 0.5), round2(centre[1] - box[1] * 0.5)]
        entry["size"] = [round2(box[0]), round2(box[1])]
    else:
        entry["offset"] = [round2(centre[0]), round2(centre[1])]
    entry.update(more)
    entry["each"] = True
    entry["group"] = "hotbar.bar"
    return entry


def tex(name):
    return ["art/gui/hud/hotbar_%s.png" % name]


def pieces():
    out = []
    first = -(9 * PITCH + SPLIT) * 0.5
    last = -first
    for pid, x, name in (("hotbar.end_left", first - RADIUS - ORB * 0.07, "end_left"),
                         ("hotbar.end_right", last + RADIUS + ORB * 0.07, "end_right")):
        out.append(piece(pid, "picture", (x, MID), (ORB * 0.26, ORB * 0.3), textures=tex(name), index="player:hotbar.frame"))
    for s in range(1, 11):
        at = (first + (s - 1) * PITCH + (SPLIT if s >= 4 else 0.0), MID)
        key = (at[0], at[1] - RADIUS * 1.17)
        number = str(s % 10)
        out.append(piece(f"hotbar.glow{s}", "picture", at, (RADIUS * 2.48, RADIUS * 2.48), textures=tex("glow"), index=f"player:hotbar.lit{s}"))
        out.append(piece(f"hotbar.ring{s}", "picture", at, (ORB + 2, ORB + 2), textures=tex("ring"), index=f"player:hotbar.ring{s}"))
        out.append(piece(f"hotbar.ring_lit{s}", "picture", at, (ORB + 2, ORB + 2), textures=tex("ring_lit"), index=f"player:hotbar.lit{s}"))
        # 4-0's orbs are their keys on a touchscreen: a finger on one presses the slot's action.
        touch = {"touch": f"slot{s % 10}"} if s >= 4 else {}
        out.append(piece(f"hotbar.orb{s}", "picture", at, (RADIUS * 1.64, RADIUS * 1.64), textures=tex("orb"), index="player:hotbar.frame", tint="#ffffffe0", **touch))
        out.append(piece(f"hotbar.icon{s}", "picture", at, (ICON, ICON)))
        out.append(piece(f"hotbar.dim{s}", "picture", at, (ICON, ICON), tint="#ffffff59"))
        out.append(piece(f"hotbar.cap{s}", "picture", key, CAPSULE, textures=tex("capsule"), index="player:hotbar.frame"))
        out.append(piece(f"hotbar.cap_rim{s}", "picture", key, CAPSULE, textures=tex("capsule_rim"), index=f"player:hotbar.ring{s}", tint=hex_of(GOLD)))
        out.append(piece(f"hotbar.cap_lit{s}", "picture", key, CAPSULE, textures=tex("capsule_rim"), index=f"player:hotbar.lit{s}", tint=hex_of(GOLD_BRIGHT)))
        out.append(piece(f"hotbar.key{s}", "picture", key, DIGIT_BOX, textures=tex("blank") + tex("digit" + number), index=f"player:hotbar.key{s}", tint=hex_of((0.98, 0.96, 0.9))))
        out.append(piece(f"hotbar.key_lit{s}", "picture", key, DIGIT_BOX, textures=tex("digit" + number), index=f"player:hotbar.lit{s}", tint=hex_of(GOLD_BRIGHT)))
        if s >= 4:
            # An item's stack, in a smaller capsule over the ring's lower right.
            stack = (at[0] + RADIUS * 0.6, at[1] + RADIUS * 0.72)
            out.append(piece(f"hotbar.badge{s}", "picture", stack, BADGE, textures=tex("capsule"), index=f"player:hotbar.badge{s}"))
            out.append(piece(f"hotbar.badge_ring{s}", "picture", stack, BADGE, textures=tex("capsule_rim"), index=f"player:hotbar.badge{s}", tint=hex_of(GOLD)))
            text = (stack[0], stack[1] - 8.5)
            for pid, format_, colour in ((f"hotbar.count{s}", "0;'';''", "#f5e8c7"), (f"hotbar.count_none{s}", "'';'';0", "#e66b57")):
                out.append(piece(pid, "text", text, None, text=f"player:hotbar.count{s}", format=format_, size=12, colour=colour,
                                 align="centre", outline="#0a0805d9", outlineWidth=1.0))
    return out


def main(folder, face):
    art = os.path.join(folder, "art", "gui", "hud")
    os.makedirs(art, exist_ok=True)
    images = {"ring": ring(False)[0], "ring_lit": ring(True)[0], "orb": orb()[0], "glow": glow()[0],
              "end_left": end(-1)[0], "end_right": end(1)[0], "capsule": capsule(True), "capsule_rim": capsule(False),
              "blank": blank()}
    for number in "1234567890":
        images["digit" + number] = digit(number, face)
    for name, image in images.items():
        image.save(os.path.join(art, "hotbar_%s.png" % name), optimize=True)

    # Larger on a phone, where the bar is seen on a small screen and tapped with a thumb.
    group = {"anchor": [0.5, 1], "pivot": [0, -12], "scale": "setting:hotbar.size", "touchScale": TOUCH_SCALE,
             "offset": [0, "-setting:hotbar.lift"]}
    lines = ",\n".join("\t\t" + json.dumps(p) for p in pieces())
    with open(os.path.join(folder, "ui", "hud.json"), "w", encoding="utf-8") as out:
        out.write('{\n\t"groups": {\n\t\t"hotbar.bar": %s\n\t},\n\t"pieces": [\n%s\n\t]\n}\n' % (json.dumps(group), lines))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
