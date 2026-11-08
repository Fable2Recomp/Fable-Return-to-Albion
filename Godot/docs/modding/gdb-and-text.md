# GDB and text overlays

The game's database (the GDB) defines every item, weapon, creature and effect; its text tables
(Babel) hold every string. A mod changes them by ROW, so two mods editing different rows both
apply.

## Contents

- [Database rows: .gdb.csv](#database-rows-gdbcsv)
  - [Rows](#rows)
  - [Keys, types and values](#keys-types-and-values)
  - [Mod morphs in rows](#mod-morphs-in-rows)
- [Binary patches: .gdb.tu](#binary-patches-gdbtu)
- [Text rows: .babel.csv](#text-rows-babelcsv)
- [Text keys](#text-keys)
- [Hashes from names](#hashes-from-names)
- [Live reload and its limits](#live-reload-and-its-limits)
- [Records in saves](#records-in-saves)

## Database rows: .gdb.csv

A `<database>.gdb.csv` (for example `globals/globals.gdb.csv`) is rows a person writes, compiled
at mount against the database they edit into the same sparse patch a `.gdb.tu` holds. Every mod's
patch merges with every other mod's through one compositor, in load order.

The Studio's **🗂️ GDB Hierarchy & Components** tab writes these for you: pick a record, edit a
component, Save. The Modding Guide's "GDB Records & Components" page explains inheritance and
overrides.

### Rows

A line is `entry,property,value` or `entry,property,type,value`. `#` starts a comment, a header
line `entry,property[,type],value` is skipped, and fields may be quoted.

| Row | Does |
|---|---|
| `Entry,Property,Value` | sets a property the entry already has, locally or by inheritance; the type comes from the database. An inherited property is set ON the entry, which is how the game's own data overrides a parent — the siblings keep the parent's value |
| `Entry,Property,type,Value` | ADDS a property the entry's chain does not have; a stated type on an existing property must match it |
| `NewEntry,parent,ParentEntry` | DECLARES an entry the database lacks; it must be the entry's first row. Its id is FNV-1 of the name (or the literal given); its other rows follow, inherited properties resolving through the parent |
| `NewEntry,@flags,N` | the new entry's schema flag byte (default: the parent's) |
| `Alias,@alias,Entry` | an extra name for an entry |

```csv
entry,property,value
# the Toy Gun's firearm component, by its id: twelve shots instead of the parent's
#2FDDF213,BulletCapacity,12
# a new firearm component derived from it, with its own capacity
MyToyGunFirearm,parent,#2FDDF213
MyToyGunFirearm,BulletCapacity,24
```

### Keys, types and values

- **Entries** are a name the database knows (an alias, or an id that is the name's FNV-1), a
  `#xxxxxxxx` / `0xXXXXXXXX` literal, or a name declared in this file. **Properties** are a name or
  a literal.
- **Types**: `bool int float string enum ref resref`, or `t<N>` by number.
- **Values**: bool `true`/`false`/`1`/`0`; int decimal or `0x` hex; float decimal or `0x` bits;
  string text or a `#` literal; enum a number or a name; ref an entry name or literal — a name that
  resolves to nothing is **refused**, because a dangling reference is what the game crashes on;
  resref an entry, an asset path, or a literal.
- Rows that cannot apply are reported with their line and reason, never guessed at.
- `@morph:<name>` in an int or enum value is the type of a morph the mod declares in
  `morphs.json` (`@morph:<mod>.<name>` for another mod's); `@model:<path>` in a string or resref
  value is a model's hash — FNV-1 of the path in lower case with `\`, relative to the data folder,
  the way the game refers to models.

### Mod morphs in rows

A morph record is any record with a `MorphType` on one of a character's morph lists. The heroes'
lists (both genders draw from these):

| List | Holder | Record |
|---|---|---|
| ParticleMorphs `#6f7b08b3` | MorphComponent `#8f30e3c8` | CreatureHeroBase (both heroes) |
| TextureMorphs `#88275944`, MorphTargets `#6a3f620c`, SkeletalMorphs `#66a80357` | GraphicAppearanceMorph `#2a6273b0` | CreatureHero (male) |
| TextureMorphs `#31dfb252`, MorphTargets `#6a3f620d`, SkeletalMorphs `#c64877a7` | GraphicAppearanceMorph `#16d58ec4` | CreatureHeroFemale |

A list is a record whose reference properties are its items, so ADDING an item is a new property
on the list: `#88275944,MyItem,ref,MyItem`. Make the item by deriving one the game ships and
stating its fields on it:

```csv
entry,property,type,value
# a texture layer: the shipped corrupt face layer again, at this mod's morph's weight
MyGlowFace,parent,#703c836c
MyGlowFace,Layer,2
MyGlowFace,MorphTexture,#b05adb60
MyGlowFace,OriginalTexture,#7dfdad45
MyGlowFace,MorphType,@morph:glow
#88275944,MyGlowFace,ref,MyGlowFace
# a MorphTargets pair: the male body (CH_HeroMale_Morph_Thin, #a7cbf4da) blended toward a model this mod adds
MyBulkBody,parent,#ac91b969
MyBulkBody,OriginalModel,#a7cbf4da
MyBulkBody,TargetModel,@model:art/mymod/heromale_bulk.mdl
MyBulkBody,MorphType,@morph:glow
#6a3f620c,MyBulkBody,ref,MyBulkBody
```

- **Item fields by kind:** MorphTargets `OriginalModel`, `TargetModel` (model hashes), `MorphType`;
  TextureMorphs `Layer`, `MorphTexture`, `OriginalTexture` (texture hashes), `MorphType`, optional
  `BlendType` (0 alpha over, 1 additive, else a swap); SkeletalMorphs `SkeletonFilename` (a name
  under `Data\Art\SkeletalMorphs\`, the game adds `.mof`), `MorphType`; ParticleMorphs
  `ParticleEmitter` (an emitter entity), `DummyObject`, `MorphType`, `StartAtMorphStrength`,
  `EndAtMorphStrength`.
- **A new target model** for a MorphTargets pair is a `.mdl.glb` at a path the game does not ship,
  declared with its template — the part it morphs — in `morph_targets.json` (see
  [Models and textures](models-and-textures.md#new-morph-targets)).
- **A new `.mof`** for a SkeletalMorphs item is a file in the mod at
  `art/skeletalmorphs/<Name>.mof` (the game opens `Data\Art\SkeletalMorphs\<Name>.mof` through the
  mount, which serves a mod's file at any path).
- The shipped morph list of a record that DECLARES its own list (a child hero, the statues) is not
  the one above: the game takes the first record in the chain that declares the property.

## Binary patches: .gdb.tu

A `.gdb.tu` is the game's own Title Update format: a sparse database whose records replace the
base's. The Studio writes them; they merge with `.gdb.csv` patches through the same compositor.
Prefer `.gdb.csv` for hand-written changes — it is readable and diffable.

## Text rows: .babel.csv

`language/<lang>/text/book.babel.csv` overlays the string book, one string per line:

```csv
MY_MOD_MENU,My Mod
MY_MOD_MENU_GREET,Say hello
0x1A2B3C4D,Replaced text by hash
```

- A key is a tag (`TEXT_SWORD_NAME`) or a `0x…` hash. A key the book does not have is ADDED — new
  text is a legitimate thing for a mod to ship.
- `key,kind,value` is read too, so a row copied out of a full dump works; only `text` rows apply.
- Two mods retranslating different keys both apply.
- Write your text in `language/en-uk/text/book.babel.csv`. When the game runs in another language
  (a Spanish disc ships only `es-es`), your `language/<that language>/text/book.babel.csv` is used
  if you ship one, otherwise your en-uk rows go into that language's book — so your menus are
  never blank on a non-English install. Ship a per-language file only for the languages you
  actually translate. A file for a language the player's disc does not ship is skipped quietly;
  one for a language it does ship edits that book too, ready for a switch from the title's
  Language menu.
- Name the folder exactly as the disc does: `fr-fr`, `de-de`, `it-it`, `es-es`, `ru-ru`, `pl-pl`,
  `cs-cz`, `hu-hu`, `sk-sk`, `ja-jp`, `ko-kr`, `zh-cht`. Keep every key and every `%1`, `{TAG}`,
  `<…>` tag and `\n` of the en-uk row; quote a value that contains a comma. The bundled mods are
  translated into all twelve and the tests hold them to the en-uk keys and markup.
- Settings pages, input labels, `mod.toml`, notices and script text name these rows as `{KEY}` —
  see [Text keys](#text-keys).
- Menus, the HUD and item names read the reloaded book live (see
  [Live iteration](live-iteration.md)).

## Text keys

Everything of a mod's that the player reads can be translated: write `{KEY}` where the text goes,
and put `KEY` in your `language/en-uk/text/book.babel.csv` (and in each language you translate).
It is the same form a stated menu row's label takes (`"{QUICKSLOTS_SLOT_1}: {OBJECT_HEALTH_POTION_NAME}"`).

| Where | What takes `{KEY}` |
|---|---|
| `settings.ui.json` | each row's `label` and `description`, each choice option's `label` |
| `input.json` | each action's `label` (the Controls page adds the device: "Label (key)" / "Label (pad)", in the player's language) |
| `mod.toml` | `name` and `description` — in game (the Options › Mods page, the Controls page, the co-op mods list) and in the launcher's Mods panel |
| notices and boxes | `Refii.notice` / `RefiiHost.notice` / `refii_mod_notice`, and the message and menu boxes: title, text, captions and options |
| script text | `Refii.text(text [, a .. h])` in Lua, `RefiiHost.text(text, args = [])` in Godot, `refii_mod_text(text, out, capacity)` natively |

```toml
name = "{MY_MOD_NAME}"
description = "{MY_MOD_DESCRIPTION}"
```

```json
{ "range": { "label": "{MY_MOD_RANGE}", "description": "{MY_MOD_RANGE_DESCRIPTION}", "type": "slider" } }
```

```lua
Refii.notice("{MY_MOD_NAME}", Refii.text("MY_MOD_GAVE_GOLD", 1000))   -- "+%1 gold" in the table
```

```gdscript
host.notice("{MY_MOD_NAME}", host.text("MY_MOD_SAVED"))
```

- **One lookup everywhere.** A key is looked up in the book the game is running (the game's own
  tags, and every mod's rows merged into it), then in the enabled mods' tables for the language in
  force, then in their en-uk rows, then in the engine's own words. So a mod translated into Czech
  but missing one line shows that line in English, and `{OBJECT_HEALTH_POTION_NAME}` is the game's
  own item name in the player's language.
- **A key nothing knows is drawn as written**, braces and all. A string with no `{…}` is drawn as
  it is, so a mod written before keys is unchanged.
- **In scripts** a bare key works too: `Refii.text("MY_MOD_SAVED")`. `%1`, `%2` … take the call's
  arguments (Lua takes up to eight).
- **The launcher** has no running book: it reads the mod's own tables in the install's language
  (the one the game starts in, from the disc's `cultures.txt`), then en-uk.
- **The validator warns** when a `{KEY}` in `settings.ui.json`, `input.json` or `mod.toml` has no
  row in your en-uk table — fine for a game tag, a typo otherwise.
- Keep your en-uk table complete: it is every language's fallback. The bundled mods' tables are a
  worked example of every surface.

## Hashes from names

A record's hash and a text tag's key are the game's FNV-1 of the name: `Refii.hash(name)` in Lua
(a `"0x…"` string), `refii_mod_fnv1(text)` natively. A Lua call that takes a definition takes that
string: `Refii.add_item(hero, Refii.hash("OBJECT_HEALTH_POTION"))`.

## Live reload and its limits

With the game running, **↻ Reload mods** reloads the databases through the game's own loader:
new lookups see added rows and properties, and every record an entity already holds takes the new
values in place. Two limits:

- A row a patch no longer states goes back to the base record, but a row the **base never had
  stays until a relaunch** (the game's record map has no tombstones).
- A property ADDED to a record reaches a holder of the record's older copy only when it next looks
  the record up.

## Records in saves

A mod's items live in saves as record ids. Declare the records you add in your patch — the save
stamp lists them, so they can be filtered out when your mod is missing and restored when it
returns — and keep record ids stable across versions. See
[Saves, sharing and co-op sync](saves-and-sharing.md#mods-and-saves).
