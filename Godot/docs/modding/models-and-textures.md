# Models and textures

Art reaches the game through the mod mount: a mod's file stands in for the shipped one of the
same path, converted in memory where it needs to be. Nothing is converted onto disk.

## Contents

- [How art reaches the game](#how-art-reaches-the-game)
- [Textures: .tex.png](#textures-texpng)
- [Models: .mdl.glb](#models-mdlglb)
  - [New morph targets](#new-morph-targets)
  - [What is carried, what is refused](#what-is-carried-what-is-refused)
  - [In the Studio](#in-the-studio)
- [Attachment sockets: .mdl.gmd](#attachment-sockets-mdlgmd)
- [Presentations: .bgf](#presentations-bgf)

## How art reaches the game

- A mod's `.bgf`, `.fac` or `.bsg` stands in for the shipped one of the same path; a `.bgf` no
  bank ships becomes a presentation of its own.
- A mod's `.tex` (or `.tex.png`) reaches every menu and HUD piece, including a texture a `.fac`
  names only by hash, and every material that uses it.
- A pattern book is built once per session; a mod enabled mid-session reaches books built after
  the remount.
- Inputs the mount converts: `.gdb.csv` (a database patch), `.babel.csv` (text rows), `.tex.png`
  (a texture) and `.mdl.glb` (a model).

## Textures: .tex.png

Ship `foo.tex.png` at the shipped texture's path — `art/gui/wheelicons/abilities/will_shock.tex.png`
— and the mount serves `foo.tex`. Textures refresh in place on a live reload, in every material
and menu holding them.

## Models: .mdl.glb

Ship `foo.mdl.glb` (or `.gltf` with its buffers embedded) at the shipped model's own path —
`art/models/furniture/chair.mdl.glb` — and the mount serves `art/models/furniture/chair.mdl`,
converted in memory against the shipped model.

- **The model being replaced is the template**: its bounds, material names, movement block, bones
  and the fields that pick its renderable class are carried; only the mesh comes from the glTF.
- Every primitive becomes one section keyed by its glTF material index (the material table grows
  to fit; the Studio's material slots then point each at a texture). The mesh node's transform is
  applied, the coarser LODs collapse onto the new mesh, and a surface with no normals gets smooth
  ones.
- **Axes**: glTF is Y-up and the model file is Z-up: a glTF `(x, y, z)` lands at model
  `(x, -z, y)`, so a model exported from the Studio and re-imported lands where it was.
- **Rigged models** (a creature, a weapon on a hand) need a rigged glTF whose skin joints are
  NAMED as the model's bones are — export the model first and rig to those names.
- Only the model's FIRST mesh part is replaced; the others are carried.

### What is carried, what is refused

Refused rather than guessed, by name:

- a joint the model has no bone for; an unrigged glTF over a skinned model;
- a model whose first part carries a cloth simulation or fur, or a tree/foliage part;
- a glTF with an external `.bin`; more than 65,535 vertices.

A replacement's sections carry no hide region, so a garment's "hide these clusters" does not apply
to a replaced body.

### New morph targets

A model at a path the game does NOT ship is refused — unless it is a MORPH TARGET: the shape a
character part blends toward (a MorphTargets pair's `TargetModel`, usually at a mod morph's weight;
see [Mod morphs](gameplay.md#mod-morphs)). Declare each one, with the part it morphs as its
template, in `morph_targets.json` at the mod's root:

```json
[
    { "model": "art/mymod/heromale_bulk.mdl", "template": "art/characters/heros/unclothed male/ch_heromale_morph_thin.mdl" }
]
```

and ship `art/mymod/heromale_bulk.mdl.glb`. It is converted against the template like a
replacement and mounted at its own path, where the game finds it by its hash
(`@model:art/mymod/heromale_bulk.mdl` in a row) and Godot draws it.

- **A target is its template with the vertices moved**: the same mesh parts, each with the same
  vertices in the same order and the same triangles. Export the template from the Studio, move
  vertices (no adding, deleting, splitting or re-ordering), and import. The game blends a part's
  models vertex by vertex, drops the WHOLE morph of the part — the shipped fat, strong and age
  shapes too — when one listed model will not load or its mesh count differs, and leaves a mesh
  unmorphed whose vertex count differs; so a target whose mesh parts, vertex counts or face
  counts differ from its template's is refused here, by name. Vertex ORDER cannot be checked from
  counts: a reordered target loads and blends into a tangle.
- Only the position and normal are blended (the tangent comes from the heaviest model); UVs,
  materials, bones and skin weights are the template's.

### In the Studio

Right-click a model → **Import .glb over <model> into [mod]…** converts it against the shipped
model first and places the glTF in the mod only if the mount would accept it, then remounts. Model
changes need a **relaunch** to be seen in the game: a mesh already placed in the world is not
rebuilt.

The Studio's GDB tab also has drop zones on every Model and Texture property: drop a `.glb` or
`.png`, and the Studio compiles it into the package (embedded PBR textures extracted, ORM maps
channel-packed). The Modding Guide's "Models, Textures & .gmd Sockets" page walks through it.

## Attachment sockets: .mdl.gmd

Every model has a `<model>.mdl.gmd` sidecar naming its attachment sockets: where a hand grips a
weapon (`Weapon.Weapon.Hold`), its tip (`Weapon.Weapon.Top`), where it sheathes
(`Weapon.Weapon.Sheath`), and the character's carry points. Exporting a model from the Studio
writes the sockets into the `.glb` as empties; importing reads them back. The same names are the
[helper points](effects.md#helper-points) effects and lights stand at. The Modding Guide's
socket table lists the standard names with a copy button.

## Presentations: .bgf

The Studio's **🖼️ UI** tab has a **Presentations** page: pick a presentation the mount serves (or
make a new one), add, clone, rename or move a node in its tree, and **Export .bgf into the mod** —
the file lands at the presentation's own bank path (`art/gui/gameface/<name>.bgf`), so the mount
serves it in the shipped one's place; a name no bank ships is a presentation of the mod's own.

- The writer is the game parser's exact inverse: every shipped presentation writes back
  byte-identical, and each export is read back before it is handed over.
- The `.fac` and `.bsg` sidecars travel with it: a cloned node takes its source's UV, text and
  scene records under its own id, and **Add quad** makes a textured quad from a texture path (a
  shipped texture, or the mod's own `art/.../foo.tex` shipped as `foo.tex.png`).
- The `.bgf` is the art and strings a template draws, not layout: a piece's place is
  `ui/hud.json` / `ui/templates.json` (see [UI and HUD](ui-and-hud.md)).
