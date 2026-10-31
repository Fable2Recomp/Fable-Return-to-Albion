# Effects

Every effect on screen is a `FableFx` node drawing the particle bank
(`art/particles/particle_bank.bnk`). The game states WHICH effect plays and WHERE; Godot draws it.
A mod changes effects three ways, all through that one bank and that one node.

## Contents

- [Three ways in](#three-ways-in)
- [Mod effects: fx/effects/*.json](#mod-effects-fxeffectsjson)
  - [Fields](#fields)
  - [The overlay rules](#the-overlay-rules)
- [play_fx](#play_fx)
  - [Options](#options)
  - [Where an effect stands](#where-an-effect-stands)
  - [Helper points](#helper-points)
  - [Recipes that follow a played effect](#recipes-that-follow-a-played-effect)
- [Lights](#lights)
- [The FX Lab and the inspector](#the-fx-lab-and-the-inspector)
- [Script API](#script-api)

## Three ways in

| Way | File or call | What it does |
|---|---|---|
| **Recipes** | `fx/recipes.json` | dress the live effects the game plays while a mod value holds — a tint, intensity and size, a light, attached effects, authored pieces |
| **Effects** | `fx/effects/<name>.json` | the bank's own model as JSON, overlaid on the shipped bank as it loads: new effects, replaced effects, replaced emitters |
| **play_fx** | native, Lua or Godot | draws any bank or mod effect where a mod says |

The game's own bank is never rewritten; the overlay happens as Godot loads it, so the game, the
editor's bank view, `FableFx` in a scene and `play_fx` all draw from one bank.

## Mod effects: fx/effects/*.json

One effect per file, `fx/effects/<name>.json` (or `{ "effects": [ ... ] }` for several):

```json
{
  "format": 1,
  "name": "my_mod_green_torch",
  "updateFrequency": 1,
  "scale": 1,
  "tint": [0.5, 1, 0.6],
  "parts": [ { "emitter": 0, "system": 0, "group": 1, "vector": 0 } ],
  "emitters": [
    { "subType": 1, "maxActive": 48, "initialNum": 4, "spawnRadius": 0.15,
      "params": { "rate": [[0, 24]], "speed": [[0, 0.6]], "speedSpread": [[0, 0.2]], "velZ": [[0, 1]] },
      "vectors": { "vec1": [[0, 1, 1, 1]] } }
  ],
  "systems": [
    { "lastStartFrames": 30, "childCount": 1,
      "visuals": [
        { "hasTexture": true, "texture": "art\\fx\\pfx_textures\\fxt_flames_double_03.tex", "texCols": 4, "texRows": 4,
          "frameSpeed": 20, "hasColor": true, "color": [0.4, 1, 0.5], "colorSq": [0.16, 1, 0.25],
          "hasSize": true, "sizeX": 0.5, "sizeY": 0.7 }
      ] }
  ]
}
```

### Fields

- **Keys are the model's fields** in camelCase: every public field of an emitter, a system and a
  visual, plus the effect's `name`, `zSortEmitters`, `updateFrequency` (0 = 15 Hz, 1 = 30 Hz),
  `scale` and `tint`. A key left out takes the model's default; the writer leaves out every field
  at its default. Unknown keys are warnings (ignored).
- **Hashes are text**: `entityId`, `haloTextureHash` and a system's `id` are `"0x…"` (a number is
  read too).
- **Entity types** (`typeId`): 0 standard emitter, 3 orbital, 4 attractor, 5 spline, 6 root,
  7 point light, 8 spot light, 9 spline emitter.
- **Parameters**: `params.<name>` is a timeline `[[seconds, value], …]` — `rate`, `rateSpread`,
  `speed`, `speedSpread`, `accelDirScale`, `gravityScale` (1 = normal gravity),
  `globalWindScale`, `velX`, `velY`, `velZ` (the spawn direction), `wind`, `lightRange`,
  `lightIntensity`, `lightCutoff`, `lightFadeRamp`, `lightConeAngle`, `attractRadius`,
  `attractStrength`, or `p0`..`p27`. `vectors.vec0|vec1|vec2|lightColour` are
  `[[seconds, x, y, z], …]`. The FIRST key (file order) starts the timeline, one key is a
  constant, two or more are resampled; a timeline whose first key is before 0 s never starts.
- **Parts** index the file's own `emitters` and `systems` (`system` / `trail` -1 or left out:
  none). `vector` is the chain an entity belongs to (each chain parents its entities in order);
  `group` is the update bucket.
- **`rate` is particles per FRAME** (30 Hz). 40 a second is `[[0, 1.3333]]`.
- **`scale` and `tint`** are the effect's own size and colour multipliers. Identity for every
  shipped effect.
- **`spin`** (an emitter's, radians per second): every particle of the emitter turns about the
  emitter's own Z axis after each step — a twister's swirl. Positive is counter-clockwise looking
  down the axis.
- **`hasMeshAlign` / `meshAlign`** (a mesh visual's, `[x, y, z, w]`): the rest orientation a mesh
  particle is born with, in the emitter's frame. `orAxis` is stated in the frame the particles are
  stored in (the effect's root, for `attachToRoot`), so `[0, 0, -1]` with the orbit's rate keeps a
  blade laid along its orbit.
- `scale`, `tint`, `spin` and `meshAlign` reach the classic look too, so both looks draw the same
  thing.

A file is checked before it loads, by the same reader the game uses, with each issue's place
(`emitters[2].params.rate`). A Studio or `RefiiModApi` write with an error is refused.

### The overlay rules

Applied in load order (the highest-priority mod last, and winning):

1. An emitter or system stated **exactly** as the bank holds it is a reference: the effect draws
   the shipped record — how an effect chains a shipped effect's own emitters.
2. A **new** effect name adds an effect.
3. A **shipped** effect name (or an `FX_XXXXXXXX` token) replaces that effect: every level
   placement and every live game spawn of it draws the mod's parts.
4. An emitter whose `entityId` the bank holds replaces that emitter's parameters **wherever the
   bank uses it** — in every effect that shares it — so the game's own spawns pick the change up.
   An emitter without one takes an id derived from its effect and slot and is added.
5. A system whose `id` the bank holds is replaced the same way; one without is added.
6. Two mods stating the same effect, `entityId` or system `id` are a conflict, tallied in the
   Mods panel beside file and text conflicts. The later mod wins.

A node already built keeps what it built; a remount that changes the mods' effects re-overlays
the bank for the next one.

## play_fx

| Surface | Calls |
|---|---|
| Native | `uint32_t refii_mod_play_fx(name, xyz, options)` · `refii_mod_play_fx_on(name, entity, options)` · `refii_mod_play_fx_at(name, entity, helper, options)` · `refii_mod_stop_fx(id)` |
| Lua | `Refii.play_fx(name, x, y, z [, options])` · `Refii.play_fx_on(e, name [, options])` · `Refii.stop_fx(id)` |
| Godot | `play_fx(name, x, y, z, options)` · `play_fx_on(name, handle, options)` · `stop_fx(id)` |

```cpp
refii_mod_fx_options o = refii_mod_fx_default_options();
o.duration = 1.5f;
o.tint[1] = 0.4f;
uint32_t id = refii_mod_play_fx("my_mod_green_torch", xyz, &o);
```

```lua
local id = Refii.play_fx("my_mod_green_torch", x, y, z, { duration = 1.5, scale = 0.8 })
Refii.play_fx_on(e.victim, "fxspl_blast_fire_ring", { loop = false, offset = {0, 0, 1} })
Refii.stop_fx(id)
```

```gdscript
var host = Engine.get_singleton("RefiiHost")
var id = host.play_fx("my_mod_green_torch", x, y, z, {"duration": 1.5, "tint": Color(0.5, 1, 0.6)})
host.play_fx_on("fxspl_blast_fire_ring", handle, {"offset": Vector3(0, 0, 1), "loop": false})
host.stop_fx(id)
```

- Any bank or mod effect by name (or `FX_XXXXXXXX`). Positions are the game's world axes, as
  every event states them. An entity is an address natively and a handle in Lua and Godot.
- Ids are shared: any surface's `stop_fx` stops any surface's id, and every id is exact as a Lua
  number. Everything a session played stops when the session ends.
- The game spawns nothing and is not told; a mod that wants the game to know uses the game's own
  calls. Native calls work from any thread.

### Options

| Option | Meaning |
|---|---|
| `duration` | seconds; 0 plays until `stop_fx`, or with `loop` false until the effect has run out (at most a minute) |
| `scale` | size multiplier |
| `tint` | `{r, g, b}` (Lua), `Color` or `[r, g, b]` (Godot) |
| `intensity` | an alpha term |
| `loop` | default true |
| `offset` | from the entity, along the entity's own axes |
| `at` | a helper point of the entity's model (below) |

### Where an effect stands

- **A world effect** stands unrotated at the stated point.
- **An entity effect** stands under that entity's drawn node IN THE ENTITY'S OWN FRAME: the
  effect's axes are the entity's, `offset` is along them, and it moves and turns with the entity
  (an effect authored along a sword's blade stays on the blade however the sword flies), freed
  with it.
- **An entity with no drawn body** of its own (a vortex, a fireball's shot) is followed by
  position, through its own live effect or the position the game states for it; the effect is
  unrotated and `offset` is in world axes.
- A request waits up to two seconds for its entity's node, then is dropped with a warning; an
  unknown effect name is warned once.

### Helper points

`at` names an attachment or node of the entity's `.mdl.gmd` (matched case-insensitively) and the
effect stands THERE, in the helper's own frame, with `offset` along the helper's axes. On a rigged
body the helper hangs from a bone and the effect follows the pose; on a rigid model it is the
model-space rest point. Under the classic look the helper's rest point is used. An unknown name is
warned once and the entity's own frame used.

| Model | Helper names |
|---|---|
| A weapon | `Weapon.Weapon.Hold` (the grip), `Weapon.Weapon.Top` (the tip), `Weapon.Weapon.Sheath` (the model origin) |
| A character (creatures share the scheme) | `Character.Carry.Hand.Right` (the weapon hand), `Character.Carry.Hand.Left`, `Character.Carry.SheathWeaponBack`, `Character.Carry.SheathWeaponFront`, `Character.Carry.SheathRangedWeaponBack`, `Character.Focal.Mouth`, `Character.Focal.Eye.Left` / `Eye.Right`, `Character.Focal.Chest`, `Character.Focal.Bum`, `Character.Focal.GameplayCamera`, `Character.Focal.RangedWeaponTarget`, `Character.FX.Particle.Effect_1.` (where the game's own body effect hangs), `Character.Layout.Light` |
| Anything | any name in a model's node table, a bone included (`Shadow_Head`, `Shadow_R_BlendHand`) |

### Recipes that follow a played effect

`"with": "<effect>"` in `fx/recipes.json` holds a recipe only where that effect plays: a base
effect node is dressed when a play of it rides the entity the game states the node belongs to —
its owner, what it is attached to, or the spell whose own update places it. A node stated to
another cast is never dressed by this play; only a node the game states no cast for is dressed by
a play standing within `withRadius` (default 10) of it. One cast's `play_fx_on` therefore dresses
that cast's own base effects and no other's. Spell Weaving plays each pair's look on what the cast
rides and tints the base spell's own effects with a `with` recipe.

## Lights

A mod places an omni light — or, with a cone stated, a spot — the way it plays an effect, on an
entity or at a world point:

| Surface | Calls |
|---|---|
| Native | `refii_mod_light(entity, xyz, rgb, energy, range, options)` · `refii_mod_light_remove(id)`; `refii_mod_light_options { offset, at, flicker, direction, cone, attenuation }` |
| Lua | `Refii.light(target, color, energy, range [, o])` · `Refii.light_remove(id)` |
| Godot | `light(target, color, energy, range, options)` · `light_remove(id)` · `lights()` |

From GDScript, `options` may be left off: `light(target, color, energy, range)`.

```lua
local torch = Refii.light(e.victim, {1, 0.6, 0.25}, 1.5, 6, { at = "Weapon.Weapon.Top", flicker = 0.3 })
Refii.light({x, y, z}, {0.4, 0.6, 1}, 2, 8)
-- a spot along the blade from its tip (the helper's own +Y is the blade), a 25-degree cone
local beam = Refii.light(e.victim, {0.6, 0.8, 1}, 3, 12, { at = "Weapon.Weapon.Top", direction = {0, 1, 0}, cone = 25 })
-- a spot at a point, shining straight down
Refii.light({x, y, z + 4}, {1, 1, 1}, 2, 10, { direction = {0, 0, -1}, cone = 40, attenuation = 2 })
Refii.light_remove(torch)
```

| Argument | Meaning |
|---|---|
| `target` | an entity handle (the light rides its drawn body, in its own frame) or a point `{x, y, z}` (world axes) |
| `color` | `{r, g, b}` |
| `energy` | 1 is a placed lamp's |
| `range` | metres (0: 6, at most 100) |
| `offset`, `at` | as `play_fx_on`'s |
| `flicker` | 0..1, wavers the energy |
| `direction`, `cone` | a SPOT when `cone` is above 0: `direction` (any length) is the axis it shines along, in the offset's axes — the entity's own, or the helper's with `at`, so a spot on a sword's tip turns with the blade; at a world point, world axes. `cone` is the half-angle in degrees (at most 89) |
| `attenuation` | the spot's edge falloff: 0 or 1 linear (the game's own form), higher tightens the beam (modern look only) |

- A mod light is a dynamic light: no shadow map, no shadow budget, baked into nothing.
- Under the classic look the light joins the game's own light list (at most 32 mod lights), so
  the classic renderer lights draws with it the way it lights its own.
- `lights()` lists what stands (`id`, `entity`, `r`, `g`, `b`, `energy`, `range`, `x`, `y`, `z`,
  and for a spot `cone`, `dx`, `dy`, `dz`). Ids share one space with the effects; any surface's
  `light_remove` takes any id. Everything goes when the game goes down.

## The FX Lab and the inspector

The Mod Studio's **✨ FX Lab** tab has two editors over one turntable:

- **Recipes**: `+ New recipe`, Delete, and every field — id, when and equals, with and
  withRadius, base, anchors, entities, everyNode, tint, intensity, size, the light (colour,
  energy, range, height, flicker), attached effects (every number, and their own tint), and
  pieces (kind, blend, texture, mesh, colour and end colour, height, radius, radiusTop, rate,
  speed, size, sizeEnd, count, period, width, tilt, intensity). The preview is the base effect
  dressed exactly as the game dresses it.
- **Effects**: pick an effect (★ marks the active mod's own). A bank effect opens as an OVERRIDE
  draft (its name and ids kept: saved, it replaces the shipped effect and its emitters); "Clone
  into mod" makes it a NEW effect under a new name. Edit the update rate, scale and tint; each
  entity's fields for its type; every parameter as an editable key list; spline points; the
  system and its visuals, with duplicate and remove. The turntable draws the draft on a preview
  bank — the shared bank is never touched. Save writes `fx/effects/<name>.json`.
- **Inspector**: a `FableFx` selected in a scene gets a card — "Save as mod effect" writes the
  effect it draws as a new one, carrying the node's `EffectScale` and `Tint` as the effect's own
  `scale` and `tint`; "Edit in FX Lab" opens it in the effect editor.

The **Effect** starter writes a green flame as `fx/effects/<mod>_green_flame.json` and an
`init.lua` that plays it where a hero's hit lands.

## Script API

`RefiiModApi`: `ListBankEffects()`, `ListModEffects()`, `GetEffectJson(name)`,
`SetEffectJson(json)`, `SetEffect(dict)`, `CloneEffect(source, name)` (a NEW effect: every id
cleared, nothing shipped changes), `OverrideEffect(name)` (the bank's effect as it stands, ids
kept: edit it and it replaces), `SetEmitterField(effect, emitter, key, value)`,
`SetVisualField(effect, system, visual, key, value)`, `SetEmitterKeys(effect, emitter, param,
[[s, v], …])`, `RemoveEffect(name)`, `SaveFableFxAsEffect(node, name)`; for recipes
`ListRecipes()`, `GetRecipe(id)`, `SetRecipe(dict)`, `RemoveRecipe(id)`.
