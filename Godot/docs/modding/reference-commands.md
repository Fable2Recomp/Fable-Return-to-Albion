# Commands and queries reference

The text commands a Godot script's queries speak, the debug channel's mod operations, and the
editor's scripting class.

## Contents

- [Godot queries](#godot-queries)
  - [The world](#the-world)
  - [Players and co-op](#players-and-co-op)
  - [Bodies, combat and spells](#bodies-combat-and-spells)
  - [Space and spawning](#space-and-spawning)
- [Debug channel operations](#debug-channel-operations)
- [RefiiModApi in the editor](#refiimodapi-in-the-editor)
  - [Events and handlers](#events-and-handlers)
  - [Lua, settings, input and spells](#lua-settings-input-and-spells)
  - [UI](#ui)
  - [Effects](#effects)
  - [Sharing and the live game](#sharing-and-the-live-game)
- [The game's own Lua natives](#the-games-own-lua-natives)

## Godot queries

`host.query(what, args) -> id` asks; the answer arrives on `reply(id, result)` on a frame after the
guest's tick, with `result.ok` false when it could not be answered. `E` is an entity handle, `P` a
player (1 or 2), `DEF` a `"0x…"` hash.

```gdscript
var id := host.query("position", [handle])
# later, in the reply handler:  if r.ok: print(r.x, r.y, r.z)
```

Inside an `on_verdict` handler, `host.read(what, args)` answers the same query at once with the same
fields — every verb below except `spawn` and `lua_int` (see
[Reading inside a verdict](events.md#reading-inside-a-verdict)).

### The world

| Query | Answer |
|---|---|
| `level_name` | `level` |
| `record_ref DEF FIELD` · `creature_tier E` | `value` (`'0x…'`: the record a type-6 reference property names) · `tier` (1 elite, 2 lieutenant, as the creature's definition states; ok false without a creature component) |
| `record_number DEF FIELD` · `record_bool DEF FIELD` · `record_exists DEF` | `value` (a float, or an int / enum as a number) · `value` (true / false) · `exists`: a GDB record's inherited property through the game's typed getter, and whether the record is there |
| `time_of_day` | `hours` |
| `lua_int EXPR LO HI` | `value`: a Lua expression's integer, clamped |
| `frontend` | `signed_in`, `saves`, `screen` (ok false with no front end up) |
| `in_cutscene P` | `kinds` |
| `camera_mode P` | `mode` (the game's ECameraMode, -1 none), `fov` (degrees, 0 with no camera) |

### Players and co-op

| Query | Answer |
|---|---|
| `hero P` | `hero` |
| `player_of E` | `player` |
| `player_for E` | `player` (who "the hero" is for E) |
| `world_here` | `here` |
| `combat_state P` | `midst`, `close_to_combat`, `in_any_combat` |
| `local_hero` · `coop_mode` · `menu_player` | `hero` · `mode` · `player` |
| `coop_state` | `local_hero`, `coop_mode`, `menu_player` |
| `player_value P KEY [FALLBACK]` | `value`, `has` (ok false when there is neither a value nor a fallback) |
| `player_text P KEY` | `text` (a quote, backslash, comma or line break as `_`), `hash` (`"0x…"` when the text reads as one, else empty), `has` — the typed text `player_set_text` / `player_save_text` wrote (ok false for none) |
| `player_here P` | `here` |
| `can_save P` | `able`: Player.IsAbleToSaveGame for P's hero |
| `player_dog P` | `dog` |
| `focus P` | `entity` |
| `opinion E P` | `love`, `fear`, `attractiveness`, `morality`, `respect` |
| `discovered_crime E P` · `perceived_renown E P` | whether E has discovered one of P's crimes · the renown E credits P with |
| `uid E` | `uid` (`"0x…"`) |

### Bodies, combat and spells

| Query | Answer |
|---|---|
| `health E` | `current`, `max` |
| `position E` | `x`, `y`, `z` |
| `facing E` · `velocity E` | `x`, `y`, `z`: Physics.GetFacingVector · GetVelocity (guest axes, z up) |
| `gold E` | `gold` |
| `item_count E DEF` | `count` |
| `experience E POOL` | `amount` (pools 0 General, 1 Strength, 2 Skill, 3 Will) |
| `ability_level E T` | `level` |
| `relation A B` | `relation` (1 neutral, 2 ally, 4 enemy) |
| `alive E` · `living E` | `alive` · `living` |
| `definition E` · `name E` | `definition` · `name` |
| `renown E` · `morality E` · `purity E` · `age E` · `physique E` | the hero stats the game reports |
| `appearance P` | `automatic`, `tan`, `fat`, `height`, `strength`, `skill`, `will`, `morality`, `purity`, `age`: the look in force for player P's hero (Murgo's Mirror's inputs when manual, the game's own when automatic) |
| `morph P MORPH` | `weight`, `registered`: player P's weight for a mod morph (`"0x…"` or `<mod>.<name>`) — the stored weight clamped to its declared range, else the default; ok false for a morph no enabled mod declares (`refii_mod_morph_weight`). Set it with the line `morph_set P MORPH W` |
| `equipped E` | the weapon set's definition hashes |
| `inventory_stack E I` | the I-th stack's definition and count |
| `inventory P [OFFSET]` | player P's hero's stacks in the game's order, 40 a reply from stack OFFSET: `total`, `offset`, `next`, `count`, `d1`..`dk` (definitions), `n1`..`nk` (counts), `c1`..`ck` (categories), `v1`..`vk` (viewed 0 or 1), `u1`..`uk` (usable 0 or 1: the item's own UsableFromInventory test, what `Refii.inventory`'s `usable` answers); ask again from `next` while it is below `total` |
| `sprinting P` | `sprinting`: the sprint component's flag for P's hero (ok false without a hero) |
| `locomotion P KEY` | `value`: P's hero's own LocomotionData field KEY (refii_mod_locomotion) |
| `parry P` | `window`, `wedge`: P's hero's own parry window (seconds) and wedge (degrees) |
| `locked_target P` | the target held |
| `has_status E KIND` | whether the status holds |
| `speed_multiplier P` | `overridden`, `factor` |
| `current_action E` | `action`, `elapsed`, `interruptible` |
| `action A` | `type`, `name` (ok false for an action made before the mods loaded) |
| `spell_in_slot P SLOT` | `type` |
| `spell_parts SERIAL` | `spell`, `type`, `targeted`, `level`, `caster`, `caster_player`, `vortex`, `anchor`, `sword_count`, `s1`..`s8`, `x`, `y`, `z`, `has_origin`, `exploded` (ok false for an unknown cast) |
| `vortex_held SERIAL` | `count`, `v1`..`v16`: the handles the cast's live vortex holds, its list order, 0 past `count` (ok false when no live vortex carries the serial) |
| `spell_balance C T L` | `status_seconds`, `status_spread`, `duration`, `swords`: power L of spell T for caster C as its Targeted class's own data read loads it (-1 a field the class lacks; ok false for another type or a caster with no spell manager) |
| `host_serial SERIAL` / `local_serial SERIAL` | `serial`: the online host's serial of this machine's spell / this machine's spell for the host's (ok false while the cast is not paired; the serial itself on the host, alone, local co-op) |

### Space and spawning

| Query | Answer |
|---|---|
| `near X Y Z R [CREATURES [OFFSET]]` | `total`, `offset`, `count` and `e1`..`e<count>`, 100 a reply: ask again from `offset + count` for the next page |
| `line_of_sight X1 Y1 Z1 X2 Y2 Z2` | true when the game's physics sees nothing between |
| `spawn DEF X Y Z` | the new entity's handle |

## Debug channel operations

A running game with `debug_channel = true` answers these (the Studio's Live Game tab uses them):

| Operation | Answer |
|---|---|
| `{"op":"mods-remount","lua":true}` | when the remount is done: `refreshed`, `relaunch` (`[{kind, reason, paths}]`), `changed`, `mods`, `modsRoot`, `lua` (`{queued, seqBefore, note}`; the Lua's outcome is the first `call-result` numbered after `seqBefore`) |
| `{"op":"mod-errors","since":N}` | `{seq, errors: [{seq, time, mod, text}]}` |
| `mod-setting` | the Studio's live tune of a mod setting |

## RefiiModApi in the editor

`RefiiModApi` is the editor's scripting class for the active mod (`var api := RefiiModApi.new()`
in an editor script). Every write is held to the same schema check the game uses, and refused on
an error.

### Events and handlers

| Call | Does |
|---|---|
| `GetApiVersion()` | the API version |
| `ListEvents()` · `DescribeEvent(name)` | every event; one event's fields and meanings |
| `ListNativeHelpers()` · `ListLuaHelpers()` · `ListGodotHelpers()` | the helper catalogs |
| `NewNativeHandler(event, file)` | writes a filled-in native handler under `native/` |
| `AddLuaHandler(event)` | appends a `Refii.on` block to `init.lua` |

### Lua, settings, input and spells

| Call | Does |
|---|---|
| `SetModLua(text)` | writes `mod/init.lua` |
| `SetSettingRow(setting, row)` · `SetSettingChoice(setting, label, options)` · `RemoveSettingRow` · `GetSettingsUi` | the settings page (`settings.ui.json`) |
| `SetInputAction(action, binding)` · `RemoveInputAction(action)` | `input.json` |
| `SetSpell(dict)` · `RemoveSpell(id)` · `ListSpellTypes()` | `spells/*.json` |

### UI

| Call | Does |
|---|---|
| `SetHudLayout(pieceId, anchor, offset)` · `RemoveHudLayout` · `GetHudLayout` · `GetHud` · `SetHudText(piece)` · `ListShippedHudPieces()` | `ui/hud.json` |
| `SetModal(key, record)` · `ReleaseModal(key)` · `RemoveModal` · `GetModals` · `ListModals` · `ShippedModals` | the modal answer table |
| `SetMenu(name, record)` · `RemoveMenu` · `GetMenu` · `ListModMenus` · `SetMenuRow(menu, entry)` · `RemoveMenuRow` · `OpensCommand(menu)` · `LuaCommand(function, arg)` · `ParseMenuCommand(command)` | mod menus and patched rows |

### Effects

| Call | Does |
|---|---|
| `ListBankEffects()` · `ListModEffects()` · `GetEffectJson(name)` · `SetEffectJson(json)` · `SetEffect(dict)` | read and write mod effects |
| `CloneEffect(source, name)` · `OverrideEffect(name)` | a NEW effect with fresh ids · the bank's effect with ids kept (it replaces) |
| `SetEmitterField(effect, emitter, key, value)` · `SetVisualField(effect, system, visual, key, value)` · `SetEmitterKeys(effect, emitter, param, keys)` | edit one field or timeline |
| `RemoveEffect(name)` · `SaveFableFxAsEffect(node, name)` | delete · save a scene's `FableFx` as a mod effect |
| `ListRecipes()` · `GetRecipe(id)` · `SetRecipe(dict)` · `RemoveRecipe(id)` | `fx/recipes.json` |

### Sharing and the live game

| Call | Does |
|---|---|
| `SharePackage(folder)` | writes `<mod>.f2mod` there; empty on success, otherwise the reason |
| `RemountInGame(lua)` | reloads the mods into a running game (see [Live iteration](live-iteration.md)) |
| `ModErrorsInGame()` | the running game's mod errors, newest first |

## The game's own Lua natives

| Call | Does |
|---|---|
| `ListGameLuaTables()` | the 234 tables of the game's 3,602 Lua natives |
| `ListGameLuaNatives(table)` | one table's natives |
| `FindGameLuaNatives(text)` | natives whose name contains the text |

The same lists ship as `modding_tools/game_lua_api.tsv` (table, name) and
`modding_tools/game_lua_signatures.tsv` (arguments and results).
