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
- [Godot scripts](#godot-scripts)
  - [Signals](#signals)
  - [Verdicts](#verdicts)
  - [Helper calls and queries](#helper-calls-and-queries)
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
- **With nothing to write** (`spell_created`, `hit_landed`, `vortex_started`, …) it is
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

## Godot scripts

Every `godot/*.gd` in an enabled package is compiled when the game's session is up, instantiated
and added under one `RefiiMods` node in the game's scene tree, and freed when the session stops.
Godot renders everything, so this is where a visual, audio or UI mod lives.

- A script must `extends Node` (or a subclass). One that does not parse, or extends something
  else, is named in a warning and skipped; the others load.
- A script has no resource path, so a relative `preload` does not resolve.
- `mod/init.lua` and `native/` stay the way to reach the game's own state.

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
- A verdict runs on the **game's thread** while the game waits. It must only compute: no nodes,
  no scene tree, no resources (queue those with `call_deferred`), and it should finish in well
  under a millisecond. Helper calls and queries are safe there, since they are only queued.
- Verdicts are never nested: an event raised while one runs gets no verdict.
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

### Helper calls and queries

A helper call (`give_gold`, `play_fx`, `set_position`, …) is a line the guest tick runs with the
real game context, since a Godot script never holds one. It is fire-and-forget, and silent when it
has nothing to act on (no such entity, no component).

A **query** returns an id at once and answers later on `reply(id, result)`, on a frame after the
guest's tick; `result.ok` is false when it could not be answered. Match a reply by its id —
`await host.reply` would take whichever answer came first, so connect and compare instead.

```gdscript
var id := host.query("health", [handle])
```

Every query and its answer is in [Commands and queries](reference-commands.md#godot-queries).

`host.node_for(handle)` is the Node3D Godot draws that entity with, or null (not drawn yet, or the
game draws it itself). Parent a light, a label or an effect to it to follow the creature; never
move it — the entity feed owns its transform.

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
