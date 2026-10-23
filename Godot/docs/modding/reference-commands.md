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

### The world

| Query | Answer |
|---|---|
| `level_name` | `level` |
| `time_of_day` | `hours` |
| `lua_int EXPR LO HI` | `value`: a Lua expression's integer, clamped |
| `frontend` | `signed_in`, `saves`, `screen` (ok false with no front end up) |
| `in_cutscene P` | `kinds` |

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
| `player_here P` | `here` |
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
| `gold E` | `gold` |
| `item_count E DEF` | `count` |
| `experience E POOL` | `amount` (pools 0 General, 1 Strength, 2 Skill, 3 Will) |
| `ability_level E T` | `level` |
| `relation A B` | `relation` (1 neutral, 2 ally, 4 enemy) |
| `alive E` · `living E` | `alive` · `living` |
| `definition E` · `name E` | `definition` · `name` |
| `renown E` · `morality E` · `purity E` · `age E` · `physique E` | the hero stats the game reports |
| `equipped E` | the weapon set's definition hashes |
| `inventory_stack E I` | the I-th stack's definition and count |
| `locked_target P` | the target held |
| `has_status E KIND` | whether the status holds |
| `speed_multiplier P` | `overridden`, `factor` |
| `current_action E` | `action`, `elapsed`, `interruptible` |
| `action A` | `type`, `name` (ok false for an action made before the mods loaded) |
| `spell_in_slot P SLOT` | `type` |
| `spell_parts SERIAL` | `spell`, `type`, `targeted`, `level`, `caster`, `caster_player`, `vortex`, `anchor`, `sword_count`, `s1`..`s8`, `x`, `y`, `z`, `has_origin`, `exploded` (ok false for an unknown cast) |

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
