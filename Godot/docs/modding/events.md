# Events and verdicts

How a mod hears what the game does and changes what happens next. Every event is listed in the
[events reference](reference-events.md); every call in the [calls reference](reference-calls.md).

## Contents

- [The model](#the-model)
- [Lua handlers](#lua-handlers)
  - [Writable fields](#writable-fields)
  - [Nested and deferred events](#nested-and-deferred-events)
  - [One file per concern](#one-file-per-concern)
  - [The game's own Lua natives](#the-games-own-lua-natives)
  - [The runtime: clock, timers, values, pools](#the-runtime-clock-timers-values-pools)
- [Godot scripts](#godot-scripts)
  - [Signals](#signals)
  - [Verdicts](#verdicts)
  - [Reading inside a verdict](#reading-inside-a-verdict)
  - [Helper calls and queries](#helper-calls-and-queries)
  - [Lua and Godot alike](#lua-and-godot-alike)
- [Native handlers](#native-handlers)
  - [Hooks on game functions](#hooks-on-game-functions)
- [Capacity](#capacity)

## The model

| Layer | Who | What |
|---|---|---|
| Events | any mod: Lua, Godot or native | a typed table, dictionary or struct per seam; cancellable where the seam allows; writable fields where the game reads them back |
| Helpers | every surface | calls that read and change the game: heroes, health, stats, spells, gold, inventory, effects, the HUD, the save |
| Hooks | native mods | `REX_EXTERN(name) { ... }` on a game function by its config name; shared functions chain in load order |

**Events never conflict.** Refii hooks each seam once and every subscriber sees every event, in
priority order (higher first), then registration order (load order for mods, Refii's own last).
The event's state after the last handler is what the game gets — the **verdict**.

Order across surfaces inside a seam: native handlers, then Lua, then Godot verdict handlers. Godot
signals arrive afterwards, once a frame.

**Cancel.** Where an event can be refused, a handler cancels it: `return true` in Lua,
`e.cancel = true` natively or in a Godot verdict. The reference's "Cancel" column says what that
means for each event; a blank means it cannot be cancelled.

## Lua handlers

`mod/init.lua` runs in the game's Lua VM once per session, after the shipped scripts. A preamble
in front of it defines the `Refii` table.

```lua
Refii.on("hit", function(e)
  if e.victim_player ~= 0 and e.spell_type == 2 then
    return true                      -- heroes ignore Slow Time hits
  end
end)

Refii.on("level_loaded", function(e)
  Refii.log("level " .. e.level .. (e.from_save and " from a save" or ""))
end)
```

- `e` is a table of the event's fields. Entities are handles, hashes are `"0x…"` strings (see
  [Handles](handles-and-players.md#handles)).
- A handler's error is caught: it lands in `RefiiModErrors` and the Studio's Live Game tab, and
  never stops the other handlers.
- A failed chunk (no VM yet, an error outside the handlers) never counts as a cancel; only a
  handler returning `true` does.
- `RefiiModSave.<key>` is state that persists with the save game.
- `mod/camera.lua` runs after every camera-script load; `mod/frontend.lua` runs on the title
  screen (see [UI and HUD](ui-and-hud.md#lua-on-the-title-screen)).

**Timing.** Every event is dispatched the moment it fires, the `tick` included: a Lua `tick`
handler runs every guest tick with that tick's `seconds`, so delays and cooldowns resolve on the
same grain native handlers get. The game's `RunScriptBuffer` is re-entrant, so a handler runs even
when the seam was reached from the game's own Lua.

### Writable fields

Assigning a writable field changes what the game gets:

| Event | Writable |
|---|---|
| `hit` | `damage`, `knockdown` |
| `gold` | `delta` |
| `experience` | `amount` |
| `ability_level`, `spell_charge_level` | `level` |
| `spell_launch` | `type` |
| `relation` | `relation` |
| `vortex_started` | `lifts` |

The reference marks every other writable field. The preamble compares the fields after the
handlers ran and writes each change back; booleans go back as 0 or 1.

### Nested and deferred events

An event the game raises while a Lua handler is running — `Refii.cast_spell` makes the factory
raise `spell_created`, and a targeted Lightning lands its `hit` inside the call; `Refii.give_gold`
reaches the hooked purse change — goes to native handlers and Godot scripts as it happens. To Lua
it goes one of two ways:

- **With writable fields** (`hit`, `gold`, `experience`, `spell_launch`, `relation`, …) it runs
  **live, nested** inside the running chunk, so your write reaches the game. A chunk nests once;
  a writable event raised two chunks deep is deferred instead, and the log says so. The running
  event and its cancel verdict are saved and restored around the nested run, so a nested cancel
  never becomes the outer one's.
- **With nothing to write** (`spell_created`, `hit_landed`, `vortex_ended`, …) it is
  **deferred**: it runs once the outermost chunk has returned, in the order raised, with
  `e.deferred = true`. The game has already acted, so a cancel there does nothing; every field is
  as it was when raised. An event a deferred handler raises is deferred behind it; one drain runs
  at most 64 chunks and at most 32 wait at once (the rest are dropped and logged).

`tick` and `spell_charge_level` are per-frame statements: never nested, never deferred.

### One file per concern

A mod's Lua need not be one file. Every other `mod/*.lua` is a **module**, reached with
`require`:

```lua
-- mod/init.lua
local common = require("common")      -- mod/common.lua
require("ai.wheel")                    -- mod/ai/wheel.lua
```

- The name is the path under `mod/` without `.lua`, with `.` or `/` between folders; names are
  lower-cased.
- A module runs once, on its first `require`; its return value is cached (`nil` as `true`; a cycle
  sees `true`). Requiring a module the mod does not ship is an error naming the file.
- A mod sees only its own modules. They are lazy, so only what a chunk requires runs, in the
  order it requires it.
- `camera.lua` shares the game VM's registry; `frontend.lua` has the modules again in its own
  state.
- A module that does not compile is left out by name.

Souls Combat is the example: `init.lua` is ten `require`s, one per rule, with `common.lua` for
what they share.

### The game's own Lua natives

The game's own natives — 3,602 in 234 tables — are all callable from `init.lua`.
`modding_tools/game_lua_api.tsv` lists them (table, name) and `game_lua_signatures.tsv` their
arguments and results. In the editor: `RefiiModApi.ListGameLuaTables()`,
`ListGameLuaNatives("Player")`, `FindGameLuaNatives("Gold")`. The shipped scripts show each call's
arguments in use.

### The runtime: clock, timers, values, pools

What every mod would otherwise write by hand, composed in front of every `init.lua` and run on the
guest tick the bus already raises — it hears the tick, `entity_removed`, `paused` and the level
events itself, so it runs with no handler subscribed:

| Call | Meaning |
|---|---|
| `Refii.now()` · `Refii.real_now()` | THE game clock: the sum of the tick's seconds while no `paused` mode is in force, so it stands still exactly while the world does (the pause and every menu, a level change); timers and pools run on it. `real_now()` counts every tick, menus included. A hitch counts at most 0.25 s. A Lua number is a 32-bit float: exact to a millisecond for the first four hours |
| `Refii.after(seconds, fn [, key])` → id · `Refii.cancel(key or id)` | `fn` run once, protected, on the first tick the clock passes now + seconds, in due order; a key replaces the timer standing under it. A Lua reload drops every timer (its functions are the old chunk's) |
| `Refii.on("setting_changed", fn)` | `e = { key, value, previous }` when a mod setting changes live (the Options › Mods page, a script's `set_setting`, the debug channel), after `Refii.setting` answers the new value; only on a real change. Godot: the `setting_changed(key, value)` signal |
| `Refii.entity_value(e, key [, default])` · `Refii.entity_set(e, key, v)` | a value per entity handle, forgotten at that entity's `entity_removed`; kept across a Lua reload; `nil` clears |
| `Refii.pool(p, key, { max, floor, regen, delay [, max_key] })` → value, max · `Refii.pool_spend(p, key, n)` → bool · `Refii.pool_value(p, key)` → value, max | a regenerating pool in player p's store under `key` — so a `player:key` HUD source and `Refii.player_value` read it on both co-op machines — its maximum under `max_key` (`key .. ".max"`), the seconds since the last spend under `key .. ".rest"`. Each field is a number or `function(p)`, read every tick (a maximum that grows with an ability). Ticked on the game clock where p's hero is played: `regen` per second once `delay` seconds have passed since a spend, clamped to `floor`..`max`. A spend is refused (false, nothing taken) when the pool holds less than `n` — unless `floor` is below 0 and the pool holds more than 0, which goes into debt down to `floor`; a pool not stated yet is full (true, nothing taken) |
| `Refii.in_play(p)` | player p is in play: no pause or GUI mode and no level change in force (the `paused` event's modes), no level loading (`level_unloading` .. `level_loaded`), p's hero standing, and no FULL cutscene for p. Godot: `in_play(player)` |
| `Refii.spell_info(type)` → name_tag, icon | a spell type's name tag and wheel icon path, the ones the game draws it with (a stand-in spell's, when one replaces it). Godot: `spell_info(type)` → `{ name_tag, icon }` |
| `Refii.SPELL_*` · `ABILITY_*` · `REQUEST_*` · `ACTION_*` · `RELATION_*` · `ATTACK_MODE_*` | the named numbers: `SPELL_LIGHTNING` 0 .. `SPELL_RAISE_DEAD` 7; `ABILITY_HEAVY_WEAPONS` 1, `PHYSIQUE` 2, `TOUGHNESS` 3, `RANGED_WEAPONS` 4, `ACCURACY` 5, `SPEED` 6, `LIGHTNING` 7 .. `RAISE_DEAD` 14; `REQUEST_MELEE_PRESSED` 16, `REQUEST_MELEE_RELEASED` 17, `REQUEST_MAGIC_CHARGE` 18 (`ability_request` types); `ACTION_INTERACT` 11, `ACTION_LOCK_SET` 49, `ACTION_LOCK_RELEASE` 50, `ACTION_SPRINT_SET` 66, `ACTION_SPRINT_RELEASE` 67 (`game_action` ids); `RELATION_NEUTRAL` 1, `RELATION_ALLY` 2, `RELATION_ENEMY` 4; `ATTACK_MODE_MELEE` 1, `ATTACK_MODE_RANGED` 2, `ATTACK_MODE_MAGIC` 3 (`attack_mode`). Godot: `constants()` |

Godot has the same runtime (`now`, `real_now`, `after`, `cancel`, `entity_value`, `entity_set`,
`pool`, `pool_value`, `pool_spend`, `in_play`, `spell_info`, `constants`): the clock counts the same
statements from the host's start, and a pool declared from Godot is this runtime's pool.

```lua
-- Stamina: swings spend it, it comes back after a pause; the HUD reads player:my_mod.stamina.
for p = 1, 2 do
  Refii.pool(p, "my_mod.stamina", { max = function(p) return 100 + 20 * (Refii.player_value(p, "my_mod.level") or 0) end,
                                    floor = -25, regen = 55, delay = 0.7 })
end
Refii.on("ability_request", function(e)
  if e.player ~= 0 and e.type == Refii.REQUEST_MELEE_PRESSED then
    return not Refii.pool_spend(e.player, "my_mod.stamina", 18)   -- the release follows on its own
  end
end)
Refii.after(3, function() Refii.log("three seconds in") end, "my_mod.hello")
```

## Godot scripts

Every `godot/*.gd` in an enabled package is compiled when the game's session is up, instantiated
and added under one `RefiiMods` node in the game's scene tree, and freed when the session stops.
Godot renders everything, so this is where a visual, audio or UI mod lives.

- A script must `extends Node` (or a subclass). One that does not parse, or extends something
  else, is named in a warning and skipped; the others load.
- A script has no resource path, so a relative `preload` does not resolve.
- A script reaches the game's own state through the host — signals, verdicts, helper calls and
  queries (`ask`) — with the same access a Lua mod has ([Lua and Godot alike](#lua-and-godot-alike)).
  The bundled Health Bars, Toggle Sprint and Hotbar are Godot scripts, First Person's switch too.

Scripts talk to the game through the `RefiiHost` singleton:

```gdscript
var host := Engine.get_singleton("RefiiHost")
```

### Signals

| Signal | Carries |
|---|---|
| `mod_event(name, e)` | every event; `e` is a Dictionary of its fields (int, float, bool or String) |
| `<lua_name>(e)` | one per event: `hit`, `spell_launched`, `level_loaded`, … |
| `reply(id, result)` | a query's answer |
| `message_box_answered(id, button)`, `mod_menu_row(e)`, `partner_message(e)` | see [UI and HUD](ui-and-hud.md) and [partner messages](handles-and-players.md#partner-messages) |

- A signal handler sees the event **after** the verdict and cannot change it.
- Signals arrive once per frame, in order, after the guest's step.
- The host asks the bus only for the kinds some script is connected to (a `mod_event`
  connection asks for all), so an unconnected event costs nothing.
- An event dictionary carries `replica = true` when it is a copy of the other machine's event
  (see [Replicas](handles-and-players.md#replicas-and-relayed-events)).

```gdscript
extends Node

var host: Object
var pending_health := 0

func _ready() -> void:
	host = Engine.get_singleton("RefiiHost")
	host.hit.connect(_on_hit)
	host.reply.connect(_on_reply)

func _on_hit(e: Dictionary) -> void:
	# local_hero(): this machine's hero. hero(1) is the HOST's hero on both machines.
	var who := "my hero" if e.victim == host.local_hero() else str(e.victim)
	print("%s took %.1f damage" % [who, e.damage])
	if e.damage > host.setting("hitwatch.loud_above", 20.0):
		host.run_lua("RefiiModSave.loud_hits = (RefiiModSave.loud_hits or 0) + 1")
		pending_health = host.query("health", [e.victim])

func _on_reply(id: int, r: Dictionary) -> void:
	if id == pending_health and r.ok and r.current < r.max * 0.25:
		host.give_gold(host.local_hero(), 10)   # a consolation purse at a quarter health
```

### Verdicts

`host.on_verdict(event, callable)` runs `callable(e)` **inside** the seam, before the game acts,
after native and Lua handlers. `e` is the same Dictionary a signal carries.

- Change a writable field (`e.damage *= 0.5`, `e.knockdown = true`), or set `e.cancel = true` or
  return `true` to refuse an event that can be refused.
- Only the catalog's writable fields go back, and only when they changed.
- `host.remove_verdict(event, callable)` takes one back; `on_verdict` returns false for an
  unknown event name.
- A verdict runs on the **game's thread** while the game waits. It must only compute and read: no
  nodes, no scene tree, no resources (queue those with `call_deferred`), and it should finish in
  well under a millisecond. Helper calls and queries are safe there, since they are only queued;
  `read` answers at once (below).
- Verdicts are never nested: an event raised while one runs — by a `read`'s guest code too — gets
  no verdict.
- Only the kinds that have a verdict handler are asked.

```gdscript
func _ready() -> void:
	Engine.get_singleton("RefiiHost").on_verdict("hit", _halve_and_floor)

func _halve_and_floor(e: Dictionary) -> bool:
	if e.spell_type == 6:      # force push: keep the shove, lose the knockdown
		e.knockdown = false
	e.damage *= 0.5
	return false               # true would drop the hit
```

### Reading inside a verdict

`host.read(what, args)` answers a guest read **at once**, inside the verdict: the handler already
runs on the game's thread inside the seam, so the guest answers the query there, through the same
branch that answers `query` and `ask`, and returns the Dictionary `ask`'s callback would get — `ok`
and the verb's fields. It is the Godot form of a Lua handler's synchronous `Refii.*` read, so a
verdict decides on the game's state as it stands when the event arrives, not on a copy asked a tick
before.

- Only the `query` verbs that change nothing: every verb in
  [Commands and queries](reference-commands.md#godot-queries) except `spawn` (it makes an entity)
  and `lua_int` (it runs any game Lua expression); the list is the catalog's `ModApi.VerdictReads`.
- Outside a verdict handler — `_ready`, a signal, a timer, another thread — or for another verb it
  reads nothing and answers `{ ok = false, error }`, with a warning: use `ask` there.
- A read runs guest code on the game's thread like a Lua read does, so keep it to what the verdict
  needs; an event it raises gets no verdict.

```gdscript
func _on_game_action(e: Dictionary) -> bool:
	if e.action != sprint_set:
		return false
	# the game's own sprint flag, now: a second press is cancelled
	return bool(host.read("sprinting", [e.player]).get("sprinting", false))
```

### Helper calls and queries

A helper call (`give_gold`, `play_fx`, `set_position`, …) is a line the guest tick runs with the
real game context, since a Godot script never holds one. It is fire-and-forget, and silent when it
has nothing to act on (no such entity, no component).

A **query** returns an id at once and answers later on `reply(id, result)`, on a frame after the
guest's tick; `result.ok` is false when it could not be answered. Match a reply by its id —
`await host.reply` would take whichever answer came first, so connect and compare instead — or
hand the answer straight to a callable with `ask`, which never reaches `reply`. `call_lua`
answers any Lua expression the same way, with `{ ok, values }`.

```gdscript
var id := host.query("health", [handle])
host.ask("health", [handle], func(r): if r.ok: print(r.current, "/", r.max))
host.call_lua("Refii.pool_value(1, 'my_mod.stamina')", func(r): print(r.values))
```

Every query and its answer is in [Commands and queries](reference-commands.md#godot-queries).

`host.node_for(handle)` is the Node3D Godot draws that entity with, or null (not drawn yet, or the
game draws it itself). Parent a light, a label or an effect to it to follow the creature; never
move it — the entity feed owns its transform.

### Lua and Godot alike

Every event reaches both surfaces, and both may refuse and change it the same way — the rule is
that [Lua and Godot have the same access](handles-and-players.md#lua-and-godot-have-the-same-access).

| | Lua | Godot |
|---|---|---|
| Hear | `Refii.on(name, fn)` (`Refii.off(name, fn)` takes it back) | signal `<name>(e)` or `mod_event(name, e)` |
| Refuse | `return true`, or `e.cancel = true` | `on_verdict(name, fn)`: `return true`, or `e.cancel = true` (`remove_verdict` takes it back) |
| Change | assign a writable field (`e.damage = …`) | assign the same writable field in the verdict |
| When | inside the seam, after native handlers | the verdict inside the seam, after Lua (it sees Lua's writes); the signal a frame later |
| Read the game | `Refii.*` reads at the call (`Refii.sprinting(p)`) | `read(what, args)` inside the verdict, at once; `ask` anywhere else, a frame later |
| Nested | an event with writable fields raised inside a handler runs live, one chunk deep; deeper, or with nothing to write, it is deferred (`e.deferred = true`) | a verdict never nests (its calls are queued, and an event its `read` raises gets none); a verdict still runs for an event raised inside a Lua handler |
| Replica | `Refii.event_replica()` | `e.replica` |

The writable fields and the events that can be refused are the catalog's, one list for both (the
Lua preamble's write-back table and the verdict's answer are both read from it). The runtime's own
events — `setting_changed`, `mod_signal`, `value_changed` — are Lua's twins of the Godot signals of
the same names; they cannot be refused.

## Native handlers

A native mod is C++ under `native/` against `core/refii_mod.h` (helpers and events). The launcher
compiles each enabled mod's `native/*.cpp` into the game module with `-DREFII_MOD_NAME="<mod>"`,
which the bus logs handlers under. The player is asked to allow a mod's native code.

```cpp
#include "core/refii_mod.h"   // helpers + events

static void OnHit(refii::mods::HitEvent& e) {
  if (e.attacker_player != 0) e.damage *= 2.0f;                  // either hero hits twice as hard
  if (e.victim_player != 0 && e.spell_type == 2) e.cancel = true; // heroes ignore Slow Time hits
}
REFII_MOD_ON(Hit, OnHit);            // REFII_MOD_ON_PRIORITY(Hit, OnHit, 10) runs earlier

static void OnTick(refii::mods::TickEvent& e) {
  float hp, max;
  if (refii_mod_health(e.base, refii_mod_player_hero(e.base, 1), &hp, &max))
    refii::mods::Publish("my_mod.hp_share", hp / max);   // "mod:my_mod.hp_share" in the UI
}
REFII_MOD_ON(Tick, OnTick);
```

- Every event carries `ctx` (a copy of the guest context, legal for guest calls), `base` (guest
  memory) and `cancel`.
- A `player` field is 1 or 2 when the entity is that player's hero, 0 otherwise.
- Handlers run on the guest tick thread inside the seam: keep them short, never block.
- `REFII_MOD_ON_TICK(fn)` is the plain tick, the same moment as the Tick event.
- `RefiiModApi.NewNativeHandler(event, "file.cpp")` writes a filled-in handler for any event.

### Hooks on game functions

For a seam no event covers, hook the game function by its config name (the names in
`versions/*/refii_config.toml`, the same on the disc and Title Update builds):

```cpp
REX_EXTERN(rex_SomeGameFunction) {
  // ... before
  REFII_MOD_ORIGINAL(rex_SomeGameFunction);
  // ... after
}
```

A hook on a function another mod also hooks chains with it in load order. Never hook a function
an event already covers; subscribe to the event.

## Capacity

A subscription crosses each seam as a bit mask. Lua masks hold 200 kinds and the Godot masks 256;
the API has 101 events today, so this matters only to someone adding events: past 200 add a Lua
mask group, past 256 a host mask word. A test fails when the event count outgrows either.
