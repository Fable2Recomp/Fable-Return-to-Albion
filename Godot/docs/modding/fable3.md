# Modding Fable III

Fable III mods are `.f3mod` packages in `assets/Fable3/mods/`, built and installed exactly as
Fable II's are. This chapter says what is the same, what Fable III adds, and what is not there yet.

## Contents

- [What works](#what-works)
  - [The game's Lua](#the-games-lua)
- [Calls](#calls)
- [Native mods](#native-mods)
- [The editor](#the-editor)
- [Events](#events)
- [Not yet](#not-yet)

## What works

| Kind | Fable III |
|---|---|
| `mod/init.lua`, `mod/*.lua` modules, `mod/camera.lua` | run in the game's own Lua VM after its startup scripts, once per VM |
| Replacing a shipped script with plain Lua text | yes: the game compiles source as well as bytecode |
| `godot/*.gd` | yes; `tick`, `input_action`, `publish` and `run_lua` reach the game |
| `*.babel.csv` text rows | yes: the game reads the book with your rows merged in |
| Files at a game path (`data/...`) | yes, banked and loose (a loose file up to 64 MB) |
| `native/*.cpp` hard mods | yes: hooks on named game functions, and the native bus (below) for events |

### The game's Lua

Fable III runs **Kore** (Lionhead's Lua 5.1, later Havok Script; `_VERSION` is
`"2.0 Beta rLionhead-2010-07-05"`). For a mod it is Lua 5.1, with these differences:

- **No `continue`.** Fable II's compiler has it; Fable III's does not.
- **Numbers are 32-bit floats**, as in Fable II: objects cross as handles, hashes as `"0x…"` strings.
- **Extra words** `kstructure` and `kmake` are reserved.

The game's own API is open to a mod: **5,340 natives in 313 tables** (`Debug`, `Stats`,
`Money`, `Treasury`, `Province`, `HandHolding`, `SpellGauntlet`, ...). They take the game's own
`Entity` values; `Refii.entity(handle)` gives you one for a handle, and `Refii.handle(e)` the
handle for one:

```lua
local hero = Refii.entity(Refii.player_hero(1))
Money.Add(hero, 100)                 -- raises gold
Stats.ModifyGuildSeals(hero, 1)      -- raises guild_seals, source 'granted'
```

The Studio's Lua view decompiles the shipped scripts (Kore bytecode) to Lua you can read and
copy into a mod; Fable III compiles the text when it loads it.

The game also runs two optional scripts it never ships, if a mod provides them:
`data/scripts/startup/MyStartup.lua` (at boot, before the quest scripts) and
`data/scripts/startup/MyStartupConsoleScript.lua` (after every startup script, once per component
init). `mod/init.lua` is usually the better place: it runs on every new VM, with `Refii` ready.

## Calls

The core: `log`, `_error`, `publish`, `value`, `hash`, `player_hero(p)`, `local_hero()`, `entity(h)`,
`handle(e)`, `alive(e)`, and the preamble's `on`, `setting`, timers and pools.

Gameplay, over the game's own natives (each takes a handle or a game `Entity`; a dead one answers
nil): `health(e)` → current, max · `set_health(e, v)` · `modify_health(e, delta [, attacker])` ·
`set_invulnerable(e, on)` · `position(e)` → x, y, z · `set_position(e, x, y, z)` · `name(e)` ·
`gold(e)` · `give_gold(e, delta)` · `item_count(e, name)` · `add_item(e, name [, n])` ·
`remove_item(e, name [, n])` · `morality(e)` · `modify_morality(e, delta)` · `renown(e)` ·
`modify_renown(e, delta)` · `guild_seals(e)` · `give_guild_seals(e, n)` ·
`play_sound(cue, e [, tag])` · `spawn(definition, x, y, z [, name])` → handle ·
`time_of_day()` · `set_time_of_day(hours)`. Items are named by their definition name
(`"ObjectInventoryWeaponRifleBasic"`), as Fable III's scripts name them.

Godot's side, as on Fable II: `play_fx` / `play_fx_on` / `stop_fx`, `light` / `light_remove`,
`message_box`, `notice`, `menu_box`, `open_menu`, `mark` / `unmark`, `world_meter` / `mark_meter`,
`meter` / `meter_remove`, `fade`, `text`, `input_claim`, `hud_icon` / `hud_slide` / `hud_hold`,
`press` / `tap`, `set_menu_rows`, `open_screen`.

The Fable II calls that read Fable II's own systems (spells and the spell wheel, abilities,
households, the co-op partner) are not on Fable III; a Godot `query` answers `ok=false` with the
reason.

## Native mods

A Fable III hard mod hooks game functions by name, as on Fable II, and hears events through the
native bus (`core/refii_mod_bus.h`): a handler gets the same `name=value` fields Lua does and
answers like a Godot verdict, `cancel=1` and `field=number`. Native handlers run before Lua.

```cpp
#include <cstdio>
#include "core/refii_mod_bus.h"

static int32_t OnGold(int32_t, const char* fields, size_t length, char* out, size_t capacity, void*) {
  return std::snprintf(out, capacity, "delta=%d", 2 * refii_mod_bus_int(fields, length, "delta"));
}
REFII_MOD_BUS_ON("gold", OnGold, nullptr);
```

## The editor

The editor's toolbar has a **Game** picker: Fable II or Fable III. Switching restarts the editor
(open scenes are saved) so everything is built for the chosen game, and the choice is remembered
when the editor is opened directly. A script of the other game opens read-only.

## Events

**Gold, renown, ability level, fast travel and a quest's start are decided before the game applies them**, as on
Fable II: a handler that returns `true` (or a Godot verdict's `cancel`) stops them, and a written
`delta` / `level` replaces the game's. Every other Fable III event reports after the fact, from the
next game tick. Fable III raises these, with the catalog's fields except where noted:

| Event | Fable III |
|---|---|
| `tick`, `hero_ready`, `hero_gone`, `lua_ready`, `save_loaded`, `input_action` | as Fable II |
| `entity_spawned`, `entity_removed` | `entity` only |
| `entity_died`, `hero_downed` | `hero_downed` only when no resurrection potion revived the hero |
| `gold`, `ability_level` | **decided**: cancel, write `delta` / `level`; `gold` has no `passive` / `share` |
| `renown` | **decided**: cancel, write `delta` (followers, a number); no `share` |
| `health_changed` | as Fable II |
| `guild_seals` | **Fable III only:** `delta`, `seals`, `source` = `'granted'`, `'spent'` or `'earned'` |
| `treasury` | **Fable III only:** `treasury`, `delta`, `type` |
| `kingdom_decision` | **Fable III only:** `demand`, `province`, `level`, `accepted` |
| `quest_started` | **decided**: cancel leaves the quest inactive; the hero is player 1's |
| `quest_ended`, `quest_objective` | the hero is player 1's |
| `fast_travel` | **decided**: cancel refuses the travel; player 1 |
| `level_loaded`, `level_unloading` | `level` only |

The [Events reference](reference-events.md) lists every event; one absent above is Fable II's
alone.

## Not yet

- Cancelling or writing the events not marked decided above.
- Fable II's spell, ability, household and co-op helper calls.
