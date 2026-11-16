# Modding Fable III

Fable III mods are `.f3mod` packages in `assets/Fable3/mods/`, built and installed exactly as
Fable II's are. This chapter says what is the same, what Fable III adds, and what is not there yet.

## Contents

- [What works](#what-works)
  - [The game's Lua](#the-games-lua)
- [Calls](#calls)
  - [Abilities](#abilities)
  - [On screen](#on-screen)
- [Native mods](#native-mods)
- [The editor](#the-editor)
- [Events](#events)
- [Not yet](#not-yet)
- [Saves](#saves)
- [The front end](#the-front-end)
- [Godot scripts](#godot-scripts)

## What works

| Kind | Fable III |
|---|---|
| `mod/init.lua`, `mod/*.lua` modules, `mod/camera.lua` | run in the game's own Lua VM after its startup scripts, once per VM |
| Replacing a shipped script with plain Lua text | yes: the game compiles source as well as bytecode |
| `godot/*.gd` | yes; `tick`, `input_action`, `publish` and `run_lua` reach the game |
| `*.babel.csv` text rows | yes: the game reads the book with your rows merged in |
| `ui/hud.json` pieces (meters, panels, pictures, text) and `meter` calls | yes, on the HUD's own view, as on Fable II; there is no shipped health bar to copy |
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

**A `pairs` loop needs exactly two variables on Fable III.** The game's Lua is Havok Script, whose
`pairs` iterates only in `for k, v in pairs(t)`: `for k in pairs(t)` (or three variables) runs zero
times, with no error. Write two variables, or use `for k in next, t`. The mod checker flags it.

**The AI and camera tables come later than `init.lua`.** Fable III runs its AI scripts
(`CombatWheel`, `CombatInfo`, the combat styles and sequences) at each new game and save load, after
`init.lua`, and builds new tables each time; `lua_ready` comes again right after (with
`scripts=true`). Patch them in a function you call at load and on `lua_ready`, and keep a wrap
keyed on the table it wrapped. The camera scripts run each time a camera manager is built, and
`mod/camera.lua` runs after them, as on Fable II.

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

Combat and control: `current_action(e)` → action · `action_elapsed(a)` → seconds ·
`action_interruptible(a)` · `allow_action_interrupt(a)` · `allow_action_interrupt_at(a, seconds)` ·
`action_type(a)` (always -1 on Fable III) · `player_of(e)` · `living(e)` · `lock_target(p [, e])` ·
`locked_target(p)` → target · `release_lock(p)` · `in_cutscene([p [, kinds]])` → inside, kinds ·
`creature_tier(e)` → `Refii.CREATURE_ELITE` / `CREATURE_LIEUTENANT` bits · `event_replica()` (always
false: every Fable III event is this machine's own).

Fable II's world, hero and creature calls, over Fable III's own natives (the same names and arguments):
`definition`, `uid`, `_by_uid`, `near`, `line_of_sight`, `velocity`, `facing`, `set_facing`, `level_name`,
`world_here`, `relation`, `age`, `physique`, `entity_scale` / `alpha` / `tint` / `untint` /
`saturation_mask` / `drawable`, `hide_part`, `knockback`, `stagger`, `play_animation`, `stop_animation`,
`sprinting`, `end_sprint`, `set_speed_multiplier`, `clear_speed_multiplier`, `speed_multiplier`, the
`record_*` readers, `record_id(name)` (a record's ID as "0x…", what `item_used` carries), `experience`, `give_experience`, `spend_experience`, `ability_level`, `inventory`,
`equipped`, `equip_weapon`, `use_item`, `perform_expression`, `purity` / `modify_purity` (Fable III's
Primal), `opinion`, `modify_opinion`, `perceived_renown`, `discovered_crime`, `forgive_crimes`,
`appearance`, `has_status`, `clear_status`, `can_save`, `save_game`, `player_dog`, `key_taken`,
`spell_in_slot` (a gauntlet slot's main and woven types), `spells_known`, `assign_spell`, `cast_spell`,
the `npc_*` orders, `switch_target`, `focus`, `action_name`, `camera_mode`, `play_music`, `stop_music`,
`stop_sound`, `set_weather`, `coop_mode`, `split_screen`, `menu_player`, `player_for`, `host_serial`,
`local_serial`.

Where Fable III answers differently: a record or a definition is Fable III's own record ID (a light
userdata, what `GDB.GetRecord` takes) or its NAME, never Fable II's "0x…" hash text; `uid` is decimal
text; `level_name` is lower case; `physique` has no height; a tint has no alpha; `perceived_renown` and
`discovered_crime` answer for the hero the world names, not per player; `cast_spell` returns no spell.
`set_locomotion` takes Fable II's fields but `MinVelMagForMovement` (Fable III has none) and also
`FrictionCoefficientSecondOrder`, `FrictionCoefficientLateral`, `MinControlMagForTurn` and
`AllowedToMoveBackwards`; `locomotion` answers 1.0 for a field nothing sets. `spell_balance(caster, type,
power [, targeted])` takes Fable III's spell types and an optional `targeted` (default true; the area
form has its own values); Fireball's burn has no added spread (Fable III varies it by ±7.5%).
`morph_get` / `morph_set` / `morph_id` and `morph_changed` are Fable II's: a mod's `morphs.json`
declares them and the hero is redrawn through the game's own morph update; Fable III has no texture
morphs, so a mod morph moves geometry and bones only. `play_sound_at` is not heard on the partner's machine. `release_expression` also ends an NPC interaction
the hero is in, as the game's own release does.

The player store, the same one Fable II's mods and HUD read: `player_here(p)` · `player_value(p, key [, fallback])` ·
`player_set` / `player_add` / `player_save` · `player_value_text` / `player_set_text` / `player_save_text`. A
`ui/hud.json` piece reads it as `player:<key>`.

The Fable II calls that read Fable II's own systems (the spell wheel, households) are not on
Fable III; a Godot `query` answers `ok=false` with the reason.

### Abilities

A mod's `ui/abilities.json` declares abilities as on Fable II, but Fable III has no experience pools:
a level is bought with **guild seals**. Give each ability a `seals` list, the price of each level in
turn:

```json
{ "abilities": [ { "name": "SoulsEndurance", "category": "Strength", "nameTag": "Endurance",
  "descriptionTag": "More stamina", "maxLevel": 3, "levelKey": "souls_combat.endurance",
  "refund": "half", "seals": [2, 4, 6] } ] }
```

An ability with no seal price for its next level cannot be bought on Fable III. Fable III has no
abilities book (the Road to Rule is the game's), so a mod's own menu buys and refunds:
`ability_buy(p, name)` / `ability_refund(p, name)` → true when done. A refund with `"refund": "half"`
gives back half the seals of the level given up. The level is the saved player value `levelKey`;
beside it the engine states `<levelKey>.cost` (-1 at the maximum), `.refund` and `.afford`, which a
`ui/hud.json` piece or a menu row can read. `mod_ability` is raised and can be cancelled, as on
Fable II; its `experience` is the seals spent or given back.

### On screen

A mod's world markers (`mark`, `world_meter`) stand on the same layer as Fable III's own world icons.
The icon names (`quest`, `gold`, `follow`, the opinions and morphs…) load Fable III's own art, plus
`gift`, `heart`, `funny`, `guild_seal_bronze` / `_silver` / `_gold` and `property_owned` /
`_marital` / `_rented`. Message boxes, notices, menu boxes and a mod's menus are drawn as Fable III's
own boxes. `open_screen` with a mod's `screens` record stands that screen's menu the same way:
Fable III has no book, so a screen's page art (its presentation) is Fable II's alone. A Godot
script's `own_screen` hides Fable III's screens and HUD and keeps the pad for the script, but Start.

HUD overrides (`hud_icon`, `hud_slide`, `hud_hold`) work on Fable III's HUD. Its one HUD carries both
players: player one's d-pad is `dpad`, player two's `dpad2`, and a player's override reaches only their
own. `DPadUp` / `DPadLeft` / `DPadDown` / `DPadRight` name a d-pad slot's icon. Fable III keeps the
d-pad closed until the game opens it, so a mod showing its own slots holds it open:
`Refii.hud_hold(p, "dpad", true)`. Fable III authors the d-pad toggles' on and off slides the same, so
`hud_slide` on them shows nothing; `dpadmenu` 1 / 2 closes and opens it. Fable III's HUD has no health
bar, minimap or spell and weapon icons to name.

The controller's mod wheel: an input action with `"wheel"` (and `"wheel_icon"`) is on it. Fable III
has no expression wheel to put it on, so it is its own ring over the world: Back opens and closes it
while something is on it (Back is the game's otherwise), the left stick points, A chooses, B closes,
LB and RB turn the page. A couch partner has their own on their pad.

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

The calls every title shares are on Fable III too (`core/refii_mod_requests.h`): the player store
(`refii_mod_player_value` / `_set` / `_add` / `_save`, texts and hashes, `refii_mod_player_here`,
`refii_mod_event_replica`), handles (`refii_mod_handle` / `refii_mod_handle_address`),
`refii_mod_fnv1`, a tick callback (`REFII_MOD_ON_TICK`), the game VM (`refii_mod_run_game_lua`,
`refii_mod_game_lua_int`, `refii_mod_lua_generation`, `refii_mod_save_set_int` / `_get_int`), and
Godot's requests (`refii_mod_message_box`, `refii_mod_mark`, `refii_mod_play_fx`, …).

Fable II's own calls (`core/refii_mod.h`: health, stats, items, spells, actions, the lock, records,
creatures, sound, the camera…) and its typed events (`refii_mod_subscribe`, `REFII_MOD_ON`) link on
Fable III as well. Each answers through the same `Refii.*` call a Lua mod makes, so it runs on the
game's thread: inside a tick callback or an event handler, never from a thread of the mod's own. A
typed handler gets Fable II's event struct filled from the event's fields; a field Fable III does not
state stays 0. What differs: `refii_mod_cast_spell` casts but returns 0 (Fable III's spell call keeps
the spell), `refii_mod_add_item` adds but returns 0, `refii_mod_set_appearance` does nothing yet, and
the abilities are Fable III's (`refii_mod_ability_cost` takes its types 1 to 19).

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
| `entity_died`, `hero_downed` | **decided**: a cancelled death by damage leaves the creature at 1 health; a cancelled downing keeps the hero standing |
| `gold`, `ability_level` | **decided**: cancel, write `delta` / `level`; `gold` has no `passive` / `share` |
| `renown` | **decided**: cancel, write `delta` (followers, a number); no `share` |
| `health_changed` | as Fable II |
| `guild_seals` | **Fable III only:** `delta`, `seals`, `source` = `'granted'`, `'spent'` or `'earned'` |
| `treasury` | **Fable III only:** `treasury`, `delta`, `type` |
| `kingdom_decision` | **Fable III only:** `demand`, `province`, `level`, `accepted` |
| `quest_started` | **decided**: cancel leaves the quest inactive; the hero is player 1's |
| `quest_ended`, `quest_objective` | the hero is player 1's; `quest_objective` is **decided** (the tag, its notice and its final mark together) |
| `fast_travel` | **decided**: cancel refuses the travel; player 1 |
| `level_loaded`, `level_unloading` | `level` only |
| `hit` | **decided**: cancel drops the hit, write `damage` / `knockdown`; `spell_type` and `spell_level` are Fable III's numbers; `tag` is 0 |
| `hit_landed`, `damage`, `hit_response`, `block` | as Fable II; `damage`, `hit_response` and `block` are **decided** (cancel) |
| `melee_swing`, `melee_swing_ended`, `anim_event`, `action_move_interrupt` | as Fable II; `action_type` is -1, `action_name` empty |
| `roll_can_start`, `roll_started`, `ability_request`, `game_action` | as Fable II; Fable III's GameAction ids |
| `spell_charge`, `spell_charge_level`, `spell_launch`, `spell_launched` | as Fable II; a launch's `type` is the slot's first spell, `tag` is 0 |
| `mod_message_box`, `mod_notice`, `mod_menu_box`, `mod_menu_row`, `attack_mode_changed` | as Fable II |
| `experience`, `morality`, `purity` | **decided**: cancel, write `amount` / `delta`; purity is Fable III's Primal; experience pools 4 and 5 are FROM_CHEST and LOST_ON_DEATH |
| `item_added`, `item_used`, `expression_started`, `crime` | `item_used`, `expression_started` and `crime` are **decided** (cancel); crime types are Fable III's (0 attempted murder .. 10 vandalism) |
| `menu_screen`, `menu_closed` | the GUI manager's screens (Fable III's: 0 download, 6 epilogue, 8 credits, 32 none); Fable III's START is the Sanctuary, a place, not a menu |
| `time_of_day`, `paused`, `game_saving`, `save_written` | as Fable II; paused modes are Fable III's (1 pause, 2 load pause, 12 maps) |
| `travel` | after the load starts (not cancellable); type is Fable III's |
| `spell_created`, `status_effect`, `status_applying`, `ranged_shot`, `slow_time` | as Fable II; spell types are Fable III's (0 Lightning .. 8 Ice Storm), `tag` 0; `status_applying` and `ranged_shot` are **decided** |
| `opinion_deed`, `opinion_modified`, `gift_given`, `follow`, `sleep_menu`, `weather_theme` | **decided**, as Fable II; an opinion is per hero (`player` is whose) |
| `collectable_taken` | as Fable II, each hero's own status: the Lua setter, gnomes and pickups |
| `dog_trick` | **decided**, as Fable II; the player is the dog's master's |
| `held_item`, `item_removed`, `gift_accepted` | as Fable II, read-only |
| `marriage`, `engaged`, `property_rented` | **decided**, as Fable II |
| `property_bought`, `property_sold`, `furniture_upgraded`, `household_budget` | **decided**; a sale's `price` is -1 (not stated yet) |
| `rent_paid`, `property_condition`, `redecorating` | read-only; rent's `gold` is what reached the purse |
| `divorce`, `widowed`, `child_born` | read-only; a child is an adoption with the spouse |
| `relation`, `spell_pick` | **decided**: write `relation` / `spell` |
| `weapon_equipped`, `weather_region` | read-only |
| `dialogue` | **decided**: a cancel plays nothing |
| `start_game` | **decided** for the boot's Continue (a cancel returns to Press START); every other start reports |
| `menu_opened`, `sleep_chosen`, `dynamic_weather` | as Fable II; `sleep_chosen` is **decided** at the bed's sleep row (choice 0 morning, 1 midday, 2 midnight) |
| `partner_message`, `partner_consent` | as Fable II: `Refii.send_to_partner` online, `Refii.ask_partner` as a message box for the partner |
| `camera_changed` | as Fable II; `camera_mode(p)` answers it |
| `achievement`, `tracked_stat` | **decided**; an achievement's `id` is its XLAST id, a tracked stat's `stat` its name hash as "0x…" text |
| `spell_ended`, `spell_charge_ended` | as Fable II, read-only; `tag` 0 |
| `summoned`, `vortex_ended`, `vortex_caught`, `fireball_impact` | as Fable II, read-only; `tag` 0 |
| `vortex_started` | **decided**: write `lifts` false and the vortex shakes creatures without lifting them; `damage` 0 |
| `target_changed`, `look_at_button`, `lock_lost`, `cutscene` | as Fable II; `lock_lost` and `cutscene` are read from the game's own state each tick |
| `item_bought`, `item_sold` | **decided** on Fable III (Fable II reports them): a cancel stops the purchase or sale; a theft from a closed shop is an `item_bought` with `stolen` true and `shopkeeper` 0 |
| `shop_price`, `drink` | **decided**: write `price` / `amount`, as Fable II; `drink` cancels |
| `chest_opened`, `dig_spot_dug`, `demon_door` | read-only; a chest's `seals` and `first` and a door's `face_out` / `face_in` are Fable III's |
| `sanctuary`, `sanctuary_request` | **Fable III only:** the Sanctuary entered or left; a request is **decided** for an entry (reasons 1..10) |
| `weapon_level_up`, `weapon_augment_unlocked`, `weapon_upgrade_ready` | **Fable III only:** the weapon's morph and augments; level-up and augment are **decided** (write `level`) |
| `hand_holding`, `hand_holding_request` | **Fable III only:** a request is **decided** (the game's own refusal) |
| `interaction_started`, `interaction_result`, `interaction_mode`, `relationship_stage` | **Fable III only:** a started interaction is **decided**; a result's `success` is writable |
| `job_started`, `job_level`, `job_earnings`, `job_worked` | **Fable III only:** the job signs; `job_level` is **decided** (write `level`) |
| `dog_dig`, `dog_leading`, `dog_trick_learned`, `dog_learned`, `dive_spot_used`, `vomit`, `job_multiplier`, `guild_message` | as Fable II (dive spots are Fable III's), read-only; `job_multiplier` and `guild_message` have player 0 |
| `title_changed`, `title_unlocked`, `chapter_progress`, `scar_removed`, `hero_stage`, `clothing_removed` | as Fable II, read-only |
| `scar_added`, `age_changed`, `clothing_worn`, `clothing_dyed`, `village_wealth` | **decided**, as Fable II (write `age`, the dye's colours, `delta`); a village change kept local is reported only |

The [Events reference](reference-events.md) lists every event; one absent above is Fable II's
alone.

## Not yet

- A mod morph's own target mesh is unproven in game on Fable III. The records are a `.gdb.csv` as on
  Fable II (a `MorphTarget` with `MorphType` `@morph:<name>`, listed on the hero's `MorphTargets`:
  `#c0f877e5` male, `#bb78c77f` female), and the target a `<name>.mdl.glb` listed in
  `morph_targets.json`, with exactly the template part's vertices in its order: Fable III's model is
  rewritten in place (positions, normals, tangents of the first LOD), never rebuilt.
- Fable II only, nothing to map to: `parry` / `set_parry` (Fable III has no parry window), the Mirror
  (`set_appearance_automatic`), the expression wheel and `selected_expression`.

## Saves

Saved player values (`player_save`, `player_add(..., true)`) go with the save, in `refii_player.txt` beside
the game's own files, and every save is stamped with the mods it was made with (`refii_save.json`), as on
Fable II. Loading a save made with mods that are not enabled now asks first and drops their saved values.
Fable III's own save files are never rewritten: what a mod put in the game's records stays there.

## The front end

Fable III runs no front-end Lua: its game VM stands from START, so `mod/init.lua` runs on the
front end too. `Refii.signed_in()` (the signed-in pad, -1 none), `Refii.save_count()` (the saves the
game's own enumeration last found, -1 before) and `Refii.screen()` (`"splash"` while "Press START"
stands, else `""`) answer there, and Godot's `query frontend` reads them.

## Godot scripts

A Godot script's calls and queries reach Fable III through the same Lua calls a Lua mod makes: anything
the table above lists, a Godot script can do too.
