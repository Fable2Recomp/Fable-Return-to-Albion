# SPDX-License-Identifier: MIT
# Refii — Fable II Model & .mdl.gmd Socket Authoring Add-on for Blender (3.6 / 4.x+)
#
# Installation:
#   1. In Blender, open Edit -> Preferences -> Add-ons -> Install...
#   2. Select this file (res://modding_tools/refii_blender_tools.py)
#   3. Enable "Import-Export: Refii: Fable II Model & GMD Socket Tools"
#   4. In the 3D Viewport, press N and open the "Refii Fable 2" tab.

bl_info = {
    "name": "Refii: Fable II Model & GMD Socket Tools",
    "author": "Refii Mod Studio",
    "version": (1, 3, 0),
    "blender": (3, 6, 0),
    "location": "View3D > Sidebar (N-Panel) > Refii Fable 2",
    "description": (
        "Visualizes authentic Fable II .mdl.gmd weapon & prop attachment sockets using exact "
        "CH_HeroMale_Morph_Body.mdl.gmd parent-dummy math (Carry_Hand_DummyObject1 & "
        "Carry_SheathWeaponBack_DummyObject), spawns 1-click weapon/prop rigs, and exports Refii-ready .glb files."
    ),
    "category": "Import-Export",
}

import math
import bpy
import blf
import gpu
from gpu_extras.batch import batch_for_shader
from mathutils import Vector, Matrix, Quaternion
from bpy_extras.view3d_utils import location_3d_to_region_2d


# =============================================================================
# Ground-Truth Hero Parent Dummy Quaternions (from CH_HeroMale_Morph_Body.mdl.gmd)
# =============================================================================
# In Blender (Z-Up), imported .glb socket transforms are identical to raw GameMesh (Z-Up) space:
#   - Weapon barrel / blade length extends along +Y_bl (-Z in glTF).
#   - On Guns (Pistols, Rifles, Crossbows):
#       * -X_bl is Top of Barrel / Top Sight; +X_bl is Bottom / Trigger Guard.
#   - On Melee Weapons (Katana, LongSword, Cleaver, CH_Sword_Wood):
#       * +X_bl is the Thick Dull Spine (Back Edge, facing up/back toward the Hero).
#       * -X_bl is the Razor Sharp Cutting Edge (Front Edge, facing forward/down toward enemies).
#
# Why vanilla sockets have non-axis-aligned rotations:
#   1. Hero Right Hand Parent Dummy (`Carry_Hand_DummyObject1` on `Shadow_R_BlendHand`):
#        q_wxyz = (0.6409, 0.7602, -0.0506, 0.0940)
#      It has a built-in ~12° wrist tilt relative to the Hero's hand bone (`Shadow_R_BlendHand`).
#      - Melee weapons (`Katana`, `LongSword`, `CH_Sword_Wood`, `Hammer`) use `Hold = Identity (1, 0, 0, 0)`,
#        so in-game the sword sits at `Carry_Hand_DummyObject1`'s ~12° wrist angle relative to the hand bone.
#      - 1H Pistols (`Pistol_Sucker` / `toy_gun`, `Pistol_Clockwork`, `Pistol_Flintlock`) use
#        `Hold q_wxyz = (0.1196, -0.6250, 0.7713, 0.0022)` which compensates for the 1H pistol aiming
#        wrist pose so that the pistol's `+Y_bl` barrel points 100% straight ahead in-game!
#      - 2H Rifles (`DLC_SpartanGun`, `RifleFlintlock`) use
#        `Hold q_wxyz = (0.0038, 0.7168, -0.6970, 0.0211)` so the rifle's `+Y_bl` barrel points straight ahead.
#
#   2. Hero Back Sheath Parent Dummy (`Character.Carry.SheathWeaponBack`):
#        q_wxyz = (0.6416, -0.7280, 0.1595, 0.1809)
#      The back scabbard is slung diagonally at 28.1° across the Hero's back.
#      Therefore, in `Weapon.Weapon.Sheath`'s local space (`T_SheathWeaponBack^-1 * v_char`):
#        * True Ground (`-Z_char`) is `(+0.4681, +0.8765, +0.1110)` (rotated 28.1° clockwise from local +Y!).
#        * Into Hero Back (`+Y_char`) is `(-0.0001, -0.1256, +0.9919)` (local +Z).

Q_HERO_HAND_PARENT = Quaternion((0.6409, 0.7602, -0.0506, 0.0940)).normalized()
Q_PISTOL_HOLD_CANONICAL = Quaternion((0.119587, -0.625000, 0.771345, 0.002216)).normalized()
Q_PISTOL_RED_DRAGON_HOLD_CANONICAL = Quaternion((0.077414, -0.618856, 0.774583, -0.105091)).normalized()
Q_RIFLE_HOLD_CANONICAL = Quaternion((0.003780, 0.716775, -0.696974, 0.021127)).normalized()
Q_CROSSBOW_HOLD_CANONICAL = Quaternion((0.0, 0.70710678, -0.70710678, 0.0)).normalized()
Q_MELEE_HOLD_CANONICAL = Quaternion((1.0, 0.0, 0.0, 0.0))

# Canonical `Weapon.Weapon.UpperHold` quaternions from `globals_streaming.bnk`:
#   - 21 Rifles & Blunderbusses (`DLC_SpartanGun`, `RifleFlintlock_*`, `Blunderbuss_*`):
#       q_wxyz = (0.624879, -0.243130, 0.733820, -0.109186)
#   - 12 Crossbows (`Crossbow_Light_*`, `Crossbow_Heavy_*`):
#       q_wxyz = (0.70710678, 0.0, 0.70710678, 0.0)
#   - 15 2H Melee (`Katana_Master`, `Hammer_Legendary`, `SH_GreatSword_*`):
#       q_wxyz = (1.0, 0.0, 0.0, 0.0)
Q_RIFLE_UPPER_HOLD_CANONICAL = Quaternion((0.624879, -0.243130, 0.733820, -0.109186)).normalized()
Q_CROSSBOW_UPPER_HOLD_CANONICAL = Quaternion((0.70710678, 0.0, 0.70710678, 0.0)).normalized()

# Exact local-space vectors inside `Weapon.Weapon.Hold` that map to Barrel Forward (+Y_bl) and Top Sight (-X_bl)
# when `Weapon.Weapon.Hold` is at its canonical Pistol or Rifle rotation:
PISTOL_HOLD_LOCAL_AIM = (Q_PISTOL_HOLD_CANONICAL.inverted() @ Vector((0.0, 1.0, 0.0))).normalized()
PISTOL_HOLD_LOCAL_TOP = (Q_PISTOL_HOLD_CANONICAL.inverted() @ Vector((-1.0, 0.0, 0.0))).normalized()

RIFLE_HOLD_LOCAL_AIM = (Q_RIFLE_HOLD_CANONICAL.inverted() @ Vector((0.0, 1.0, 0.0))).normalized()
RIFLE_HOLD_LOCAL_TOP = (Q_RIFLE_HOLD_CANONICAL.inverted() @ Vector((-1.0, 0.0, 0.0))).normalized()

# Exact local-space vectors inside `Weapon.Weapon.UpperHold` that map to Foregrip Forward (+Y_bl) and Cradle Up (-X_bl):
RIFLE_UPPER_HOLD_LOCAL_FORWARD = (Q_RIFLE_UPPER_HOLD_CANONICAL.inverted() @ Vector((0.0, 1.0, 0.0))).normalized()
RIFLE_UPPER_HOLD_LOCAL_UP = (Q_RIFLE_UPPER_HOLD_CANONICAL.inverted() @ Vector((-1.0, 0.0, 0.0))).normalized()

CROSSBOW_UPPER_HOLD_LOCAL_FORWARD = (Q_CROSSBOW_UPPER_HOLD_CANONICAL.inverted() @ Vector((0.0, 1.0, 0.0))).normalized()
CROSSBOW_UPPER_HOLD_LOCAL_UP = (Q_CROSSBOW_UPPER_HOLD_CANONICAL.inverted() @ Vector((-1.0, 0.0, 0.0))).normalized()

# Exact local-space vectors inside `Weapon.Weapon.Sheath` (from `Character.Carry.SheathWeaponBack`):
SHEATH_LOCAL_TO_GROUND = Vector((0.4681, 0.8765, 0.1110)).normalized()
SHEATH_LOCAL_TO_BODY = Vector((-0.0001, -0.1256, +0.9919)).normalized()



# =============================================================================
# Fable II Authentic .mdl.gmd Socket Catalog (from globals_streaming.bnk)
# =============================================================================

STANDARD_SOCKETS = [
    (
        "Weapon.Weapon.Hold",
        "Weapon.Weapon.Hold (Right-Hand Primary Grip)",
        "Primary right-hand grip socket on the weapon handle/hilt where the Hero's right hand attaches (used by all 114 weapons).",
        "Weapons (All)",
        (0.0, -0.0855, 0.0),
        (1.0, 0.0, 0.0, 0.0),
    ),
    (
        "Weapon.Weapon.UpperHold",
        "Weapon.Weapon.UpperHold (Left-Hand Foregrip / 2H Grip)",
        "Secondary left-hand support grip on rifle/blunderbuss/crossbow foregrips and 2H greatsword/hammer hilts.",
        "Weapons (2H)",
        (-0.0162, 0.2925, 0.0200),
        (0.6249, -0.2431, 0.7338, -0.1092),
    ),
    (
        "Weapon.Weapon.Sheath",
        "Weapon.Weapon.Sheath (Holster / Back Sheath Pivot)",
        "Pivot on the weapon where it attaches to the character's back sheath or hip holster when not drawn.",
        "Weapons (All)",
        (0.0, 0.0, 0.0100),
        (1.0, 0.0, 0.0, 0.0),
    ),
    (
        "Weapon.Weapon.ProjectileEmitter",
        "Weapon.Weapon.ProjectileEmitter (Muzzle / Bullet Spawn)",
        "Barrel tip socket where bullets, pellets, and crossbow bolts spawn and fire along +Y in Blender (-Z in glTF).",
        "Ranged Weapons",
        (-0.0613, 0.1904, 0.0),
        (1.0, 0.0, 0.0, 0.0),
    ),
    (
        "Weapon.Weapon.Top",
        "Weapon.Weapon.Top (Blade / Hammer Tip)",
        "Tip of the melee blade or top of the hammer head; defines weapon reach from Hold -> Top for hit sweeps and swing trails.",
        "Melee Weapons",
        (0.0, 0.8470, 0.0),
        (1.0, 0.0, 0.0, 0.0),
    ),
    (
        "Weapon.FX.Particle.FXINV_Flintlock_Blast_01.par",
        "Weapon.FX.Particle.FXINV_Flintlock_Blast_01.par (Flintlock Muzzle Flash)",
        "Standard flintlock/clockwork muzzle flash & smoke particle emitter co-located with Weapon.Weapon.ProjectileEmitter.",
        "Ranged FX",
        (-0.0613, 0.1904, 0.0),
        (1.0, 0.0, 0.0, 0.0),
    ),
    (
        "Weapon.FX.Particle.FX_Flintlock_Trigger_01.par",
        "Weapon.FX.Particle.FX_Flintlock_Trigger_01.par (Crossbow Trigger FX)",
        "Crossbow trigger/bowstring particle effect emitter.",
        "Ranged FX",
        (-0.0834, 0.0910, 0.0),
        (1.0, 0.0, 0.0, 0.0),
    ),
    (
        "Weapon.FX.Particle.Effect_1.",
        "Weapon.FX.Particle.Effect_1. (Legendary / Custom Weapon FX)",
        "Generic weapon particle effect socket used on Hal's Rifle (DLC_SpartanGun) and Legendary weapons (note trailing period).",
        "Ranged / Custom FX",
        (-0.1132, 0.5750, 0.0),
        (1.0, 0.0, 0.0, 0.0),
    ),
    (
        "Prop.Carry.1Hand.Right",
        "Prop.Carry.1Hand.Right (Right-Hand Carried Prop Grip)",
        "Right-hand grip socket for carried props, mugs, potions, trophies, and wooden toy swords (CH_Sword_Wood).",
        "Props & Items",
        (0.0100, -0.0687, 0.0050),
        (1.0, 0.0, 0.0, 0.0),
    ),
    (
        "Prop.Carry.1Hand.Left",
        "Prop.Carry.1Hand.Left (Left-Hand Carried Prop Grip)",
        "Left-hand grip socket for off-hand carried props and items.",
        "Props & Items",
        (0.0, 0.0, 0.0),
        (1.0, 0.0, 0.0, 0.0),
    ),
    (
        "Prop.Layout.Position",
        "Prop.Layout.Position (Prop Placement Pivot)",
        "Root layout and placement pivot socket on props.",
        "Props & Items",
        (0.0, 0.0, 0.0),
        (1.0, 0.0, 0.0, 0.0),
    ),
    (
        "Prop.Action.Generic",
        "Prop.Action.Generic (Prop Interaction Point)",
        "Generic character interaction alignment point on interactive props.",
        "Props & Items",
        (0.0, 0.0, 0.0),
        (1.0, 0.0, 0.0, 0.0),
    ),
    (
        "Prop.FX.Particle.Effect_1.",
        "Prop.FX.Particle.Effect_1. (Prop / Projectile Particle FX)",
        "Particle emitter socket on props and projectile bolts (note trailing period).",
        "Props & Items",
        (0.0, 0.0, 0.0),
        (1.0, 0.0, 0.0, 0.0),
    ),
    (
        "Prop.Layout.Light",
        "Prop.Layout.Light (Lantern / Torch Point Light)",
        "Dynamic point-light attachment origin for lanterns, torches, and lit props.",
        "Props & Items",
        (0.0, 0.0, 0.12),
        (1.0, 0.0, 0.0, 0.0),
    ),
]

SOCKET_LOOKUP = {item[0]: item for item in STANDARD_SOCKETS}

# Authentic weapon & prop socket rigs extracted directly from globals_streaming.bnk:
WEAPON_RIG_PRESETS = {
    "PISTOL": {
        "label": "Pistol / Flintlock / Clockwork (1H Ranged)",
        "sockets": [
            # From Pistol_Sucker.mdl.gmd / Pistol_Clockwork_Steel.mdl.gmd:
            (
                "Weapon.Weapon.Hold",
                (0.0141, -0.0855, -0.0009),
                (0.119587, -0.625000, 0.771345, 0.002216),
            ),
            (
                "Weapon.Weapon.Sheath",
                (-0.0355, 0.0287, -0.0093),
                (0.998587, 0.022572, -0.021970, -0.042789),
            ),
            (
                "Weapon.Weapon.ProjectileEmitter",
                (-0.0613, 0.1904, 0.0),
                (1.0, 0.0, 0.0, 0.0),
            ),
            (
                "Weapon.FX.Particle.FXINV_Flintlock_Blast_01.par",
                (-0.0613, 0.1904, 0.0),
                (1.0, 0.0, 0.0, 0.0),
            ),
        ],
    },
    "RIFLE": {
        "label": "Rifle / Blunderbuss (2H Ranged)",
        "sockets": [
            # From DLC_SpartanGun.mdl.gmd / RifleFlintlock_Steel.mdl.gmd:
            (
                "Weapon.Weapon.Hold",
                (-0.0042, -0.0409, 0.0024),
                (0.003780, 0.716775, -0.696974, 0.021127),
            ),
            (
                "Weapon.Weapon.UpperHold",
                (-0.0162, 0.2925, 0.0200),
                (0.624879, -0.243130, 0.733820, -0.109186),
            ),
            (
                "Weapon.Weapon.Sheath",
                (-0.0060, -0.1114, 0.0102),
                (-0.021706, 0.161685, 0.985050, 0.055351),
            ),
            (
                "Weapon.Weapon.ProjectileEmitter",
                (-0.1132, 0.5729, 0.0),
                (1.0, 0.0, 0.0, 0.0),
            ),
            (
                "Weapon.FX.Particle.FXINV_Flintlock_Blast_01.par",
                (-0.1132, 0.5729, 0.0),
                (1.0, 0.0, 0.0, 0.0),
            ),
        ],
    },
    "CROSSBOW": {
        "label": "Crossbow (2H Ranged)",
        "sockets": [
            # From Crossbow_Light_Iron.mdl.gmd:
            (
                "Weapon.Weapon.Hold",
                (0.0, -0.0821, 0.0),
                (0.0, 0.70710678, -0.70710678, 0.0),
            ),
            (
                "Weapon.Weapon.UpperHold",
                (0.0, 0.2585, 0.0),
                (0.70710678, 0.0, 0.70710678, 0.0),
            ),
            (
                "Weapon.Weapon.Sheath",
                (-0.0729, 0.0625, 0.0),
                (0.70710678, 0.0, -0.70710678, 0.0),
            ),
            (
                "Weapon.Weapon.ProjectileEmitter",
                (-0.0701, 0.5227, 0.0),
                (1.0, 0.0, 0.0, 0.0),
            ),
            (
                "Weapon.FX.Particle.FX_Flintlock_Trigger_01.par",
                (-0.0834, 0.0910, 0.0),
                (1.0, 0.0, 0.0, 0.0),
            ),
        ],
    },
    "MELEE_1H": {
        "label": "1H Sword / Axe / Cleaver / Mace",
        "sockets": [
            # From SH_LongSword_Master.mdl.gmd / Cleaver_Iron.mdl.gmd (Hold & Sheath are Identity!):
            (
                "Weapon.Weapon.Hold",
                (0.0, -0.1267, 0.0),
                (1.0, 0.0, 0.0, 0.0),
            ),
            (
                "Weapon.Weapon.Sheath",
                (0.0, 0.0, 0.0100),
                (1.0, 0.0, 0.0, 0.0),
            ),
            (
                "Weapon.Weapon.Top",
                (0.0, 0.8468, 0.0),
                (1.0, 0.0, 0.0, 0.0),
            ),
        ],
    },
    "MELEE_2H": {
        "label": "2H Greatsword / Hammer / Katana",
        "sockets": [
            # From Katana_Master.mdl.gmd / Hammer_Legendary.mdl.gmd / NPC_Hammer_Master.mdl.gmd:
            (
                "Weapon.Weapon.Hold",
                (0.0, -0.1455, 0.0),
                (1.0, 0.0, 0.0, 0.0),
            ),
            (
                "Weapon.Weapon.UpperHold",
                (0.0, -0.0200, 0.0),
                (1.0, 0.0, 0.0, 0.0),
            ),
            (
                "Weapon.Weapon.Sheath",
                (0.0, 0.0, 0.0),
                (1.0, 0.0, 0.0, 0.0),
            ),
            (
                "Weapon.Weapon.Top",
                (0.0976, 0.9999, 0.0),
                (1.0, 0.0, 0.0, 0.0),
            ),
        ],
    },
    "PROP_CARRIED": {
        "label": "Carried Prop / Mug / Potion / Wooden Sword",
        "sockets": [
            # From CH_Sword_Wood.mdl.gmd:
            (
                "Prop.Carry.1Hand.Right",
                (0.0100, -0.0687, 0.0050),
                (1.0, 0.0, 0.0, 0.0),
            ),
            (
                "Prop.Layout.Position",
                (0.0, 0.0, 0.0),
                (1.0, 0.0, 0.0, 0.0),
            ),
        ],
    },
}


# =============================================================================
# Helper Functions
# =============================================================================

def resolve_canonical_socket_name(obj):
    """Returns the canonical Fable II socket name if `obj` is a GMD socket Empty, else None."""
    if obj is None or obj.type != "EMPTY":
        return None

    if obj.name.startswith("GmdDummies") or obj.name.startswith("GmdSockets"):
        return None

    prop_name = obj.get("gmd_dummy_name")
    if isinstance(prop_name, str) and prop_name.strip():
        return prop_name.strip()

    raw_name = obj.name.strip()
    if "." in raw_name:
        head, tail = raw_name.rsplit(".", 1)
        if tail.isdigit() and len(tail) == 3:
            raw_name = head

    if raw_name in SOCKET_LOOKUP:
        return raw_name

    if (raw_name + ".") in SOCKET_LOOKUP:
        return raw_name + "."

    normalized = raw_name.replace(".", "_").strip("_").lower()
    for canonical in SOCKET_LOOKUP:
        if canonical.replace(".", "_").strip("_").lower() == normalized:
            return canonical

    if obj.parent and obj.parent.name.startswith("GmdDummies"):
        return raw_name

    known_prefixes = ("Weapon.", "Prop.", "Character.", "Weapon_", "Prop_", "Character_")
    if raw_name.startswith(known_prefixes):
        return raw_name

    return None


def find_all_gmd_sockets(context):
    """Returns a list of `(obj, canonical_name)` for all GMD socket Empties in the current scene."""
    result = []
    if context is None or context.scene is None:
        return result
    for obj in context.scene.objects:
        cname = resolve_canonical_socket_name(obj)
        if cname:
            result.append((obj, cname))
    return result


def detect_scene_weapon_mode(sockets, hold_quat=None):
    """
    Determines whether the current scene's socket rig is:
      - 'PISTOL'   (1H Ranged: has ProjectileEmitter and no UpperHold, or Hold quat near Pistol canonical)
      - 'RIFLE'    (2H Ranged: has ProjectileEmitter and UpperHold)
      - 'CROSSBOW' (2H Crossbow: has FX_Flintlock_Trigger_01.par)
      - 'MELEE'    (Melee or Carried Prop: has Weapon.Weapon.Top or Hold quat near Identity)
    """
    names = {cname for _, cname in sockets}
    has_emitter = any("ProjectileEmitter" in n or "Projectile" in n for n in names)
    has_upper = "Weapon.Weapon.UpperHold" in names
    has_top = "Weapon.Weapon.Top" in names
    has_crossbow_fx = any("Trigger" in n for n in names)

    if has_top and not has_emitter:
        return "MELEE"
    if has_emitter:
        if has_crossbow_fx:
            return "CROSSBOW"
        if has_upper:
            return "RIFLE"
        return "PISTOL"

    if hold_quat is not None:
        q = hold_quat.normalized()
        if abs(q.w) > 0.70:
            return "MELEE"
        # Compare against Pistol vs Rifle canonical Hold quaternions
        dot_pistol = abs(q.dot(Q_PISTOL_HOLD_CANONICAL))
        dot_rifle = abs(q.dot(Q_RIFLE_HOLD_CANONICAL))
        if dot_pistol >= dot_rifle:
            return "PISTOL"
        return "RIFLE"

    return "MELEE"


def get_or_create_gmd_dummies_root(context):
    """Finds or creates the `GmdDummies` container Empty in the active scene."""
    scene = context.scene
    for obj in scene.objects:
        if obj.type == "EMPTY" and obj.name.startswith("GmdDummies"):
            return obj

    holder = bpy.data.objects.new("GmdDummies", None)
    holder.empty_display_type = "PLAIN_AXES"
    holder.empty_display_size = 0.05
    context.collection.objects.link(holder)
    return holder


def create_or_update_socket_empty(
    context,
    socket_name,
    location=(0.0, 0.0, 0.0),
    rotation_quat_wxyz=(1.0, 0.0, 0.0, 0.0),
    index=0,
    overwrite_transform=True,
):
    """Creates or updates a named Fable II GMD socket Empty under `GmdDummies`."""
    holder = get_or_create_gmd_dummies_root(context)

    for obj, cname in find_all_gmd_sockets(context):
        if cname.rstrip(".").lower() == socket_name.rstrip(".").lower():
            obj.name = socket_name
            obj["gmd_dummy_name"] = socket_name
            if "gmd_parent_node" not in obj:
                obj["gmd_parent_node"] = -1
            if overwrite_transform:
                obj.location = Vector(location)
                obj.rotation_mode = "QUATERNION"
                obj.rotation_quaternion = Quaternion(rotation_quat_wxyz).normalized()
            return obj

    empty = bpy.data.objects.new(socket_name, None)
    empty.empty_display_type = "PLAIN_AXES"
    empty.empty_display_size = 0.04
    empty.parent = holder
    empty.location = Vector(location)
    empty.rotation_mode = "QUATERNION"
    empty.rotation_quaternion = Quaternion(rotation_quat_wxyz).normalized()
    empty["gmd_dummy_name"] = socket_name
    empty["gmd_parent_node"] = -1
    empty["gmd_index"] = index
    context.collection.objects.link(empty)
    return empty


# =============================================================================
# Directional 3D Viewport GPU Gizmos (Verified Against CH_HeroMale_Morph_Body)
# =============================================================================

_draw_handler_3d = None
_draw_handler_2d = None
_overlay_enabled = True


def _add_polyline(lines, pts, closed=False):
    count = len(pts)
    if count < 2:
        return
    for i in range(count - 1):
        lines.extend([pts[i], pts[i + 1]])
    if closed and count > 2:
        lines.extend([pts[-1], pts[0]])


def _build_arrow_lines(origin, direction, length=0.24, head_size=0.042, up_vec=Vector((-1.0, 0.0, 0.0))):
    d = direction.normalized()
    tip = origin + d * length
    side1 = d.cross(up_vec)
    if side1.length_squared < 1e-4:
        side1 = d.cross(Vector((0.0, 0.0, 1.0)))
    side1.normalize()
    side2 = side1.cross(d).normalized()

    base = tip - d * head_size
    w = head_size * 0.45
    f1 = base + side1 * w
    f2 = base - side1 * w
    f3 = base + side2 * w
    f4 = base - side2 * w

    return [
        origin, tip,
        tip, f1, tip, f2, tip, f3, tip, f4,
        f1, f3, f3, f2, f2, f4, f4, f1,
    ]


def _resolve_hold_gizmo_vectors(rot_mat, weapon_mode):
    """
    Computes the exact world-space vectors for `Weapon.Weapon.Hold` / `Prop.Carry.1Hand.Right`:
      - `aim_or_blade_dir`:
          * For PISTOL: `rot_mat @ PISTOL_HOLD_LOCAL_AIM` -> Points 100% straight along the barrel (+Y_bl)
            when Hold has its vanilla Pistol quaternion! Rotating Hold tilts this arrow by the exact in-game offset.
          * For RIFLE / CROSSBOW: `rot_mat @ RIFLE_HOLD_LOCAL_AIM` -> Points 100% straight along the barrel (+Y_bl).
          * For MELEE: `rot_mat @ (0, +1, 0)` -> Points 100% straight along the sword blade / hammer shaft (+Y_bl).
      - `grip_up_dir`:
          Direction from Pinky/Pommel up through the Thumb of the gripped fist.
      - `knuckle_front_dir`:
          Direction the fingers wrap around the front of the handle (toward trigger or cutting edge).
      - `palm_side_dir`:
          Outward from the right palm.
      - `hand_bone_blade_dir` (Melee only):
          Shows the actual ~12° angle (`Carry_Hand_DummyObject1` on `Shadow_R_BlendHand`) of the Hero's hand bone.
    """
    if weapon_mode == "PISTOL":
        aim_dir = (rot_mat @ PISTOL_HOLD_LOCAL_AIM).normalized()
        top_sight_dir = (rot_mat @ PISTOL_HOLD_LOCAL_TOP).normalized()
        # The physical flintlock grip handle runs along Hold's local +Y (tilted ~13° back from top_sight_dir):
        grip_up_dir = (rot_mat @ Vector((0.0, 1.0, 0.0))).normalized()
        palm_side_dir = aim_dir.cross(top_sight_dir).normalized()
        return aim_dir, grip_up_dir, aim_dir, palm_side_dir, top_sight_dir, None

    if weapon_mode in ("RIFLE", "CROSSBOW"):
        aim_dir = (rot_mat @ RIFLE_HOLD_LOCAL_AIM).normalized()
        top_sight_dir = (rot_mat @ RIFLE_HOLD_LOCAL_TOP).normalized()
        grip_up_dir = (rot_mat @ Vector((0.0, 1.0, 0.0))).normalized()
        palm_side_dir = aim_dir.cross(top_sight_dir).normalized()
        return aim_dir, grip_up_dir, aim_dir, palm_side_dir, top_sight_dir, None

    # MELEE / PROP_CARRIED (`Hold = Identity (1, 0, 0, 0)` in all vanilla swords, axes, maces, hammers, wooden sword):
    #   - Mesh Blade Axis is local +Y `(0, 1, 0)`
    #   - Mesh Cutting Edge (Front / Knuckles) is local -X `(-1, 0, 0)`
    #   - Mesh Spine (Back / Thumb) is local +X `(+1, 0, 0)`
    #   - In-game, `Hold` snaps to `Carry_Hand_DummyObject1` (`Q_HERO_HAND_PARENT`), which has a ~12° wrist tilt
    #     relative to `Shadow_R_BlendHand`:
    blade_dir = (rot_mat @ Vector((0.0, 1.0, 0.0))).normalized()
    cutting_edge_dir = (rot_mat @ Vector((-1.0, 0.0, 0.0))).normalized()
    spine_dir = (rot_mat @ Vector((1.0, 0.0, 0.0))).normalized()
    palm_side_dir = (rot_mat @ Vector((0.0, 0.0, 1.0))).normalized()

    # Exact Hero Hand Bone (`Shadow_R_BlendHand`) thumb axis (+Z_hand) in weapon space:
    hand_rot = rot_mat @ Q_HERO_HAND_PARENT.inverted().to_matrix()
    hand_thumb_dir = (hand_rot @ Vector((0.0, 0.0, 1.0))).normalized()

    return blade_dir, blade_dir, cutting_edge_dir, palm_side_dir, spine_dir, hand_thumb_dir


def _build_right_hand_grip_gizmo(origin, rot_mat, weapon_mode):
    """
    Builds a directional 3D Right-Hand Glove + Handle Grip + Compensated Aim/Blade Pointer:
      - On Ranged Weapons (PISTOL / RIFLE / CROSSBOW):
          * The `AIM ➔` arrow uses the exact wrist-compensated barrel vector so on vanilla guns
            (including `toy_gun` / `Pistol_Sucker`!) it points 100% straight down the barrel (`+Y_bl`).
          * The 3D Hand Glove wraps around the angled pistol/rifle handle (`grip_up_dir`), with
            Index Finger on the trigger (`+knuckle_front_dir`), Wrist Cuff behind (`-knuckle_front_dir`),
            and Dorsal Top Sight Fin (`+top_or_spine_dir`).
      - On Melee Weapons (MELEE):
          * The `BLADE +Y ➔` arrow points straight up the hilt/blade (`local +Y`),
          * The `CUTTING EDGE ➔` fin points toward `local -X` (where the Hero's knuckles face),
          * And a secondary dashed/angled line (`HAND AXIS`) shows the ~12° `Carry_Hand_DummyObject1`
            wrist tilt of the Hero's hand bone in-game!
    """
    (
        primary_dir,
        grip_up,
        knuckle_front,
        palm_side,
        top_or_spine,
        hand_thumb_dir,
    ) = _resolve_hold_gizmo_vectors(rot_mat, weapon_mode)

    lines = []
    u = grip_up.normalized()
    f = knuckle_front.normalized()
    # Orthogonalize f against u for clean glove geometry around the handle:
    f_ortho = f - u * f.dot(u)
    if f_ortho.length_squared < 1e-4:
        f_ortho = palm_side.cross(u)
    f_ortho.normalize()
    s = u.cross(f_ortho).normalized()

    # 1. Handle grip core (from bottom pommel `-u` to top guard `+u`)
    top_pt = origin + u * 0.048
    bot_pt = origin - u * 0.058
    lines.extend([bot_pt, top_pt])

    # Pommel flare ring at bottom (`-u`) so bottom of grip is visually wider than top
    pommel_r = 0.022
    _add_polyline(
        lines,
        [
            bot_pt + f_ortho * pommel_r,
            bot_pt + s * pommel_r,
            bot_pt - f_ortho * pommel_r,
            bot_pt - s * pommel_r,
        ],
        closed=True,
    )

    # Top guard bar (`+u`)
    lines.extend([top_pt - f_ortho * 0.024, top_pt + f_ortho * 0.032])

    # 2. Back of Palm & Wrist Cuff (extending toward `-f_ortho`, where the Hero's forearm enters)
    palm_back_top = origin - f_ortho * 0.026 + u * 0.034 + s * 0.014
    palm_back_bot = origin - f_ortho * 0.026 - u * 0.038 + s * 0.014
    wrist_top = origin - f_ortho * 0.080 + u * 0.026 + s * 0.010
    wrist_bot = origin - f_ortho * 0.080 - u * 0.028 + s * 0.010
    wrist_in_top = origin - f_ortho * 0.080 + u * 0.026 - s * 0.016
    wrist_in_bot = origin - f_ortho * 0.080 - u * 0.028 - s * 0.016

    lines.extend([
        palm_back_top, palm_back_bot,
        palm_back_top, wrist_top,
        palm_back_bot, wrist_bot,
    ])
    _add_polyline(lines, [wrist_top, wrist_bot, wrist_in_bot, wrist_in_top], closed=True)

    # 3. Thumb (arching up on the `+u` top side of the grip)
    thumb_knuckle = origin + u * 0.048 - f_ortho * 0.004 - s * 0.018
    thumb_tip = origin + u * 0.042 + f_ortho * 0.034 - s * 0.012
    _add_polyline(lines, [palm_back_top, thumb_knuckle, thumb_tip], closed=False)

    # 4. Index Finger + 3 Wrapped Fingers on the front (`+f_ortho`) of the grip
    idx_knuckle = origin + u * 0.028 + f_ortho * 0.018 + s * 0.024
    if weapon_mode in ("PISTOL", "RIFLE", "CROSSBOW"):
        # Trigger finger extended forward toward the trigger
        idx_mid = origin + u * 0.028 + f_ortho * 0.058 + s * 0.016
        idx_tip = origin + u * 0.026 + f_ortho * 0.068 - s * 0.004
        _add_polyline(lines, [palm_back_top, idx_knuckle, idx_mid, idx_tip], closed=False)
    else:
        # Wrapped index finger around sword hilt
        idx_front = origin + u * 0.028 + f_ortho * 0.030 + s * 0.002
        idx_wrap = origin + u * 0.028 + f_ortho * 0.014 - s * 0.020
        _add_polyline(lines, [palm_back_top, idx_knuckle, idx_front, idx_wrap], closed=False)

    for finger_u in (0.006, -0.015, -0.035):
        f_back = origin + u * finger_u - f_ortho * 0.022 + s * 0.016
        f_side = origin + u * finger_u + f_ortho * 0.016 + s * 0.024
        f_front = origin + u * finger_u + f_ortho * 0.030 + s * 0.002
        f_wrap = origin + u * finger_u + f_ortho * 0.014 - s * 0.020
        _add_polyline(lines, [f_back, f_side, f_front, f_wrap], closed=False)

    lines.extend([
        idx_knuckle,
        origin - u * 0.035 + f_ortho * 0.016 + s * 0.024,
    ])

    # 5. Primary Direction Arrow:
    #    - For Ranged: Compensated In-Game Barrel Aim Arrow (`AIM ➔`, pointing along +Y_bl on vanilla Pistol & Rifle!)
    #    - For Melee: Sword Hilt/Blade Axis (`BLADE +Y ➔`) + Cutting Edge Fin (`EDGE -X`) + Hero Hand Bone Axis (`HAND ~12°`)
    if weapon_mode in ("PISTOL", "RIFLE", "CROSSBOW"):
        aim_start = origin + top_or_spine * 0.038 + primary_dir * 0.025
        lines.extend(_build_arrow_lines(aim_start, primary_dir, length=0.24, head_size=0.040, up_vec=top_or_spine))
        # Dorsal "TOP SIGHT" fin on the aim arrow shaft
        fin_b1 = aim_start + primary_dir * 0.04
        fin_b2 = aim_start + primary_dir * 0.10
        fin_pk = aim_start + primary_dir * 0.07 + top_or_spine * 0.035
        _add_polyline(lines, [fin_b1, fin_pk, fin_b2], closed=False)
    else:
        # Melee: Blade Axis arrow along local +Y (`primary_dir`)
        lines.extend(_build_arrow_lines(top_pt, primary_dir, length=0.18, head_size=0.034, up_vec=top_or_spine))
        # Cutting Edge indicator arrow (`knuckle_front` = local -X)
        lines.extend(_build_arrow_lines(origin + f_ortho * 0.030, f_ortho, length=0.085, head_size=0.022, up_vec=primary_dir))
        # Hero Hand Bone (`Carry_Hand_DummyObject1`) ~12° natural grip axis line
        if hand_thumb_dir is not None:
            lines.extend(_build_arrow_lines(origin, hand_thumb_dir, length=0.16, head_size=0.026, up_vec=top_or_spine))

    return lines


def _resolve_upper_hold_gizmo_vectors(rot_mat, weapon_mode):
    """
    Computes the exact world-space `forward_dir` (along barrel/blade `+Y_bl`) and `up_dir`
    (open top of the U-cradle `-X_bl`) for `Weapon.Weapon.UpperHold` across all 3 2H weapon families:
      - RIFLE / Blunderbuss (`q_wxyz = (0.6249, -0.2431, 0.7338, -0.1092)` on all 21 rifles including `DLC_SpartanGun`):
          * Uses `RIFLE_UPPER_HOLD_LOCAL_FORWARD` & `RIFLE_UPPER_HOLD_LOCAL_UP` so on vanilla rifles
            the arrow points 100% straight along the barrel (`+Y_bl`) and the U-cradle sits upright (`-X_bl`)!
      - CROSSBOW (`q_wxyz = (0.7071, 0.0, 0.7071, 0.0)` on all 12 crossbows):
          * Uses `CROSSBOW_UPPER_HOLD_LOCAL_FORWARD` (`local +Y`) & `CROSSBOW_UPPER_HOLD_LOCAL_UP` (`local -Z`).
      - MELEE 2H (`q_wxyz = (1.0, 0.0, 0.0, 0.0)` on all 15 greatswords/hammers/katanas):
          * Uses `local +Y` (blade/shaft axis) & `local -X` (cutting edge).
    """
    q = rot_mat.to_quaternion().normalized()
    dot_rifle = abs(q.dot(Q_RIFLE_UPPER_HOLD_CANONICAL))
    dot_crossbow = abs(q.dot(Q_CROSSBOW_UPPER_HOLD_CANONICAL))
    dot_melee = abs(q.w)  # dot with Identity (1,0,0,0)

    if weapon_mode == "CROSSBOW" or (dot_crossbow > dot_rifle and dot_crossbow > dot_melee):
        forward_dir = (rot_mat @ CROSSBOW_UPPER_HOLD_LOCAL_FORWARD).normalized()
        up_dir = (rot_mat @ CROSSBOW_UPPER_HOLD_LOCAL_UP).normalized()
    elif weapon_mode in ("RIFLE", "PISTOL") or (dot_rifle >= dot_melee and dot_rifle >= dot_crossbow):
        forward_dir = (rot_mat @ RIFLE_UPPER_HOLD_LOCAL_FORWARD).normalized()
        up_dir = (rot_mat @ RIFLE_UPPER_HOLD_LOCAL_UP).normalized()
    else:
        forward_dir = (rot_mat @ Vector((0.0, 1.0, 0.0))).normalized()
        up_dir = (rot_mat @ Vector((-1.0, 0.0, 0.0))).normalized()

    side_dir = forward_dir.cross(up_dir)
    if side_dir.length_squared < 1e-4:
        side_dir = Vector((0.0, 0.0, 1.0))
    side_dir.normalize()
    up_dir = side_dir.cross(forward_dir).normalized()

    # Exact Hero left-forearm / wrist entry axis (`local +Z` on Rifle/Crossbow UpperHold):
    wrist_axis = (rot_mat @ Vector((0.0, 0.0, 1.0))).normalized()
    return forward_dir, up_dir, side_dir, wrist_axis


def _build_left_foregrip_gizmo(origin, rot_mat, weapon_mode="RIFLE"):
    """
    Builds a directional 3D Left-Hand Foregrip Cradle gizmo for `Weapon.Weapon.UpperHold`
    compensated for the canonical Rifle (`DLC_SpartanGun`), Crossbow, or 2H Melee rotation:
      - U-shaped palm & finger cradle sits flush beneath the forend (`origin - up_dir * 0.015`)
        and wraps up both sides (`±side_dir * 0.046`, `+up_dir * 0.062`) of the barrel/stock.
      - `FOREGRIP +Y ➔` arrow points 100% straight forward along `+Y_bl` on vanilla rifles & crossbows.
      - Left wrist cuff shows the natural left-hand entry angle from beneath the foregrip.
    """
    forward_dir, up_dir, side_dir, wrist_axis = _resolve_upper_hold_gizmo_vectors(rot_mat, weapon_mode)

    lines = []
    # 3 U-shaped cradle ribs cupping the underside and left/right sides of the rifle forend
    for f_off in (-0.032, 0.0, 0.032):
        center = origin + forward_dir * f_off
        u_pts = [
            center + up_dir * 0.062 - side_dir * 0.046,
            center + up_dir * 0.008 - side_dir * 0.042,
            center - up_dir * 0.016 - side_dir * 0.022,
            center - up_dir * 0.020,
            center - up_dir * 0.016 + side_dir * 0.022,
            center + up_dir * 0.008 + side_dir * 0.042,
            center + up_dir * 0.062 + side_dir * 0.046,
        ]
        _add_polyline(lines, u_pts, closed=False)

    # Longitudinal rails connecting the front and rear cradle ribs
    lines.extend([
        origin - forward_dir * 0.032 - up_dir * 0.020,
        origin + forward_dir * 0.032 - up_dir * 0.020,
        origin - forward_dir * 0.032 + up_dir * 0.062 - side_dir * 0.046,
        origin + forward_dir * 0.032 + up_dir * 0.062 - side_dir * 0.046,
        origin - forward_dir * 0.032 + up_dir * 0.062 + side_dir * 0.046,
        origin + forward_dir * 0.032 + up_dir * 0.062 + side_dir * 0.046,
    ])

    # Left wrist / forearm entry line extending along `wrist_axis` from bottom of palm
    palm_bot = origin - up_dir * 0.020
    wrist_end = palm_bot + wrist_axis * 0.055
    lines.extend([palm_bot, wrist_end])
    lines.extend([wrist_end - side_dir * 0.020, wrist_end + side_dir * 0.020])

    # Forward barrel alignment arrow (`FOREGRIP +Y ➔`)
    lines.extend(_build_arrow_lines(origin + forward_dir * 0.032, forward_dir, length=0.16, head_size=0.032, up_vec=up_dir))
    return lines


def _build_muzzle_gizmo(origin, rot_mat):
    """Builds a directional Muzzle Cone + Bullet Trajectory Ray + Front Sight Post for `Weapon.Weapon.ProjectileEmitter`."""
    forward = (rot_mat @ Vector((0.0, 1.0, 0.0))).normalized()
    up_dir = (rot_mat @ Vector((-1.0, 0.0, 0.0))).normalized()
    side_dir = forward.cross(up_dir)
    if side_dir.length_squared < 1e-4:
        side_dir = Vector((0.0, 0.0, 1.0))
    side_dir.normalize()

    lines = []
    cone_len = 0.085
    cone_rad = 0.030
    segments = 10
    ring_center = origin + forward * cone_len
    ring = []
    for i in range(segments):
        ang = (2.0 * math.pi * i) / segments
        pt = ring_center + (side_dir * math.cos(ang) + up_dir * math.sin(ang)) * cone_rad
        ring.append(pt)

    for i in range(segments):
        nxt = (i + 1) % segments
        lines.extend([ring[i], ring[nxt]])
        if i % 2 == 0:
            lines.extend([origin, ring[i]])

    lines.extend(_build_arrow_lines(origin, forward, length=0.38, head_size=0.048, up_vec=up_dir))
    lines.extend([origin, origin + up_dir * 0.038])
    return lines


def _build_blade_top_gizmo(origin, rot_mat, hold_pos=None):
    """
    Builds an asymmetric 3D Spearhead Cap for `Weapon.Weapon.Top`:
      - Verified against `Katana_Master.glb` vertex cross-section:
          * `+X_bl` (`rot_mat @ (+1, 0, 0)`) is the THICK DULL SPINE (Back Edge, facing up/back toward the Hero).
          * `-X_bl` (`rot_mat @ (-1, 0, 0)`) is the RAZOR SHARP CUTTING EDGE (Front Edge, facing enemies).
      - Draws a swept-back spearhead whose open base faces `Weapon.Weapon.Hold`, with a prominent
        `▲ SPINE (+X)` dorsal fin on the `+X` spine side!
    """
    if hold_pos is not None and (origin - hold_pos).length > 1e-3:
        tip_forward = (origin - hold_pos).normalized()
    else:
        tip_forward = (rot_mat @ Vector((0.0, 1.0, 0.0))).normalized()

    # +X is the thick dull spine (up/back when held); -X is the sharp cutting edge (down/forward when held)
    spine_up = (rot_mat @ Vector((1.0, 0.0, 0.0))).normalized()
    spine_up = spine_up - tip_forward * spine_up.dot(tip_forward)
    if spine_up.length_squared < 1e-4:
        spine_up = Vector((1.0, 0.0, 0.0))
    spine_up.normalize()
    side_dir = tip_forward.cross(spine_up).normalized()

    lines = []
    apex = origin + tip_forward * 0.038
    base_center = origin - tip_forward * 0.055
    b_spine = base_center + spine_up * 0.032
    b_edge = base_center - spine_up * 0.032
    b_left = base_center + side_dir * 0.016
    b_right = base_center - side_dir * 0.016

    lines.extend([
        apex, b_spine,
        apex, b_edge,
        apex, b_left,
        apex, b_right,
        b_spine, b_left, b_left, b_edge, b_edge, b_right, b_right, b_spine,
    ])

    # Dorsal "▲ SPINE (+X)" fin on the +X spine side
    spine_peak = base_center + tip_forward * 0.02 + spine_up * 0.058
    lines.extend([b_spine, spine_peak, spine_peak, apex])

    if hold_pos is not None:
        lines.extend([hold_pos, origin])
        span = origin - hold_pos
        dist = span.length
        if dist > 0.15:
            for frac in (0.30, 0.55, 0.80):
                c_tip = hold_pos + span * frac
                c_back = c_tip - tip_forward * 0.035
                lines.extend([
                    c_tip, c_back + spine_up * 0.022,
                    c_tip, c_back - spine_up * 0.022,
                ])

    return lines


def _build_sheath_holster_gizmo(origin, rot_mat):
    """
    Builds a directional 3D Holster / Back-Sheath Mount gizmo for `Weapon.Weapon.Sheath`
    using the exact `Character.Carry.SheathWeaponBack` parent dummy transform from `CH_HeroMale_Morph_Body.mdl.gmd`:
      - `TRUE GROUND (-Z_char)` in `Sheath` local space is `SHEATH_LOCAL_TO_GROUND = (+0.4681, +0.8765, +0.1110)`,
        which is rotated 28.1° clockwise from `local +Y` because the scabbard hangs diagonally across the Hero's back!
      - `INTO HERO BACK (+Y_char)` in `Sheath` local space is `SHEATH_LOCAL_TO_BODY = (-0.0001, -0.1256, +0.9919)`.
      - Draws:
          1. Rectangular Body-Contact Plate lying parallel to the Hero's back.
          2. Long `TO GROUND ▼` arrow along `rot_mat @ SHEATH_LOCAL_TO_GROUND` (true vertical down in-game!).
          3. Shorter `SCABBARD AXIS` tick along `rot_mat @ (0, 1, 0)` (the 28.1° diagonal sling angle).
          4. Perpendicular `TO BODY` pin arrow along `rot_mat @ SHEATH_LOCAL_TO_BODY`.
    """
    to_ground = (rot_mat @ SHEATH_LOCAL_TO_GROUND).normalized()
    to_body = (rot_mat @ SHEATH_LOCAL_TO_BODY).normalized()
    scabbard_axis = (rot_mat @ Vector((0.0, 1.0, 0.0))).normalized()

    # In-plane horizontal axis across the Hero's back (perpendicular to both `to_body` and `to_ground`):
    across_back = to_ground.cross(to_body)
    if across_back.length_squared < 1e-4:
        across_back = Vector((1.0, 0.0, 0.0))
    across_back.normalize()

    lines = []
    # 1. Rectangular Body-Contact Plate oriented with True Ground & Across-Back
    p1 = origin + across_back * 0.032 - to_ground * 0.032
    p2 = origin + across_back * 0.032 + to_ground * 0.052
    p3 = origin - across_back * 0.032 + to_ground * 0.052
    p4 = origin - across_back * 0.032 - to_ground * 0.032
    _add_polyline(lines, [p1, p2, p3, p4], closed=True)
    lines.extend([p1, p3, p2, p4])

    # 2. Primary "TO GROUND ▼" arrow (exact -Z_char ground direction when holstered on the Hero's back!)
    lines.extend(_build_arrow_lines(origin, to_ground, length=0.18, head_size=0.036, up_vec=to_body))

    # 3. Secondary tick showing the 28.1° diagonal scabbard/blade line (`local +Y`)
    lines.extend([origin, origin + scabbard_axis * 0.11])

    # 4. Perpendicular "TO BODY" pin arrow showing which side presses against the Hero's back
    lines.extend(_build_arrow_lines(origin, to_body, length=0.085, head_size=0.024, up_vec=to_ground))

    return lines


def _build_starburst_lines(origin, radius=0.036):
    d = radius * 0.7071
    return [
        origin - Vector((radius, 0, 0)), origin + Vector((radius, 0, 0)),
        origin - Vector((0, radius, 0)), origin + Vector((0, radius, 0)),
        origin - Vector((0, 0, radius)), origin + Vector((0, 0, radius)),
        origin - Vector((d, d, 0)), origin + Vector((d, d, 0)),
        origin - Vector((d, -d, 0)), origin + Vector((d, -d, 0)),
    ]


def _draw_callback_3d():
    if not _overlay_enabled:
        return

    context = bpy.context
    sockets = find_all_gmd_sockets(context)
    if not sockets:
        return

    try:
        shader = gpu.shader.from_builtin("UNIFORM_COLOR")
    except Exception:
        shader = gpu.shader.from_builtin("3D_UNIFORM_COLOR")

    gpu.state.blend_set("ALPHA")
    gpu.state.line_width_set(2.0)

    hold_lines = []       # Cyan: Weapon.Weapon.Hold / Prop.Carry.*
    upper_hold_lines = [] # Spring Green: Weapon.Weapon.UpperHold
    muzzle_lines = []     # Orange: Weapon.Weapon.ProjectileEmitter
    blade_lines = []      # Magenta: Weapon.Weapon.Top + Hold->Top directional spine
    sheath_lines = []     # Sky Blue: Weapon.Weapon.Sheath
    fx_lines = []         # Gold: Weapon.FX.Particle.* / Prop.FX.Particle.*

    hold_pos = None
    hold_quat = None
    for obj, cname in sockets:
        if cname == "Weapon.Weapon.Hold":
            hold_pos = obj.matrix_world.to_translation()
            hold_quat = obj.matrix_world.to_quaternion()
            break

    weapon_mode = detect_scene_weapon_mode(sockets, hold_quat=hold_quat)

    for obj, cname in sockets:
        mw = obj.matrix_world
        origin = mw.to_translation()
        rot = mw.to_3x3()
        clower = cname.lower()

        if cname == "Weapon.Weapon.Hold" or clower.startswith("prop.carry.") or clower.startswith("character.carry."):
            mode_for_sock = "MELEE" if clower.startswith("prop.carry.") else weapon_mode
            hold_lines.extend(_build_right_hand_grip_gizmo(origin, rot, mode_for_sock))

        elif cname == "Weapon.Weapon.UpperHold":
            upper_hold_lines.extend(_build_left_foregrip_gizmo(origin, rot, weapon_mode))

        elif "projectileemitter" in clower or "projectile" in clower:
            muzzle_lines.extend(_build_muzzle_gizmo(origin, rot))

        elif cname == "Weapon.Weapon.Top":
            blade_lines.extend(_build_blade_top_gizmo(origin, rot, hold_pos=hold_pos))

        elif cname == "Weapon.Weapon.Sheath":
            sheath_lines.extend(_build_sheath_holster_gizmo(origin, rot))

        elif ".fx." in clower or "particle" in clower or "light" in clower:
            fx_lines.extend(_build_starburst_lines(origin, radius=0.036))

        else:
            sheath_lines.extend(_build_sheath_holster_gizmo(origin, rot))

    shader.bind()

    if hold_lines:
        shader.uniform_float("color", (0.18, 0.95, 0.88, 0.95))
        batch_for_shader(shader, "LINES", {"pos": hold_lines}).draw(shader)

    if upper_hold_lines:
        shader.uniform_float("color", (0.35, 0.98, 0.48, 0.95))
        batch_for_shader(shader, "LINES", {"pos": upper_hold_lines}).draw(shader)

    if muzzle_lines:
        shader.uniform_float("color", (1.0, 0.58, 0.12, 0.96))
        batch_for_shader(shader, "LINES", {"pos": muzzle_lines}).draw(shader)

    if blade_lines:
        shader.uniform_float("color", (0.92, 0.38, 1.0, 0.95))
        batch_for_shader(shader, "LINES", {"pos": blade_lines}).draw(shader)

    if sheath_lines:
        shader.uniform_float("color", (0.42, 0.72, 1.0, 0.92))
        batch_for_shader(shader, "LINES", {"pos": sheath_lines}).draw(shader)

    if fx_lines:
        shader.uniform_float("color", (1.0, 0.88, 0.25, 0.92))
        batch_for_shader(shader, "LINES", {"pos": fx_lines}).draw(shader)

    gpu.state.line_width_set(1.0)
    gpu.state.blend_set("NONE")


def _draw_sub_label(region, rv3d, font_id, world_pt, text, rgba):
    sp = location_3d_to_region_2d(region, rv3d, world_pt)
    if sp is None:
        return
    blf.size(font_id, 10)
    blf.color(font_id, *rgba)
    blf.position(font_id, sp.x + 4, sp.y - 4, 0)
    blf.draw(font_id, text)


def _draw_callback_2d():
    if not _overlay_enabled:
        return

    context = bpy.context
    region = context.region
    rv3d = context.region_data
    if region is None or rv3d is None:
        return

    sockets = find_all_gmd_sockets(context)
    if not sockets:
        return

    font_id = 0
    placed_screen_coords = []

    hold_pos = None
    hold_quat = None
    for obj, cname in sockets:
        if cname == "Weapon.Weapon.Hold":
            hold_pos = obj.matrix_world.to_translation()
            hold_quat = obj.matrix_world.to_quaternion()
            break

    weapon_mode = detect_scene_weapon_mode(sockets, hold_quat=hold_quat)

    for obj, cname in sockets:
        mw = obj.matrix_world
        world_pos = mw.to_translation()
        rot = mw.to_3x3()
        screen_pos = location_3d_to_region_2d(region, rv3d, world_pos)
        if screen_pos is None:
            continue

        stack_offset = 0
        for prev_x, prev_y in placed_screen_coords:
            if abs(screen_pos.x - prev_x) < 12 and abs(screen_pos.y - prev_y) < 12:
                stack_offset += 16
        placed_screen_coords.append((screen_pos.x, screen_pos.y))

        clower = cname.lower()
        blf.size(font_id, 12)

        if cname == "Weapon.Weapon.Hold" or clower.startswith("prop.carry."):
            mode_for_sock = "MELEE" if clower.startswith("prop.carry.") else weapon_mode
            blf.color(font_id, 0.25, 0.98, 0.90, 1.0)
            tag = f"✋ {cname} ({mode_for_sock} Right-Hand Grip)"
            blf.position(font_id, screen_pos.x + 10, screen_pos.y + 4 - stack_offset, 0)
            blf.draw(font_id, tag)

            (
                primary_dir,
                grip_up,
                knuckle_front,
                _,
                top_or_spine,
                hand_thumb_dir,
            ) = _resolve_hold_gizmo_vectors(rot, mode_for_sock)

            if mode_for_sock in ("PISTOL", "RIFLE", "CROSSBOW"):
                aim_tip = world_pos + top_or_spine * 0.038 + primary_dir * 0.27
                _draw_sub_label(region, rv3d, font_id, aim_tip, "IN-GAME AIM ➔", (0.25, 1.0, 0.92, 0.95))
                _draw_sub_label(region, rv3d, font_id, world_pos - knuckle_front * 0.085, "WRIST", (0.20, 0.85, 0.80, 0.85))
            else:
                _draw_sub_label(region, rv3d, font_id, world_pos + primary_dir * 0.23, "BLADE +Y ➔", (0.25, 1.0, 0.92, 0.95))
                _draw_sub_label(region, rv3d, font_id, world_pos + knuckle_front * 0.12, "CUTTING EDGE (-X)", (0.25, 1.0, 0.92, 0.90))
                if hand_thumb_dir is not None:
                    _draw_sub_label(region, rv3d, font_id, world_pos + hand_thumb_dir * 0.17, "HERO HAND (~12°)", (0.20, 0.85, 0.80, 0.85))

        elif cname == "Weapon.Weapon.UpperHold":
            blf.color(font_id, 0.42, 0.98, 0.55, 1.0)
            tag = f"🤚 {cname} (Left-Hand 2H Cradle)"
            blf.position(font_id, screen_pos.x + 10, screen_pos.y + 4 - stack_offset, 0)
            blf.draw(font_id, tag)

            fwd_u, _, _, _ = _resolve_upper_hold_gizmo_vectors(rot, weapon_mode)
            _draw_sub_label(region, rv3d, font_id, world_pos + fwd_u * 0.20, "FOREGRIP +Y ➔", (0.45, 1.0, 0.58, 0.95))

        elif "projectileemitter" in clower:
            blf.color(font_id, 1.0, 0.68, 0.20, 1.0)
            tag = f"💥 {cname} (Bullet Spawn)"
            blf.position(font_id, screen_pos.x + 10, screen_pos.y + 4 - stack_offset, 0)
            blf.draw(font_id, tag)

            forward = (rot @ Vector((0.0, 1.0, 0.0))).normalized()
            _draw_sub_label(region, rv3d, font_id, world_pos + forward * 0.39, "FIRE ➔", (1.0, 0.72, 0.22, 0.95))

        elif cname == "Weapon.Weapon.Top":
            blf.color(font_id, 0.95, 0.50, 1.0, 1.0)
            tag = f"⚔️ {cname} (Blade Tip Spearhead)"
            blf.position(font_id, screen_pos.x + 10, screen_pos.y + 4 - stack_offset, 0)
            blf.draw(font_id, tag)

            tip_forward = (
                (world_pos - hold_pos).normalized()
                if (hold_pos and (world_pos - hold_pos).length > 1e-3)
                else (rot @ Vector((0.0, 1.0, 0.0))).normalized()
            )
            spine_up = (rot @ Vector((1.0, 0.0, 0.0))).normalized()
            _draw_sub_label(region, rv3d, font_id, world_pos + tip_forward * 0.045, "TIP ➔", (0.98, 0.58, 1.0, 0.95))
            _draw_sub_label(region, rv3d, font_id, world_pos - tip_forward * 0.035 + spine_up * 0.062, "▲ SPINE (+X)", (0.95, 0.55, 1.0, 0.90))
            _draw_sub_label(region, rv3d, font_id, world_pos - tip_forward * 0.055 - spine_up * 0.038, "▼ EDGE (-X)", (0.90, 0.45, 0.95, 0.80))

        elif cname == "Weapon.Weapon.Sheath":
            blf.color(font_id, 0.52, 0.80, 1.0, 1.0)
            tag = f"🛡️ {cname} (Holster / Back Plate)"
            blf.position(font_id, screen_pos.x + 10, screen_pos.y + 4 - stack_offset, 0)
            blf.draw(font_id, tag)

            to_ground = (rot @ SHEATH_LOCAL_TO_GROUND).normalized()
            to_body = (rot @ SHEATH_LOCAL_TO_BODY).normalized()
            _draw_sub_label(region, rv3d, font_id, world_pos + to_ground * 0.19, "TO GROUND ▼", (0.55, 0.82, 1.0, 0.95))
            _draw_sub_label(region, rv3d, font_id, world_pos + to_body * 0.09, "TO HERO BACK", (0.55, 0.82, 1.0, 0.90))

        elif ".fx." in clower or "particle" in clower:
            blf.color(font_id, 1.0, 0.90, 0.32, 1.0)
            tag = f"✨ {cname}"
            blf.position(font_id, screen_pos.x + 10, screen_pos.y + 4 - stack_offset, 0)
            blf.draw(font_id, tag)

        else:
            blf.color(font_id, 0.85, 0.85, 0.92, 1.0)
            tag = f"🎯 {cname}"
            blf.position(font_id, screen_pos.x + 10, screen_pos.y + 4 - stack_offset, 0)
            blf.draw(font_id, tag)


def ensure_draw_handlers():
    global _draw_handler_3d, _draw_handler_2d
    if _draw_handler_3d is None:
        _draw_handler_3d = bpy.types.SpaceView3D.draw_handler_add(
            _draw_callback_3d, (), "WINDOW", "POST_VIEW"
        )
    if _draw_handler_2d is None:
        _draw_handler_2d = bpy.types.SpaceView3D.draw_handler_add(
            _draw_callback_2d, (), "WINDOW", "POST_PIXEL"
        )


def remove_draw_handlers():
    global _draw_handler_3d, _draw_handler_2d
    if _draw_handler_3d is not None:
        bpy.types.SpaceView3D.draw_handler_remove(_draw_handler_3d, "WINDOW")
        _draw_handler_3d = None
    if _draw_handler_2d is not None:
        bpy.types.SpaceView3D.draw_handler_remove(_draw_handler_2d, "WINDOW")
        _draw_handler_2d = None


def tag_redraw_all_viewports(context):
    if context and context.window_manager:
        for window in context.window_manager.windows:
            for area in window.screen.areas:
                if area.type == "VIEW_3D":
                    area.tag_redraw()


# =============================================================================
# Operators
# =============================================================================

class REFII_OT_spawn_weapon_rig(bpy.types.Operator):
    bl_idname = "refii.spawn_weapon_rig"
    bl_label = "Spawn Fable II Weapon / Prop Socket Rig"
    bl_description = "Creates or resets a GmdDummies Empty hierarchy with authentic Fable II .mdl.gmd sockets and orientations"
    bl_options = {"REGISTER", "UNDO"}

    preset: bpy.props.EnumProperty(
        name="Rig Type",
        items=[
            (
                "PISTOL",
                "Pistol / 1H Ranged",
                "Weapon.Weapon.Hold + Sheath + ProjectileEmitter + FXINV_Flintlock_Blast_01.par",
            ),
            (
                "RIFLE",
                "Rifle / Blunderbuss (2H Ranged)",
                "Weapon.Weapon.Hold + UpperHold + Sheath + ProjectileEmitter + FXINV_Flintlock_Blast_01.par",
            ),
            (
                "CROSSBOW",
                "Crossbow (2H Ranged)",
                "Weapon.Weapon.Hold + UpperHold + Sheath + ProjectileEmitter + FX_Flintlock_Trigger_01.par",
            ),
            (
                "MELEE_1H",
                "1H Sword / Axe / Cleaver / Mace",
                "Weapon.Weapon.Hold (Identity) + Sheath + Top",
            ),
            (
                "MELEE_2H",
                "2H Greatsword / Hammer / Katana",
                "Weapon.Weapon.Hold (Identity) + UpperHold + Sheath + Top",
            ),
            (
                "PROP_CARRIED",
                "Carried Prop / Item / Wooden Sword",
                "Prop.Carry.1Hand.Right (Identity) + Prop.Layout.Position",
            ),
        ],
        default="PISTOL",
    )

    def execute(self, context):
        spec = WEAPON_RIG_PRESETS.get(self.preset)
        if not spec:
            self.report({"ERROR"}, f"Unknown preset: {self.preset}")
            return {"CANCELLED"}

        created = []
        for idx, (sock_name, loc, quat_wxyz) in enumerate(spec["sockets"]):
            obj = create_or_update_socket_empty(
                context, sock_name, loc, quat_wxyz, index=idx, overwrite_transform=True
            )
            created.append(obj)

        if created:
            bpy.ops.object.select_all(action="DESELECT")
            created[0].select_set(True)
            context.view_layer.objects.active = created[0]

        ensure_draw_handlers()
        tag_redraw_all_viewports(context)
        self.report({"INFO"}, f"Spawned {len(created)} authentic Fable II sockets for {spec['label']}.")
        return {"FINISHED"}


class REFII_OT_add_single_socket(bpy.types.Operator):
    bl_idname = "refii.add_single_socket"
    bl_label = "Add / Select Fable II Socket"
    bl_description = "Creates or selects a specific Fable II .mdl.gmd attachment socket Empty under GmdDummies"
    bl_options = {"REGISTER", "UNDO"}

    socket_name: bpy.props.EnumProperty(
        name="Socket",
        items=[(s[0], s[1], s[2]) for s in STANDARD_SOCKETS],
    )

    def execute(self, context):
        entry = SOCKET_LOOKUP.get(self.socket_name)
        default_loc = entry[4] if entry else (0.0, 0.0, 0.0)
        default_quat = entry[5] if entry and len(entry) > 5 else (1.0, 0.0, 0.0, 0.0)
        obj = create_or_update_socket_empty(
            context, self.socket_name, default_loc, default_quat, overwrite_transform=False
        )

        bpy.ops.object.select_all(action="DESELECT")
        obj.select_set(True)
        context.view_layer.objects.active = obj

        ensure_draw_handlers()
        tag_redraw_all_viewports(context)
        self.report({"INFO"}, f"Selected socket '{self.socket_name}'.")
        return {"FINISHED"}


class REFII_OT_snap_socket_to_cursor(bpy.types.Operator):
    bl_idname = "refii.snap_socket_to_cursor"
    bl_label = "Snap Active Socket to 3D Cursor"
    bl_description = "Moves the active GMD socket Empty to Blender's 3D Cursor location (preserving its Fable II rotation)"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        obj = context.active_object
        cname = resolve_canonical_socket_name(obj)
        if not obj or not cname:
            self.report({"WARNING"}, "Select a GMD socket Empty first.")
            return {"CANCELLED"}

        obj.matrix_world.translation = context.scene.cursor.location.copy()
        tag_redraw_all_viewports(context)
        self.report({"INFO"}, f"Snapped '{cname}' to 3D Cursor.")
        return {"FINISHED"}


class REFII_OT_reset_socket_rotation(bpy.types.Operator):
    bl_idname = "refii.reset_socket_rotation"
    bl_label = "Reset Socket Rotation to Vanilla Fable II"
    bl_description = "Resets the active GMD socket (or all GMD sockets) to the canonical Fable II quaternion for the detected weapon type"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        sockets = find_all_gmd_sockets(context)
        if not sockets:
            self.report({"WARNING"}, "No GMD sockets found in scene.")
            return {"CANCELLED"}

        mode = detect_scene_weapon_mode(sockets)
        preset_key = {
            "PISTOL": "PISTOL",
            "RIFLE": "RIFLE",
            "CROSSBOW": "CROSSBOW",
            "MELEE": "MELEE_1H",
        }.get(mode, "MELEE_1H")
        preset_quats = {
            s_name: q_wxyz for s_name, _, q_wxyz in WEAPON_RIG_PRESETS[preset_key]["sockets"]
        }

        obj = context.active_object
        cname = resolve_canonical_socket_name(obj)
        targets = [(obj, cname)] if (obj and cname) else sockets

        count = 0
        for sock_obj, sock_name in targets:
            q_wxyz = preset_quats.get(sock_name)
            if q_wxyz is None:
                entry = SOCKET_LOOKUP.get(sock_name)
                q_wxyz = entry[5] if entry and len(entry) > 5 else (1.0, 0.0, 0.0, 0.0)
            sock_obj.rotation_mode = "QUATERNION"
            sock_obj.rotation_quaternion = Quaternion(q_wxyz).normalized()
            count += 1

        tag_redraw_all_viewports(context)
        self.report({"INFO"}, f"Reset rotation on {count} socket(s) to vanilla {mode} orientation.")
        return {"FINISHED"}


class REFII_OT_toggle_socket_overlay(bpy.types.Operator):
    bl_idname = "refii.toggle_socket_overlay"
    bl_label = "Toggle Refii 3D Socket Gizmos"
    bl_description = "Shows or hides the 3D wireframe hand gloves, holster contact plates, blade tip spearheads, and muzzle rays"

    def execute(self, context):
        global _overlay_enabled
        _overlay_enabled = not _overlay_enabled
        ensure_draw_handlers()
        tag_redraw_all_viewports(context)
        return {"FINISHED"}


class REFII_OT_export_refii_glb(bpy.types.Operator):
    bl_idname = "refii.export_glb"
    bl_label = "Export Refii .glb (+Y Up & GMD Sockets)"
    bl_description = (
        "Exports the scene as a Refii-ready .glb with +Y Up, Applied Modifiers, "
        "Normals/Tangents, and GMD Socket Custom Properties enabled"
    )

    filepath: bpy.props.StringProperty(subtype="FILE_PATH")
    filter_glob: bpy.props.StringProperty(default="*.glb", options={"HIDDEN"})

    def invoke(self, context, event):
        for obj, cname in find_all_gmd_sockets(context):
            obj["gmd_dummy_name"] = cname
            if "gmd_parent_node" not in obj:
                obj["gmd_parent_node"] = -1

        if not self.filepath:
            blend_path = bpy.data.filepath
            if blend_path:
                self.filepath = bpy.path.ensure_ext(blend_path, ".glb")
            else:
                self.filepath = "weapon_mod.glb"

        context.window_manager.fileselect_add(self)
        return {"RUNNING_MODAL"}

    def execute(self, context):
        target_path = bpy.path.ensure_ext(self.filepath, ".glb")

        for obj, cname in find_all_gmd_sockets(context):
            obj["gmd_dummy_name"] = cname
            if "gmd_parent_node" not in obj:
                obj["gmd_parent_node"] = -1

        export_kwargs = {
            "filepath": target_path,
            "export_format": "GLB",
            "export_yup": True,
            "export_apply": True,
            "export_extras": True,
            "export_normals": True,
            "export_tangents": True,
        }

        try:
            op_props = bpy.ops.export_scene.gltf.get_rna_type().properties.keys()
            filtered_kwargs = {k: v for k, v in export_kwargs.items() if k in op_props}
        except Exception:
            filtered_kwargs = export_kwargs

        res = bpy.ops.export_scene.gltf(**filtered_kwargs)
        if "FINISHED" in res:
            sock_count = len(find_all_gmd_sockets(context))
            self.report(
                {"INFO"},
                f"Exported Refii .glb to '{target_path}' ({sock_count} GMD socket(s) included).",
            )
            return {"FINISHED"}

        self.report({"ERROR"}, "glTF export failed.")
        return {"CANCELLED"}


# =============================================================================
# 3D Viewport Sidebar Panel (N-Panel -> "Refii Fable 2")
# =============================================================================

class REFII_PT_main_panel(bpy.types.Panel):
    bl_label = "Refii: Fable II Mod Tools"
    bl_idname = "REFII_PT_main_panel"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Refii Fable 2"

    def draw(self, context):
        layout = self.layout
        sockets = find_all_gmd_sockets(context)

        # Section 1: 3D Overlay Toggle
        box_vis = layout.box()
        row = box_vis.row(align=True)
        row.operator(
            "refii.toggle_socket_overlay",
            text="Hide 3D Socket Gizmos" if _overlay_enabled else "Show 3D Socket Gizmos",
            icon="HIDE_OFF" if _overlay_enabled else "HIDE_ON",
            depress=_overlay_enabled,
        )
        col_legend = box_vis.column(align=True)
        col_legend.scale_y = 0.85
        col_legend.label(text="✋ Cyan 3D Hand Glove: Right Hand + Compensated Aim")
        col_legend.label(text="🤚 Green U-Cradle: Left-Hand 2H Foregrip Support")
        col_legend.label(text="💥 Orange Cone+Sight: Muzzle & Bullet Fire Ray")
        col_legend.label(text="⚔️ Magenta Spearhead: Blade Tip (+X Spine / -X Edge)")
        col_legend.label(text="🛡️ Blue Contact Plate: Holster (TO HERO BACK + GROUND)")
        col_legend.label(text="✨ Gold Starburst: Weapon.FX.Particle.* (Muzzle/FX)")

        # Section 2: 1-Click Weapon & Prop Rig Spawners
        box_rigs = layout.box()
        box_rigs.label(text="1-Click Weapon & Prop Rigs:", icon="ARMATURE_DATA")
        col_rigs = box_rigs.column(align=True)

        op = col_rigs.operator("refii.spawn_weapon_rig", text="🔫 Spawn Pistol Rig (1H Ranged)")
        op.preset = "PISTOL"

        op = col_rigs.operator("refii.spawn_weapon_rig", text="🎯 Spawn Rifle / Blunderbuss Rig (2H)")
        op.preset = "RIFLE"

        op = col_rigs.operator("refii.spawn_weapon_rig", text="🏹 Spawn Crossbow Rig (2H)")
        op.preset = "CROSSBOW"

        op = col_rigs.operator("refii.spawn_weapon_rig", text="⚔️ Spawn 1H Melee Sword / Mace Rig")
        op.preset = "MELEE_1H"

        op = col_rigs.operator("refii.spawn_weapon_rig", text="🗡️ Spawn 2H Greatsword / Hammer Rig")
        op.preset = "MELEE_2H"

        op = col_rigs.operator("refii.spawn_weapon_rig", text="🍺 Spawn Carried Prop / Item Rig")
        op.preset = "PROP_CARRIED"

        # Section 3: Add Individual Socket & Snap Tools
        box_single = layout.box()
        box_single.label(text="Individual Sockets & Alignment:", icon="EMPTY_AXIS")
        box_single.operator_menu_enum("refii.add_single_socket", "socket_name", text="➕ Add / Select Socket...")
        box_single.operator("refii.snap_socket_to_cursor", icon="PIVOT_CURSOR")
        box_single.operator("refii.reset_socket_rotation", icon="FILE_REFRESH")

        # Section 4: Active Scene Sockets List
        box_list = layout.box()
        box_list.label(text=f"Sockets in Scene ({len(sockets)}):", icon="OUTLINER_OB_EMPTY")
        if not sockets:
            box_list.label(text="No GmdDummies sockets in scene yet.")
        else:
            col_list = box_list.column(align=True)
            for obj, cname in sockets:
                row = col_list.row(align=True)
                is_active = (context.active_object == obj)
                row.label(text=cname, icon="RADIOBUT_ON" if is_active else "EMPTY_AXIS")

        # Section 5: 1-Click Refii .glb Exporter
        box_exp = layout.box()
        box_exp.label(text="Export for Refii Mod Studio:", icon="EXPORT")
        col_exp = box_exp.column(align=True)
        col_exp.scale_y = 1.35
        col_exp.operator("refii.export_glb", icon="CHECKMARK")


# =============================================================================
# Registration
# =============================================================================

CLASSES = (
    REFII_OT_spawn_weapon_rig,
    REFII_OT_add_single_socket,
    REFII_OT_snap_socket_to_cursor,
    REFII_OT_reset_socket_rotation,
    REFII_OT_toggle_socket_overlay,
    REFII_OT_export_refii_glb,
    REFII_PT_main_panel,
)


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)
    ensure_draw_handlers()


def unregister():
    remove_draw_handlers()
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)


if __name__ == "__main__":
    register()
