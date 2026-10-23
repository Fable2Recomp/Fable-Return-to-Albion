# The mod API

Refii's modding surface in the shape of Fabric and Forge: **events first, hooks second**. The
game's seams are named, typed events a mod subscribes to; a direct hook on a game function is
the primitive underneath, for a seam no event covers yet. Everything here is implemented in
`src/vfs/include/core/refii_mod.h` (helpers) and `src/vfs/include/core/refii_mod_events.h`
(events), raised by `src/vfs/src/hooks/mod_events.cpp`, described by
`Godot/src/Engine/mods/Engine_ModApi.cs` (the catalog the Mod Studio's Guide, `RefiiModApi` and
the tests read) and composed into every mod's Lua by `Engine_ModLua.cs`. The companion plans are
`modding_plan.md` and `hard_mods_plan.md`.

Version: `REFII_MOD_API_VERSION` 2. It rises when an event's fields change meaning or an event is
removed; new events and new trailing fields do not bump it.

**Every API serves both players; never assume player one.** The API is not a side door for mods:
it is the shared layer the base game's own co-op fixes and the mods are both written on, as
Forge's and Fabric's own patches and the mods share one event bus. Events carry the player they
are about (1 or 2) and every helper takes a player or an entity. The "which hero" questions have
one answer each — the one the core co-op layer itself uses — and a mod asks it the same way:

| Question | Native | Lua | Godot |
|---|---|---|---|
| is this a hero, and whose | `refii_mod_player_of` | `Refii.player_of(e)` | `query player_of E` |
| who is "the hero" for this NPC or dog | `refii_mod_player_for` | `Refii.player_for(e)` | `query player_for E` |
| who "the hero" means right now (GetPlayerHero) | — (a Lua-VM state) | `Refii.acting_player()` | via `run_lua` |
| run a shipped script as player p | — | `Refii.run_as_player(p, fn, ...)` | via `run_lua` |
| is player p in combat | — | `Refii.combat_state(p)` | `query combat_state P` |
| where are player p's own effects made | `refii_mod_player_here` | `Refii.player_here(p)` | `query player_here P` |
| where are world effects made | `refii_mod_world_here` | `Refii.world_here()` | `world_here()` |
| is this event the other machine's | `refii_mod_event_replica` | `Refii.event_replica()` | `e.replica` |
| send the other machine a message of the mod's own | `refii_mod_send_to_partner` | `Refii.send_to_partner(mod, name, payload)` | `send_to_partner(mod, name, payload)`; it arrives as `partner_message` |
| show something to player p (a box, a question, a tutorial, a fade, a camera change) | — | `Refii.show_to_player(p, name, ...)`, or call it acting as p | via `run_lua` |
| a villager's crimes and renown for player p | `refii_mod_forgive_crimes` · `refii_mod_discovered_crime` · `refii_mod_perceived_renown` | `Refii.forgive_crimes(v, p)` · `Refii.discovered_crime(v, p)` · `Refii.perceived_renown(v, p)` | `forgive_crimes E P` · `query discovered_crime E P` · `query perceived_renown E P` |

`player_for`: a hero is its own player; a player's dog is its master's (`Refii.player_dog`); anything
else answers to the nearer living hero, keeping its last answer until the other hero is more than
3 m nearer, so an NPC between the two does not flicker; one hero standing is everyone's.
`acting_player`, `run_as_player` and `combat_state` live in the game VM (the core co-op Lua defines
them on `Refii`): they are the state the shipped scripts read, so native code has no copy of them.

An event decided on one machine reaches the other when its effect belongs there: those events are
**relayed** (see "Relayed events" under Player state). Player two's swings reach the host's AI;
the host's hits reach the joiner's looks. No mod keeps a co-op channel of its own.

## The three layers

| Layer | Who | What |
|---|---|---|
| Events | any mod, native or Lua | `REFII_MOD_ON(Hit, fn)` / `Refii.on("hit", fn)`: a typed struct or table per seam, cancellable where the seam allows, writable fields where the game reads them back |
| Helpers | native mods | `refii_mod_*` C calls: the heroes, components, health, stats, experience, spells, gold, inventory, the level, the save, the game VM |
| Hooks | native mods | `REX_EXTERN(name) { ... }` on a config-named game function; shared functions chain in load order |

Events never conflict: refii hooks each seam once and every subscriber sees every event, in
priority order (higher first) then registration order (load order for mods, refii's own last).
The struct's state after the last handler is what the game gets.

## Native

```cpp
#include "core/refii_mod.h"   // helpers + events

static void OnHit(refii::mods::HitEvent& e) {
  if (e.attacker_player != 0) e.damage *= 2.0f;                 // either hero hits twice as hard
  if (e.victim_player != 0 && e.spell_type == 2) e.cancel = true; // heroes ignore slow-time hits
}
REFII_MOD_ON(Hit, OnHit);                     // REFII_MOD_ON_PRIORITY(Hit, OnHit, 10) runs earlier

static void OnTick(refii::mods::TickEvent& e) {
  float hp, max;
  if (refii_mod_health(e.base, refii_mod_player_hero(e.base, 1), &hp, &max))
    refii::mods::Publish("my_mod.hp_share", hp / max);   // "mod:my_mod.hp_share" in the UI
}
REFII_MOD_ON(Tick, OnTick);
```

Every event carries `ctx` (a copy of the guest context, legal for guest calls), `base` (guest
memory) and `cancel`. A `player` field is 1 or 2 when the entity is that player's hero and 0
otherwise — both players are principals. Handlers run on the guest tick thread inside the seam:
keep them short, never block.

The launcher compiles each mod's `native/*.cpp` with `-DREFII_MOD_NAME="<mod>"`, which the bus
logs handlers under.

## Lua

`mod/init.lua` runs in the game's VM after the shipped scripts; the preamble the host composes in
front of it defines:

| Call | Meaning |
|---|---|
| `Refii.on(event, function(e) ... end)` | subscribes; `e` is a table of the event's fields; `return true` cancels where the event allows it. An event raised while a handler runs is delivered nested and live when it has writable fields, else after that chunk returns with `e.deferred = true` (see Nested and deferred events) |
| `Refii.setting(key, default)` | a `settings.json` value with the player's choice over it, read at the call: `RefiiSettings`, composed in at session start and kept current — a change made live (the Options › Mods page, a script's `set_setting`, the Studio's live tune) is written into the running VM's table at the next guest tick, so no reload is needed |
| `require(name)` | the mod's own module `mod/<name>.lua` (see One file per concern): run once on the first `require`, its return value cached — `nil` as `true` — and an error naming the file when there is none |
| `Refii.hero(player)` | the player's hero handle as the bus last stated it, or nil |
| `Refii.publish(key, n)` / `Refii.value(key)` | a mod value Godot reads as `mod:<key>` (HUD pieces, FX recipes); the last value under a key |
| `Refii.log(text)` | a line in the guest log, prefixed `[mod lua]` |
| `Refii.hash(name)` | the game's FNV-1 of a name as a `"0x…"` string: a GDB record's hash, a text tag's key |
| `Refii.health(e)` → current, max · `Refii.set_health(e, v)` · `Refii.modify_health(e, delta, attacker)` | health read, written, or changed through the game's own Modify (death included) |
| `Refii.position(e)` → x, y, z · `Refii.set_position(e, x, y, z)` | a position through the entity's own placement |
| `Refii.entity_tint(e, r, g, b [, a])` · `Refii.entity_untint(e)` | tints an entity through the game's own appearance setter (see Tint below); `a` defaults to 1; untint puts back the constructor's 1, 1, 1, 1 |
| `Refii.entity_alpha(e, a)` · `Refii.entity_scale(e, s)` · `Refii.entity_saturation_mask(e, on)` · `Refii.entity_drawable(e, on)` | the other setters of the same renderable (see Appearance below); reset = 1, 1, false, true |
| `Refii.lock_target(p [, e])` · `Refii.release_lock(p)` · `Refii.switch_target(p, left) -> target` · `Refii.locked_target(p) -> target` | the target lock as the game's LT states it (see The target lock below); `e` nil or 0 is the game's own pick |
| `Refii.apply_status(e, kind, seconds [, level [, source]])` · `Refii.clear_status(e, kind)` · `Refii.has_status(e, kind)` | a status through the spell's own installer: 1 electrified, 2 chaos, 3 burning (see Status effects below) |
| `Refii.knockback(e, x, y, z [, force])` · `Refii.stagger(e)` | a Force Push's knockdown hit along a direction, or the flinch a 0-damage tap plays (see Knockback and stagger below) |
| `Refii.set_speed_multiplier(p, factor)` · `Refii.clear_speed_multiplier(p)` · `Refii.speed_multiplier(p) -> overridden, factor` | the player component's own movement-speed override (see Movement speed below); 0 holds the hero in place; nil without a player component |
| `Refii.current_action(e) -> action` · `Refii.action_elapsed(a) -> seconds` · `Refii.action_interruptible(a)` · `Refii.allow_action_interrupt(a)` | the action an entity's manager runs (nil for none or once stopped), its clock, and the interrupt permission every animation grants (see Actions and commitment below) |
| `Refii.action_type(a) -> type` · `Refii.action_name(a) -> name` | what an action is: its definition's `Type`, EScriptableAction (0 PLAY_ANIMATION, 1 MELEE_STRIKE, 2 STRAFE — the roll, ...), and that enum's name; nil for an action the factory never saw (see Actions and commitment below: What an action is) |
| `Refii.living(e)` | in the world and, for a creature, not yet died (see Actions and commitment below: the target selector's own test) |
| `Refii.spell_in_slot(p, slot) -> type` | the spell held in slot 0..4, -1 for none (the spell manager's slot table, row 0) |
| `Refii.spell_parts(serial) -> table` | what a cast put in the world, by serial: `spell`, `type`, `targeted`, `level`, `caster`, `caster_player`, `vortex`, `anchor`, `swords = { .. }`, `origin = { x, y, z }`, `exploded` — absent parts nil, nil for a cast the factory never saw (see A live cast below) |
| `Refii.play_sound(cue, e [, tag])` · `Refii.play_sound_at(cue, x, y, z [, tag])` · `Refii.stop_sound(e, tag [, fade_ms])` · `Refii.play_music(name)` · `Refii.stop_music(name)` | an audio cue through the game's own cue player, on an entity or at a point, stopped by tag; a music cue through SoundTools' own PlayMusic/StopMusic (see Sound below) |
| `Refii.play_animation(e, name [, loops [, blend_seconds [, speed]]])` · `Refii.stop_animation(e)` | a named animation as the shipped AI plays one, a scripted action (see Animation below) |
| `Refii.modify_opinion(villager, hero, axis, delta)` · `Refii.add_deed(villager, hero, deed)` | a villager's opinion of a hero moved through the game's own setter, or a deed told through its own message (see Opinion and deeds below); `deed` is a `"0x…"` string (`Refii.hash` of the Deed record's name) |
| `Refii.npc_follow(e, target [, radius])` · `Refii.npc_go_to(e, x, y, z)` · `Refii.npc_flee(e, from)` · `Refii.npc_attack(e, target)` · `Refii.npc_set_hostile(e, other, on)` · `Refii.npc_stop(e)` | orders a creature as the shipped quests do, through the game's own entries in its VM (see NPC behaviour below); `other` is an entity — player two's hero is its own |
| `Refii.set_time_of_day(hours)` · `Refii.time_of_day() -> hours` · `Refii.set_weather(theme [, weight [, seconds]])` | the game clock (0..24) through its own setter, read back; the GAME's environment theme blended as the quests do (see Time and weather below); weight defaults to 1, seconds to 1 |
| `Refii.gold(e)` · `Refii.give_gold(e, delta)` | a purse, read and changed through the game's own Modify |
| `Refii.item_count(e, def)` · `Refii.add_item(e, def)` | an inventory; `def` is a `"0x…"` string (`Refii.hash` of the record's name) |
| `Refii.experience(e, pool)` · `Refii.give_experience(e, pool, n)` · `Refii.spend_experience(e, pool, n)` | experience: pools 0 General 1 Strength 2 Skill 3 Will |
| `Refii.ability_level(e, type)` | the level of ability 1..14, or nil |
| `Refii.cast_spell(caster, type, targeted, target, level [, pos [, dir]])` → spell, serial · `Refii.spell_serial(spell)` → serial | a real spell through the game's factory, no will cost, started at `pos = {x, y, z}` facing `dir` (the caster's own position and facing when absent): Fable 2's targeted classes place from the position along the facing — Lightning draws its bolt from there to the target (`sub_827407F0`), Fireball spawns its shot there (`sub_82744AE0`), Force Push shoves the target away from there (`sub_828B2CC8`) — so a cast started ON a victim is Fable III's position cast. Returns the spell object's handle and its cast serial (the number its hits, `spell_created` and `spell_parts` carry), nil for no cast; `spell_serial` reads a spell handle's serial. The serial, not the handle, is a cast's identity: a spell handle is the object's address for its life and is never forgotten, so the next cast of the same class at the same address shares it |
| `Refii.relation(a, b)` · `Refii.alive(e)` · `Refii.definition(e)` · `Refii.name(e)` · `Refii.player_of(e)` | the faction rule (1 neutral 2 ally 4 enemy), liveness, the definition hash, the instance name, 1/2/0 |
| `Refii.level_name()` · `Refii.player_hero(p)` · `Refii.local_hero()` · `Refii.menu_player()` | the loaded level, a player's hero, the local hero, the player whose menus are open (1 or 2). The local hero is the guest's own local-player rule: player two's on an online joiner, player one's on a host or alone, and in LOCAL co-op the hero of whoever owns the open menus — both players are local there, so name a player (`Refii.player_hero(p)`) when the answer must not follow the menus |
| `Refii.player_value(p, key [, fallback])` · `Refii.player_set(p, key, v)` · `Refii.player_add(p, key, d [, saved])` · `Refii.player_save(p, key, v)` · `Refii.player_here(p)` | per-player values, the same on both co-op machines; saved ones go home with the hero (see Player state) |
| `Refii.player_dog(p)` | a player's dog: player one's, or player two's co-op dog bound to them; nil when none. Use this, never the game's `GetDog()`: inside player two's dog's own AI and item use, `GetDog()` answers that dog |
| `Refii.event_replica()` | true when the event being handled is a copy of something the other co-op machine decides — the game's own copy (verdicts apply) or a relayed one (it already happened): player-state writes are dropped; skip world and body effects in it |
| `Refii.player_for(e)` | the player `e` deals with, 1 or 2: a hero its own, a dog its master's, anything else the nearer living hero (3 m of hysteresis). Defined by the core co-op Lua |
| `Refii.acting_player()` | 1 or 2: who "the hero" (`GetPlayerHero()`) means right now — 2 inside `Refii.run_as_player(2, …)` and in AI acting for player two. Defined by the core co-op Lua |
| `Refii.run_as_player(p, fn, ...)` | runs `fn` with `GetPlayerHero()`, `GetPlayerHenchman()` and `QuestManager.HeroEntity` as player p's (the partner as the henchman), so a shipped script acts for either hero. Defined by the core co-op Lua |
| `Refii.combat_state(p)` → midst, close_to_combat, in_any_combat | player p's combat state, kept per hero (the shipped global is one state). Defined by the core co-op Lua |
| `Refii.show_to_player(p, name, ...)` | runs the function a global path names (`"GUI.DisplayMessageBox"`, `"GUI.AskYesNoQuestion"`, `"TutorialManager.DisplayTutorial"`, …) for player p, on p's screen: online on the machine that plays p (a question waits — call it from a coroutine — and returns the answer p gave), else here as p (split screen: in p's half, on p's pad). Defined by the core co-op Lua. See "Player two's screen" |
| `Refii.forgive_crimes(villager, p)` → n · `Refii.discovered_crime(villager, p)` · `Refii.perceived_renown(villager, p)` | a villager's crimes and renown for player p's hero: forgives only p's crimes it knows of (the other hero's stand; how many), whether it has discovered one of p's, the renown it credits p with (nil for no villager). The shipped `Villager.ForgiveHerosCrimes` / `HasHeroCommittedCrimeIHaveDiscovered` / `GetPerceivedRenownOfHero` name no hero; with a partner they answer for the acting player through these |
| `Refii.world_here()` | true where the shared world runs — the AI, every hit's outcome, the NPCs: alone, in local co-op and on an online host; make world effects only there |
| `Refii.split_screen()` | true when local co-op runs on split screen (each local player their own camera) |
| `Refii.opinion(villager, p)` → love, fear, attractiveness, morality, respect | how a villager feels about player p's hero (-1..1); each villager keeps a set per hero, and only player two's deeds move player two's; nil when it holds no opinions |
| `Refii.focus(p)` | the living entity player p's hero is focused on, or nil. The targeting is driven where that hero is played, so ask where `Refii.player_here(p)` holds: on an online joiner `Refii.focus(1)` reads the host hero's copy, not the host player's choice |
| `Refii.wheel_switches_target(e)` | hero `e`'s target lock switches by the mouse wheel this tick — call it each tick the lock holds (the statement holds for the guest tick it is made on and the two after; the Lua `tick` runs every guest tick, so a lock stated from it never lapses). While it holds and the hero is locked under the third-person camera the keyboard and mouse drive, the mouse's movement never switches target and each wheel notch is one switch through the camera's own `Targeting.CheckForBetterTargetInDirection` (down the next target right, up the next target left), waiting on the camera's `ClearToRotate` as a stick flick does, at most one notch waiting. Without it a mouse flick switches. A pad is untouched. Souls Combat's toggled lock uses it |
| `Refii.uid(e)` | the entity's own 64-bit UniqueID as `"0x…"`: the same on both co-op machines and across saves (a handle is neither), so it keys per-thing player values (`Refii.player_save(p, "mymod.opened." .. Refii.uid(e), 1)`) |
| `Refii.coop_mode()` | 0 alone, 1 local co-op (split or shared screen), 2 online with this machine hosting, 3 online with this machine joined |
| `Refii.key_taken(e, p)` | whether player p has taken the per-player collectable `e` already (silver keys: each hero picks up their own) |
| `Refii.ask_partner(asker, question)` → ticket · `Refii.partner_answer(ticket)` → nil, true, false | puts your own question to the other player (a box on their half, or on the peer's screen online); nil while pending; thirty seconds unanswered is a no; with no partner in the game the answer is yes at once |
| `Refii.renown(e)` · `Refii.morality(e)` · `Refii.purity(e)` · `Refii.age(e)` · `Refii.physique(e)` → fat, height | the hero stats the game itself reports |
| `Refii.inventory(e)` | `{ {definition = "0x…", count = n}, … }` in the game's order |
| `Refii.equipped(e)` → melee, ranged | the weapon set's definition hashes; nil for an empty hand |
| `Refii.near(x, y, z, r [, creatures_only])` | handles within a radius, nearest first (at most 2048) |
| `Refii.line_of_sight(x1, y1, z1, x2, y2, z2)` | true when the game's physics sees nothing between |
| `Refii.spawn(def, x, y, z)` | a new entity through the game's own CreateEntityFromIDAtPosition: its handle, or nil |
| `Refii.entity(handle)` · `Refii.handle(entity)` | the game's own Entity userdata for a handle — what the shipped scripts and the game's natives take — and back |
| `Refii.play_fx(name, x, y, z [, options])` · `Refii.play_fx_on(e, name [, options])` | an effect of the particle bank or of an enabled mod's `fx/effects/`, drawn by Godot at a world position (the guest's axes) or riding an entity handle in that entity's own frame (it turns with it; `offset` is along its axes); `options = { duration = s, scale = n, tint = {r, g, b}, intensity = n, loop = false, offset = {x, y, z}, at = "Weapon.Weapon.Top" }` — `at` names a helper point of the entity's model (see play_fx); returns the id, or nil |
| `Refii.stop_fx(id)` | stops a played effect by any surface's id (native, Lua or Godot) |
| `Refii.mark(target, icon, text [, p])` · `Refii.unmark(id)` | a marker on the HUD's world-icon channel over an entity handle or at a point `{x, y, z}` for player p (nil: every player this machine plays): `icon` a game texture path or a mod PNG (`art/x.png`), `text` a line under it; the id `unmark` takes (see HUD: markers, fades, meters) |
| `Refii.fade(p, dir [, seconds])` | fades player p's screen (0: every player played here) `"to_black"` / `"from_black"` (`"out"` / `"in"`) over seconds (nil: at once); the host's own layer, never the game's fade |
| `Refii.meter(id, label, value, max [, p [, style]])` · `Refii.meter_remove(id)` | a HUD meter (a timer, a boss bar) for player p in style `"bar"` (nil), `"boss"` or `"timer"` (the HUD's primitives), or `"generic"`, `"crucible"`, `"price"` (the game's own meter art, see HUD: markers, fades, meters); the same id restates it in place |
| `Refii.light(target, color, energy, range [, o])` · `Refii.light_remove(id)` | an omni light on an entity handle (in its own frame, riding the body; `o.at` a model helper, `o.offset` along its axes, `o.flicker` 0..1) or at a point `{x, y, z}`; `color = {r, g, b}`, `energy` 1 a placed lamp's, `range` metres (0: 6); the id `light_remove` takes, or nil (see Lights) |
| `Refii.send_to_partner(mod, name, payload)` | a mod's own message to the other machine of an online session, raised there as `partner_message` once and in order (see Partner messages); true when posted, false alone or in local co-op |
| `Refii.api_version` | the API version the chunk was composed for |
| `RefiiModSave.<key>` | state that persists with the save game |
| `RefiiModErrors` | every init and handler error, appended; a handler's error never stops the others |

**One file per concern.** A mod's Lua need not be one file. Every other `mod/*.lua` is a
MODULE, reached from the mod's own chunks with `require("name")` — the path under `mod/` without
`.lua`, `.` or `/` between folders (`require("ai.wheel")` is `mod/ai/wheel.lua`; names are
lower-cased). The game VM has no `require` and no file system to resolve one from, so the loader
composes each module INTO the init chunk as a function registered under the mod's name, and
`require` is a local of the mod's block: it runs the function once, caches its result (`nil` as
`true`, a cycle sees `true`), and errors by file name for a module the mod does not ship. A mod
sees only its own modules; they are lazy, so only what a chunk requires runs, in the order it
requires it (handlers subscribe in that order). `camera.lua` shares the game VM's registry;
`frontend.lua` has the modules again in its own state. A module is compiled with the game's
compiler like the entry points and left out by name when it fails. Souls Combat is the example:
`init.lua` is ten `require`s, one per rule, with `common.lua` for what they share.

**Handles, not addresses.** This Lua's number is a 32-bit float, so an entity or object reaches
Lua (and a Godot script) as a HANDLE: a small integer that stands for that object for the
session, the same in every event and every `Refii.*` call (`refii_mod_handle` natively). Compare
them (`e.victim == Refii.hero(1)`), key tables by them, pass them back; never treat one as an
address. A hash — an item definition, a text tag — is a `"0x…"` string, exact in a string, and
`Refii.hash("OBJECT_HEALTH_POTION")` makes one from a name.

**Nested and deferred events.** An event the game raises while a Lua handler is running — a
`Refii.cast_spell`, whose factory raises the cast's `spell_created` and, for a targeted Lightning,
lands the bolt's `hit` / `hit_landed` inside its Start; a `Refii.give_gold`, which reaches the hooked
purse change — goes to native handlers and Godot scripts as it happens. To Lua it goes one of two
ways. An event **with writable fields** (`hit`, `gold`, `experience`, `spell_launch`, `relation`,
… — every kind with a writable column below) runs **live, nested** inside the running chunk, so
your write reaches the game: the bolt's `hit` handler scales `e.damage` as a native handler would.
A chunk nests once (an outer chunk and one inside it); a writable event raised two chunks deep is
deferred instead and the log says so. The bus's own state is per depth: the running event a write
goes to is saved and restored around the nested run, and so is the cancel verdict, so a nested
cancel never becomes the outer's. An event **with nothing to write** (`spell_created`,
`hit_landed`, `vortex_started`, …) is deferred: the bus keeps its table text and runs it once the
outermost chunk has returned, in the order raised, with `e.deferred = true` — by then the game has
acted, so a cancel there has no effect; every field is as it was when raised (handles resolved
then). An event a deferred handler raises is deferred behind it; one drain runs at most 64 chunks
and at most 32 wait at once (the rest are dropped and logged). The `tick` and `spell_charge_level`
are per-frame statements and are neither nested nor deferred. A failed chunk (no VM yet, a runtime
error outside the handlers) never counts as a cancel; only a handler returning `true` does.

**Dead handles.** When an entity is destroyed its handle resolves to nothing from then on
(`Refii.alive(h)` is false, helper calls do nothing); an object that later takes the same memory
gets a new handle.

**Writable fields.** Assigning a writable number in a handler changes what the game gets:
`e.damage = e.damage * 2` on `hit`, `e.delta` on `gold`, `e.amount` on `experience`, `e.level`
on `ability_level` and `spell_charge_level`, `e.type` on `spell_launch`, `e.relation` on
`relation`. The preamble diffs the fields after the handlers ran and writes each change back
through the native `Refii._write`. Every event is dispatched the moment it fires, the `tick`
included: Lua tick handlers run every guest tick with that tick's `seconds`, so a Lua mod's delays
and cooldowns resolve on the grain native handlers get (the chunk run is a compile of one 119-byte
line: 2.7 µs load + run on the host's own Lua 5.1.2 with eight handlers, the handlers' work on top).
The game's `RunScriptBuffer` is re-entrant (verified in the binary: per-state, frame-relative, no
globals), so a handler runs even when the seam was reached from the game's own Lua.

The game's own natives — 3,602 in 234 tables — are all callable from `init.lua`;
`Godot/modding_tools/game_lua_api.tsv` lists them (table, name, native address), and
`RefiiModApi.ListGameLuaTables()` / `ListGameLuaNatives(table)` / `FindGameLuaNatives(text)`
read it in the editor.

## The events

Kinds are stable within an API version. "Cancel" says what `cancel` (native) or `return true`
(Lua) does; blank means the event cannot be cancelled. Seams are config names
(`versions/*/refii_config.toml`), the same on the disc and Title Update builds.

**Capacity.** A subscription crosses each seam as a bit mask, and every mask holds at least 200
kinds (`REFII_EVENT_COUNT` is 100). Lua: `Refii._masks[g]` is groups of 20 bits — the game Lua's
number is a 32-bit float, exact to 24 bits, so no number crossing the seam ever carries more than
20 significant bits — and the payload reads 10 groups, kinds 0..199 (`mod_events.cpp`
`kLuaMaskBits` / `kLuaMaskGroups`; `ModApi.LuaMaskBits` / `LuaMaskGroups` state the same to the
preamble's `Refii.on`). Godot: `RefiiHost`'s event mask and verdict mask are 4 words of 64, kinds
0..255 (`REFII_MOD_HOST_MASK_WORDS` in `payload_bridge.h`, `ModApi.HostMaskWords`, the
`ModEventMask` struct); words 0 and 1 cross through the two mask exports, the words past them
through the tick's `mask_word` command, so a kind at or past 128 is wanted from the next guest
tick. Native subscriptions are a table, not a mask. The front-end state (`mod/frontend.lua`) has no
mask: the events that reach it are fired into it unconditionally. `ModApiTests.
TheMasksHoldEveryKindOnEverySurface` (and a `static_assert` beside each constant) fails when
`REFII_EVENT_COUNT` passes either capacity: past 200 add a Lua group (`kLuaMaskGroups` and
`ModApi.LuaMaskGroups` together), past 256 a host word (`REFII_MOD_HOST_MASK_WORDS`,
`ModApi.HostMaskWords`, `ModEventMask`'s fields).

| # | Native / Lua | Fires | Cancel | Seam |
|---|---|---|---|---|
| 0 | Tick / tick | once per game step, menus included, on every surface at once; `seconds` (that tick's, capped 0.25) | | the guest tick |
| 1 | Hero / hero_ready | a player's hero exists (session, save, co-op join); `player`, `hero` | | derived in the tick |
| 2 | HeroGone / hero_gone | a player's hero stopped existing | | derived in the tick |
| 3 | Lua / lua_ready | the game's Lua was rebuilt and every init.lua ran; `generation` | | rex_lhLuaScriptSystem_LoadStartupScripts |
| 4 | SaveLoaded / save_loaded | a save's Lua table loaded: RefiiModSave holds the save | | rex_lhLuaScriptSystem_LoadScriptSave |
| 5 | GameAction / game_action | a button's GameAction reached a hero's receiver; `action`, `hero` | the game never sees it | rex_CPlayerActionReceiver_ProcessAction |
| 6 | AbilityRequest / ability_request | a creature asked for an ability; `type` (16 melee press, 17 release, 18 charge) | refused | rex_CECAbilities_PerformRequest |
| 7 | MeleeSwing / melee_swing | a strike's swing committed; `action` (the live strike action — a handle in Lua and Godot, what `current_action` answers; 0 in a relayed copy). **Relayed** (player) | | rex_CAbilityActionMelee_CommitSwing |
| 8 | Roll / roll_can_start | the roll asks to start | not started | rex_CAbilityActionStrafeRoll_CanStart |
| 9 | RollStarted / roll_started | a roll was queued. **Relayed** (player) | | rex_CAbilityActionStrafeRoll_Start |
| 10 | Block / block | a block takes a hit (once per hit); `damage`, `unblockable` | the guard breaks | rex_CActionBlock_OnHitBlockPose / _OnHitBlockedResponse |
| 11 | AnimEvent / anim_event | an animation event reached an action; `name`, `entity` (the action's owner), `action` (a handle in Lua and Godot: match a committed strike on it) | dropped | rex_CAction_OnAnimEvent |
| 12 | Hit / hit | a hit arrives at its victim, before delivery; `damage` and `knockdown` writable, spell fields. Force push arrives here: it builds a hit with spell type 6 and its knockdown flag set (0x828B496C) | the hit is dropped | rex_CECHitReceiver_OnHit |
| 13 | HitLanded / hit_landed | the same hit, after delivery. **Relayed** (world: a hit with a hero in it, host to joiner) | | rex_CECHitReceiver_OnHit |
| 14 | HitResponse / hit_response | the victim is about to flinch; `damage`, `health`, `max_health` | absorbed, no flinch | rex_CECHitResponsePlayAction_OnHit |
| 15 | SpellCharge / spell_charge | a hero pressed B to charge | refused | rex_CAbilityActionMagicCharge_CanStart |
| 16 | SpellChargeLevel / spell_charge_level | the charge became a top slot (the cascade launches 0..level); `level` writable. Every frame of a held charge, to every surface alike: a cap is re-evaluated each frame, a repeat charge to the same slot is reported again | | rex_CECSpellManager_ChargeLevelFromTime |
| 17 | SpellLaunch / spell_launch | a slot is about to launch; `type` writable for this launch, `cascading` | skipped | rex_CECSpellManager_LaunchSlot |
| 18 | SpellLaunched / spell_launched | a spell was really created for a slot | | rex_CECSpellManager_OnSpellLaunched |
| 19 | SpellCreated / spell_created | the spell factory made a spell object; `serial` | | rex_CSpellRegistry_Create |
| 20 | Experience / experience | experience changes; `pool`, `amount` writable, `spend`, `nested` (a Spend's payments or the co-op share: scale only when false) | untouched (not when nested) | rex_CECExperience_Modify / _Spend |
| 21 | EntityDied / entity_died | a creature is dying, from any cause (including deaths finished without the dying step); `killer` | for a death by damage only: it keeps 1 health | rex_CECCreature_Die / rex_CECCreature_FinishDeath |
| 22 | HeroDowned / hero_downed | a hero hit zero: knockdown, scar, revive follow | skipped, hero keeps 1 health | rex_CECHealth_OnHeroDeath |
| 23 | Damage / damage | health is about to take a hit's final damage; `lethal` | no health removed | rex_CECHealth_OnHit |
| 24 | LevelLoaded / level_loaded | a level is up and the heroes stand in it; `level`, `from_save` | | rex_lhLuaScriptSystem_OnLevelLoad |
| 25 | LevelUnloading / level_unloading | the current level is about to go; `level` | | rex_lhWorld_UnloadLevel |
| 26 | GameSaving / game_saving | a save file is about to be written; `file` | | rex_lhSaveManager_WriteSave |
| 27 | ItemAdded / item_added | an item was added to an inventory (after the add); `definition` | | rex_CECInventory_AddOneOfRecord |
| 28 | ItemUsed / item_used | an entity uses an item; `definition`, `count`. **Relayed** (player) | refused | rex_lhEntity_UseInventoryItem |
| 29 | HeldItem / held_item | an item attached to or released from a holding slot; `slot`, `attached` | a release is refused (an attach cannot be) | rex_CECHeldItems_AttachItemToSlot / _ReleaseSlot |
| 30 | WeaponEquipped / weapon_equipped | a weapon equipped into the weapon set; `definition` | | rex_CECWeaponSet_RecordDrawnItem |
| 31 | AbilityLevel / ability_level | an ability's level is set; `type`, `level` writable, `old_level` | unchanged | rex_CECStats_SetAbilityLevel |
| 32 | Gold / gold | a purse changes; `gold`, `delta` writable, `passive`, `share` (the game's co-op split of a change already reported: scale only when false) | unchanged (not for a share) | rex_CECMoney_Modify |
| 33 | Relation / relation | the game asked how `entity` stands to `other`; `relation` writable (1 neutral 2 ally 4 enemy) | | rex_CECFaction_RelationTo |
| 34 | InputAction / input_action | a mod's input.json action pressed or released; `name`, `player`, `pressed` | | the guest tick, from the host's input queue |
| 35 | QuestStarted / quest_started | a quest is activated on a hero's tracker; `quest`, `make_primary` | stays inactive | rex_CECQuestTracker_SetAsActive |
| 36 | QuestEnded / quest_ended | a quest completed (rewards given) or failed; `quest`, `completed`, `failed` | | rex_CECQuestTracker_OnQuestEnded |
| 37 | QuestObjective / quest_objective | an objective tag is added to a quest; `quest`, `tag` | nothing added | rex_CECQuestTracker_SetObjectiveTagSilently |
| 38 | Dialogue / dialogue | a creature is about to speak a line; `speaker`, `text` (the key's hash), `listener`, `subtitle` | the line is not spoken | rex_CECTalk_PlayLine |
| 39 | EntitySpawned / entity_spawned | an entity joined the world (next tick, components built; at most 64 a tick); `entity`, `definition` | | rex_lhEntityManager_Add |
| 40 | EntityRemoved / entity_removed | an entity is leaving the world, still whole; `entity`, `definition` | | rex_lhEntity_Destroy |
| 41 | ExpressionStarted / expression_started | a creature starts an expression; `expression` | refused | rex_CECExpressionPerformer_StartExpression |
| 42 | Renown / renown | renown changes; `delta` writable, `share` (the game's co-op split of a change already reported: scale only when false) | unchanged (not for a share) | rex_lhHeroStats_ModifyRenown |
| 43 | Morality / morality | morality changes; `delta` writable | unchanged | rex_lhHeroStats_ModifyMorality |
| 44 | Purity / purity | purity changes; `delta` writable | unchanged | rex_lhHeroStats_ModifyPurity |
| 45 | Marriage / marriage | a hero marries; `spouse` | refused | rex_CECPlayerFamily_Marry |
| 46 | DogTrick / dog_trick | the dog is told a trick; `dog`, `trick`, `player` (whose dog: by its master, Dog.GetMaster at the dog component's +300, else the players' dog records; 0 neither) | not done | rex_CECDog_PerformCommand |
| 47 | RangedShot / ranged_shot | a ranged weapon is about to fire; `shooter` (the holder), `weapon`, `target`, `aimed`; the game may still refuse it | not fired | rex_CECFirearm_Fire |
| 48 | StatusEffect / status_effect | electrified (1), chaos (2), burning (3), caught in a vortex (4), knocked down — every ragdoll fall (5), mauled by the dog (6), unconscious (7) or a hollow man crumbled to bones (8) started or ended; `effect`, `started` | | rex_lhEntity_AddComponent / _RemoveComponent, rex_lhRagdoll_GoFloppy / _Recover, rex_CECCreature_SetUnconscious, rex_HollowMan_SetCrumbled |
| 49 | TargetChanged / target_changed | a player's target changed; `target` (0 cleared) | | rex_CECTargetingPlayer_SetTarget / _ClearTarget |
| 50 | OpinionDeed / opinion_deed | a villager reacts to a deed, expression or gift; `doer`, `deed`, `kind` | ignored | rex_CECOpinionReaction_OnDeed |
| 51 | OpinionModified / opinion_modified | a script changes an opinion axis; `axis`, `delta` writable, `player` (whose set moves: 2 inside player two's opinion scope for the villager, else 1) | unchanged | rex_CECOpinionReaction_ModifyAxisValue |
| 52 | TimeOfDay / time_of_day | a new hour or day; `hour`, `day`, `time`, `new_day` | | rex_lhGameTimeManager_Update |
| 53 | WeatherTheme / weather_theme | a scripted environment-theme blend; `theme`, `weight` and `seconds` writable | skipped | rex_lhEnvironmentThemeManager_BlendToTheme |
| 54 | SleepMenu / sleep_menu | a bed's sleep menu is about to open; `player` (who is sleeping: the bed's user as the game's use routine passed it to the bed's script) | stays closed | rex_SleepMenu_Show (the user: rex_CECOnActionUse_Use) |
| 55 | Travel / travel | a travel started; `world`, `level`, `entrance`, `type`, `full_load` (false inside the loaded level), `player` (who asked: see below) | | rex_CmdLevelChange_Start |
| 56 | FastTravel / fast_travel | a fast-travel stop was chosen; `level`, `stop`, `paid`, `player` (the world map's owner; 0 for a script's) | stays put | rex_FastTravelManager_TravelToStop (the map's call: rex_lhGuiWorldMap_FastTravel) |
| 57 | MenuOpened / menu_opened | the game entered menu mode (the world pauses) — real transitions only; `player` (whose menu) | | rex_lhGuiManager_EnterGuiMode / _EnterGuiModeQuiet |
| 58 | MenuClosed / menu_closed | the game left menu mode — real transitions only; `player` | | rex_lhGuiManager_ExitScreen |
| 59 | MenuScreen / menu_screen | a menu shows a screen; `screen` (0 pause, 2 spells, 14 abilities, 16 equipment, 17 logbook, 18 options, 22 shop, 23 world map, 24 co-op), `player` | | rex_lhGuiManager_SetScreen |
| 60 | WeatherRegion / weather_region | the hero walked into a different painted weather region; `day_set`, `previous` (that player's), `inside`, `player` (the hero the game tested: player one's slot, once a frame — never once per split-screen view) | | rex_lhPaintMap_DaySetAt |
| 61 | SleepChosen / sleep_chosen | the player answered a bed's sleep menu; `choice` writable (0 fast-forwards six hours, 1 twelve, 2 adds a day with the clock unchanged, 3 adds seven days), `cancelled`, `bed`, `player` (who is sleeping, as SleepMenu said); any other length: wrap the shipped `SleepFunction.Sleep` in Lua | the sleep is refused | rex_lhMenuBox_Close |
| 62 | SlowTime / slow_time | Slow Time changed the world's time scale (every creature but the caster slows); `scale` writable, `active` | | rex_SpellSlowTime_SetWorldScale |
| 63 | Summoned / summoned | a spell summoned a Raise Dead minion (1) or a Blades sword (2); `summoner`, `summon`, `kind` | | rex_SpellRaiseDead_BindSummoner / rex_SpellDamoclesSword_Create |
| 64 | Crime / crime | a crime is recorded; `criminal`, `player`, `victim`, `victim_player`, `type` (2 murder, 3 assault, 6 trespass, 11 vandalism among them); a reported murder also records an assault, raised again; the co-op partner's crimes are their own | no witness record, no bounty, no guards | rex_CECCrime_Add |
| 65 | Achievement / achievement | an achievement unlocks, for both heroes; `id` (1..50), `source`, `player` | it stays locked | rex_CECStats_UnlockAchievement |
| 66 | CollectableTaken / collectable_taken | a player picks up a one-time collectable kept per player in co-op (a silver key); `player`, `entity`, `record`, `stays` (it stays in the world for the partner) | | rex_CECInventory_AddEntity |
| 67 | PartnerConsent / partner_consent | a co-op script asks the other player's consent (the beds, `Refii.ask_partner`); `ticket`, `kind` (1 sleep, 2 sex, 0 a mod's question), `asker`, `detail`; `answer` writable: -1 asks the partner, 0 refuses, 1 agrees for them | | the consent ask |
| 68 | PropertyBought / property_bought | a hero buys a building; `hero`, `player`, `building`; `price` writable (condition included) | stays unbought, gold untouched | rex_CECPlayerProperties_Buy |
| 69 | PropertySold / property_sold | a hero sells a building (a shared one gives the partner half); `hero`, `player`, `building`; `price` writable | stays owned | rex_CECPlayerProperties_Sell |
| 70 | RentPaid / rent_paid | rent and shop profits paid (every five real minutes, or the catch-up for time away); `hero`, `player`, `gold`, `drops`, `away` | | rex_CECPlayerProperties_PayIncome |
| 71 | PropertyRented / property_rented | a building rented out or its tenants evicted; `hero`, `player`, `building`, `rented` | stays as it was | rex_CECPlayerProperties_SetRentedOut |
| 72 | PropertyCondition / property_condition | Fable 3's condition changed (wear at a rent drop, or a repair); `uid`, `player`, `condition`, `previous`, `repaired` | | the condition rules |
| 73 | Divorce / divorce | the spouse has left; `hero`, `player`, `spouse` | | rex_CECPlayerFamily_FinishDivorcing |
| 74 | Widowed / widowed | a spouse died or was removed; `hero`, `player`, `uid` | | rex_CECPlayerFamily_Widow |
| 75 | ChildBorn / child_born | a child born to (or adopted by) a hero and spouse; `hero`, `player`, `spouse`, `child` | | rex_CECPlayerFamily_AdoptWithSpouse |
| 76 | GiftGiven / gift_given | a hero gives a gift (a ring among them) to their target; back in 15 s unless kept; `hero`, `player`, `target`, `item` | nothing given | rex_lhGiftComponent_GiveGift |
| 77 | GiftAccepted / gift_accepted | a villager kept the gift (its morality applies); `hero`, `player`, `item` | | rex_CECHeroGifts_RemovePermanently |
| 78 | Engaged / engaged | a villager engaged to a hero, or the engagement ends; `villager`, `player`, `engaged` | stays as it was | rex_CECOpinionReaction_SetEngaged |
| 79 | Follow / follow | a creature starts or stops following; `follower`, `leader`, `player`, `following` | on a start: does not follow | rex_CECFollow_Start / rex_CECFollow_Stop |
| 80 | FurnitureUpgraded / furniture_upgraded | furniture changes level while redecorating; `piece`, `player`, `previous`; `level` writable | keeps its level | rex_CECUpgradeableFurniture_SetLevel |
| 81 | Redecorating / redecorating | redecorating starts or ends; `building`, `player`, `on` | | rex_CECBuilding_SetRedecorating |
| 82 | HouseholdBudget / household_budget | a household's budget set at its home; `hero`, `player`, `building`; `gold` writable | budget stays | rex_lhGuiRealEstateScreen_SetBudget |
| 83 | TrackedStat / tracked_stat | a tracked stat counts (ETrackedStatType: kills, sex, marriages, children…); `hero`, `player`, `stat`; `amount` writable | nothing counted | rex_CECStats_ModifyTrackedStat |
| 84 | Partnership / partnership | Fable 3's player partnership changed; `kind` (0 none, 1 business, 2 married), `previous` | | the household tick |
| 85 | HouseholdRestored / household_restored | player two's household and property put back on their hero in the host's world; `hero`, `player`, `spouses` | | the household tick |
| 86 | DynamicWeather / dynamic_weather | the dynamic weather in effect changed: `level` ("<level>/<scenario>"), `weather` (a preset — Clear, PartlyCloudy, Cloudy, Overcast, Rain, Storm, Snow, Fog, LightRain, LightSnow, Blizzard — or `theme`, `interior`), `previous`, `seconds` (the blend), `from_host` (an online joiner adopting the host's statement) | | the guest tick, from Godot's `ModWeather:` pump action |
| 87 | ModMenuRow / mod_menu_row | a row of a mod's menu was chosen (a menu a mod defines in `ui/templates.json`, a row a mod patched into a shipped menu, or any `Opens:` / `Mod:lua:` row); `menu`, `row`, `player`, `mod`. Godot scripts get it as it happens; native and Lua handlers on the next guest tick — Lua in a game (init.lua) and on the title and front end (mod/frontend.lua) | | Godot's menu lists (`Engine_UiModMenus`), then the guest command `menu_row` |
| 88 | ModMessageBox / mod_message_box | a mod's message box was answered; `id`, `button` (0 A, 1 B, -1 replaced or played elsewhere), `player`. Lua in a game and on the front end | | Godot's host boxes, then the guest command `box_answer` |
| 89 | ModNotice / mod_notice | a mod's notice has gone; `id`, `outcome` (0 shown, -1 never shown), `player`. Lua in a game and on the front end | | Godot's notice channel, then the guest command `notice_done` |
| 90 | ModMenuBox / mod_menu_box | a mod's menu box was answered; `id`, `option` (-1 none), `player`. Lua in a game and on the front end | | Godot's menu box, then the guest command `menu_box_answer` |
| 91 | ModAbility / mod_ability | a player is buying (`refund` false) or unlearning (`refund` true) a level of a mod ability (`ui/abilities.json`) — the ENGINE's purchase, raised before anything changes and only for one that can happen; `ability`, `mod`, `previous`, `level`, `experience` (the price, or what comes back), `player` | nothing spent, the level stands | the engine's purchase (`mod_abilities.cpp`), from the book's `ability_buy` / `ability_refund` |
| 92 | SpellPick / spell_pick | a player picked (or cleared, `spell` -1) a pick on a spell slot: a mod's (`ui/spellwheel.json`; the engine saves it after the handlers) or the game's own, the slot's spell (`pick` "spell", `mod` ""; every step of the selectors' carousel, the scripts' spell-table natives, the host's assignment — not the template's defaults, the other machine's pick arriving, or a save load); `pick`, `mod`, `slot` (0..4), `spell` writable, `previous`, `player` | the slot's pick stays as it was | a mod's: the spell selectors (`Engine_UiSpellPicks`), then the guest command `spell_pick`; the game's: the guest's slot writer `CECSpellManager::SetSpellSlot` (TU1 `sub_82541F30`, `mod_abilities.cpp`) |
| 93 | PartnerMessage / partner_message | a mod's own message from THE OTHER MACHINE of an online session (`send_to_partner` there): `mod`, `name`, `payload` (at most 256 bytes; a quote, backslash or line break arrives as `_` on Lua and Godot), `from` (the player the sender plays: 1 host, 2 joiner). Once, in the order sent, on the route; never on the sender's machine, never alone or in local co-op; not a replica (player-state writes count) | | the receiving machine's guest tick, from the route's mod lane (`core/event_relay.h` `pm:` lines, `DrainPartnerMessages`) |
| 94 | LookAtButton / look_at_button | a player hero's look-at flag is being written — the byte every lock camera script reads, what a held LT states — by the camera's LT-down event (1), the hero's release handler (0) or the API's own `lock_target` / `release_lock`: `hero`, `player`, `down` writable, `component` (native) | the flag stays as it was; the writer never runs | `rex_CPlayer_SetLookAtButtonDown` (r3 comp33, owner +4; r4 the value), before it |
| 95 | VortexStarted / vortex_started | a vortex is live: its component (211) was initialised on the vortex entity by the vortex spell's post-init, inside the factory and BEFORE `spell_created` (the spell object finishes at once); `vortex` (native), `entity`, `caster`, `caster_player`, `target`, `serial`, `level`, `x, y, z` (the entity's position). Online the cast runs on both machines (replica on the copy) | | `rex_VortexComponent_Init` (r3 component, r4 caster, r5 target, r6 serial -> +0x18, r8 level -> +0x14), after it |
| 96 | VortexEnded / vortex_ended | the vortex's Shutdown, with the twister's last point (component +0x140): `vortex` (native), `entity`, `caster`, `caster_player`, `serial`, `x, y, z` | | `rex_VortexComponent_Shutdown` (r3 component), before it |
| 97 | FireballImpact / fireball_impact | a targeted fireball's Impact ran (its exploded-byte guard passed): `spell` (native), `serial`, `level`, `caster`, `caster_player`, `struck` (0 for ground, wall or water), `struck_player`, `x, y, z` the impact position, `nx, ny, nz` the normal, `detonated` (spell+0x48 went 0 -> 1 inside: the blast delivered every victim's hit inside the call; a creature the fireball passes and ignites leaves it clear), `hits` (hits taken inside the call carrying this serial: 0 on a detonation = the blast found nothing alive). Online the cast runs on both machines; hits are decided where `world_here` holds | | `rex_TargetedFireball_Impact` (r3 spell, r4 struck, v1 position, v2 normal), after it |
| 98 | Paused / paused | THE GAME'S PAUSE STATE flipped — a GameFlow mode command of the pause family became the mode in force (its apply: both players' input categories applied, the world held behind it, what `rex_lhGameFlow_IsGuiActive` answers) or stopped being it (its remove): `paused`, `mode` (1 the GUI / pause command — the pause menu and every screen entered from play, the partner's relayed pause, a camera mode; 2 a level change), `player` (mode 1 with a menu screen up: whose menu, 2 on an online joiner or for local player two's pause book, else 1; 0 for a pause with no screen and for mode 2), `screen` (the GUI manager's screen id, TU1 numbering: 31 none, 0 the pause root, 29 co-op; -1 when the in-game GUI is not up). Raised again when a command above it is removed (the mode re-applied) and when one is pushed over it (removed for its length) | | `rex_lhGameFlowGuiMode_Apply` / `rex_lhGameFlowGuiMode_Remove` (r3 the command; its id by vtable slot 1), after each |
| 99 | StartGame / start_game | the front end starts a game — New Game, Load, Continue or a co-op join stated the target the in-game state is created from: `save` ("" for a new game), `level`, `new_game`, `player` (1). On the title there is no game VM: Lua hears it in `mod/frontend.lua` | | `rex_lhFrontEnd_SetStartGameTarget` (r3 the front-end object, r4 the save name, r5 the start level), before it |

**Whose menu, whose travel, whose sleep.** The menu events' `player` is whose menu it is: 2 on an
online joiner, and in local co-op while player two owns the pause (the pause screen's owner, set
before the menu mode is entered) or their shop; 1 otherwise. Travel names no traveller in the game
— both heroes go — so its `player` is who asked: a level exit's user (the player who held A on
it), the player its walk-in test tested (the hero; player two by the co-op travel rule), an online
joiner's request (2), or the world map's owner for a fast travel; 0 for a script's travel, and on a
joiner for one the host started. Sleep's `player` is the bed's user as the game's own use routine
handed it to the bed's script (OnActionUseBed passes it to SleepFunction.Sleep).

**One dynamic weather per session.** The weather is decided on the host side — Auto, or a set
weather — and an online joiner adopts the host's statement for the level it is in; each player's
own sky switch only shows or hides it. DynamicWeather fires on both machines: on the host when it
decides, on the joiner when it adopts (`from_host = true`).

**Where the household events fire online.** Each fires once, on the machine where it takes effect:
player two's household, property and the economy are the host's, so 68–75, 77, 78, 80 and 82–85 fire on
the host (a joiner's estate actions, gifts, furniture and bed are run there). A gift fires on the
giver's machine, and redecorating on the redecorating player's machine. Player two's tracked stats
from the host's household work are counted on the joiner, and fire there.

The Guide's tab 7 ("Events & the Mod API") lists every field with its meaning, generated from the
catalog; `RefiiModApi.DescribeEvent(name)` returns the same.

## The helpers (native)

All C linkage in `core/refii_mod.h`. `ctx` is the event's or hook's context.

- Heroes and entities: `refii_mod_player_hero(base, 1|2)`, `refii_mod_local_hero`,
  `refii_mod_player_of(base, entity)` (1, 2 or 0), `refii_mod_player_for(ctx, base, entity)` (who
  "the hero" is for it: a dog its master's, else the nearer living hero; 0 only with no hero),
  `refii_mod_world_here(base)` (this machine runs the shared world: alone, local co-op, the online
  host), `refii_mod_menu_player(base)` (whose book is open, 1 or 2), `refii_mod_component(base, entity, id)`,
  `refii_mod_opinion(base, villager, player, out[5])` (the villager's current axes for that hero),
  `refii_mod_focus(base, player)` (the living entity that hero is focused on, or 0),
  `refii_mod_wheel_switches_target(base, hero)` (that hero's target lock switches by the mouse
  wheel this tick, not by a mouse flick — state it each tick the lock holds; `Refii.wheel_switches_target`),
  `refii_mod_coop_mode(base)` (0 alone, 1 local co-op, 2 online hosting, 3 online joined),
  `refii_mod_entity_alive`, `refii_mod_entity_position(ctx, base, entity, out[3])`,
  `refii_mod_set_position(ctx, base, entity, xyz)`, `refii_mod_entity_definition` (the GDB
  record hash), `refii_mod_entity_name(base, entity, out, size)`,
  `refii_mod_relation(ctx, base, a, b)` (1 neutral, 2 ally, 4 enemy).
- Tint: `refii_mod_entity_tint(ctx, base, entity, r, g, b, a)`,
  `refii_mod_entity_untint(ctx, base, entity)` (see Tint below).
- The other appearance setters: `refii_mod_entity_alpha(ctx, base, entity, alpha)`,
  `refii_mod_entity_scale(ctx, base, entity, scale)`, `refii_mod_entity_saturation_mask(ctx, base,
  entity, on)`, `refii_mod_entity_drawable(ctx, base, entity, on)` (see Appearance below).
- The target lock: `refii_mod_lock_target(ctx, base, player, entity)`, `refii_mod_release_lock(ctx,
  base, player)`, `refii_mod_switch_target(ctx, base, player, left)`, `refii_mod_locked_target(ctx,
  base, player)` (see The target lock below).
- Status effects: `refii_mod_apply_status(ctx, base, entity, kind, seconds, level, source)`,
  `refii_mod_clear_status(ctx, base, entity, kind)`, `refii_mod_has_status(base, entity, kind)`;
  kinds `REFII_STATUS_ELECTRIFIED` 1, `REFII_STATUS_CHAOS` 2, `REFII_STATUS_BURNING` 3,
  `REFII_STATUS_VORTEX` 4 (see Status effects below).
- Knockback and stagger: `refii_mod_knockback(ctx, base, entity, dir, force)`,
  `refii_mod_stagger(ctx, base, entity)` (see Knockback and stagger below).
- Movement speed: `refii_mod_set_speed_multiplier(base, player, factor)`,
  `refii_mod_clear_speed_multiplier(base, player)`, `refii_mod_speed_multiplier(base, player, float*
  out)` (see Movement speed below).
- Actions: `refii_mod_current_action(base, entity)`, `refii_mod_action_elapsed(ctx, base, action,
  float* out)`, `refii_mod_action_interruptible(base, action)`, `refii_mod_allow_action_interrupt(base,
  action)`; `refii_mod_entity_living(base, entity)` (see Actions and commitment below).
- What an action is: `refii_mod_action_type(base, action)` (EScriptableAction, -1 unknown),
  `refii_mod_action_name(base, action, out, size)` (its enum name),
  `refii_mod_scriptable_action_name(type)` (see Actions and commitment below: What an action is).
- The front end: `refii_mod_signed_in_user(base)` (the signed-in pad's user index, -1 none),
  `refii_mod_save_count(ctx, base)` (the profile's saves on every device, -1 with no front end),
  `refii_mod_frontend_screen(ctx, base, out, size)` (the current presentation's name) — see
  "The front-end surface" below.
- The slot table: `refii_mod_spell_in_slot(base, player, slot)` (0..7, -1 none).
- A live cast: `refii_mod_spell_parts(ctx, base, serial, refii_mod_cast_parts* out)` (see A live cast
  below).
- Sound: `refii_mod_play_sound_on(ctx, base, entity, cue, tag)`, `refii_mod_play_sound_at(ctx,
  base, xyz, cue, tag)`, `refii_mod_stop_sound(ctx, base, entity, tag, fade_ms)`,
  `refii_mod_play_music(ctx, base, name)`, `refii_mod_stop_music(ctx, base, name)` (see Sound below).
- Animation: `refii_mod_play_animation(ctx, base, entity, name, loops, blend_seconds, speed)`,
  `refii_mod_stop_animation(ctx, base, entity)` (see Animation below).
- Opinion and deeds: `refii_mod_modify_opinion(ctx, base, villager, hero, axis, delta)`,
  `refii_mod_add_deed(ctx, base, villager, hero, deed)` (see Opinion and deeds below).
- NPC behaviour: `refii_mod_npc_follow(ctx, base, entity, target, radius)`, `refii_mod_npc_go_to(ctx,
  base, entity, xyz)`, `refii_mod_npc_flee(ctx, base, entity, from)`, `refii_mod_npc_attack(ctx, base,
  entity, target)`, `refii_mod_npc_set_hostile(ctx, base, entity, other, on)`, `refii_mod_npc_stop(ctx,
  base, entity)` (see NPC behaviour below).
- Time and weather: `refii_mod_set_time_of_day(ctx, base, hours)`, `refii_mod_time_of_day(base,
  &hours)`, `refii_mod_set_weather(ctx, base, theme, weight, seconds)` (see Time and weather below).
- Health and stats: `refii_mod_health(base, entity, &cur, &max)`, `refii_mod_set_health` (a direct
  write), `refii_mod_modify_health(ctx, base, entity, delta, attacker)` (the game's own Modify:
  heals, hurts, kills), `refii_mod_ability_level(base, entity, type)`,
  `refii_mod_experience(base, entity, pool, &out)`, `refii_mod_ability_cost`,
  `refii_mod_give_experience`, `refii_mod_spend_experience`.
- Spells: `refii_mod_cast_spell(ctx, base, caster, type, targeted, target, level, pos, dir)` (a
  real spell through the game's factory, no will cost), `refii_mod_spell_serial`,
  `refii_mod_live_spell(ctx, base, type, caster)`.
- Gold and items: `refii_mod_gold`, `refii_mod_give_gold`, `refii_mod_item_count(base, entity,
  definition)`, `refii_mod_add_item(ctx, base, entity, definition)`.
- The world: `refii_mod_world`, `refii_mod_level_name(base, out, size)`.
- Stats, physique, inventory, weapons: `refii_mod_renown`, `refii_mod_morality`, `refii_mod_purity`,
  `refii_mod_physique`, `refii_mod_age`, `refii_mod_inventory_stack(base, entity, index, &def,
  &count)`, `refii_mod_equipped(base, entity, &melee, &ranged)`.
- Space and spawning: `refii_mod_entities_near(ctx, base, xyz, radius, creatures_only, out, max)`,
  `refii_mod_line_of_sight(ctx, base, from, to)`, `refii_mod_spawn(ctx, base, definition, xyz)`.
- Effects (Godot draws them; the guest spawns nothing): `refii_mod_play_fx(name, xyz, options)`,
  `refii_mod_play_fx_on(name, entity, options)` (an address; its handle crosses), returning an id,
  and `refii_mod_stop_fx(id)`. `options` is a `refii_mod_fx_options` — `duration` (seconds; 0 until
  stopped, or with `loop` 0 until the effect has run out), `scale`, `tint[3]`, `intensity`, `loop`,
  `offset[3]` (from the entity, along the entity's own axes) — or null for `refii_mod_fx_default_options()`. Any
  thread. The request crosses on the mod bus as the host-only kind `REFII_MOD_HOST_FX_KIND` (1001,
  `core/refii_mod_events.h`), posted whatever the event mask says; `RefiiHost` hands it to
  `Engine_FxPlayer`. `options.at` (or `refii_mod_play_fx_at(name, entity, helper, options)`) names
  a helper point of the entity's model — see play_fx.
- The HUD (Godot draws it; the guest is not told): `refii_mod_mark(player, entity, xyz, icon, text)`
  / `refii_mod_unmark(id)`, `refii_mod_fade(player, to_black, seconds)`,
  `refii_mod_meter(player, id, label, value, max, style)` / `refii_mod_meter_remove(id)` — see
  HUD: markers, fades, meters. They cross as `REFII_MOD_HOST_UI_KIND` requests (`op='mark'`,
  `'unmark'`, `'fade'`, `'meter'`, `'meter_remove'`). Any thread.
- Lights: `refii_mod_light(entity, xyz, rgb, energy, range, options)` / `refii_mod_light_remove(id)`
  — an omni light on an entity (its own frame; `options.at` a model helper, `options.offset`,
  `options.flicker`) or at a world point, see Lights. It crosses on the effect kind
  (`REFII_MOD_HOST_FX_KIND`, `op='light'` / `'light_off'`). Any thread.
- The partner: `refii_mod_send_to_partner(mod, name, payload)` — a mod's own message to the other
  machine of an online session, raised there as PartnerMessage, see Partner messages. Any thread.
- Handles and hashes: `refii_mod_handle(address)` / `refii_mod_handle_address(handle)` (what Lua
  and Godot hold for an object, and back), `refii_mod_fnv1(text)` (the game's FNV-1 of a name).
- The game VM and the save: `refii_mod_run_game_lua` (any length), `refii_mod_game_lua_int`,
  `refii_mod_lua_generation`, `refii_mod_save_set_int`, `refii_mod_save_get_int`.
- Godot: `refii::mods::Publish / Published / Setting` (mod values and settings).
- Guest memory and calls: `refii::mods::LoadU8/U16/U32/F32`, `StoreU8/U32/F32`, `CallGuest`,
  `CallVirtual`, `Frame`, `GetVector`, `SetVector`.
- The plain tick: `REFII_MOD_ON_TICK(fn)` — the same moment as the Tick event.

### Tint

`refii_mod_entity_tint(ctx, base, entity, r, g, b, a)` (Lua `Refii.entity_tint(e, r, g, b [, a])`,
Godot `entity_tint(handle, r, g, b, a = 1)`) calls the game's own vector tint setter on the
entity's renderable — `sub_82573770` (disc `sub_82548418`) on component 3, the vector twin of the
byte setter `sub_82573570` a death's grey {103, 103, 103, 255} goes through (bytes n/255). So:

- Each lane is a linear multiplier of the entity's lit colour (albedo, emission and specular
  under the modern look; the title's PS c49 under the classic look). 1 is untouched, {1, 0.3, 0.3}
  reads red, {0.4, 0.4, 0.4} darker; above 1 brightens, up to the modern look's cap of 31.875/4.
- `a` is stored and replicated, but no shader reads it. Opacity is SetAlpha's (+112), which many
  entities' own updates restate every frame (a Blades sword's does, from its fade).
- The setter stores +80..+95 and calls `sub_82573318`, which packs the appearance record: the
  entity feed draws it under both looks, and co-op replicates it where the game replicates the
  entity. It skips a change under 1e-4 on every lane, so restating a tint costs nothing.
- `refii_mod_entity_untint` / `Refii.entity_untint(e)` / `entity_untint(handle)` put back the
  constructor's {1, 1, 1, 1}. Untint only what you tinted: the game tints corpses and ghosts itself.

Guest tick thread only (Lua and Godot calls run there).

### Appearance

The same renderable has three more setters beside the tint, each storing its value and calling
`sub_82573318`, which packs the record the entity feed draws under both looks and co-op replicates
(`src/vfs/src/hooks/entity_appearance.cpp` lists all seven):

| call (native / Lua / Godot) | setter | stores | reset |
|---|---|---|---|
| `refii_mod_entity_alpha` / `Refii.entity_alpha(e, a)` / `entity_alpha(handle, alpha)` | SetAlpha `sub_82573220` (disc `sub_82547EC8`), f1 | +112; crossing 1e-4 re-registers the drawable (`sub_825728E8`), so 0 hides the body | 1 |
| `refii_mod_entity_scale` / `Refii.entity_scale(e, s)` / `entity_scale(handle, scale)` | SetScale `sub_82573078` (disc `sub_82547D20`), f1 | +108 unless within 0.003 of the old value; tells the placement (vtable slot 43) | 1 |
| `refii_mod_entity_saturation_mask` / `Refii.entity_saturation_mask(e, on)` / `entity_saturation_mask(handle, on)` | `sub_822B8188` (disc `sub_822B7EA8`), r4 | byte +196, "draw desaturated" | off |
| `refii_mod_entity_drawable` / `Refii.entity_drawable(e, on)` / `entity_drawable(handle, on)` | SetAsDrawable `sub_825725A8` (disc `sub_82547250`), r4 | byte +197; off takes the body out of the drawn set, the entity keeps simulating | on |

A game-driven value wins where the game restates it: a Blades sword's update (`sub_825A63A8`)
restates its scale and alpha every frame, and deaths 5/6/7 ramp alpha to 0 through
`sub_82681868`. A value stated on such an entity holds for one frame; do not fight those paths.
Alpha is clamped to 0..1; scale must be positive and the entity must have a placement.

### The target lock

What a held LT states, as calls, for player 1 or 2 (both players are principals):

- `refii_mod_lock_target(ctx, base, player, entity)` / `Refii.lock_target(p [, e])` /
  `lock_target(player, entity = 0)`: with an entity, the targeting component's (77) own SetTarget
  (vtable slot 19, `sub_82845120`: refuses an entity not in the world, marks it targeted-by-player,
  and is the TargetChanged event's seam); then the FORCE byte, comp77 +0x14 (`Targeting.
  SetForceTargetLock`, `sub_8290F078` = `stb r4,0x14(r3); blr`, disc `sub_82455F10`), which the
  component's 8-tick update turns into player mode 4 TARGET_LOCK while a target exists and
  re-selects one while none does (`sub_82181EA0`, 0x821821dc..0x82182274); then the look-at flag
  through `rex_CPlayer_SetLookAtButtonDown(comp33, 1)`, the byte every lock camera reads. Entity 0
  is the game's own pick.
- `refii_mod_release_lock` / `Refii.release_lock(p)` / `release_lock(player)`: the force byte
  cleared and the look-at flag written 0; the update drops mode 4 on its next pass (`+112 && !+20`,
  0x82182290..0x821822a4). It cannot end a lock the player is holding LT for.
- `refii_mod_switch_target(..., left)` / `Refii.switch_target(p, left)` / `switch_target(player,
  left)`: the component's slot 49 `sub_82844DF0`, the camera's own `CheckForBetterTargetInDirection`
  — it seeds the direction from the current target, re-selects, and keeps the old target when
  nothing better stands that way; needs a current target. Answers the target held afterwards.
- `refii_mod_locked_target` / `Refii.locked_target(p)` / `query locked_target P`: the component's
  GetTarget (slot 17) while player mode 4 (`sub_821E8B90(*(comp33+0x14), 4)`, the HasPlayerMode
  every script asks) or the force byte holds; 0 otherwise.

Souls Combat's toggled lock is these calls, in Lua (`mods-src/souls_combat/mod/init.lua`, the
lock-on section); its wheel-switching stays `Refii.wheel_switches_target`.

### Status effects

`refii_mod_apply_status(ctx, base, entity, kind, seconds, level, source)` (Lua
`Refii.apply_status(e, kind, seconds [, level [, source]])`, Godot `apply_status(handle, kind,
seconds, level = 0, source = 0)`) puts a status on an entity as the spell's own hit would, through
the installer each spell calls — the component is added by the installer (`sub_8238CD68(entity,
"CEC…", 0)`), so the `status_effect` event fires, and an entity not in the world is refused:

| kind | component | installer | what is passed |
|---|---|---|---|
| 1 electrified | 205 CECElectrified | `rex_SpellLightning_Electrify` `sub_82742FA8` (disc `sub_827180A8`) | r3 source, r4 victim, r5 -1, r6 level; f1 duration, f2 0 damage, f3/f4 0.5 tick timers (moot at 0 damage), f5 0 |
| 2 chaos | 206 CECInChaos | `rex_SpellChaos_ApplyToVictim` `sub_8274A230` (disc `sub_8271F330`) | r3 source, r4 victim, r5 -1, r7 is-creature, r8 0; f1 seconds; v1 the victim's position. Its FX and zero-damage chaos hit take their direction from the source's body |
| 3 burning | 208 CECOnFire | `rex_SpellFireball_Ignite` `sub_82747260` (disc `sub_8271C360`) | r3 victim, r4 source, r5 -1, r6 level; f1 strength 1, f2 falloff 0, f3 burn time = seconds, f4 burn damage 0, f5 heat 1; v1 the victim's position; the three bytes it reads from its caller's frame (+0x6F 1, +0x74 0, +0x7F 1, as the fireball's impact leaves them) |
| 4 vortex | 209 CECInVortex | refused | a vortex holds a creature by a reference its own update inserts |

`source` 0 makes the entity its own source. The serial recorded is -1, a hit's "no cast". No
damage is dealt: these are statuses, not hits.

`refii_mod_clear_status` / `Refii.clear_status(e, kind)` / `clear_status(handle, kind)` ends one
through the game's own remover — what its expiry and a death call, stopping the FX and removing the
component (the ended event fires): electrified `sub_827427B8` (disc `sub_827178B8`), chaos
`sub_82749F38` (disc `sub_8271F038`), burning `sub_827469A8` (disc `sub_8271BAA8`). Vortex is
refused. `refii_mod_has_status` / `Refii.has_status(e, kind)` / `query has_status E KIND` reads the
component's presence.

### Knockback and stagger

Both are one hit of the game's own shape through its one receiver `rex_CECHitReceiver_OnHit`
(`sub_825C5220`): the record is built by the hit's own constructor `sub_825C4958` (disc
`sub_824831E8`), named through `sub_82275438`, delivered, and destroyed by `sub_823919C0` (disc
`sub_8237D608`). The hit has no attacker and no damage.

- `refii_mod_knockback(ctx, base, entity, dir, force)` / `Refii.knockback(e, x, y, z [, force])` /
  `knockback(handle, x, y, z, force = 1)`: the Force Push's knockdown hit, field for field as
  `sub_828B4650` fills it (0x828b4954..0x828b49f4): knockdown +0x18 = 1, unblockable +0x19 = 1,
  flags +0x40 = 0x20000, kind +0x50 = 3, spell type +0x54 = 6, +0x58 = 1, origin +0x70 = the victim
  less the vector, +0x90 = the vector (`dir` times `force`), +0xA0 = its negation, +0xC0 =
  "ResponseToKnockdownUp". The victim's hit-response listener takes the knockdown (`sub_82689268`)
  unless the creature cannot be knocked down (comp36 +307, comp31 slot 41). Whether the knockdown's
  physics keeps the vector's length is unproven: the direction is read, the magnitude may be
  normalised away.
- `refii_mod_stagger(ctx, base, entity)` / `Refii.stagger(e)` / `stagger(handle)`: the same hit
  with the knockdown byte clear, no flags and the constructor's melee kind, so the listener plays
  the flinch (`sub_828B6F38`), blockable like a tap.

Both fire `hit` and `hit_landed` for every mod. Neither is offered to `hit_response`: a stagger a
mod states is already the mod's decision (so Souls Combat's poise cannot absorb it). Refused when
the receiver drops hits (comp31 +0x18 / +0x19).

### Movement speed

What the scripts' `Player.SetOverriddenMovementSpeedMultiplier` / `SetPlayerMovementSpeedOverridden`
state, for player 1 or 2:

- `refii_mod_set_speed_multiplier(base, player, factor)` / `Refii.set_speed_multiplier(p, factor)` /
  `set_speed_multiplier(player, factor)`: the float at comp33 +0xCC (`sub_8283ECC8` = `stfs
  f1,0xCC(r3); blr`, disc 0x827EDB38) and then the byte at +0x1E (`sub_823DFF78` = `stb r4,0x1E(r3);
  blr`, disc 0x823BE7D0). The locomotion update (`sub_822B1F78`, disc 0x822B1DB0) multiplies the whole
  velocity vector by the float while the byte is set — `lbz r11,0x1E(r30); cmplwi; beq` 0x822b2c2c,
  `lvlx v0,r30,0xCC` 0x822b2c3c, `vspltw v13,v0,0; vmulfp128 v127,v127,v13` 0x822b2c64..68 — so 0
  holds the hero in place; turning runs earlier in the same update, and rolls and blocks are actions
  that never pass through this velocity. With the byte clear the update takes 1, or 0.8 while bit 8
  of comp33 +0x7C is set.
- `refii_mod_clear_speed_multiplier` / `Refii.clear_speed_multiplier(p)` / `clear_speed_multiplier(player)`:
  the byte written 0 (the float keeps its value; nothing reads it while the byte is clear).
- `refii_mod_speed_multiplier(base, player, &factor)` / `Refii.speed_multiplier(p)` / `query
  speed_multiplier P`: whether an override holds, and the float (`Player.
  GetOverriddenMovementSpeedMultiplier`, `sub_823E3258` = `lfs f1,0xCC(r3)`).

The game never clears the byte itself (every other `stb ..,0x1E` / `stfs ..,0xCC` site surveyed is
another class, or the constructor stating 0): a script that set it keeps it until it clears it. So
take the override only while `speed_multiplier` says none holds, and give it back — Souls Combat's
cast recovery plants the caster at 0 for 0.6 s this way.

### Actions and commitment

- `refii_mod_current_action(base, entity)` / `Refii.current_action(e)` / `query current_action E`:
  the action the entity's action manager (component 49, CECActionManagerSimple) runs — its +0x10,
  written by the manager's own start `sub_822A4888` (`addi r31,r30,0x10` 0x822a48a4, `stw r28,0(r31)`
  0x822a496c, after the old action's Stop at 0x822a4900; disc 0x822A4648). 0 (nil) for none and once
  that action was stopped (+0x4B: `li r11,1; stb r11,0x4B(r30)` 0x82709ff0..f8 in `sub_82709FD8`). The
  address is reused by the next action anyone plays: compare it only while this still answers it.
- WHAT AN ACTION IS — `refii_mod_action_type(base, action)` / `Refii.action_type(a)` / `query
  action A`, and `_action_name` / `Refii.action_name(a)`: an action's definition is NOT a GDB record
  and is not kept on the object (H.15): it is a Lua TABLE of the game's scripts, handed to
  `Action.SetCurrentAction(entity, def)` (the glue `sub_8218AAA0`: `lua_touserdata(L, 1)` the
  entity, `sub_82302080(.., L, 2)` a registry ref of stack slot 2 — the def travels as {lua_State,
  ref}) or built by a parent action from its own fields ("RiseAction", "IntoAction" /
  "LoopAction" / "OutOfAction", "Actions"[i]). Every action is made from its definition in ONE
  place, the factory `sub_8218A8E0` (disc `sub_8218A738`, the same 63 instructions): it reads the
  table's `Type` through the game's field reader (`lis r11,aType_0@ha; addi r4,..; li r5,0; bl
  sub_8229DDF0` 0x8218a8f0..904 — `lua_rawgeti(L, LUA_REGISTRYINDEX, ref)`, `lua_getfield(L, -1,
  "Type")`, an integer), finds that type's factory in the map at `unk_8349E30C` (`bl sub_822C92B0`
  0x8218a920; 89 types registered by `sub_829CF2A8`), calls it (`bctrl` 0x8218a990) and stores
  the action and its refcount at r3[0] / r3[1] (0x8218a9a0..a4). The type is `EScriptableAction`
  of the game's own `miscellaneous/scriptableactionenum.lua`: 0 PLAY_ANIMATION, 1 MELEE_STRIKE,
  2 STRAFE (the roll), 3 TALK_TO_ENTITY, 16 BATCH, 27 SHOOT, 48 LOOP, 70
  MELEE_STRIKE_UNSHEATHE_AND_SHEATHE_WEAPON, ... 88 DIG — and that enum's names are what
  `action_name` answers. The factory is hooked (`rex_CActionFactory_CreateFromDefinition`) and
  the last 64 {action, type} pairs kept, so `melee_swing` and `anim_event` carry `action_type`
  and `action_name` too; -1 / nil / "" for an action the factory never saw (a hit response's,
  made before the mods loaded). An address reused by a later action takes the later type — the
  same rule as `current_action`. The definition's own script key (the table's name in the quest
  or AI script) is not stated anywhere the guest reads it and is not answered.
- `refii_mod_action_elapsed(ctx, base, action, &seconds)` / `Refii.action_elapsed(a)`: the action's
  clock, vtable slot 7 (`sub_8226D418`, disc 0x8226D3D8: the float at +8 of the action's key-1
  sub-object, found by a binary search of the sorted vector at action+0x18; 0 with none). The mods
  have timed the commitment's release by it as elapsed animation seconds; **what writes that float is
  unproven** (the sub-object is built by the subclass's OnStart, slot 5).
- `refii_mod_action_interruptible(base, action)` / `Refii.action_interruptible(a)`: whether the action
  has granted the next one of the same priority its way in — the pair the interrupt rule
  `sub_822CB150` (disc 0x822CB140) reads, `lbz r11,0x4C(r10); cmplwi; bne` then `lbz r11,0x4D(r10)`
  0x822cb1b4..c4: a same-priority action passes only while +0x4C is clear and +0x4D is set (or the
  action's own +0x38 bit 0); a higher priority always passes; priority 13 never; the manager's start
  asks with the new action's +0x34 (0x82195cf0). Strikes, rolls and the plain blocks play at 6; the
  preemptive block and the parry at 8 need no permission.
- `refii_mod_allow_action_interrupt(base, action)` / `Refii.allow_action_interrupt(a)` /
  `allow_action_interrupt(action)`: grants it — exactly what the action base's animation-event
  handler `sub_82709938` (disc 0x826DF198) does for "ALLOW_ACTION_INTERRUPT" and nothing else: `lbz
  r11,0x4C(r26); cmplwi; bne; stb r25,0x4D(r26)` 0x82709cac..cb8. A committed swing is therefore: cancel
  the `anim_event` named ALLOW_ACTION_INTERRUPT for the strike — matched on `e.action`, the action
  `melee_swing` handed you (the same handle on both events), not on `current_action`, which can
  differ when the event is delivered to a non-current action — and call this when your own clock
  says so, while `current_action` still answers that action.
- `refii_mod_entity_living(base, entity)` / `Refii.living(e)` / `query living E`: `alive`'s bit AND, for
  a creature, the creature component's (20) byte +0x18 — 1 from its constructor (`li r29,1; stb
  r29,0x18(r31)` 0x823a3160..8c, disc 0x82431718..44), 0 once `rex_CECCreature_Die` ran (`stb
  r27,0x18(r24)` 0x823a4ec8, disc 0x82433430). The target selector's own test (`sub_82844950` 0x82844c44,
  disc 0x8277b5a4) rejects a candidate with it clear — a lock that should drop when its target dies
  asks this.

The action's NAME or definition is not on the action object: the base constructor stores none, and
the definition reaches only the subclass constructor and the start hook `dword_83496D94(owner, def)`
(0x82195cdc) — unproven as a readable field, so none is offered.

### A live cast: its parts and lifecycle

`refii_mod_spell_parts(ctx, base, serial, &parts)` / `Refii.spell_parts(serial)` / `query spell_parts
SERIAL` answers what a cast put in the world, by the serial its hits and events carry (`spell_created`,
`vortex_started`, `fireball_impact`, a hit's `spell_serial`, `refii_mod_spell_serial`). Every object is
read live from the guest and validated — a part is 0 (nil) once its thing is gone:

- `spell`: the spell object while it still carries the serial at +0x44 and its type at +0x28 (the
  base constructor `sub_828E5020`: `stw r28,0x28(r31)` 0x828e5090, `stw r27,0x40(r31)` level,
  `stb r30,0x48(r31)` exploded 0); the object's address is reused by the next cast of the same class.
  `type`, `targeted`, `level`, `caster`, `caster_player` as the factory stated them (the bus keeps the
  last 32 casts from `rex_CSpellRegistry_Create`).
- `vortex`: the live CECVortex component (type 4) — kept by the `vortex_started` / `vortex_ended`
  seams, answered while its +0x18 still carries the serial (Init's `stw r25,0x18(r31)` 0x827588f4);
  `anchor` its entity (+4, the constructor's `stw r4,4(r31)` 0x82757720); `origin` the twister's point
  (+0x140: `li r28,0x140; lvx128 v1,r31,r28` 0x82759210..38 in the update, written back as the twister
  moves, `stvx128 v127,r29,r25` 0x82759bdc, and the entity placed there, 0x82759be4..f4).
- a fireball (type 1): `anchor` the shot entity behind the weak reference at spell+0x10/+0x14
  (`sub_82740568` = *(*(spell+0x14)), 0x82740574..90, disc 0x8294a304..20) while it is in the world
  (the projectile keeps falling after the blast), `exploded` once an Impact detonated it, `origin`
  the impact point the `fireball_impact` seam was given (v1), else the shot's position.
- the Blades (type 3): `swords` — the controller the cast's Start built inside the factory, at
  spell+0xE4 for a targeted cast (class vtable 0x820C4F30, `sub_825A2188` `stw r3,0xE4(r31)` 0x825a21d4,
  disc 0x82552fbc) or spell+0xC8 for an area cast (0x820C4F4C, `sub_825A2720` 0x825a28e0, disc
  0x825536c8); +0 the count, at most 8 (`sub_825A2E68` 0x825a2ea8..b4); +116+4i the records
  (`sub_825A37E0` `stwx r3,r30,r31`, r30 = 4*(29+i), 0x825a3820..2c); a record's +4 its weak-reference
  body (`sub_825A5EF8` 0x825a5f14), the body's +0 the sword entity, which points back at the body at
  +4 (`sub_82214898`: `stw r31,0(r3)` 0x82214910, `stw r9,4(r31)` 0x82214924) — a sword is taken only
  while it still does and is in the world, since the body is freed once nothing holds it.
- `origin` falls back to the caster's position; `has_origin` says whether any was read.

The lifecycle is three events (the table above): `vortex_started` inside the factory, before
`spell_created` (the vortex's spell object finishes at once, so `spell_parts` is the only way back to
it); `vortex_ended` before the component's Shutdown, with the twister's last point (Shutdown stores
nothing to +4, +0x18 or +0x140 — its only store is a global counter at 0x827590d4); `fireball_impact`
after each Impact whose guard passed, with `detonated` and `hits` so a blast that found nothing alive
is one test (`detonated and hits == 0`). Spell Weaving's vortex, fireball and Blades looks are these
three events plus `spell_parts` (`mods-src/spell_weaving/mod/init.lua`, pure Lua).

### The look-at button

`look_at_button` (94) is raised before `rex_CPlayer_SetLookAtButtonDown` writes the player's
look-at flag (comp33 +0x20, `stb r29,0x20(r30)` 0x823e9f34, disc 0x823c8964, behind an equal-value
early return), with `down` writable and `cancel` leaving the flag as it was. Its callers are the
camera's input event on LT down (`sub_825ADD58`, `li r4,1` 0x825ae6e0), the hero's message handler on
the release (`sub_826E5B30` case 0x32, `li r4,0` 0x826e5fc0) and the API's own `lock_target` /
`release_lock`, which call this name — so a mod whose lock is its own (Souls Combat's toggled LT)
writes `e.down = locked` and the flag equals its lock whoever asks, with no hook of its own.

### Sound

The game plays a cue by name through one path, the body of the scripts' `Sound.PlayEvent(entity,
cue, tag)` (`sub_82431A50`): the cue record is built from the name (`sub_822671A8`, disc
`sub_82267168`: the name copied, +8 its FNV-1 hash in upper case), and `sub_821D9608(out, comp135,
cue, tag)` (disc `sub_821D9258`, the same 62 instructions) gives the entity's sound component (135)
an emitter at +44 if it has none (`sub_829FD018(manager, v1 = its position)`), refuses a hash no
loaded bank holds (`sub_82287DE0(manager+12, hash)`, 0x821d9698) and starts the voice on the emitter
(`sub_821E0F48`, disc `sub_821E0B08`), recorded under `tag` in the emitter's voice list. The
voice handle is released at once, as the native releases it.

- `refii_mod_play_sound_on(ctx, base, entity, cue, tag)` / `Refii.play_sound(cue, e [, tag])` /
  `play_sound(cue, handle, tag = "")`: that call on the entity's sound component; an entity
  without one (a prop) plays at its position instead. True when a voice started — an unknown cue
  starts none.
- `refii_mod_play_sound_at(ctx, base, xyz, cue, tag)` / `Refii.play_sound_at(cue, x, y, z [,
  tag])` / `play_sound_at(cue, x, y, z, tag = "")`: an emitter of the API's own at the point
  (`sub_829FD018`), the cue checked against the banks the same way, started by `sub_821E0F48`.
  Eight such emitters are kept; the ninth play frees the oldest through the manager's own
  `sub_829FD168` (disc `sub_829F83A0`), ending whatever it still played. An emitter the game
  itself freed (a level change) is recognised by its absence from the manager's list and left
  alone.
- `refii_mod_stop_sound(ctx, base, entity, tag, fade_ms)` / `Refii.stop_sound(e, tag [,
  fade_ms])` / `stop_sound(handle, tag, fade_ms = 0)`: `Sound.StopSoundCategoryPlaying`'s body,
  `sub_829FDEF0(emitter, tag, ms)` (disc `sub_829F9128`): every voice on the emitter whose tag
  equals is told to stop over `fade_ms` milliseconds (the scripts pass 3000). Entity 0 walks the
  point emitters. The tag given at play time is the handle: the game has no per-voice stop.
- `refii_mod_play_music(ctx, base, name)` / `Refii.play_music(name)` / `play_music(name)` and
  `refii_mod_stop_music` / `Refii.stop_music(name)` / `stop_music(name)`: `SoundTools.PlayMusic`
  and `StopMusic`'s own bodies (`sub_8255BB28` / `sub_8255BC20`, disc `sub_825204B0` /
  `sub_825205A8`): a known cue only, noted in the music box, played in the manager's category 3;
  the stop fades over the game's 2000 ms.

There is no volume argument: the game's player takes none, the cue's own volume and attenuation
apply (`Sound.SetSoundVariableValue` drives bank variables, not a voice; not exposed).

**Cue names.** A cue is an event name in an audio bank: the `.adb` files the extractor unpacks
under `audio/` (`~/.local/share/refii/assets/unpacked/audio/<bank>.adb`, 107 of them). An `.adb`
is `LhCoMpReSsEd`, 12 bytes of header and a zlib stream; inflated (`python3 -c 'import
zlib,sys,re; print(*sorted(set(re.findall(rb"[A-Z][A-Z0-9_]{5,}",
zlib.decompress(open(sys.argv[1],"rb").read()[24:])))), sep="\n")' music.adb`) the event names
are plain strings — music.adb holds 143 (`MUSIC_MENU`, `MUSIC_HERO_DEATH`,
`MUSIC_QO020_CHARLESALIVE`…); `SE_*` are effects, `atmos_*` banks the ambiences. The quest scripts
under `luasource/scripts/quests/` show which cues are played where (`Sound.PlayEvent(e,
"SE_HERO_HILL_WHITEOUT", "")`, `SoundTools.PlayMusic("MUSIC_QC030_IN_WAREHOUSE")`). A bank must be
loaded for its cues to play: `Startup/audio.lua` loads the global ones (globals, music, music_3d,
expressions, pub_games, crescendo), a level's own banks come with the level.

### Animation

The shipped AI plays a named animation as a scripted action: `BehaviourBase:PlayAnimation`
(ai/aibase.lua) calls `Action.SetCurrentAction(entity, { Type = EScriptableAction.PLAY_ANIMATION,
Anim = name, OverrideLooking = ..., SpeedMultiplier = ... })`; the native (`sub_82960FD8`'s
binding, `sub_822BF530` into the action component's slot 33) builds the action from the table —
the base parser `sub_8229DC48` reads `Target`, `Priority`, `BlendInTime` (0.1 s), `BlendOutTime`,
`OverrideLooking`, `IsComboBreak`, `IsPrelude`, `KillFXOnFinish`, `CanBeOverridenBySamePriority`,
the animation parser `Anim` and `SpeedMultiplier` — and the game's own evaluator plays it, so the
pose feed shows it in Godot with nothing added.

- `refii_mod_play_animation(ctx, base, entity, name, loops, blend_seconds, speed)` /
  `Refii.play_animation(e, name [, loops [, blend_seconds [, speed]]])` / `play_animation(handle,
  name, loops = 1, blend_seconds = 0.1, speed = 1)`: that table, run in the game VM against
  `Refii.entity(handle)`; `loops` above 1 wraps it as `PlayLoopedAnimation` does — `{ Type =
  BATCH, LoopAction = <it>, NumLoops = loops, KillFXOnFinish = true }` (`sub_829CEDA0` reads
  `NumLoops`, `IntoAction`, `LoopAction`, `OutOfAction`; the shipped loop gives all three, ours
  only the loop — unproven that the batch accepts the two missing). True when the chunk ran: a
  higher-priority action (combat, a cutscene) keeps the body, which the call cannot see.
- `refii_mod_stop_animation(ctx, base, entity)` / `Refii.stop_animation(e)` /
  `stop_animation(handle)`: `Action.FinishScriptedActions(entity)` (`sub_829647F0`), what the
  behaviours call to cut a played animation.

**Animation names.** `Anim` is a GDB animation record's name, the ones the scripts use
(`"DrunkOutOf"`, `"GreetingPosh"`, `"HeroHillWhiteout"`…): `strings` over the quest and AI
scripts under `luasource/scripts/` list every name the game plays, and the Studio's model view
lists a model's bound animations by record name (`Engine_AnimGdbScan`: each binding's
`AnimationName`). A name is letters, digits and `_`; a name the entity's skeleton has no clip for
plays nothing.

### Opinion and deeds

A villager's opinion of a hero is five axes (0 love, 1 fear, 2 attractiveness, 3 morality, 4
respect — `EOpinionAxes`) on component 47; `refii_mod_opinion` reads them per player. Two setters,
both the game's own:

- `refii_mod_modify_opinion(ctx, base, villager, hero, axis, delta)` / `Refii.modify_opinion(villager,
  hero, axis, delta)` / `modify_opinion(villager, hero, axis, delta)`: `OpinionReaction.ModifyAxisValue`'s
  body (`sub_827915D8`, the hooked rex_CECOpinionReaction_ModifyAxisValue): `+24 + 4*axis += delta`,
  clamped to [-1, 1]. With player two's hero the call runs inside their opinion scope, so the write
  lands in the block the co-op core keeps for them, never in hero one's; the `opinion_modified`
  event fires with that player.
- `refii_mod_add_deed(ctx, base, villager, hero, deed)` / `Refii.add_deed(villager, hero, deed)` /
  `add_deed(villager, hero, deed)`: the deed message `Debug.PostGenericDeed` (ai/opinionsdebug.lua)
  sends, through the game's own poster `sub_82279B80(messages, 50 = MESSAGE_EVENT_DEED_PERSON,
  &pos, from = hero, to = villager, lifespan 101, extra_data = the record's id)` — `deed` is the
  Deed record's GDB hash (`Refii.hash("Generic_Scary")`), its id the record's first word (what
  `RecordPtr:GetID` answers). The villager's opinion component takes it in `OnDeed`
  (`sub_82786F58`: +0x20 the deed, +0x34 the kind, +0x40 the doer), so the record's strength and
  kind and whether this villager cares are the game's rules; `opinion_deed` fires, routed to the
  doer's player. The shipped debug post names no target; whether `to` narrows delivery to this
  villager alone is unproven. Refused when the hash names no record.

**Deed names.** Deed records are GDB entries (`Generic_Scary`, the `Deed*` records the AI's
`BehaviourReactToDeed` evaluates); the Studio's GDB browser lists them, and `Refii.hash(name)`
gives the hash a call takes.

### NPC behaviour

A creature is ordered about the way the shipped quests and AI scripts order it — each call is the
game's own entry (a native of the `Follow`, `Navigation` or `Faction` namespace, or a shipped Lua
function), run in the game VM against `Refii.entity(handle)`, so the AI mind sees the order as a
quest's. All six return true when the chunk ran; a behaviour the mind runs later (combat, a
cutscene, a group mind's job) may retake the body, which the call cannot see — the same caveat as
`play_animation`. Both players are principals: every target is an entity, and player two's hero is
its own entity, never "the hero".

- `refii_mod_npc_follow(ctx, base, entity, target, radius)` / `Refii.npc_follow(e, target [, radius])`
  / `npc_follow(entity, target, radius = 2)`: `Follow.FollowEntity(e, t, radius)`, what
  qr_assassination.lua orders a guard with (`Follow.FollowEntity(GuardA, TargetNPC, 2.0)`). The
  follow component's native `sub_825E33D0` (disc `sub_82456568`, 147 instructions both) puts the
  target's weak reference at +28, the radius at +24 (`stfs` 0x825e3484), marks +17 "for script" and
  +12 following; `BehaviourFollow` then walks it with `MoveToEntityNoWaitForFollowing`. `radius` ≤ 0
  is the quests' 2.
- `refii_mod_npc_go_to(ctx, base, entity, xyz)` / `Refii.npc_go_to(e, x, y, z)` / `npc_go_to(entity,
  x, y, z)`: `ScriptFunction.StartScriptControlledMovement(e, CVector3(x, y, z), false)`
  (quests/miscfunctions.lua, as qc080_monksquest.lua calls it): `AIManager:SetScriptMoveToPosAndDir`,
  so `BehaviourScriptMoveToPos` owns the walk, and `Navigation.MoveToPosition(e, { position, radius =
  Debug.MOVE_TO_POS_BODGE_DIST, speed })` (`sub_821B9DF0`, disc `sub_821B9ED0`, the table's
  `position` / `radius` / `speed` / `check_destination` keys). Not waited for; `npc_stop` ends it.
- `refii_mod_npc_flee(ctx, base, entity, from)` / `Refii.npc_flee(e, from)` / `npc_flee(entity, from)`:
  `Navigation.FleeFromEntity(e, from, ENavigationSpeed.NAV_SPEED_SPRINT, true)`, the call
  `BehaviourBase:FleeFromEntityNoWait` (ai/aibase.lua) makes: the navigation component's
  `sub_82663FF8` (disc `sub_82674C08`, 67 instructions both) sets mode +168 = 3, kind +172 = 2
  (flee) and the target at +640. The AI's own `BehaviourFlee` repeats this call every tick while it
  flees; ours is one order — the body runs until the navigator is done or the mind takes it back.
- `refii_mod_npc_attack(ctx, base, entity, target)` / `Refii.npc_attack(e, target)` /
  `npc_attack(entity, target)`: `CombatRegister:SetScriptTarget(e, t, true, false)`
  (ai/combatregister.lua): `Faction.AddTemporaryEntityRelationship(e, t, FACTION_STATUS_ENEMY)`
  and the combat info's `ScriptTarget = t`, `IgnoreChaseRadius = true`, which
  `FindTargetForCombatant` takes before any other target. The shipped function refuses a dead or
  non-combatant entity with `Debug.Error` — the chunk still "ran" (true); check `relation` after.
- `refii_mod_npc_set_hostile(ctx, base, entity, other, on)` / `Refii.npc_set_hostile(e, other, on)`
  / `npc_set_hostile(entity, other, on)`: the faction component's own temporary relationship —
  `Faction.AddTemporaryEntityRelationship(e, other, EFactionStatus.FACTION_STATUS_ENEMY)`
  (`sub_824D2F90`, disc `sub_82540398`, 354 instructions both: status 4 moves `other` into the enemy
  list at component+36 and out of the ally and neutral lists; `IsMyEnemy` 0x824d2f30 is `== 4`,
  ally 2, neutral 1 — the numbers `relation` answers) or `RemoveTemporaryEntityRelationship(e,
  other)` (`sub_824D3518`, disc `sub_82540920`: out of all three). Hostile to a hero means that
  hero's entity: `Refii.npc_set_hostile(e, Refii.player_hero(2), true)` is hostility to player two alone.
- `refii_mod_npc_stop(ctx, base, entity)` / `Refii.npc_stop(e)` / `npc_stop(entity)`:
  `Follow.StopFollowing(e)` (`sub_825E3660`, disc `sub_82456800`),
  `ScriptFunction.StopScriptControlledMovement(e)` (`AIManager:ClearScriptMoveToPos` and
  `Navigation.StopMoving` — `sub_821AFFB8`, disc `sub_821B01D0`: modes +168/+172 = 0, the target
  cleared) and `CombatRegister:ClearScriptTarget(e)` (the enemy relationship removed). The mind then
  picks its next behaviour itself — idle, or whatever its group mind says; there is no "stand still
  forever" short of a scripted action.

### Time and weather

- `refii_mod_set_time_of_day(ctx, base, hours)` / `Refii.set_time_of_day(hours)` /
  `set_time_of_day(hours)`: the game clock to `hours` (0..24; 13.5 is half past one) through
  `Timing.SetTimeOfDayAbsolute`'s own body `sub_82515AA0` (disc `sub_8249A790`, 41 instructions
  both): r3 = the time manager, f1 = the day fraction — `stfs f1,4(r3)`, the clamp to [0, 1]
  (`fsel` 0x82515aec / 0x82515b0c, `stfs f7,4(r3)` 0x82515b10) and, in an online session (the
  session bytes +0x34/+0x35 of `*(*(root+12)+0x8C)`), `sub_82516670` tells the partner — the
  host's clock is everyone's. The manager is the one the Timing natives are bound to,
  `*(*(*(*(root+0xC)+0x58)+4)+0x14)` (0x82459000..14; disc 0x825ad218..2c, the same loads). The
  `time_of_day` event fires from the clock's next update. (`Timing.SetTimeOfDay`, the quests'
  call, takes HH.MM — `sub_821BFE18` splits 13.30 into 13 h 30 min; ours takes decimal hours.)
- `refii_mod_time_of_day(base, &hours)` / `Refii.time_of_day() -> hours` / `query time_of_day`:
  the manager's day fraction at +4 (what `Timing.GetTimeOfDay` `sub_8288B918` answers) times 24.
- `refii_mod_set_weather(ctx, base, theme, weight, seconds)` / `Refii.set_weather(theme [, weight
  [, seconds]])` / `set_weather(theme, weight = 1, seconds = 1)`: the GAME's environment theme — its
  weather and the look of its sky — blended as the quests blend it:
  `EnvironmentTheme.BlendToEnvironmentTheme(theme, weight, seconds)` (qc090_garth.lua:
  `("EnvThemeWestcliffNight", 1.0, 1.0)` in, `(.., 0.0, 1.0)` out), the native the mod event hooks
  (`sub_82549B80`, rex_lhEnvironmentThemeManager_BlendToTheme: r3 the theme manager, r4 the name,
  f1 weight, f2 seconds), so `weather_theme` fires with those fields and a handler may change or
  cancel it. `weight` is clamped to [0, 1]; 0 blends the theme out. Theme names are environment-theme
  records (`EnvTheme*`): `strings` over the quest scripts list the ones the game blends —
  `EnvThemeWestcliffNight`, `EnvThemeNuclearTheme`, `EnvThemeTurnOnRain`, `EnvThemeCemeterySummon`,
  `EnvThemeNewCombatHighIntensity`, `EnvThemeDreamworldEndOfLevel`, `EnvThemeChildhoodNight`, the
  `EnvThemeDLC2_Present_*` set — and the Studio's GDB browser lists them all.
  **The Dynamic Sky's weather override is the host's switch, not this call:** Modern's dynamic
  weather (`Engine_DynamicSky`, the `dynamic_sky_weather` switch, the `dynamic_weather` event) is
  Godot's own, stated by the host; `set_weather` states the guest's theme, which the host's sky
  reads as a theme like any the quests blend. In co-op the host's weather is everyone's: run the
  blend where `world_here()` is true and let the joiner's copy follow.

## Component ids and offsets the API relies on (verified in the binary)

Every entity component keeps its owner entity at +4 (the component base constructor; every
factory calls `ctor(obj, entity)`). Presence bits at entity+0x24, the sorted {id, component} pairs
at +0x48..+0x4C, the index table at +0x8C. Entity: +0x1C definition hash, +0x7C placement
(vtable slot 16 position getter, 17 orientation, 25 set position, 26 set orientation), +0x80 name
(an LHString: a pointer to a body whose first word is the text), +0x90 bit 0x40 in world.

| id | component | fields the API reads |
|---|---|---|
| 3 | graphic appearance | +80..+95 tint {r, g, b, a} (constructor 1, 1, 1, 1), +108 scale, +112 alpha, +116 camera alpha; setters in Tint above |
| 13 | inventory | +16 list sentinel (node: +0 next, +8 item; item: +4 definition, +12 count), +20 count |
| 20 | creature | +0x18 alive |
| 24 | held items | slots by name |
| 28 | weapon set | +84 melee record, +88 ranged record |
| 31 | hit receiver | the hit dispatch |
| 33 | player | +4 owner |
| 36 | health | +0x14 current, +0x1C max, +0xC the hit listener, +0x131 invulnerable, +0x133 CantDie |
| 49 | action manager | +0x10 current action |
| 56 | spell manager | +4 caster, +0x70 state, +0x80 slot table int8[2][5], +400 definitions |
| 57 | stats | +0x110 + 4*type ability levels |
| 77 | targeting | +0x14 force lock |
| 81 | purse | +12 gold |
| 133 | faction | +0x14 faction id; `rex_CECFaction_RelationTo(component, other)` |
| 164 | experience | +0x10 pools float[4]: General, Strength, Skill, Will |

Hit record: +0 attacker weak-ref, +0x18 knockdown request, +0x19 unblockable, +0x1C no response,
+0x38 damage, +0x44 serial, +0x48 level, +0x54 spell type (-1 melee). Spell object: +0x40 level,
+0x44 serial, +0x48 finished, +0x1C caster ref.

## Mod Studio

The Guide's tab 7 is generated from the catalog; tab 6 keeps the hook surface. `RefiiModApi`:
`GetApiVersion`, `ListEvents`, `DescribeEvent(name)`, `ListNativeHelpers`, `ListLuaHelpers`,
`NewNativeHandler(event, file)` (writes a filled-in native handler under `native/`),
`AddLuaHandler(event)` (appends a `Refii.on` block to `init.lua`), and the game-Lua lookups
above. `Refii.Tests/Modding/ModApiTests.cs` holds the catalog to the header and to both configs.

`RefiiModApi` (the editor's scripting class) authors all of it: `SetHudLayout(pieceId, anchor,
offset)`, `RemoveHudLayout`, `GetHudLayout`, `GetHud`, `SetHudText(piece)`; `SetModal(key,
record)`, `ReleaseModal(key)` (a `null` record), `RemoveModal`, `GetModals`, `ListModals`,
`ShippedModals`; `SetMenu(name, record)`, `RemoveMenu`, `GetMenu`, `ListModMenus`,
`SetMenuRow(menu, entry)`, `RemoveMenuRow`, `OpensCommand(menu)`, `LuaCommand(function, arg)`,
`ParseMenuCommand(command)`; `SetSettingRow(setting, row)`, `SetSettingChoice(setting, label,
options)`, `RemoveSettingRow`, `GetSettingsUi`. Each write is held to the schema check
(`ModValidate`) like every other.

The Guide's tab 8 (Effects) covers recipes, mod effects, `play_fx` and the FX Lab; `FxModEffectsTests.cs` holds `play_fx` to all three surfaces.

## Input actions (input.json)

A mod declares its own input actions in `input.json` at the package root and receives them as
events, on the keyboard and on either player's pad. One entry per action, keyed by the action's
name; the game scopes it `<mod>.<action>`:

```json
{
	"dash": { "label": "Dash", "keys": ["F"], "buttons": ["L3"], "axis": null },
	"aim":  { "label": "Aim",  "keys": ["Ctrl"], "buttons": ["LT"], "axis": "-RightY" }
}
```

| Field | Meaning |
|---|---|
| `label` | the name a rebinding UI will show; defaults to the action's name |
| `keys` | Godot key names (`OS.FindKeycodeFromString`: `F`, `Ctrl`, `Space`, `F5`, `BracketLeft` ...) |
| `buttons` | `JoyButton` names (`A`, `LeftShoulder`, `DpadUp`, `Guide` ...) or the Xbox aliases `A B X Y LB RB L3 R3 LT RT Start Back`; `LT` and `RT` are the triggers, pressed past half their travel |
| `axis` | one `JoyAxis` name (`LeftX`, `LeftY`, `RightX`, `RightY`, `TriggerLeft`, `TriggerRight`) or null; pressed past half deflection either way, or one way with a leading `+` / `-` |

A list field also takes a single string. An action with nothing bound is ignored with a
warning; a later mod in the load order declaring the same scoped name replaces the earlier one.

Who presses it: player 1 is the keyboard plus the joypad holding guest pad 0; player 2 is the
joypad holding guest pad 1 — the same assignment `GameInput` makes for the game's own pads, so
whichever controller pressed START to drop in is the one player 2's actions read. Devices are
read only while the game window has focus, as the pads are; losing focus releases every held
action once. The keyboard does not count while the dev menu has it.

The event is `input_action` (native `InputActionEvent`, Lua `Refii.on("input_action", fn)`):

| Field | Meaning |
|---|---|
| `name` | the scoped action, `<mod>.<action>` |
| `player` | 1 or 2 |
| `pressed` | true on the press, false on the release |

Edges only: one event when the action goes down and one when it comes up, never per-frame
state. Hold the pressed flag in your own handler if you need "while held".

Each action is also registered in Godot's `InputMap` as `mod.<mod>.<action>` with the bindings in
force, for the Godot side. `RefiiModApi.SetInputAction(action, binding)` writes an entry (and
checks every name); `RemoveInputAction(action)` deletes one.

**Rebinding.** The player rebinds a mod's actions in the game: Options › Controls folds the
game's own Controls page with one row per enabled mod that declares actions, and the mod's page
(on the shared host options page, as every host page) has a key row for each action that declares
`keys` and a pad row for each that declares `buttons` — an `axis` is never rebound. Left and
right step a row through the mod's default, Unbound, then every bindable key or pad input; A
writes the page, B leaves it. The rebinds live where the shipped keys' do, refii.toml:
`key_<mod>.<action>` (a comma list of Godot key names) and `pad_<mod>.<action>` (a comma list of
`buttons` names), `none` for unbound; a key absent or empty leaves the file's binding in force,
and a row left at its default stores nothing, so a later change to the mod's default reaches the
player. The game reads them with the shipped keys (every two seconds, at once after the page
commits), so a hand edit while the game runs lands too. The page binds one input per row;
refii.toml itself takes the comma list, shown on the page as its first name. The launcher's
settings panel lists the same rows — a "Controls: <mod title>" group per enabled mod, read from
`input.json` when the panel loads — with an empty value standing for the mod's own binding, so a
save there pins nothing and the two places agree on every key.

## Godot scripts (godot/*.gd)

Entities and objects in a script's event dictionaries and in the helper calls are the same
HANDLES Lua gets (see "Handles, not addresses" above); hashes are `"0x…"` strings. A script
receives every event after the verdict through signals, and acts through the helper calls and
queries below. A script that must cancel an event or change a field registers a VERDICT handler
(see "Verdicts" below), which runs inside the game's seam the way a Lua handler does.

A mod can ship GDScript that runs inside the game: every `godot/*.gd` in an enabled package is
compiled when the session's guest is up, instantiated and added under one `RefiiMods` node in
the game's scene tree, and freed when the session stops. Godot renders everything, so this is
where a visual, audio or UI mod lives. A script must `extends Node` (or a Node subclass); one
that does not parse, or extends something else, is named in a warning and skipped while the
others load. A `.gd` is never mounted as an asset. The script has no resource path, so relative
`preload` does not resolve; `mod/init.lua` and `native/` stay the way to reach the game's own
state.

Scripts talk to the game through the `RefiiHost` singleton, `Engine.get_singleton("RefiiHost")`:

| Member | Meaning |
|---|---|
| signal `mod_event(name, e)` | every event; `e` is a Dictionary of the event's fields (int, float, bool or String) |
| signal `<lua_name>(e)` | one per catalog event: `hit`, `spell_launched`, `level_loaded`, ... (the table above) |
| `get_value(key) -> float` | a hard mod's published value (`combat.stamina`); NaN when none |
| `setting(key, default) -> float` | a `settings.json` value, the live override first |
| `set_setting(key, value)` | sets a mod setting in the running game, kept for the session |
| `run_lua(chunk) -> bool` | runs a Lua chunk in the game VM on the next tick, newlines kept; false when the line is over 4 KB |
| `hero(player) -> int` | the player's hero entity as the bus last stated it, or 0. Player 1 is the HOST's hero on both machines: on an online joiner `hero(1)` is the partner |
| `local_hero() -> int` · `coop_mode() -> int` · `menu_player() -> int` | `Refii.local_hero()`, `Refii.coop_mode()`, `Refii.menu_player()` as the guest last answered them (asked every frame while a script is loaded, one guest tick behind): this machine's hero (in local co-op the menu owner's), 0 alone 1 local co-op 2 hosting 3 joined, whose menus are open |
| `player_set(p, key, v)` · `player_add(p, key, d, saved)` · `player_save(p, key, v)` | per-player values in the store `Refii.player_set` / `_add` / `_save` write — the same on both co-op machines; saved ones go home with the hero (see Player state) |
| `mods() -> Array[String]` | the enabled mods, in load order |
| signal `mod_menu_row(e)` | a row of a mod's menu was chosen: `menu`, `row`, `player` (1 or 2, whose menu it is), `mod` (the mod that stated the row or its menu, `""` for a shipped row). Raised by Godot where the row is chosen, so it arrives the same frame, on the title screen too |
| `open_menu(name) -> bool` | opens a menu a mod defines in `ui/templates.json` as a list over whichever menu list has the input — the front end, the pause list or a screen's list; B returns to it. False when no list is up (plain gameplay) or no template has that name |
| `message_box(title, text, buttons) -> int` | the game's own box: `buttons` empty or one caption is a notice (one dismiss), two captions a question (A and B, captioned by `buttons[0]` and `buttons[1]`). `title` sits above `text`. Returns an id; the answer comes as `message_box_answered(id, button)`. In game only (the front end has no host box) |
| signal `message_box_answered(id, button)` | `button` 0 for A (a question's accept, a notice's dismiss), 1 for B, -1 when another box of the same kind replaced it first. One question and one notice stand at a time |
| `publish(key, value)` | publishes a mod value under `key` (`"<mod>.<name>"`, no spaces): what a HUD piece reads as `mod:<key>`, Lua as `Refii.value(key)`, native code as `refii::mods::Published`. Set on the guest's next tick, the moment every reader sees it |
| `play_fx(name, x, y, z, options) -> int` | an effect of the bank or a mod's `fx/effects/` as a `FableFx` node at a world position (guest axes); `options` is a Dictionary: `duration`, `scale`, `tint` (Color or `[r, g, b]`), `intensity`, `loop` (true); the id, 0 for an unknown effect |
| `play_fx_on(name, handle, options) -> int` | the same on an entity by its HANDLE: it stands in the entity's own frame, `offset` (Vector3 or `[x, y, z]`) along the entity's axes, and moves and turns with it, freed with it; `"at": "Weapon.Weapon.Top"` stands it at that helper point of the model (see play_fx) |
| `stop_fx(id) -> bool` | stops any surface's id; false when nothing plays under it |
| `mark(target, icon, text = "", player = 0) -> int` · `unmark(id) -> bool` · `markers() -> Array` | a marker on the HUD's world-icon channel over an entity handle (int) or at a Vector3 point for player 1 or 2 (0: every player this machine plays); `icon` a game texture path or a mod PNG; the id `unmark` takes; `markers()` lists what stands (id, player, entity, x, y, z, icon, text) |
| `fade(player, to_black, seconds = 0) -> bool` · `fade_state(player) -> Dictionary` | fades the player's screen to black or back over seconds (the host's own layer over their half, never the game's fade); `fade_state` answers `alpha` (0 clear .. 1 black), `target`, `seconds` |
| `meter(id, label, value, max, player = 0, style = "") -> bool` · `meter_remove(id) -> bool` · `meters() -> Array` | a HUD meter for player 1 or 2 (0: every player played here) in style `bar`, `boss` or `timer` (the HUD's primitives), or `generic`, `crucible`, `price` (the game's own meter art); the same id restates it in place; `meters()` lists what stands in stack order |
| `light(target, color, energy, range, options = null) -> int` · `light_remove(id) -> bool` · `lights() -> Array` | an omni light on an entity handle (int; its own frame, riding the body) or at a Vector3 / `[x, y, z]` point (guest axes); `color` a Color or `[r, g, b]`, `energy` (1: a placed lamp's), `range` metres (0: 6); `options` `{ offset, at, flicker }` as play_fx_on's `offset` / `at`, `flicker` 0..1; the id `light_remove` takes, 0 for an unknown handle; `lights()` lists what stands (id, entity, r, g, b, energy, range, x, y, z) — see Lights |
| `send_to_partner(mod, name, payload = "") -> bool` · signal `partner_message(e)` | a mod's own message to the other machine of an online session (the guest command `partner_send`), raised there as `partner_message` — `mod`, `name`, `payload`, `from` — once and in order; false alone or in local co-op, for a bad name or a payload over 256 bytes (see Partner messages) |
| `api_version() -> int` | the API version |

The Guide's events tab lists these calls as "Godot helpers" (`ModApi.GodotHelpers`), beside the
native and Lua helpers; a test holds every method name there (signals, query verbs and `e.` fields aside) to a public method of `RefiiHost`.

The host asks the bus only for the kinds some script is connected to (a `mod_event` connection
asks for all), so an unconnected event costs nothing. A signal handler sees the event after the
verdict and cannot change it; use a verdict handler for that. Signals arrive once per frame, in
order, after the guest's step.

**Verdicts.** `host.on_verdict(event, callable)` runs `callable(e)` INSIDE the seam, before the
game acts, after native and Lua handlers. `e` is the same Dictionary a signal carries. Change a
writable field in it (`e.damage *= 0.5`, `e.knockdown = true`), or set `e.cancel = true` or return
`true` to refuse an event that can be refused; booleans are sent back as 0 or 1. Only the
catalog's writable fields go back, and only when they changed. `host.remove_verdict(event,
callable)` takes one back; `on_verdict` returns false for an unknown event name.

A verdict runs on the GAME'S thread while the game waits for it, so it must only compute: no
nodes, no scene tree, no resources (queue those with `call_deferred`), and it should finish in
well under a millisecond. Helper calls and queries are safe there, since they are only queued.
Verdicts are never nested: an event raised while one runs gets no verdict. Only the kinds that
have a verdict handler are asked, so the rest cost nothing.

```gdscript
func _ready() -> void:
	var host := Engine.get_singleton("RefiiHost")
	host.on_verdict("hit", _halve_and_floor)

func _halve_and_floor(e: Dictionary) -> bool:
	if e.spell_type == 6:      # force push: keep the shove, lose the knockdown
		e.knockdown = false
	e.damage *= 0.5
	return false               # true would drop the hit
```

Helper calls and queries are LINES the guest tick runs with the real guest context (a Godot
script never holds one; `src/vfs/src/hooks/mod_commands.cpp` is the grammar). A helper call is
fire-and-forget and silent when it has nothing to act on (no such entity, no component). A query
returns an id at once and answers later on `reply(id, result)`, on a frame after the guest's
tick; `result.ok` is false when it could not be answered.

| Member | Meaning |
|---|---|
| `give_gold(entity, delta)` | adds (negative: takes) gold |
| `modify_health(entity, delta, attacker)` | changes health, attributed to `attacker` (0 for none) |
| `set_health(entity, value)` | sets current health |
| `add_item(entity, definition)` | one of an item definition (its GDB record hash) into the inventory |
| `give_experience(entity, pool, amount)` / `spend_experience(...)` | one experience pool |
| `cast_spell(caster, type, targeted, target, level)` · `cast_spell_at(caster, type, targeted, target, level, pos, dir)` | a cast as `caster`; `target` 0 for none, level 0..4; `cast_spell_at` starts it at `pos` (Vector3, guest axes) facing `dir`, as `refii_mod_cast_spell`'s pos / dir (the line: `cast_spell C T TARGETED TARGET LEVEL [X Y Z [DX DY DZ]]`) |
| `set_position(entity, x, y, z)` | moves an entity |
| `entity_tint(entity, r, g, b, a = 1)` / `entity_untint(entity)` | tints an entity through the game's own appearance setter (see Tint); untint puts back 1, 1, 1, 1 |
| `entity_alpha(entity, alpha)` / `entity_scale(entity, scale)` / `entity_saturation_mask(entity, on)` / `entity_drawable(entity, on)` | the other setters of the same renderable (see Appearance) |
| `lock_target(player, entity = 0)` / `release_lock(player)` / `switch_target(player, left)` | the target lock as the game's LT states it (see The target lock); `query locked_target P` answers the target held |
| `wheel_switches_target(handle, on = true) -> bool` | hero `handle`'s target lock switches by the mouse wheel, as `Refii.wheel_switches_target` states each tick — STICKY here: a Godot script runs off the guest tick, so the tick restates it (the command `wheel_switches_target E ON`) on every tick from the call until `on = false` or the hero's player no longer holds a lock (the game's own release, or `release_lock`); false for a bad handle |
| `apply_status(entity, kind, seconds, level = 0, source = 0)` / `clear_status(entity, kind)` | a status through the spell's own installer: 1 electrified, 2 chaos, 3 burning (see Status effects); `query has_status E KIND` asks |
| `knockback(entity, x, y, z, force = 1)` / `stagger(entity)` | a Force Push's knockdown hit along a direction, or a flinch (see Knockback and stagger) |
| `set_speed_multiplier(player, factor)` / `clear_speed_multiplier(player)` | the player component's own movement-speed override (see Movement speed); `query speed_multiplier P` answers `overridden`, `factor` |
| `allow_action_interrupt(action)` | grants an action's ALLOW_ACTION_INTERRUPT (see Actions and commitment); `query current_action E` answers `action`, `elapsed`, `interruptible` |
| `query action A` / `query frontend` | `type`, `name` — EScriptableAction and its name (ok false for an action the factory never saw; see Actions and commitment: What an action is) · `signed_in`, `saves`, `screen` — the front end's own state (ok false with no front end up; see The front-end surface) |
| `query living E` / `query spell_in_slot P SLOT` / `query spell_parts SERIAL` | `living` · `type` · `spell, type, targeted, level, caster, caster_player, vortex, anchor, sword_count, s1..s8, x, y, z, has_origin, exploded` (ok false for an unknown cast) |
| `play_sound(cue, entity, tag = "")` / `play_sound_at(cue, x, y, z, tag = "")` / `stop_sound(entity, tag, fade_ms = 0)` / `play_music(name)` / `stop_music(name)` | an audio cue through the game's own cue player, stopped by tag; a music cue through SoundTools' own bodies (see Sound) |
| `play_animation(entity, name, loops = 1, blend_seconds = 0.1, speed = 1)` / `stop_animation(entity)` | a named animation as the shipped AI plays one (see Animation) |
| `modify_opinion(villager, hero, axis, delta)` / `add_deed(villager, hero, deed)` | a villager's opinion of a hero through the game's own setter, or a deed through its own message (see Opinion and deeds); `deed` the record's hash |
| `npc_follow(entity, target, radius = 2)` / `npc_go_to(entity, x, y, z)` / `npc_flee(entity, from)` / `npc_attack(entity, target)` / `npc_set_hostile(entity, other, on)` / `npc_stop(entity)` | orders a creature as the shipped quests do, through the game's own entries (see NPC behaviour) |
| `set_time_of_day(hours)` / `set_weather(theme, weight = 1, seconds = 1)` | the game clock through its own setter; the GAME's environment theme blended as the quests do (see Time and weather); `query time_of_day` answers `hours` |
| `save_set(key, int)` | `RefiiModSave.<key>`, persisted with the save |
| `query(what, args) -> int` | asks; the id the answer carries |
| signal `reply(id, result)` | the answer: `ok` plus the fields below |

`what` and its answer: `health E` → `current, max` · `position E` → `x, y, z` · `gold E` → `gold`
· `item_count E DEF` → `count` · `experience E POOL` → `amount` · `ability_level E T` → `level`
· `relation A B` → `relation` · `alive E` → `alive` · `definition E` → `definition` · `name E` →
`name` · `level_name` → `level` · `hero P` → `hero` · `player_of E` → `player` · `lua_int EXPR LO
HI` → `value` (a Lua expression's integer, clamped). A `reply` is matched by its id; GDScript's
`await host.reply` would take whichever answer came first, so connect and compare instead.

The co-op surface answers as the Lua side does, asked now: `player_for E` → `player` (who "the
hero" is for E) · `world_here` → `here` · `combat_state P` → `midst, close_to_combat,
in_any_combat` (the core co-op Lua's `Refii.combat_state`, asked of the game VM) · `local_hero` → `hero` · `coop_mode` →
`mode` · `menu_player` → `player` · `coop_state` → `local_hero, coop_mode, menu_player` ·
`player_value P KEY [FALLBACK]` → `value, has` (`ok` false when there is neither the value nor a
fallback, where Lua gets nil) · `player_here P` → `here` · `player_dog P` → `dog` · `focus P` →
`entity` · `opinion E P` → `love, fear, attractiveness, morality, respect` · `uid E` → `uid`
(`"0x…"`). `host.world_here()` answers at once from the last `coop_state`. An event dictionary
carries `replica = true` when the event is a copy of the other machine's (the game's own copy, or
a relayed one); a script making a world effect skips it. Every member and verb is in the Guide's
tab 7 (Godot script helpers) and `RefiiModApi.ListGodotHelpers()`.

```gdscript
extends Node

var host: Object
var pending_health := 0

func _ready() -> void:
	host = Engine.get_singleton("RefiiHost")
	host.hit.connect(_on_hit)
	host.reply.connect(_on_reply)
	print("hit watcher up; mods: ", host.mods())

func _on_hit(e: Dictionary) -> void:
	# local_hero(): this machine's hero. hero(1) is the HOST's hero on both machines.
	var who := "my hero" if e.victim == host.local_hero() else str(e.victim)
	print("%s took %.1f damage (spell %d)" % [who, e.damage, e.spell_type])
	if e.damage > host.setting("hitwatch.loud_above", 20.0):
		host.run_lua("RefiiModSave.loud_hits = (RefiiModSave.loud_hits or 0) + 1")
		pending_health = host.query("health", [e.victim])

func _on_reply(id: int, r: Dictionary) -> void:
	if id == pending_health and r.ok and r.current < r.max * 0.25:
		host.give_gold(host.local_hero(), 10)  # a consolation purse at a quarter health
```


**More of the world.** `host.node_for(handle)` is the Node3D Godot draws that entity with, or
null (not drawn yet, or the guest draws it itself): parent a light, a label or an effect to it
to follow the creature, never move it — the entity feed owns its transform. The queries also
answer `renown`, `morality`, `purity`, `physique`, `age`, `equipped`, `inventory_stack E I`,
`near X Y Z R [CREATURES [OFFSET]]` (total, offset, count and e1..e*count*, 100 a reply: ask again from offset + count for the next page), `line_of_sight X1 Y1 Z1 X2 Y2 Z2` and
`spawn DEF X Y Z` (the new entity's handle).

## Custom spells (spells/*.json)

**What the binary allows.** The spell factory (`rex_CSpellRegistry_Create`) is a hard switch
over eight spell types — 0 Lightning, 1 Fireball, 2 SlowTime, 3 Blades, 4 Vortex, 5 Chaos,
6 ForcePush, 7 RaiseDead — and the wheel, the abilities book and every hit record carry the type
as that index. A mod cannot add a ninth class. **A custom spell stands in for one of the eight:**
it keeps that type's class (its mechanics, its slot, its ability record and levels) and gets its
own name, description, icon, damage, look and Lua behaviour. Two mods replacing the same type:
the later one in the load order wins and the mount log says so.

One file per spell, `spells/<id>.json` in the package (`Engine_ModSpells.cs`;
`RefiiModApi.SetSpell(dict)` writes one from a dictionary with `id` plus the fields below,
`RemoveSpell(id)` deletes it, `ListSpellTypes()` names the eight):

```json
{
  "replaces": "Chaos",
  "name": "Frost Nova",
  "description": "A ring of frost that bites everything near you.",
  "icon": "art/gui/spells/frost.tex.png",
  "card": "art/gui/spells/frost_card.tex.png",
  "damage": 1.3,
  "look": { "base": ["fxspl_chaos_orb"], "tint": [0.6, 0.8, 1.4], "intensity": 1.2, "seconds": 6 },
  "on_cast": "Refii.log('nova from ' .. tostring(e.caster))",
  "on_hit": "if e.victim_player == 0 then Refii.log('frozen') end"
}
```

| Field | Mechanism |
|---|---|
| `replaces` | required: a type name (any case, spaces ignored; `Swords`, `DeadRising` also resolve) or 0..7 |
| `name` | Babel rows over the type's name tags — the wheel's `GUI_SCREEN_ABILITIES_WILL_<X>_NAME_LEVEL_06` and the book's `_NAME_LEVEL_01..05` — applied to every language's `book.babel` the mount serves, like a `.babel.csv` |
| `description` | the book's `GUI_SCREEN_ABILITIES_WILL_<X>_DESC_LEVEL_01..05` rows (one text for every level) |
| `icon` | a package path; served at the wheel icon's shipped path `art/gui/wheelicons/abilities/<stem>.tex` (a `.png` is converted as a `.tex.png` overlay is) |
| `card` | the same for the abilities book's tarot still, `art/gui/abilitytarot/<stem>.tex` |
| `damage` | a multiplier on `e.damage` of every `hit` whose `spell_type` is the type |
| `look` | one `fx/recipes.json` recipe body (`base`, `tint`, `attach`, `pieces` …); `when`/`equals` are filled in. Published under `mod:<mod>.spells.<id>.look` on `spell_launched` and cleared `seconds` later (default 8) by the tick |
| `on_cast` | Lua statements run on `spell_launched` of the type, `e` in scope (`caster`, `player`, `slot`, `type`) |
| `on_hit` | Lua statements run on `hit_landed` of the type, `e` in scope (`victim`, `attacker`, `damage`, `spell_level`, `spell_serial` …) |

`<X>` and `<stem>` per type, from `globals.gdb`'s level records and the binary's wheel table:

| Type | `<X>` | wheel icon stem | tarot card stem |
|---|---|---|---|
| 0 Lightning | `LIGHTNING` | `will_shock` | `shock_spell` |
| 1 Fireball | `FIREBALL` | `will_blast` | `blast_spell` |
| 2 SlowTime | `SLOWTIME` | `icon_will_time_control` | `time_spell` |
| 3 Blades | `SWORDS` | `will_sword_of_damocles` | `swords_spell` |
| 4 Vortex | `VORTEX` | `will_vortex` | `vortex_spell` |
| 5 Chaos | `CHAOS` | `will_chaos` | `chaos_spell` |
| 6 ForcePush | `FORCE_PUSH` | `will_force_push` | `forcepush` |
| 7 RaiseDead | `DEAD_RISING` | `will_dead_rising` | `dead_rising` |

The composer generates one Lua source per spell and appends it to the init chunk after every
mod's own `init.lua`, wrapped like a mod's file. `spell_launched` does not state the type, so the
generated code remembers what `spell_launch` stated for each (caster, slot) and consumes it;
`on_cast` and `on_hit` run inside `pcall`, an error landing in `RefiiModErrors` labelled
`<mod>/spells/<id>`. A body that does not compile is named in the mount log and left out.
Everything the file does not state is left as the replaced spell ships it.

## Mod effects (fx/effects/*.json) and play_fx

**What the guest states, what Godot draws.** Every effect on screen is a `FableFx` node drawing the
particle bank (`art/particles/particle_bank.bnk`, parsed by `Engine_FxBank`). The guest states
WHICH effect plays and WHERE; Godot draws it. A mod's effect file is the bank's own model as JSON
(`Engine_FxEffectJson`), overlaid on the shipped bank as it loads (`Engine_FxModEffects`, applied
inside `FableFx.SharedBank`), so the game, the editor's bank view, `FableFx` in a scene and
`play_fx` all draw from one bank through one renderer. Nothing is written back to the guest's own
bank.

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

- **Keys are the model's fields** in camelCase (`SpawnRadius` → `spawnRadius`): every public field
  of `FxEmitter`, `FxSystemDef` and `FxVisual`, plus the effect's `name`, `zSortEmitters`,
  `updateFrequency` (0 = 15 Hz, 1 = 30 Hz), `scale` and `tint`. A key left out takes the model's
  default; the writer leaves out every field at its default. Unknown keys are warnings (ignored).
- **Hashes are text**: `entityId`, `haloTextureHash` and a system's `id` are `"0x…"` (a number is
  read too).
- **Entity types** (`typeId`): 0 standard emitter, 3 orbital, 4 attractor, 5 spline, 6 root,
  7 point light, 8 spot light, 9 spline emitter. `HasMotion`, `IsLight`, `IsAttractor`, `IsSpline`
  follow the type and are not keys.
- **Parameters**: `params.<name>` is a generic parameter's timeline `[[seconds, value], …]` — `rate`,
  `rateSpread`, `speed`, `speedSpread`, `accelDirScale`, `gravityScale` (1 = normal gravity),
  `globalWindScale`, `velX`, `velY`, `velZ` (the spawn direction), `wind`, `lightRange`,
  `lightIntensity`, `lightCutoff`, `lightFadeRamp`, `lightConeAngle`, `attractRadius`,
  `attractStrength`, or `p0`..`p27`. `vectors.vec0|vec1|vec2|lightColour` are `[[seconds, x, y, z], …]`.
  Keys are read as the bank's loader reads them: the FIRST key (file order) starts the timeline, one
  key is a constant, two or more are resampled; a timeline whose first key is before 0 s never
  starts (a warning).
- **Parts** index the file's own `emitters` and `systems` (`system`/`trail` -1 or left out: none).
  `vector` is the chain an entity belongs to (each chain parents its entities in order); `group` is
  the update bucket.
- **`scale` and `tint`** are the effect's own authored size and colour multipliers, applied exactly
  where a node's `EffectScale` and `Tint` apply. Identity for every shipped effect.
- **`rate` is particles per FRAME** (30 Hz): the guest's spawner adds rate × time scale × the step's
  frames each update (`sub_82A0B0B8`, `sub_822698F8`), and so does `Engine_FxSystem`. 40 a second is
  `[[0, 1.3333]]`.
- **`spin`** (an emitter's field, radians per second): every particle of the emitter turns about the
  emitter's own Z axis, through its origin, after each integrator step — position and velocity alike
  (a twister's swirl). Positive is counter-clockwise looking down the axis.
- **`hasMeshAlign` / `meshAlign`** (a mesh visual's, `[x, y, z, w]`): the REST ORIENTATION a mesh
  particle is born with, in the emitter's frame, before the emitter's rotation. A mesh particle is
  drawn as `spin(orAxis, Σ orSpeed) × emitterRotation × meshAlign × angle(orAxis, initial)`; `orAxis`
  is stated in the frame the particles are stored in (the effect's root, for `attachToRoot`), so
  `[0, 0, -1]` with the orbit's rate keeps a blade laid along its orbit. Read from the system's first
  visual. Without it a mesh particle is born at identity and drawn the same way — the game's own
  composition, in Classic and Modern alike.
- The guest's bank has no field for `scale`, `tint`, `spin` or `meshAlign`: they reach the guest's own
  instances through the dress table beside its bank (`refii/fx_effect_dress.txt`: `0xHASH scale r g b`,
  `spin 0xENTITY radiansPerSecond`, `align 0xSYSTEM x y z w`), read by `particle_dress.cpp` and applied
  by `particle_motion.cpp` (the integrator `sub_8322CF40` and the mesh particle's birth `sub_83220AC8`
  hooked), so both looks draw the same thing.

**The overlay rules**, applied in load order (the highest-priority mod last, and winning):

0. An emitter or system the bank holds, stated EXACTLY as the bank holds it (its file form equal), is
   a REFERENCE: the effect draws the shipped record and neither bank is rewritten for it — how an
   effect chains a shipped effect's own emitters (Spell Weaving's blades carry the Blades sword's).
1. A new effect name ADDS an effect.
2. A shipped effect name (or an `FX_XXXXXXXX` token) REPLACES that effect in its slot: every level
   placement and every live guest spawn of it draws the mod's parts, because the live feed joins a
   guest node to its effect by hash.
3. An emitter whose `entityId` the bank holds REPLACES that emitter's parameters wherever the bank
   uses it — in every effect that shares it — so the guest's own spawns of shipped effects pick the
   change up through the existing feed. An emitter without one takes an id derived from its effect
   and slot (FNV-1 of `<name>#emitter<i>`) and is added.
4. A system whose `id` the bank holds is replaced the same way; one without is added
   (`<name>#system<i>`).
5. Two mods stating the same effect, the same `entityId` or the same system `id` are a conflict,
   recorded as a `RowConflict` under the table `fx/effects` (the Mods panel tallies them beside file
   and text-row conflicts; the bank log warns once per conflict). The later mod wins.

The bank's own objects are never edited: a replaced entry is a new object in the same slot, so a
node already built keeps what it built. A re-mount that changes the mods' effects steps
`Engine_FxModEffects.Generation`; the next `SharedBank()` re-parses and overlays again.

**Checked before it loads**: `ModValidate` treats `fx/effects/*.json` as a kind; its verdict is the
reader's own (`Engine_FxEffectJson.Parse`), with each issue's place (`emitters[2].params.rate`). A
Studio or `RefiiModApi` write with an error is refused.

**Script API** (`RefiiModApi`): `ListBankEffects()`, `ListModEffects()`, `GetEffectJson(name)`,
`SetEffectJson(json)`, `SetEffect(dict)`, `CloneEffect(source, name)` (a NEW effect: every id
cleared, nothing shipped changes), `OverrideEffect(name)` (the bank's effect written as it stands,
ids kept: edit it and it replaces), `SetEmitterField(effect, emitter, key, value)`,
`SetVisualField(effect, system, visual, key, value)` (`meshAlign` states `hasMeshAlign`, the `or…`
fields `hasOrient`),
`SetEmitterKeys(effect, emitter, param, [[s, v], …])`, `RemoveEffect(name)`,
`SaveFableFxAsEffect(node, name)`; and for recipes `ListRecipes()`, `GetRecipe(id)`,
`SetRecipe(dict)`, `RemoveRecipe(id)`.

### play_fx

| Surface | Call |
|---|---|
| Native | `uint32_t refii_mod_play_fx(name, xyz, options)` · `refii_mod_play_fx_on(name, entity, options)` · `refii_mod_stop_fx(id)` |
| Lua | `Refii.play_fx(name, x, y, z [, options])` · `Refii.play_fx_on(e, name [, options])` · `Refii.stop_fx(id)` |
| Godot | `RefiiHost.play_fx(name, x, y, z, options)` · `play_fx_on(name, handle, options)` · `stop_fx(id)` |

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

- Any bank or mod effect by name (or `FX_XXXXXXXX`). Positions are the guest's world axes, as every
  event states them. An entity is an address natively and a handle in Lua and Godot.
- Godot spawns the same `FableFx` node it draws every bank effect with (`Engine_FxPlayer`): a world
  effect hangs under the live-effect feed (`GameFx`), unrotated at the stated point; an entity effect
  under that entity's drawn node IN THE ENTITY'S OWN FRAME — the effect's axes are the entity's,
  `offset` is along them — so it moves and turns with the entity (an effect authored along a sword's
  blade stays on the blade however the sword flies), freed with it. An entity with no drawn body of
  its own (a vortex, a fireball's shot) has no frame: it is followed by position through its own
  live effect (the first the feed says it owns or carries), else the position the guest states for
  it, the effect unrotated and `offset` in world axes. Under the classic look the guest's instance
  stands in the entity's frame the way the guest places its own FX entity's instance — the
  placement's position, `offset` along its axes, its orientation through the instance's own
  setter — each tick and — on a vortex or a Blades sword — again at the end of the
  spell's own update, where the guest moves that entity and its own effects. A request waits up to two seconds for its entity's node (or the feed), then is
  dropped with a warning; an unknown effect name is warned once.
- A RECIPE CAN FOLLOW A PLAYED EFFECT: `"with": "<effect>"` (`fx/recipes.json`) holds the recipe
  only where that effect plays — a base effect node is dressed when a play of it rides the entity
  the guest states the node belongs to: the feed's owner, what its entity is attached to (a
  fireball's shot effect is attached to the shot), or the spell whose own update places it (a
  vortex's ground effects, a Blades sword's effect entity and instance — `spell_fx_owner.cpp`). A
  node stated to another cast is never dressed by this play; only a node the guest states no cast
  for is dressed by a play standing within `withRadius` (default 10) of it (a look played at a
  point). One cast's `play_fx_on` therefore dresses that cast's own base effects and no other's. Spell Weaving plays
  each pair's look (`fx/effects/spell_weaving_<base>_<overlay>.json`, bank entities, both looks) on
  what the cast rides and tints the base spell's own effects with a `with` recipe.
- Options: `duration` (seconds; 0 plays until `stop_fx`, or with `loop` false until the effect has
  run out, at most a minute), `scale`, `tint`, `intensity` (an alpha term), `loop` (default true),
  `offset`, `at`.
- AT A HELPER POINT: `at` names an attachment or node of the entity's `.mdl.gmd` (the guest's own
  dummy names, matched case-insensitively) and the effect stands THERE, in the helper's own frame —
  its position and, when the gmd states one, its orientation — with `offset` along the helper's axes.
  On a rigged body the helper hangs from a bone (the same walk the game's weapon attachments use,
  `Engine_EntityAttachments.TryResolveHelper`) and the effect rides that bone under a
  `BoneAttachment3D`, so it follows the pose; on a rigid model it is the model-space rest point.
  Under the classic look the helper's REST point (and `offset` along its rest axes) is folded into
  the guest instance's offset in the entity's frame — the right point, not the live pose. An
  unknown name is warned once and the entity's own frame used. A play on an entity whose node is
  not built yet waits for it (two seconds), as every entity play does.
  Helper names a modder can rely on (read from the shipped gmds with the weave lab,
  `REFII_LAB_GMD=<model.mdl>`, and from the game's own stated attachments):
  - A weapon: `Weapon.Weapon.Hold` (the grip, 0.083 up the model), `Weapon.Weapon.Top` (the tip —
    `npc_sword_damocles` puts it 0.847 along the blade; Hold and Top are the two points the guest's own
    Blades sword update reads), `Weapon.Weapon.Sheath` (the model origin).
  - A character (the hero's parts; creatures share the `Character.` scheme): `Character.Carry.Hand.Right`
    (the weapon hand, node `Carry_Hand_DummyObject1`), `Character.Carry.Hand.Left` (`Carry_Hand_DummyObject`),
    `Character.Carry.SheathWeaponBack`, `Character.Carry.SheathWeaponFront`,
    `Character.Carry.SheathRangedWeaponBack` (the dummies the game attaches weapons by);
    `Character.Focal.Mouth`, `Character.Focal.Eye.Left` / `Eye.Right` (the head), `Character.Focal.Chest`,
    `Character.Focal.Bum`, `Character.Focal.GameplayCamera`, `Character.Focal.RangedWeaponTarget`;
    `Character.FX.Particle.Effect_1.` (where the game's own body effect hangs) and `Character.Layout.Light`.
    On a rigged body each hangs from a bone (the hand dummies from the hands, the focal points from the
    head and chest), so the effect follows the pose.
  - Any name in a model's node table (a bone: `Shadow_Head`, `Shadow_R_BlendHand`) works too.
- IDS are shared: the payload issues 1 .. 2^23-1, Godot 2^23 .. 2^24-1 — every id is exact as a Lua
  number and any surface's `stop_fx` stops any surface's id. Everything a session played stops when
  the session ends.
- The guest spawns nothing and is not told; a mod that wants the game to know uses the game's own
  calls.

### HUD: markers, fades, meters

Three HUD calls on every surface, drawn by Godot for the player named — 1 or 2, or 0 for every
player this machine plays — on split screen in that player's half, through that player's camera.
A machine draws only the players it plays; the mod's code runs on the other machine too.

| Surface | Call |
|---|---|
| Native | `refii_mod_mark(player, entity, xyz, icon, text)` · `refii_mod_unmark(id)` · `refii_mod_fade(player, to_black, seconds)` · `refii_mod_meter(player, id, label, value, max, style)` · `refii_mod_meter_remove(id)` |
| Lua | `Refii.mark(target, icon, text [, p])` · `Refii.unmark(id)` · `Refii.fade(p, dir [, seconds])` · `Refii.meter(id, label, value, max [, p [, style]])` · `Refii.meter_remove(id)` |
| Godot | `RefiiHost.mark(target, icon, text, player)` · `unmark(id)` · `markers()` · `fade(player, to_black, seconds)` · `fade_state(player)` · `meter(id, label, value, max, player, style)` · `meter_remove(id)` · `meters()` |

```lua
local id = Refii.mark(e.victim, "art/gui/wheelicons/abilities/will_shock.tex", "Marked", 0)
Refii.mark({x, y, z}, "art/my_mod_flag.png", "Go here", 2)
Refii.fade(1, "to_black", 0.5)
Refii.meter("my_mod.boss", "Thag", hp, hp_max, 0, "boss")
Refii.meter_remove("my_mod.boss"); Refii.unmark(id); Refii.fade(1, "from_black", 0.5)
```

- MARKERS ride the HUD's world-icon channel — the sprite layer the game's own property signs,
  quest markers and opinion splashes are drawn on (`Engine_HudWorldSprites`): the same projection,
  the same half, clipped as they are. A marker on an entity rides the drawn body (lifted by its drawn
  height) and goes five seconds after the entity does; a point marker stands a little above its point.
  It is sized by the game's own distance curve for a world sprite (`sub_821AA758`: full size within
  10 m, 0.6 beyond 17 m — fed the viewing camera's distance). `icon` is a game texture path
  (`art/gui/.../x.tex`, the wheel icons under `art/gui/wheelicons/` are a known set), one of the
  game's own world-icon names below, or a PNG an enabled mod ships under `art/`; an icon that
  loads nothing draws the text alone, warned once.
- THE GAME'S OWN WORLD ICONS, by alias (`icon = "quest"`): the textures the guest's world-icon
  builder names per icon kind (`sub_82820640` on TU1: a `switch` on the icon's kind, each case a
  name under `art\gui\`, `.tex` appended at 0x82820a44). `Engine_HudModMarkers.IconAliases` is the
  table; an unknown name is a path as given.

  | Alias | Texture | The game draws it for |
  |---|---|---|
  | `quest` | `hud/inworld_reward_icon_quest` | the quest marker over a giver or objective (kinds 0x1B, 0x1C) |
  | `reward`, `freebie` | `hud/inworld_reward_icon`, `hud/inworld_reward_icon_freebie` | a reward, a free one (0x32, 0x33) |
  | `reward_quest` | `wheelicons/rewards/reward_quest` | the quest reward sign (0x3A) |
  | `follow`, `wait` | `wheelicons/npcstatus/icon_npc_follow`, `..._wait` | a follower's status (0x1D, 0x1E) |
  | `engaged`, `married`, `child` | `wheelicons/npcstatus/icon_npc_engaged`, `..._married`, `..._child` | a family member (0x18..0x1A) |
  | `romance`, `love` | `wheelicons/icon_romantically_interested`, `wheelicons/opinions/opinion_love` | a love interest (0x16, 0x17) |
  | `exit` | `wheelicons/icon_level_exit` | a region exit (0x1F) |
  | `button_a` | `controller/icon_button_a` | an interaction prompt (0x20..0x22) |
  | `gold` | `wheelicons/icon_gold_coin` | gold on the ground, a sale (0x24, 0x25) |
  | `player1`, `player2` | `wheelicons/splashopinions/icon_player1`, `..._player2` | the co-op partner's splash (0x34, 0x35) |
  | `dig`, `chest`, `pet_quest` | `wheelicons/pet/icon_pet_point_dig`, `..._chest`, `..._quest` | the dog's finds (0x37..0x39) |
  | `talk` | `multiplayer/orb_icon_talk` | the co-op orb's talk prompt (0x3B) |
  | `lock` | `icon_lock` | the builder's default for a kind without a texture |
  | `opinion_attractiveness`, `opinion_ridiculousness`, `opinion_love`, `opinion_morality`, `opinion_respect`, `opinion_ugly`, `opinion_fear`, `opinion_hate`, `opinion_evil`, `opinion_disrespect`, `opinion_renown`, and each with `_gold` (not renown) | `wheelicons/splashopinions/<alias>` | the opinion splashes over a villager (1..0x15; the gold set 0xB..0x14) |
  | `morph_fat`, `morph_thin`, `morph_tall`, `morph_short`, `morph_strong`, `morph_weak`, `morph_attractive`, `morph_ugly`, `morph_good`, `morph_evil` | `wheelicons/splashopinions/<alias>` | the morph splashes (0x28..0x31) |
  Ids: the payload issues 1 .. 2^23-1, Godot 2^23 .. 2^24-1; any surface's `unmark` takes any id.
- FADE is the host's own black layer per player (`Engine_UiModFade`, drawn by `Engine_UiScreenFade`
  under the game's fade): to black or back from the alpha standing, over the seconds asked (0 at
  once). It never touches the game's fade object — level changes and save loads block on that one.
- METERS are built as mod HUD pieces of each player's HUD view (`Engine_HudModMeters`): the HUD's
  own fonts for the label, the meter primitive the `ui/hud.json` meters use for the bar, stacked
  under the top centre in the order first shown. Styles: `bar` (gold, the default), `boss` (wide,
  red), `timer` (narrow, pale). The value and maximum are read live; a label, style or player
  change rebuilds the stack. THE GAME'S OWN METER ART: styles `generic`, `crucible` and `price`
  are COPIES of hud.bgf's `GenericMeter` (the ornate bar with the red orbs), `CrucibleMeter` (the
  plain red bar) and `PriceWidget` (the bowl with the arrows and the A prompt; the value drawn in
  it as a whole number) — the shipped game never uses them and authors them dark (inactive on
  their master slide, lit by their 'On' slide, which nothing plays). A copy is the fader element's
  piece cloned whole (`Engine_HudView.BuildMeterCopies`), its bar's `MeterName` remapped to the
  meter's value, and stood on its OWN raise slide: the copy resolves itself from the frame's
  inputs with that slide in place, its subtree switched on as the slide's end state
  (`Built.SlideOverrides` / `OwnResolve`), so the copy is lit while the original's element keeps
  the authored, dark state — nothing shipped lights up. Its fill scrubs the bar's own authored
  tracks at the meter's value, anchored at the element's pivot. Proven in the HUD meter lab
  (`res://debug/hud_meter_lab.tscn`, `Godot/src/tools/HudMeterLab.cs`): one of each lit beside the
  dark originals, no guest.
- Everything a session put up — markers, meters, fades — is cleared when the game goes down.

### Lights

A mod places an omni light — or, with a cone stated, a SPOT — the way it plays an effect — on an
entity or at a world point — on every surface (`Engine_ModLights`):

| Surface | Call |
|---|---|
| Native | `refii_mod_light(entity, xyz, rgb, energy, range, options)` · `refii_mod_light_remove(id)` — `refii_mod_light_options { offset, at, flicker, direction, cone, attenuation }` |
| Lua | `Refii.light(target, color, energy, range [, o])` · `Refii.light_remove(id)` — `o = { offset, at, flicker, direction, cone, attenuation }` |
| Godot | `RefiiHost.light(target, color, energy, range, options)` · `light_remove(id)` · `lights()` — `options { offset, at, flicker, direction, cone, attenuation }` |

```lua
local torch = Refii.light(e.victim, {1, 0.6, 0.25}, 1.5, 6, { at = "Weapon.Weapon.Top", flicker = 0.3 })
Refii.light({x, y, z}, {0.4, 0.6, 1}, 2, 8)
-- a spot: along the blade from its tip (the helper's own +Y is the blade), a 25-degree cone
local beam = Refii.light(e.victim, {0.6, 0.8, 1}, 3, 12, { at = "Weapon.Weapon.Top", direction = {0, 1, 0}, cone = 25 })
-- a spot at a point, shining straight down
Refii.light({x, y, z + 4}, {1, 1, 1}, 2, 10, { direction = {0, 0, -1}, cone = 40, attenuation = 2 })
Refii.light_remove(torch)
```

- A SPOT when `cone` is above 0: `direction` (any length) is the axis it shines along, in the
  OFFSET'S AXES — on an entity the entity's own (or the helper's when `at` names one), so a spot
  on a sword's tip turns with the blade; at a world point the guest's world axes — and `cone` the
  degrees from that axis to the cone's edge (the half-angle; at most 89). `attenuation` is the
  edge falloff: 0 or 1 is linear from the edge to the axis, the title's own spot form
  (`1/(1-cos)`), higher tightens the beam (Modern only; the classic list has the one form). A
  cone of 0 is the omni light. `lights()` answers `cone` and `dx, dy, dz` (where a spot shines
  now, guest world axes).

- PLACED AS AN EFFECT IS: the one placement every mod-placed node takes (`Engine_FxPlayer.Mount`,
  the `play_fx_on` path). On an entity the light is the drawn body's child in the entity's own
  frame — `offset` along its axes — or, with `at`, at that `.mdl.gmd` helper's frame, on a rigged
  body under its bone; it rides the body and goes with the entity. An entity with no drawn body is
  followed by position. A world point is the guest's axes, as the events state positions.
- MODERN: the node the FX feed's own point lights and a recipe's glow are (`Engine_FxRecipeLight`: an
  `OmniLight3D` with zero decay — the form `fable_attenuation` inverts — no shadow map, `flicker`
  wavering its energy), or its spot form (`Engine_ModSpotLight`: a `SpotLight3D` with the same decay
  and waver, `cone` its angle, `attenuation` its edge falloff) under the same mount. It is a dynamic
  light: not in the placed lights' table, no shadow budget, baked into nothing. `energy` 1 is a
  placed lamp's; `range` is metres (0: 6, at most 100).
- CLASSIC: the classic renderer lights a draw from the guest's own light list, so the light is
  appended to that list every frame in the guest's 20-float form — a point light (type 8), or a
  spot light (type 9: the direction the node shines along this frame in guest axes, the cone's
  half-angle whose cosine the packer writes, the title's own `1/(1-cos)` edge falloff) — position
  from the node's place this frame (guest axes), colour, intensity, range as its radius — with an
  index past the manager's 64 and the second view's 64 (from 128), as player two's lights join it.
  Every draw whose bounds its sphere meets takes it — a spot with no receive frustum stated, as the
  second view's spots are taken — grouped and passed as the title groups its own. The node is
  hidden under that look; it serves as the point and the axis. At most 32 mod lights join the list.
- Ids: the payload issues 1 .. 2^23-1, Godot 2^23 .. 2^24-1 (one space with the effects); any
  surface's `light_remove` takes any id. Everything goes when the game goes down.

### The FX Lab and the inspector

The Mod Studio's FX Lab tab has two editors over one turntable:

- **Recipes**: `+ New recipe`, Delete, and every field — id, when and equals, with and
  withRadius, base (with the base shown on the turntable), anchors, entities, everyNode, tint,
  intensity, size, the light (colour, energy, range, height, flicker), attached effects (every
  number, and their own tint), and pieces (kind, blend, texture, mesh, colour and end colour, height,
  radius, radiusTop, rate, speed, size, sizeEnd, count, period, width, tilt, intensity). The preview
  is the base effect dressed exactly as the game dresses it (`Engine_FxRecipes.DressPreview`).
- **Effects**: pick an effect (★ marks the active mod's own). A bank effect opens as an OVERRIDE
  draft (its name and ids kept: saved, it replaces the shipped effect and its emitters); "Clone into
  mod" makes it a NEW effect under a new name. Edit the effect's update rate, scale and tint; each
  entity's fields for its type (shape, counts, spawn radius, attach-to-root, fades, life, spline and
  light fields); every generic parameter and vector parameter as an editable key list; spline
  points; the entity's system (mode, loops) and its visuals (texture, sheet, frame speed, colour,
  alpha, self-illumination, size, global scale, spin, trail, mesh, blend, soft depth, flicker), with
  duplicate and remove for entities and visuals. The turntable draws the draft through `FableFx` on a
  preview bank (`Engine_FxModEffects.PreviewBank`: the shared bank's lists with the draft overlaid —
  the shared bank is never touched). Save writes `fx/effects/<name>.json`.
- **Inspector**: a `FableFx` selected in a scene gets a card — "Save as mod effect" writes the effect
  it draws as a new one, with the node's `EffectScale` and `Tint` carried as the effect's own `scale`
  and `tint` (so it plays as the node looked); "Edit in FX Lab" opens it in the effect editor.

A starter template, **Effect** (`fx_effect`), writes a green flame as `fx/effects/<mod>_green_flame.json`
and an `init.lua` that plays it where a hero's hit lands.

## Mod settings page (settings.ui.json)

An enabled mod whose author offers settings to the player gets a page in the game: Options →
Mods → the mod's title. The page lists the mod's settings as value rows on the options screen's own
furniture; left and right step a value, which is applied to the running game at once
(`ModSettings.Persist`, the same seam as a live `set_setting`) and saved to
`user://mod_settings.json`, which stands over the file's values in every later session — the
player tunes a mod without editing the package. A and B leave the page. The value reaches native
code at once and the running Lua at the next guest tick: the seam posts the assignment into the
game VM's `RefiiSettings` (and the front end's state on the title), so a mod that reads
`Refii.setting` at use — both bundled mods do — plays the new number without a reload; the
Studio's live tune (`mod-setting`) and a Godot script's `set_setting` cross the same seam.

The author OFFERS rows in `settings.ui.json` beside `settings.json`, keyed by the setting's name
without the `<mod>.` prefix. Only described settings are shown: the rest stay the author's tuning,
and a mod with no `settings.ui.json` has no page at all.

```json
{
  "swing_cost": { "label": "Swing cost", "min": 0, "max": 100, "step": 1, "type": "slider",
                  "description": "Stamina a melee swing spends." },
  "woven":      { "label": "Weaving", "type": "toggle" },
  "difficulty": { "label": "Difficulty", "type": "choice",
                  "options": [ { "value": 0, "label": "Story" },
                               { "value": 1, "label": "Normal" },
                               { "value": 2, "label": "Hard" } ] }
}
```

`type` is `slider` (a number stepped from `min` to `max` by `step`), `toggle` (Off/On, 0 or 1)
or `choice` (a fixed list of labelled values). A `choice` row steps left and right through its
`options` in the order the file lists them and writes the chosen option's `value` (a number, as
every setting is) — native code reads it with `refii::mods::Setting`, Lua with `Refii.setting`.
A value no option states (an old save of the setting, a hand edit) shows the nearest option. An
option with no `value` is skipped; one with no `label` is captioned by its number; a `choice`
with no usable option falls back to a slider. `min`, `max` and `step` are ignored on a choice. The page is `ModSettingsUi` (`Engine/mods`),
built on the shared host options page kind (`Engine_UiHostOptionsPage`) the dynamic sky's
switches use, and reaches the menu through the `OptionsMenu` template's `ModSettings` fold,
sourced from `mod-settings`.

## The UI from a mod (ui/hud.json, ui/templates.json)

Godot draws every screen, so a mod's UI is data the engine's own views read — the same views
the shipped UI runs through, with no second implementation. Both files COMPOSE across mods:
they are read from every enabled package, lowest priority first, so a higher mod wins a shared
piece and both mods' other pieces land; each piece two mods state is recorded as a conflict
(`Engine_UiTemplates.ModConflicts`, `Engine_HudLayout.ModLayoutConflicts`) and logged.

### Moving the game's own HUD pieces: `"layout"`

`ui/hud.json` takes a `"layout"` block in EXACTLY the entry shape of the player's own
`user://hud/layout.json` (the HUD editor's file), keyed by the shipped piece's id
(`ListShippedHudPieces()`, or `user://hud/layout.snapshot.json` after a run):

```json
{
  "layout": {
    "group.PlayerHealth.PlayerHealth": { "anchor": [0, 1], "offset": [64, -96] }
  }
}
```

`anchor` is the fraction of the screen the piece hangs from ((0,0) top-left, (1,1)
bottom-right) and `offset` the distance from it in 720-high reference units, +X right, +Y down.
Both are required; an entry missing either is not applied. The fields the editor writes beside
them (`art`, `value`, `driven`, `placed`) are allowed and ignored. Moving a piece places it,
including one the shipped layout leaves unplaced.

Precedence: the shipped arrangement, then every mod's `layout` in load order, then the player's
own file — a piece the player placed stays where they put it whatever a mod says. One parser
reads both files (`Engine_HudModLayout`), so an entry pastes from one into the other.

### Copies of the game's own pieces: `"clones"`

A copy is the game's own widget built again beside the original — it moves, fades and hides
with it — from the same art, with the differences the record states (`Engine_HudModPieces.Clone`,
applied by `Engine_HudView.ApplyClone`):

```json
{
  "clones": [
    { "id": "my_mod.stamina", "of": "group.PlayerHealth.PlayerHealth", "offset": [0, 34],
      "hue": 0.35, "hueElements": ["HealthBar"],
      "values": { "PlayerHealth": "player:my_mod.stamina/player:my_mod.stamina_max" },
      "bars": { "HealthBar": "PlayerHealth" } },
    { "id": "my_mod.boss", "of": "slidefader2.GenericMeter.GenericMeter", "offset": [0, 120],
      "values": { "GenericMeter": "mod:my_mod.boss/mod:my_mod.boss_max" },
      "slides": { "GenericMeter": 2 } }
  ]
}
```

- `id` (required): the copy's own id; `of` (required): the shipped piece copied
  (`ListShippedHudPieces()`, `<kind>.<Name>.<ElementName>` as hud.bgf binds it, or a `widget.` id —
  a widget copy draws exactly what the widget draws and takes none of the fields below).
- `offset`: from the original, design units, +Y down.
- `hue` / `hueElements`: a hue rotation 0..1 of the named elements' art (red health to blue or
  green); the materials are duplicated first, so the original keeps its colour.
- `icon`: `{ "element", "textures": [...] }` — replaces one element's picture with the first
  candidate that loads.
- `values`: meter name → `source` or `source/cap` (`mod:`, `player:`, `game:`, `hero:`), drawn as
  `source / cap × 100`; `"none"` holds a meter still; every meter not named stays the guest's.
- `bars`: element → meter name: the elements a meter FILLS (a bar's fill is the guest's stated
  scale on that element, not a playhead), redrawn at the meter's own value.
- `slides`: element name → slide number — THE COPY'S OWN SLIDE for that element (the copy's root,
  or one beneath it), numbered as the game counts them: 1 past the master slide. A HUD piece's
  visibility is one per-element resolve shared by the original and every copy, so stating a fader
  by name raises the ORIGINAL too; a slide stated here is resolved for the copy ALONE
  (`Built.SlideOverrides` → `Engine_HudView.ResolvedFor`: the frame's inputs with that slide in
  place, the copy's subtree switched on as the slide's end state) while the original's element
  keeps the frame's state. That is how a piece the shipped game authors dark and never lights —
  `GenericMeter` and `CrucibleMeter` are inactive on their master slide and lit by slide 2, `On` —
  becomes a mod's own: the `generic` / `crucible` / `price` meter styles are exactly this record,
  made by the engine. Which elements own slides, and their numbers and names, are hud.bgf's: the
  Mod Studio's copy card lists the copied piece's slides in a picker ("Lit on slide") and writes
  the number; the HUD meter lab prints them (`REFII_HUDLAB_NAMES`), and the lab's
  `REFII_HUDLAB_CLONE=<of>:<element>=<slide>` builds such a copy with no guest. A value that is not
  a slide number is warned and ignored; an element not under the copied piece is warned at build.
  Proven in the lab: a `GenericMeter` copy lit on its slide 2 beside the dark original.

### Each player's from one record

A HUD copy (`clones`) of player one's widget whose meters read `player:` values, or a piece that
follows player one's widget and reads one, is EACH player's: the engine builds it beside player
two's widget too (`CoopHealth`, `SpellCharging2`) wherever that is drawn, with its meters named as
that widget's, and every `player:` read is that player's (`Engine_HudModTwins`). Player one's view,
player two's half on split screen and an online joiner's window all draw the right player from
the one record. The twin's id is the record's with `2` after it; a mod that states that id itself
keeps its own. A record reading only `mod:` values (one number for everyone) stays on the widget it
names. Souls Combat's two bars and Spell Weaving's glyph are one record each.

### Text pieces: `"kind": "text"`

```json
{
  "pieces": [
    { "id": "my_mod.gold_text", "kind": "text", "anchor": [1, 0], "offset": [-48, 40],
      "text": "mod:my_mod.gold", "format": "{0:0} gold",
      "size": 18, "colour": "#d8b23a", "align": "right" }
  ]
}
```

- `text` (required): a literal, a Babel tag (looked up in the string book and drawn as written
  when the book has no such tag — the game's own fallback), or `mod:<key>`, a value a mod
  publishes (`Refii.publish`, `RefiiHost.publish`, `refii::mods::Publish`). A value that is not
  published draws nothing — never a zero.
- `format`: for a `mod:` value. A .NET composite (`"{0:0} gold"`, `"Stamina {0:0.0}"`) or a bare
  numeric format (`"0.0"`, `"N0"`), invariant culture. Empty draws up to two decimals.
- `size`: the POINT size (a number — the other kinds' `size` is `[w, h]`); default 18, the HUD's
  own text size.
- `colour`: HTML hex; default the HUD text's gold.
- `align`: `left`, `centre` (or `center`) or `right` of the piece's point; the text hangs from
  the point by its top edge.
- Placement is every piece's: `anchor` + `offset`, or `follow` / `followElement`.

It is drawn by the HUD's own text path — the theme's fonts and `Paint`, inline icon tags
included — and scales with the HUD.

### Modals: `"modals"`

`ui/templates.json` takes a `"modals"` block: the answer table for the game's query and message
boxes — which input codes A and B send back through the guest's own router — composed like
screens, one WHOLE record per box:

```json
{
  "modals": {
    "13":   { "kind": "query", "confirm": 39, "cancel": 37 },
    "20":   { "kind": "query", "confirm": 36 },
    "0@22": { "kind": "message", "confirm": 36 },
    "42":   null
  }
}
```

The key is the guest's modal id (`guiOwner+44`), or `<id>@<screen id>` for a row that holds over
one screen only (a screen-specific row wins over the any-screen one). `kind` is `query` (A
`confirm`, B `cancel`; no `cancel` means B does nothing) or `message` (one dismiss code). `null`
removes the row: the box is still drawn, but keeps the guest's own input. The table decides who
ANSWERS a box, never whether it is drawn. The shipped rows, and every row as it ran, are in
`user://ui/templates.snapshot.json` under `"modals"` in this same shape.

### A mod's own menus: `Opens` and `Mod:lua:`

A menu a mod defines in `ui/templates.json` opens as a list OVER the one it was chosen from — the
front end's list, the pause list or a screen's list (the three share one list view) — from a
row whose `opens` names it or whose command is `Opens:<menu>`. B collapses a fold first, then
closes the menu and returns to exactly where it was opened; menus nest. The list is built by the
template's own construction, so a mod menu folds, ghosts and sources rows as the shipped lists
do. On the pause list a mod menu closes with the root (a tab, the resume, a quit).

```json
{
  "menus": {
    "pause-root": {
      "patch": true,
      "entries": [ { "name": "my_mod.menu", "tag": "MY_MOD_MENU", "opens": "my_mod.menu", "hostOnly": true } ]
    },
    "my_mod.menu": {
      "title": "MY_MOD_MENU",
      "entries": [
        { "name": "greet", "tag": "MY_MOD_MENU_GREET", "command": "Mod:lua:my_mod_menu.greet" },
        { "name": "gold",  "tag": "MY_MOD_MENU_GOLD",  "command": "Mod:lua:my_mod_menu.give_gold:100" },
        { "name": "more",  "tag": "MY_MOD_MENU_MORE",  "command": "Opens:my_mod.more" }
      ]
    }
  }
}
```

A row patched onto the PAUSE list must carry `"hostOnly": true`: the pause rows take their
labels from the guest's own list by order, and a row the guest never spawned has to say so.

`Mod:lua:<function>[:<arg>]` calls a global Lua function — a name or a dotted path through
tables (`my_mod_menu.give_gold`), looked up, never compiled — in the game VM on the next guest
tick, protected, as `function(arg, e)`: `arg` is everything after the second colon (`nil` when
there is none), `e` the row's `mod_menu_row` table. An error goes to `RefiiModErrors` like any
handler's. On the title and the front end there is no game VM: the call runs in the mods'
front-end Lua instead (`mod/frontend.lua`, below).

```lua
my_mod_menu = {}
function my_mod_menu.give_gold(arg, e)
  local hero = Refii.hero(e.player)     -- the player who chose the row
  if hero ~= nil then Refii.give_gold(hero, tonumber(arg) or 0) end
end
```

Any other command on a mod menu's row runs as a host action (`setting:toggle:…`, `setting:set:…`,
an options page) and never reaches the guest, which knows nothing of the menu.

Every row of a mod's menu, every row a mod patched into a shipped menu, and every `Opens:` /
`Mod:lua:` row raises **ModMenuRow** on all three surfaces — `menu` (the mod menu's name, or the
list's own: `frontend-root`, `pause-root`, a screen's population script), `row`, `player` (1 or
2: whose menu it is) and `mod` (the mod that stated the row or its menu):

```cpp
static void OnRow(refii::mods::ModMenuRowEvent& e) { /* e.menu, e.row, e.player, e.mod */ }
REFII_MOD_ON(ModMenuRow, OnRow);
```
```lua
Refii.on("mod_menu_row", function(e) Refii.log(e.menu .. "/" .. e.row) end)
```
```gdscript
Engine.get_singleton("RefiiHost").mod_menu_row.connect(func(e): print(e.menu, "/", e.row))
```

Godot raises it where the row is chosen, so a script sees it the same frame; the native and Lua
handlers get it on the next guest tick (the guest command `menu_row`). It is not cancellable. A
script opens a menu itself with `RefiiHost.open_menu(name)`.

### Mods' Lua on the title and the front end: `mod/frontend.lua`

There is no game on the title — no hero, no save, no world, no game VM — so a mod's Lua for the
front end is its own file, `mod/frontend.lua`, run in the current Gameface presentation's own Lua
state (`mod_frontend_lua.cpp`) the first time a front-end chunk needs it (a row's `Mod:lua:` call,
a box's answer, a `start_game`); another presentation starts fresh. `Refii.frontend` is true there,
and `Refii` offers what the title HAS — the table "The front-end surface" below says what, and
what is absent and why. A row on the title is a patch on `frontend-root`:

```json
{ "menus": { "frontend-root": { "patch": true, "entries": [
  { "name": "my_mod.title", "tag": "MY_MOD_TITLE_ROW", "after": "Multiplayer", "command": "Mod:lua:my_mod_frontend.ask" } ] } } }
```
```lua
my_mod_frontend = {}
local asked = nil
function my_mod_frontend.ask(arg, e)
  asked = Refii.menu_box("My Mod", "Pick one.", { "Hello", "Goodbye" }, e.player)
end
Refii.on("mod_menu_box", function(e)
  if e.id == asked and e.option >= 0 then Refii.notice("My Mod", "You chose " .. (e.option + 1), 0, e.player) end
end)
```

The **Front end** starter (`front_end`) writes exactly this; Spell Weaving's `mod/frontend.lua` puts
a "Spell Weaving" row on the title whose menu box explains weaving, the partner pick and the ability.

### The front-end surface

What exists on the title, from the binary: the front-end app (`dword_83496AC0`; `+12` its state,
`+40` of that the save manager), the signed-in pad (`dword_83496AF8` against the sign-in mask
`dword_83496A70`, as the New Game guard reads them at 0x826e0064/68), the saves as content the
profile owns (`rex_lhSaveManager_CountSavedGamesAllDevices`, `XamContentCreateEnumerator` of type 1
for that user), the Gameface presentations (one Lua state each, `CPresentation+4` its name), the
front-end screens' handlers (`GUI_FrontendRoot_MenuActionDispatch`, `rex_lhGui_FrontendRoot_OnScreenAction`,
`rex_lhGui_FrontendTitle_OnScreenAction`) and the start target
(`rex_lhFrontEnd_SetStartGameTarget`). What does NOT exist: the game root's world, hero, entities
and script system (`*(0x83496AB8)+12` is built by `Game_InitDatabases` for the session), the game
Lua VM (`Game+0xAC`, the same), `RefiiModSave` (a table of that VM, written back into the save: a
save's mod values are read only once it is loaded — `save_loaded`), and the player store (a
hero's). The options/config values the title shows are the profile's XUSER settings and
`RefiiSettings` (the mods' own, composed in). So `mod/frontend.lua` has:

| In `Refii` on the front end | what it is |
|---|---|
| `Refii.on(name, fn)` for `start_game`, `mod_menu_row`, `mod_message_box`, `mod_notice`, `mod_menu_box` | the events raised without a world: the start target stated (`save`, `level`, `new_game`, `player`), a row of a mod's menu / a title row chosen, a box or notice answered — handlers run in the presentation's state, in a chunk the guest tick runs; no other event reaches here (every other seam needs the world) |
| `Refii.signed_in()` | the signed-in pad's user index, nil for none |
| `Refii.save_count()` | the signed-in profile's saved games on every device, nil with no front end — a content enumeration: ask once a screen (a `mod_menu_row`), never per tick |
| `Refii.screen()` | the current presentation's name (`FrontendStartup`, `FrontendMainMenu`, ...), nil for none |
| `Refii.hash(name)` | the game's FNV-1 of a name as a "0x…" string, as in a game |
| `Refii.setting(key, default)` | the mods' settings.json values composed in as `RefiiSettings` (the values in force) |
| `Refii.log(text)` | a line in the guest log, `[mod lua, front end]` |
| `Refii.publish(key, n)` · `Refii.value(key)` | the shared value store, which every surface and the UI read; a published value outlives the presentation |
| `Refii.notice(title, text [, seconds [, p]])` · `Refii.message_box(title, text [, accept [, cancel [, p]]])` · `Refii.menu_box(title, text, options [, p [, cancelable]])` | the host's own dialog, question and menu box on the front end; their answers come as the events above |
| `Refii.open_menu(name [, p])` · `Refii.menu_player()` | a mod's menu over the front end's list; whose menus are open (1, or 2 for a joiner) |
| `Refii.frontend == true` | the mark a chunk tests |

Not here, and why: `hero`, `player_hero`, `local_hero`, health, position, spawn, play_fx,
`player_*`, `save_get_int` / `save_set_int`, `run_lua` (no world, no game VM, no loaded save);
`open_screen` (a host screen over the pause book — in game); `require` (one file, `mod/frontend.lua`).
A save's name and level reach a mod at the moment they are chosen (`start_game`), its mod values
once it is loaded (`save_loaded`, in `init.lua`). A profile sign-in change is not an event: the
guard polls `XamUserGetSigninState` each press (no seam flips a value).

### Mod abilities: `ui/abilities.json`

An ability of the mod's own on the abilities book — a row after its category's own abilities, or
on a page only mods declare (a `ui/templates.json` patch on `AbilitiesMenu` with source
`abilities:<category>`). It is DECLARED; the ENGINE runs it (`Engine_UiModAbilities`,
`src/vfs/src/hooks/mod_abilities.cpp`), the same for every mod and both players:

```json
{ "abilities": [ {
  "name": "SoulsCombatStamina", "category": "Strength",
  "nameTag": "SOULS_COMBAT_STAMINA_NAME", "descriptionTag": "SOULS_COMBAT_STAMINA_DESCRIPTION",
  "maxLevel": 5, "levelKey": "souls_combat.stamina_ability",
  "costLike": 3, "refund": "half",
  "stats": [100, 128, 185, 214, 242, 271],
  "legacySaveKey": "SoulsStamina",
  "image": "art/gui/abilitytarot/souls_combat_stamina.tex" } ] }
```

| Field | Meaning |
|---|---|
| `levelKey` | the SAVED player value each player's level lives in (the player store: it goes home with that hero's save, the same on both machines). The mod reads it where it applies the effect — `refii_mod_player_value(p, key, 0)`, `Refii.player_get`, `player:<key>` |
| `costLike` | the ability type (1..14) whose experience table prices the levels, through the game's own cost call; `costLikeSetting` names a mod setting that overrides it |
| `category`, `disciplines` | the page, and the pools the price is drawn from: split evenly (the first named takes the odd point), each share from its own pool and then General, as the shipped purchase draws one |
| `refund` | `"half"`: Y unlearns a level through the game's own confirm and result boxes and half its price comes back into General (the shipped unlearn); `"none"` (the default): no unlearn |
| `stats` | the description's `%1` per level 0..maxLevel; the page shows the next level's. Or `stat`, a value source read for the book's player |
| `legacySaveKey` | the `RefiiModSave` key a save from before kept player one's level in; a hero with none saved takes it |

A on the row posts the purchase for the book's player (`Mod:buy:<name>`); the engine checks the
price against that hero's pools, raises **ModAbility** (91) — a handler may refuse it — spends
through the game's own Spend and saves the level one higher. Beside the level the engine states,
per player, `<levelKey>.cost` (the next level's price, -1 at the maximum), `.refund` and `.afford`
(1 when A can buy), which the page reads; a HUD or menu can read them as `player:` sources too.

```cpp
static void OnAbility(refii::mods::ModAbilityEvent& e) {
  if (std::strcmp(e.ability, "MyAbility") == 0 && !e.refund && e.level == 5 && !Ready(e.player)) e.cancel = true;
}
REFII_MOD_ON(ModAbility, OnAbility);
```

### Spell slot picks: `ui/spellwheel.json`

A pick per spell slot beside the slot's own spell — Spell Weaving's partner. The game's own pick
(the slot's spell, kept by the guest) and a mod's are one data form (`Engine_UiSpellPicks`); a mod
states its picks:

```json
{ "picks": [ {
  "name": "spell_weaving.partner",
  "store": "spell_weaving.partner_{slot}",
  "inEffect": "player:spell_weaving.partner_active_{slot}",
  "unlocked": "player:spell_weaving.ability",
  "exclude": [2, 7], "empty": "art/gui/hud/spell_weaving_slot.tex", "scale": 0.55,
  "apply": "SPELL_WEAVING_SET_PARTNER", "remove": "SPELL_WEAVING_REMOVE_PARTNER" } ] }
```

The game's own pick raises the same event — `pick` "spell", `mod` "" — from the guest's one writer
of the slot table, before it writes: every step of the selectors' carousel (the game has no preview;
each step is the slot's spell, and cast, the moment it is made), the scripts' spell-table natives
and the host's own assignment. A changed `spell` is the one stored (and sent to a co-op session), a
cancel leaves the slot as it was. The template's defaults, the other machine's pick arriving and a
save load are not picks and raise nothing.

On the quick wheel and the abilities blade, X on a will picks it for the highlighted slot and Y
clears it, for the selectors' player. The engine raises **SpellPick** (92: `spell` writable,
cancel keeps the old pick) and saves the pick under `store` (`{slot}` the slot's number, 1..5; -1
none) in that player's store — nothing else writes it, so a mod reads its players' picks from the
store and takes no command. `inEffect` and `unlocked` are value sources read for the selectors'
player: what the mod says is in effect (the stored pick that is not is drawn faint) and when the
pick is offered at all.

The selectors draw EVERY enabled mod's pick under each slot, one small icon per pick in load order
along the slot icon's lower edge (`Engine_UiSpellPicks.Layout`: one pick at its `scale` in the
lower-right corner, several at `scale` ÷ √count so together they cover no more of the slot's spell
than one, in up to three rows; they are children of the slot icon, so they scale with it at every
resolution, aspect and split). X and Y act on one of them, each player's ACTIVE pick, drawn larger:
a press with nothing to do on it — X on the slot's own spell, on a will it excludes or on the will
it already holds, Y with nothing to clear — steps to the next pick, and LB/RB step back and on while
the abilities blade is up. The blade's X/Y prompts read the active pick's `apply` and `remove`. A
pick of the same `name` from a later mod replaces an earlier one, so the validator warns a `name`
without the mod's prefix.

### Art through the mod mount

The UI reads presentations and textures through the game's mod-aware mount once it is up: a
mod's `.bgf` / `.fac` / `.bsg` stands in for the shipped one of the same path, a `.bgf` no bank
ships becomes a presentation of its own, and a mod's `.tex` reaches every
menu and HUD piece — including a texture a `.fac` names only by hash. A pattern book is built
once per session; a mod enabled mid-session reaches books built after the remount.

**Shipping an edited or new presentation.** The Studio's UI tab has a **Presentations** page: pick
a presentation the mount serves (or make a new one), add, clone, rename or move a node in its
tree, and **Export .bgf into the mod** — the file lands at the presentation's own bank path
(`art/gui/gameface/<name>.bgf`), so the mount serves it in the shipped one's place; a name no bank
ships is a presentation of the mod's own. The writer (`Engine_BgfWriter`) is the game parser's
exact inverse — the 52 shipped presentations write back byte-identical — and reads its own bytes
back before the file is handed over. The `.bgf` remains the art and strings a template draws, not
layout: a piece's place is `ui/hud.json` / `ui/templates.json`. The `.fac` and `.bsg` sidecars
travel with it: a cloned node takes its source's UV, text and scene records under its own id, and
**Add quad** makes a textured quad from a texture path — a shipped texture's, or the mod's own
`art/.../foo.tex` (shipped as `foo.tex.png`) — with its atlas rectangle and size; the three files
are exported beside each other (52/52 of each shipped sidecar write back byte-identical too).

**Shipping a model.** A mod ships `foo.mdl.glb` (or `.gltf` with its buffers embedded) at the
shipped model's own path — `art/models/furniture/chair.mdl.glb` — and the mount serves
`art/models/furniture/chair.mdl`, converted in memory against the shipped model the way a
`foo.tex.png` becomes a `.tex`. The model being replaced is the template: its bounds, material
names, movement block, bones and the mesh-kind fields that pick its renderable class are carried,
and only the mesh comes from the glTF. Every primitive becomes one triangle-list section keyed by
its glTF material index (the material table is grown to fit; the Studio's material slots then
point each at a texture), the mesh node's transform is applied, the coarser LODs collapse onto
the new mesh (the engine's own aliasing of a zero-size LOD), and a surface with no normals gets
smooth ones. glTF is Y-up and the model file is Z-up: a glTF `(x, y, z)` lands at model
`(x, -z, y)`, which is the reader's rotation run backwards, so a model exported from the Studio
and re-imported lands where it was. A RIGGED model (a creature, a weapon on a hand) needs a
rigged glTF whose skin joints are NAMED as the model's bones are — export the model first and
rig to those names; a joint the model has no bone for is refused by name, as is an unrigged glTF
over a skinned model. Refused rather than guessed: a model whose first part carries a cloth
simulation or fur (71 + 1 of the 4,134 shipped bodies), a tree/foliage part, a glTF with an
external `.bin`, more than 65,535 vertices. A replacement's sections carry no hide region, so a
garment's "hide these clusters" does not apply to a replaced body. Only the model's FIRST mesh
part is replaced; the others are carried. The Studio: right-click a model, **Import .glb over
<model> into [mod]…** converts it against the shipped model first and places the glTF in the mod
only if the mount would accept it, then remounts. Relaunch to see it in the game (the table below).

### Starters

"New mod from template" offers **Mod menu** (a pause-list row opening a menu whose rows call
the mod's Lua), **HUD text** (a top-right line showing the hero's gold from a published value) and
**Front end** (a title-screen row whose `mod/frontend.lua` raises a menu box and answers it with a
notice).

**The game's own pages are the same kind.** The Dynamic Sky and Weather page is a description in
exactly this shape (`Engine_UiWeatherOptions.Description`), read by the same parser
(`ModSettingsUi.DescribeRows`) over the game's config switches instead of a mod's settings
(`ModSettingsUi.SwitchPage`): each entry is keyed by its switch, `default` is what an unset switch
reads as, a `choice` option may store text (`"value": "rain"`) with `aliases` for other spellings
(`"aliases": ["true", "1"]`), and the page commits on A as one config write. A mod's choices stay
numbers — a mod setting holds a number — and the checker says so.

## Installing and sharing mods

A mod travels as one `.f2mod`. Players need no dev setup to install one.

**Sharing (Mod Studio).** The Studio dock's ☰ menu has **📦 Share package…**: choose a folder
and the active mod is written there as `<mod>.f2mod` (a compacted copy of its package), checked
by the same rules the launcher's installer applies, and the folder opens. From a script:
`RefiiModApi.SharePackage(folder)`, empty on success, otherwise the reason. The file name is
the install name: it is what `loadorder.toml` and other mods' `requires` call the mod, so keep
it when you upload.

**Installing (launcher, Mods tab).** **Install from file…** picks a `.f2mod`; **Install from
URL…** downloads an https link into `assets/mods/.cache/downloads/` with progress. Before
anything is installed the player is shown the title, version, whether it replaces an installed
mod, the unmet `requires`, and — for a package with `native/*.cpp` — that it is a **hard mod**
whose native code is compiled into the game on their machine. The package is then copied into
`assets/mods/` (a same-named one is kept as `<name>.f2mod.bak`) and added to the load order
**disabled**; the player ticks it to turn it on, and a hard mod also asks for consent to its
exact native sources. The code is `ModInstaller` (`Engine/mods/Engine_ModInstaller.cs`).

Refused: not a `.f2mod`; over 2 GB; an entry with an absolute path, a drive, `.`/`..` or a
control character in its path, or whose bytes lie outside the file; a name that is not letters,
digits, spaces, `_ - .`; a link that is not https or that redirects more than 5 times or to
anything but https.

## Co-op and mods

Two co-op players must run the same mods. A mismatch is refused when the peer joins, never
played through — a damage multiplier on one side only is a desync, not a feature.

**What counts.** Every enabled mod, in load order, by name, version and the SHA-256 of its
`.f2mod` bytes, plus the mod API version (`ModApi.Version`). Mods that only change visuals count
too: any package can carry Lua. Two builds of "0.1.0" with different bytes are different mods,
and the same mods in a different order are a different set. The code is `ModFingerprint`
(`Engine/mods/Engine_ModFingerprint.cs`); a package's hash is cached by its length+mtime stamp
in `assets/mods/.cache/fingerprints/`, so an unchanged package is hashed once.

**When it is checked.** Both games restate their set on Refii's co-op channel
(`mods=<digest>/<api>/<count>:<entries>`) twice a second, and each compares the other's digest
with its own as soon as the game says a peer is in the session. A mismatch is refused on BOTH
machines: the difference is written on the multiplayer page — for example "joiner lacks Souls
Combat 0.1.0; host lacks My Mod 1.2" — and the session is left the game's own way. A peer whose
Refii never states its mods is an older build and is refused the same way.

**For a mod author:** a new version of your mod is a different mod to a player who has the old
one. Tell co-op players to update together, or let mod sync below hand it across.

## Mod sync in co-op

A joiner whose mods differ from the host's is not refused out of hand: **the host's enabled set,
in the host's order, is the session's**, and the joiner is offered it — for the session only.

**What the joiner does with the host's set.** Each mod in it, in the host's order:

| The host's mod is… | The joiner… |
|---|---|
| installed here with the same package bytes (enabled or not) | uses its own copy as-is |
| received from this host in an earlier session | uses the copy in the session cache |
| not on this machine, and data + Lua only | **downloads** it from the host's machine |
| a **hard mod** (anything under `native/`) | cannot take it on: native code is compiled into the game at launch. The join is refused as a mismatch, as before; install it, enable it and relaunch |

The joiner's own enabled mods the host does not run are **suspended** for the session; one the
host runs in another version or build is replaced by the host's. A host with no mods suspends all
of the joiner's. A joiner's own hard mod the host does not run cannot be suspended (it is in the
binary): that join is refused too. The host never adopts anything: its set wins whichever side
has more. The code is `ModSync` (`Engine/mods/Engine_ModSync.cs`, pure: the plan, the consent
text, the phase statements and the transfer's framing) driven by `GameUiCoopMods`.

**Archon's Toolbox mods are in the set, and are never synced.** Mods running under Archon's
Toolbox (the Lua mod manager that patches `gamescripts_r.bnk` and writes loose scripts under
`data/scripts/`; `ArchonToolbox.Detect()`) are stated after the player's own mods as
`archon:<NameID>` entries — titled "Archon's Toolbox: <NameID>", versioned as its
`installedmods.lua` says, hashed by the patched bank, flagged like a hard mod
(`ModFingerprint.CurrentForCoop()`). They count in the digest and the diff, so a host and a
joiner with different toolbox mods differ by name like any other mismatch. They are a
NON-SYNCABLE, data-only set: loose files in the game data and a byte-patched script bank are
not a package Refii can send, mount for a session or put down, so the same mod against the same
bank on both sides is simply agreed on, and anything else blocks the join with the reason — the
joiner lacks it ("install it with Archon's Toolbox, enable it and relaunch"), has it against
another bank ("your copy differs from the host's"), or runs one the host does not ("disable it
in Archon's Toolbox and relaunch").

**Consent, once per join, before any byte.** The joiner is asked in the game's own box, naming
every mod to be downloaded with its version and size, the ones used as-is, the ones replaced and
the ones suspended, and stating plainly that mods contain scripts that run inside the game and
come from the host's machine. **Accept** proceeds; **Decline** leaves the session, as a mismatch
did before. While a package is received the box shows progress ("Receiving Souls Combat 0.1.0
from Curious Cat… 2.1 MB / 5.3 MB (mod 1 of 2)"); the host is told "Your partner is receiving
your mods." and, once the sets agree, "Your partner is running your mods."

**What is stated.** The `mods=` record now carries each mod's package size and whether it is a
hard mod (`name title version sha12 size native`, tab-separated; an older build's four-field
entries still read). On a mismatch the joiner restates its phase beside it (`modsync=<phase>:
<detail>`: `asking`, `fetching:<n>/<total>`, `applying`, `ready:<digest>`, or `declined`,
`blocked:<why>`, `failed:<why>`). The host waits on the first four and refuses on the last three,
on a joiner that states no phase (an older build), and after 15 minutes.

**The transfer.** Packages cross on Refii's **mod sync stream**: the reliable, ordered rexnet
stream the guest TCP shim provides, on Refii's own port 0xEF12 (the shadowed `xam_net.cpp`
accepts and connects it before any guest listener; host bridge ABI 217,
`HostBridge.SyncStream*`). The joiner connects and asks for one package at a time (`Get`); the
host answers with a `Head` (name, full SHA-256, size, chunk size, chunk count) and the chunks,
64 KB each, at most 8 beyond the joiner's last `Ack`, so memory stays bounded on both ends. The
joiner verifies the whole package's SHA-256 against the one stated AND the short hash the host's
set carried before it is kept, and the session may download **64 MB in all** (`ModSync.MaxSessionBytes`;
a plan over it is refused up front and says so). A stream that fails to open in 30 s, or goes
quiet for 60 s, fails the sync and the session is refused.

**Where the packages go.** `user://mods-sync/<sha256>.f2mod` — Godot's user folder, keyed by
hash, never the player's mods folder and never shared on. They are reused the next time the same
host's set names them, and the oldest are evicted past 256 MB (`ModSyncCache`).

**The session set is held apart.** `ModSession` (`Engine/mods/Engine_ModSession.cs`) holds the
resolved order in memory and every game-side reader of "the enabled mods" — the mount
(`ModIndex.ResolveOverlays`), the composers (`ModEntries`, `ModGodot`), the fingerprint — answers
from it through `ModSession.Resolve`; the launcher's `loadorder.toml`, the player's settings and
their mods folder are never written. Applying it is the live remount with the mods' Lua run again
(the same path as "Reload mods + Lua"), so the table under *Live iteration* says what reaches
the running game: Lua, textures, text, HUD, templates, FX, settings, input AND the databases
take effect now — the joiner's guest reads the host's recomposited `.gdb.tu` through its own
loader, so a host mod's new items, changed stats and new records are in the joiner's gameplay
(rows a patch stops stating revert to the base; a row the base never had stays until a relaunch);
`.save` layers and models are served but the guest read them at boot. On every
session end — leave, kick, a lost connection, the host quitting — the joiner's own mods are
remounted and their Lua run again; a process that ends mid-session comes back on its own list,
since nothing was written. Pins are not synced: the host's `[pins]` are the host's.

## Live iteration (reload and errors)

The Mod Studio's **🎮 Live Game** tab works on a RUNNING game through its debug channel (the
game needs `debug_channel = true` in its package config; the tab says when none is up and checks
every 2 s). The Studio writes straight into the mod's package, so what is saved is what reloads;
the tab warns when the game reads mods from another folder than the one the Studio writes.

**↻ Reload mods in game** folds the installed mods into the running game again: the same passes
the mount runs at start, on the mount's worker, while the game keeps serving the previous mount
(a reload takes seconds). Once the new mount is in, everything those passes feed is reset on the
main thread. **↻ Reload mods + Lua** does the same and then runs the re-composed init chunk
(`refii/mod_init.lua`, plus `refii/mod_camera.lua` once a world is up) in the game VM, after
dropping the handlers the previous run registered (`Refii._handlers = {}`, every mask group 0), so no
handler is subscribed twice.

| Change | Live after a reload | Why |
|---|---|---|
| Images (`images/`, `.tex`) | yes | refreshed in place in every material and menu holding the texture |
| Babel rows (text) | yes, the text Godot draws | menus, HUD and item names read the reloaded string book |
| HUD pieces (`ui/hud.json`) | yes | the HUD is rebuilt |
| Templates (`ui/templates.json`) | yes | a screen already open takes them when it next opens |
| FX recipes (`fx/recipes.json`) | yes | re-dressed on the next frame |
| Abilities (`ui/abilities.json`), spell picks (`ui/spellwheel.json`) | yes | declared to the engine again; the page and selectors read the new records |
| Spells (`spells/*.json`) | names, icon and look; the Lua half with **+ Lua** | the spell's Lua joins the init chunk |
| Settings (`settings.json`) | yes | pushed again; the player's choices and values set live stand (a value set live reaches the running Lua at once, no reload) |
| Input actions (`input.json`) | yes | the bindings are read again; the player's rebinds (refii.toml) stand over them |
| Godot scripts (`godot/*.gd`) | yes, restarted | their state starts over |
| Lua (`mod/init.lua`, `mod/camera.lua`, the modules `mod/*.lua`) | with **+ Lua** | otherwise at the next session; the modules are registered afresh, so a chunk's next `require` runs the new file |
| Models (`.mdl`) | **relaunch** | a mesh already placed in the world or on a character is not rebuilt |
| GDB rows (`.gdb.csv`), `.gdb.tu`, `.gdb` | yes | the guest's own loader reads the recomposited patch again (`gdb_reload`): new lookups see added rows and properties, and every record an entity already holds takes the new values in place. A row a patch no longer states goes back to the base record (`gdb_revert`); a row the base never had **stays until a relaunch** (the guest's record map has no tombstones), and a property ADDED to a record reaches a holder of the record's older copy when it next looks the record up. Godot's views (names, inventory) refresh too |
| `.save`, `.save.tu` | **relaunch** for the world | the guest reads its save layers once, at boot; Godot's views refresh now |
| Native code (`native/*.cpp`) | **relaunch** | compiled into the game module at launch |
| Any other file | **relaunch** if the guest already loaded it | served from now on, but the guest loads most files once |

The reply names, per kind, the changed files that need a relaunch.

**init.lua runs again over the live state.** `Refii.on` handlers are cleared first; everything
else the init does, it does again. A mod that wraps a shipped function wraps its own wrapper
unless it guards the wrap: `if not MyModWrapped then MyModWrapped = true ... end`.
`RefiiModSave` keeps its contents.

**Errors.** The tab lists the mods' Lua errors as the game reports them, newest first, polled
every second while a game runs: an init error is named by its mod, a handler error by its event
(`handler for "hit"`). Each also lands in the game's log as `[mods] lua error: …`. The game keeps
the last 500; **Clear** empties the list on screen only.

**From a script or the channel.** `RefiiModApi.RemountInGame(lua)` — first line `ok`, then one
line per thing refreshed, per kind needing a relaunch, and the Lua's outcome; otherwise the
reason. `RefiiModApi.ModErrorsInGame()` — every kept error, newest first. Debug-channel ops:
`{"op":"mods-remount","lua":true}` answers when the remount is done with `refreshed`,
`relaunch` (`[{kind, reason, paths}]`), `changed`, `mods`, `modsRoot` and `lua`
(`{queued, seqBefore, note}`; the Lua's outcome is the first `call-result` numbered after
`seqBefore`); `{"op":"mod-errors","since":N}` answers `{seq, errors: [{seq, time, mod, text}]}`.
The code is `ModRemount` and `ModErrors` (`Engine/mods/`) and `Engine_GameAssets.Remount`.

## Player state: per-player values in co-op

Anything that belongs to one player — a pool, a level, a cooldown, a choice — goes in the player
store instead of a mod's own array (`core/refii_mod.h`, `refii_mod_player_*`; Lua `Refii.player_*`;
a Godot script's `host.player_set` / `player_add` / `player_save` and the `player_value` /
`player_here` queries — one store for all three). `player` is 1 or 2, `key` is `"<mod>.<name>"`.

- **The same on both machines.** In an online session each player's values are owned by the machine
  that plays that hero (the host for player one, the joiner for player two). A write on either
  machine applies there at once and goes to the owner; an add merges with the owner's own changes,
  so a spend on the host and a refill on the joiner both count. The owner restates its values over
  Refii's co-op channel when they change and every half second.
- **Saved values go home with the hero.** `refii_mod_player_save` (or `add` with `saved`) marks a
  key as the hero's. It is written into `refii_player.txt` in the folder of the save the game has
  just written that hero into — player one's main save, a local player two's own save, an online
  joiner's own save on the joiner's machine — and read back when the game loads the hero from one.
  A value nobody saved is a session value: it goes with the hero's body.
- **Where per-player work runs.** A refill, a timer or anything else a mod does on its own every
  tick for a player runs only where `refii_mod_player_here(player)` is true, so it runs once per
  session. Event handlers need no test: they read and write the store wherever the event fires.
- **Events the game runs on both machines.** Online, the game runs some seams twice for one
  occurrence: AnimEvent (every replicated action is cloned), SpellChargeLevel, SpellLaunch,
  SpellLaunched and SpellCreated (a cast action is rebuilt on the other machine, whose spell
  object is the one that deals damage on the host), HitResponse (the host's run, and the joiner's
  predicted run for hits on or by player two) and Experience (the receiver applies the delta
  again). The run on the machine that does not decide is a COPY (`refii_mod_event_replica()`;
  the game's own test, `sub_82214AF8`, or the receive flag for experience). A handler runs for
  both, and its verdict applies on both from the same player values, but player-state writes made
  during a copy are dropped — the deciding machine makes them, so a spend counts once. Only a
  handler with an effect on the body itself (holding the hero in place) skips the copy. Every
  other event fires on one machine: an ability request, a swing, a roll or a game action where the
  hero is played; a hit, a hit landing and a block on the host.
- **Relayed events.** Of those one-machine events, the ones whose effect belongs on the other
  machine are RELAYED: refii sends a copy over its co-op channel after the deciding machine
  dispatched it, and the other machine dispatches it as a replica (`refii_mod_event_replica()`,
  `Refii.event_replica()`, Godot's `e.replica`). MeleeSwing, RollStarted and ItemUsed are decided
  where the hero is played and go both ways (the host's AI sees player two's; the joiner sees
  player one's); HitLanded is decided where the world runs and goes from the host to the joiner
  when a hero dealt or took the hit. A copy names the receiving machine's own copy of the hero (an
  entity that is not a hero by its UniqueID); the deciding machine's objects (a hit record, an
  ability, an action, an item) are 0, `spell_serial` is the deciding machine's number, cancel and
  writes do nothing (it already happened), and it arrives a network moment later. Handlers need
  no co-op code: record what the event says for `e.player` wherever it arrives, and skip a world or
  body effect in a copy. If the game itself ever raises a relayed kind on the machine that does not
  decide it, that kind's relay is dropped there (logged once) so nothing is seen twice. The wire is
  `ps.e=<epoch>|<seq>:<kind>:<fields>;…` beside the player state's lines (core/event_relay.h);
  adding a relayed kind is one row of `kRelayed` in mod_events.cpp and a catalog note.
- **Whose book.** `refii_mod_menu_player(base)` is the player whose menus are open on this machine;
  a mod ability's row shows their level and price and the engine's purchase buys for them, and the
  spell selectors pick for them.
- **Each villager's opinion of each hero.** A villager keeps one set of feelings per hero (Fable
  3's rule): player two's deeds move how it sees player two and leave player one's alone.
  `Refii.opinion(villager, p)` / `refii_mod_opinion` read either; player two's starts as the
  villager's own base attitudes. During an OpinionDeed for a deed of player two's, the opinion
  component itself holds player two's set for the call.
- **Player two's screen.** What the game shows follows the player it is for. A box, question,
  tutorial, received-money/item banner, fade, d-pad or HUD-suggestion mode, scripted hero camera,
  ICS camera, hero look or camera point of interest that code puts up while acting as a player
  (`Refii.run_as_player(p, …)`, AI dealing with p, a cutscene held by p), and any `Player.*` call
  naming player two's hero, appears on that player's screen and acts on that player's hero: online
  it is sent to the machine that plays them on the ROUTE (an ordered, acknowledged channel beside
  the relay, core/event_relay.h `ps.r`) and run there through the same function, so the box is the
  game's own; a question (`GUI.AskYesNoQuestion`, `AskSetQuestPrimaryQuestion`, `AskForAmount`,
  `ShowMenuBox`) waits for the answer that player gave and returns it; `Player.Is*/Has*/Get*` about
  player two are answered from what their machine states each frame (`coop.q.*` in the player
  store). On split screen the box opens in their half on their pad. `Refii.show_to_player(p, name,
  ...)` asks for any function outright. An argument that cannot travel (a table, a vector) keeps the
  call where it was made. Movies, the crime screen and the d-pad/HUD of a shared local screen stay
  where they are.
- **Asking the partner.** `Refii.ask_partner(asker, question)` puts a mod's own yes/no question to
  the other player, on their half in local co-op and on the peer's screen online, and
  `Refii.partner_answer(ticket)` is nil until they answer. The game's own bed consent is the same
  mechanism. Ask from the machine where the asking player's action runs, and poll from a tick.
  A PartnerConsent handler sees every question first and may answer it for the partner
  (`e.answer = 1`): a "never ask, always agree" mod is one handler.
- **Partner messages.** A mod's OWN message to the other machine: `refii_mod_send_to_partner(mod,
  name, payload)` / `Refii.send_to_partner(mod, name, payload)` / `host.send_to_partner(mod, name,
  payload)`. `name` and `mod` are words (letters, digits, `_`, `.`, `-`; at most 32), `payload` any
  text up to 256 bytes (empty is fine). It arrives on the other machine as the **PartnerMessage**
  event (93: `mod`, `name`, `payload`, `from` — the player the sender plays, 1 the host, 2 the
  joiner) on every surface there — native, Lua and Godot's `partner_message` signal — once and in
  the order sent: it rides the ROUTE, the ordered, acknowledged lane above, as a line of its own
  (`pm:<from>:<mod>:<name>:<base64 payload>`) the receiver keeps apart from the core Lua's and the
  mod bus drains on its tick (`mod_events.cpp` `DrainPartnerMessages`), so it waits out a load or
  a pause and is never seen twice; a sender whose peer is gone for 20 s loses the line. It is NOT a
  replica: the receiver's handler is its own, so player-state writes in it count. Either machine
  may send. Nothing is sent alone or in local co-op (the call answers 0 / false / nil) — on one
  machine a mod talks to itself through the player store. On the Lua and Godot surfaces a quote,
  backslash or line break in the payload arrives as `_` (the bus's table text); native handlers get
  the bytes as sent. The sender does not get its own message back; what both machines must agree
  on still belongs in the player store.


## Mods and saves

Mods fuse into a save: a mod's items and equipment are record ids in the guest's own inventory
data, a mod's player values live in the player store beside the save, and `RefiiModSave` is in
the save's Lua table. Refii stamps every save with the mod set it was written under and filters
a save loaded with some of those mods missing, so the guest never meets a record it cannot
resolve and a mod enabled again gets its things back. Nothing of this is inside the guest's own
files: a Refii save stays the console game's format.

### The save-format contract: layers

A save is the folder `user_data_root/<xuid>/4D5307F1/00000001/<name>/` — exactly Xenia's
content layout — holding the guest's files `mainsave.bin`, `herosave.bin`, `chaptersave.bin`,
`saveuid.bin`, `texturemorphs.bin`, `failquestsave.bin` and `Fable2PubInfo.xml`, byte for byte
what the console game writes. Beside them, Refii's own files form layers that each apply only
when present; the game reads none of them.

| Layer | File(s) | When present | What it holds |
|---|---|---|---|
| base | the guest's seven files | always | the save, as a 360 or Xenia reads it |
| `refii` | `refii_save.json` → `"refii"`; the player store's `refii_player.txt` and `refii_blob_*.bin` | a save written by Refii | what our build persists per save that the vanilla game does not: the build, when, and the names of the player store's files (saved per-player values, see *Player state*) |
| `mods` | `refii_save.json` → `"mods"` | a save written under at least one mod, or holding filtered records | the stamp and the held records |
| `archon` | `refii_save.json` → `"archon"` | a save written with Archon's Toolbox applied to the install | the toolbox's bank hash, runner version and the mods enabled in it |

`refii_save.json` (`Engine/save/Engine_SaveSidecar.cs`), schema `refii-save/1`:

```json
{
  "schema": "refii-save/1",
  "refii": { "build": "2", "written": "2026-10-22T20:14:03Z",
             "player_state": ["refii_player.txt", "refii_blob_*.bin"] },
  "mods": {
    "api": 2, "digest": "5c1e…",
    "set": [
      { "name": "souls_combat", "title": "Souls Combat", "version": "0.1.0", "sha256": "…",
        "order": 0, "native": false, "session": false,
        "records": ["0x3A1F0C22"], "keys": ["souls_combat."] }
    ],
    "held": {
      "fancy_hats": { "title": "Fancy Hats", "version": "1.2",
                      "inventory": [ { "n": "InventoryItem", "c": [ … ] } ],
                      "worn": [ { "record": "0x7B2…", "modifier": { "n": "Modifier", "c": [ … ] } } ],
                      "melee": "0x00000000", "ranged": "0x00000000",
                      "player": { "1": { "fancy_hats.polish": "3" } } }
    }
  }
}
```

- `set` is the stamp: every enabled mod when the save was written, in load order (`order`), by
  name, title, version and package SHA-256 — the same identity co-op compares — with `native`
  (a hard mod), `session` (applied for a co-op session only by mod sync: it never fuses, its
  absence raises no box), `records` (the GDB record ids the mod's patch ADDS, read from the
  patch compositor at mount: `GdbPatchCompositor.AddedRecordsByMod`) and `keys` (the save-key
  prefixes the mod owns: its name and a dot).
- `held` is what the filter took out of the save for a mod that was missing, kept to put back:
  the inventory item elements as the guest wrote them, the worn modifiers that pointed at them,
  the last-wielded weapon ids among them, and the player store's lines under the mod's prefixes.
- A reader without a layer ignores it; a vanilla save has no layer; a Refii save without
  mods has only `refii`; the layers are independent. The schema name is versioned so later layers
  can be added. Values are written and read by hand (no serializer options), the way every
  JSON the game reads must be.

**Archon's Toolbox** (`"archon"`, `Engine/mods/Engine_ArchonToolbox.cs`). The toolbox is
detected from the install: `data/gamescripts_r.bnk` hashed against the two originals it accepts
and the two patched results it writes (SHA-256, from its own `Gamescripts.cs`; the user's 10.1
bank is `68a2ef1f…`, patched `7234bccd…`), the loose runner under `data/scripts/Mod Manager/`
(`runner.lua`, its `Version = N`), the mod list in `installedmods.lua` (`NameID`, `Enabled`,
`Installed`, `VersionMajor.VersionMinor`), one folder per mod under `data/scripts/Mods/` with a
`modmanifest.json`, and the `dir.manifest` lines pointing at them; cached by the files' size and
mtime. Every save is stamped with it — present or not — as
`{ "bank": "<sha256>", "patched": true, "runner": "1.04", "mods": [ { "name", "version" } ] }`,
the mods being those installed and enabled when the save was written. On load the "Mods in this
save" page lists them under their own heading, marking "— not enabled in Archon's Toolbox" or
"— Archon's Toolbox not present". NOTHING OF THEIRS IS FILTERED, by design and by evidence: the
toolbox's runner is a script table added through the game's own `GeneralScriptManager.AddScript`,
and every running mod's environment (its functions included), the disabled and uninstalled mods'
data and the hook callbacks hang off it; the save carries all of that inside the script save
table, the one Pluto object graph the guest writes after the tokenised documents (TU1
`sub_8245C7B8` → `sub_8245CB10`: the global `GetSaveTable` is invoked and its table handed to
`pluto_persist_entry` at `0x8245CC10`). In that graph every new object is numbered by a running
counter (`pluto_persist` `0x82BFAC78`: `++a1[1]` at `0x82BFAD20`, written as `[1][ref][type]…`,
a repeat as `[0][ref]`), a table is its metatable then key/value pairs to a nil
(`0x82BFA290`), a function is bytecode and upvalues (`0x82BFA4D0`); cutting one entry out
renumbers every object after it and leaves every back-reference into it dangling. So the blob
stays verbatim (`SaveStream.Tail`), the page says their state stays in the save, and the runner
inside the save does what it does on its own: without its files it reports `installedmods.lua`
missing; with the toolbox back, a mod whose folder is gone is offered for removal by the runner's
own box. A save made without the toolbox loads with it as before (its runner starts fresh on
load); the console copy carries the blob as it is.

### When a save is stamped

After the guest closes a save package — the full write `sub_824457E8` and the hero-only write
`sub_82445A28` (TU1), which every save path goes through: manual, autosave, checkpoint, player
one's and player two's packages, an online joiner's own — the player-state hooks post the package
name and player to the host, and `ModSaveGate` writes the sidecar: the running set
(`ModFingerprint.Current()` with the mount's record sets), the `refii` layer, and the held records
of mods still missing carried over. A mod the save never saw is safe to add: no box, and the next
save stamps it.

### When a save is loaded

The front end's own start call (`rex_lhFrontEnd_SetStartGameTarget`, event 99 `start_game`, the
save name in r4) runs the gate's verdict before the guest reads anything. Presence is by NAME: a
mod present at another version is not missing.

- Same set, or only additions or version changes: the save loads as it is.
- Mods in the stamp not enabled now (session mods excepted): the start is refused (the event's
  cancel), and a box names them with their versions — *"This save was made with mods that are not
  enabled: Souls Combat 0.1.0 … Items and equipment from them will be left out, and so will their
  saved values. They are kept with the save and come back when the mod is enabled again.
  Continue?"* — through the game's own question box. **Continue** filters the save and posts the
  same commit again (the fan's `LoadSave:N`, the root's Continue), now let through; **Cancel**
  leaves the front end on the save list.
- Held records of a mod enabled again are put back into the save before the guest reads it, and
  the hold is dropped.

The filter (`ModSaveFilter`) rewrites `mainsave.bin` in place with the missing mods' records out of
every entity's inventory (a chest can hold a mod's item too), the worn modifiers re-pointed —
they are unit indices into the inventory, so every removal shifts the ones after it — the weapon
ids among them zeroed, and the player store without their keys; the hero's removals go under
`held`. The unfiltered main save is kept beside it as `refii_mainsave_unfiltered.bin` until the
next save replaces both. Once the save's Lua table is loaded (`save_loaded`), the missing mods'
`RefiiModSave` keys under their prefixes are dropped in the game VM. Player two's hero read off a
picked save (the co-op kit) is filtered with the same sets; online, a joiner's own save goes
through the gate on the joiner's machine. The whole of this is read from the save's own
tokenised stream (`Engine/save/Engine_SaveStream.cs`): the two documents re-tokenised exactly,
everything after them copied verbatim, and re-chunked the way the guest's reader needs: the LHVFS
decompressing stream (`sub_82B4D990` / refill `sub_82B4E000`) reads per chunk a big-endian
compressed size and raw size, inflates with Z_SYNC_FLUSH on one zlib context (`sub_82B4F0B8`,
`0x82B4F134 li r4,2`) and requires consumed == compressed and produced == raw
(`0x82B4E294`, `0x82B4E2A4`); it never reads the inflate return code (no Z_STREAM_END, no final
block) and has no total-size field — the stream ends where the next header cannot be read. The
writer flushes a sync point per 8192 raw bytes, which is all the reader depends on.

**What the guest does with a record it cannot resolve**, read in the binary (TU1): it tolerates
one. The inventory loader `sub_824EF0B0` builds the item through `sub_82763E88`, whose
`GDBEntry_GetPropTyped` on the unresolved entry returns 0 and `0x82763F48 li r11,-1; stw
r11,0x10(r31)` files it as Category −1 — a dead entry in the list, never dropped, never a fault.
A worn modifier at a missing item is skipped (`0x824C0678 beq` on the NULL item); a weapon id
builds a component-less entity. `GDBManager_FindEntryByHash` logs nothing on a miss. So the
filter is what keeps dead entries out of a save, not what keeps the game up — and it is not
optional either way. One real hazard is elsewhere: `StatsComponent HeroAbility{Index}` is
written to `stats + 0x110 + 4*Index` with no bound (`0x824DE198 addi r7,r8,0x44; slwi; add; bl
sub_824807C8`), which is why mod abilities keep their levels in the player store, never in
`HeroAbility`.

### Portability: Xbox 360 and Xenia

The guest's files are the console's: a save folder copied into a Xenia profile's
`4D5307F1/00000001/` loads there, and a real 360 takes the same files wrapped into a
console-signed CON package (Velocity, Horizon — Refii does not sign one). That is unchanged by
any of this: Refii's files beside the save are ignored by the game.

A save made with mods carries mod record ids in its inventory data, which the vanilla game does
not know. It tolerates them as dead entries, so the save is not unusable, but they are dead
entries. **Copy for Xbox / Xenia** — X on a card of the Load Game list — writes a vanilla-only copy
under `<assets>/save-export/4D5307F1/00000001/<name>/`: the guest's seven files, the main save
filtered of every record of every mod the save was stamped with (and of every held record),
nothing of Refii's beside them. The original is only read and keeps everything, for when the mod
is enabled again.

The Load Game list marks a stamped save under its description: *Mods: Souls Combat 0.1.0* (two
names, or *and N more*), through the shared card template.

### For a mod author

- **Declare the records you add in your patch.** The stamp's `records` are what your `.gdb.tu` /
  `.gdb.csv` adds; an item given through any other route (a record id written by Lua into a save
  the base lacks) cannot be filtered for your mod when it is missing.
- **Namespace your save keys.** The player store's keys (`refii_mod_player_save`, `Refii.player_save`)
  and `RefiiModSave` keys must start with your mod's folder name and a dot
  (`souls_combat.level`): that prefix is what is filtered and restored.
- **Keep ability levels in the player store** (the `levelKey` of `ui/abilities.json`), never in
  `HeroAbility`: the guest's loader has no bound on the index.
- A new version of your mod is not a missing mod: a save stamped with 0.1.0 loads under 0.2.0
  unfiltered and is re-stamped on the next save. Keep your record ids stable across versions.
