# Calls reference

Every call a mod makes into the game, by surface. The concept chapters explain each family; this
page is the list. The Mod Studio's **⚡ Events & the Mod API** page lists the same calls with a
search box, generated from the catalog.

## Contents

- [Lua: the Refii table](#lua-the-refii-table)
- [Native helpers](#native-helpers)
- [Godot: RefiiHost](#godot-refiihost)
  - [Members and signals](#members-and-signals)
  - [Helper calls](#helper-calls)
- [Native: guest memory](#native-guest-memory)

## Lua: the Refii table

Composed in front of every mod's `mod/init.lua`. Entities are [handles](handles-and-players.md#handles);
hashes are `"0x…"` strings. The title screen's `mod/frontend.lua` has a smaller table: see
[The front-end surface](ui-and-hud.md#the-front-end-surface).

| Call | Meaning |
|---|---|
| `Refii.on(event, function(e) ... end)` | subscribes; `e` is a table of the event's fields; `return true` cancels where the event allows it. An event raised while a handler runs is delivered nested and live when it has writable fields, else after that chunk returns with `e.deferred = true` (see [Nested and deferred events](events.md#nested-and-deferred-events)) |
| `Refii.setting(key, default)` | a `settings.json` value with the player's choice over it, read at the call: `RefiiSettings`, composed in at session start and kept current — a change made live (the Options › Mods page, a script's `set_setting`, the Studio's live tune) is written into the running VM's table at the next guest tick, so no reload is needed |
| `require(name)` | the mod's own module `mod/<name>.lua` (see [One file per concern](events.md#one-file-per-concern)): run once on the first `require`, its return value cached — `nil` as `true` — and an error naming the file when there is none |
| `Refii.hero(player)` | the player's hero handle as the bus last stated it, or nil |
| `Refii.publish(key, n)` / `Refii.value(key)` | a mod value Godot reads as `mod:<key>` (HUD pieces, FX recipes); the last value under a key |
| `Refii.log(text)` | a line in the guest log, prefixed `[mod lua]` |
| `Refii.hash(name)` | the game's FNV-1 of a name as a `"0x…"` string: a GDB record's hash, a text tag's key |
| `Refii.health(e)` → current, max · `Refii.set_health(e, v)` · `Refii.modify_health(e, delta, attacker)` | health read, written, or changed through the game's own Modify (death included) |
| `Refii.position(e)` → x, y, z · `Refii.set_position(e, x, y, z)` | a position through the entity's own placement |
| `Refii.entity_tint(e, r, g, b [, a])` · `Refii.entity_untint(e)` | tints an entity through the game's own appearance setter (see [Tint](gameplay.md#appearance-tint-alpha-scale)); `a` defaults to 1; untint puts back the constructor's 1, 1, 1, 1 |
| `Refii.entity_alpha(e, a)` · `Refii.entity_scale(e, s)` · `Refii.entity_saturation_mask(e, on)` · `Refii.entity_drawable(e, on)` | the other setters of the same renderable (see [Appearance](gameplay.md#appearance-tint-alpha-scale)); reset = 1, 1, false, true |
| `Refii.lock_target(p [, e])` · `Refii.release_lock(p)` · `Refii.switch_target(p, left) -> target` · `Refii.locked_target(p) -> target` | the target lock as the game's LT states it (see [The target lock](gameplay.md#the-target-lock)); `e` nil or 0 is the game's own pick |
| `Refii.in_cutscene([p [, kinds]]) -> inside, kinds` | whether any of `kinds` (bits; nil: all three) holds for player p (nil or 0: either), and every bit holding — 1 full, 2 interactive, 4 look-at, named `Refii.CUTSCENE_FULL` / `_INTERACTIVE` / `_LOOK_AT` (see [Cutscenes](cutscenes.md)) |
| `Refii.apply_status(e, kind, seconds [, level [, source]])` · `Refii.clear_status(e, kind)` · `Refii.has_status(e, kind)` | a status through the spell's own installer: 1 electrified, 2 chaos, 3 burning (see [Status effects](gameplay.md#status-effects)) |
| `Refii.knockback(e, x, y, z [, force])` · `Refii.stagger(e)` | a Force Push's knockdown hit along a direction, or the flinch a 0-damage tap plays (see [Knockback and stagger](gameplay.md#knockback-and-stagger)) |
| `Refii.set_speed_multiplier(p, factor)` · `Refii.clear_speed_multiplier(p)` · `Refii.speed_multiplier(p) -> overridden, factor` | the player component's own movement-speed override (see [Movement speed](gameplay.md#movement-speed)); 0 holds the hero in place; nil without a player component |
| `Refii.current_action(e) -> action` · `Refii.action_elapsed(a) -> seconds` · `Refii.action_interruptible(a)` · `Refii.allow_action_interrupt(a)` | the action an entity's manager runs (nil for none or once stopped), its clock, and the interrupt permission every animation grants (see [Actions and commitment](gameplay.md#actions-and-commitment)) |
| `Refii.action_type(a) -> type` · `Refii.action_name(a) -> name` | what an action is: its definition's `Type`, EScriptableAction (0 PLAY_ANIMATION, 1 MELEE_STRIKE, 2 STRAFE — the roll, ...), and that enum's name; nil for an action the factory never saw (see [Actions and commitment](gameplay.md#actions-and-commitment)) |
| `Refii.living(e)` | in the world and, for a creature, not yet died (see [Actions and commitment](gameplay.md#actions-and-commitment)) |
| `Refii.spell_in_slot(p, slot) -> type` | the spell held in slot 0..4, -1 for none (the spell manager's slot table, row 0) |
| `Refii.spell_parts(serial) -> table` | what a cast put in the world, by serial: `spell`, `type`, `targeted`, `level`, `caster`, `caster_player`, `vortex`, `anchor`, `swords = { .. }`, `origin = { x, y, z }`, `exploded` — absent parts nil, nil for a cast the factory never saw (see [A live cast](gameplay.md#a-live-cast-its-parts-and-lifecycle)) |
| `Refii.play_sound(cue, e [, tag])` · `Refii.play_sound_at(cue, x, y, z [, tag])` · `Refii.stop_sound(e, tag [, fade_ms])` · `Refii.play_music(name)` · `Refii.stop_music(name)` | an audio cue through the game's own cue player, on an entity or at a point, stopped by tag; a music cue through SoundTools' own PlayMusic/StopMusic (see [Sound](gameplay.md#sound)) |
| `Refii.play_animation(e, name [, loops [, blend_seconds [, speed]]])` · `Refii.stop_animation(e)` | a named animation as the shipped AI plays one, a scripted action (see [Animation](gameplay.md#animation)) |
| `Refii.modify_opinion(villager, hero, axis, delta)` · `Refii.add_deed(villager, hero, deed)` | a villager's opinion of a hero moved through the game's own setter, or a deed told through its own message (see [Opinion and deeds](gameplay.md#opinion-and-deeds)); `deed` is a `"0x…"` string (`Refii.hash` of the Deed record's name) |
| `Refii.npc_follow(e, target [, radius])` · `Refii.npc_go_to(e, x, y, z)` · `Refii.npc_flee(e, from)` · `Refii.npc_attack(e, target)` · `Refii.npc_set_hostile(e, other, on)` · `Refii.npc_stop(e)` | orders a creature as the shipped quests do, through the game's own entries in its VM (see [NPC behaviour](gameplay.md#npc-orders)); `other` is an entity — player two's hero is its own |
| `Refii.set_time_of_day(hours)` · `Refii.time_of_day() -> hours` · `Refii.set_weather(theme [, weight [, seconds]])` | the game clock (0..24) through its own setter, read back; the GAME's environment theme blended as the quests do (see [Time and weather](gameplay.md#time-and-weather)); weight defaults to 1, seconds to 1 |
| `Refii.gold(e)` · `Refii.give_gold(e, delta)` | a purse, read and changed through the game's own Modify |
| `Refii.item_count(e, def)` · `Refii.add_item(e, def)` | an inventory; `def` is a `"0x…"` string (`Refii.hash` of the record's name) |
| `Refii.experience(e, pool)` · `Refii.give_experience(e, pool, n)` · `Refii.spend_experience(e, pool, n)` | experience: pools 0 General 1 Strength 2 Skill 3 Will |
| `Refii.ability_level(e, type)` | the level of ability 1..14, or nil |
| `Refii.cast_spell(caster, type, targeted, target, level [, pos [, dir]])` → spell, serial · `Refii.spell_serial(spell)` → serial | a real spell through the game's factory, no will cost, started at `pos = {x, y, z}` facing `dir` (the caster's own position and facing when absent): Fable 2's targeted classes place from the position along the facing — Lightning draws its bolt from there to the target, Fireball spawns its shot there, Force Push shoves the target away from there — so a cast started ON a victim is Fable III's position cast. Returns the spell object's handle and its cast serial (the number its hits, `spell_created` and `spell_parts` carry), nil for no cast; `spell_serial` reads a spell handle's serial. The serial, not the handle, is a cast's identity: a spell handle is reused by the next cast of the same class |
| `Refii.relation(a, b)` · `Refii.alive(e)` · `Refii.definition(e)` · `Refii.name(e)` · `Refii.player_of(e)` | the faction rule (1 neutral 2 ally 4 enemy), liveness, the definition hash, the instance name, 1/2/0 |
| `Refii.level_name()` · `Refii.player_hero(p)` · `Refii.local_hero()` · `Refii.menu_player()` | the loaded level, a player's hero, the local hero, the player whose menus are open (1 or 2). The local hero is the guest's own local-player rule: player two's on an online joiner, player one's on a host or alone, and in LOCAL co-op the hero of whoever owns the open menus — both players are local there, so name a player (`Refii.player_hero(p)`) when the answer must not follow the menus |
| `Refii.player_value(p, key [, fallback])` · `Refii.player_set(p, key, v)` · `Refii.player_add(p, key, d [, saved])` · `Refii.player_save(p, key, v)` · `Refii.player_here(p)` | per-player values, the same on both co-op machines; saved ones go home with the hero (see [Player state](handles-and-players.md#the-player-store)) |
| `Refii.player_dog(p)` | a player's dog: player one's, or player two's co-op dog bound to them; nil when none. Use this, never the game's `GetDog()`: inside player two's dog's own AI and item use, `GetDog()` answers that dog |
| `Refii.event_replica()` | true when the event being handled is a copy of something the other co-op machine decides — the game's own copy (verdicts apply) or a relayed one (it already happened): player-state writes are dropped; skip world and body effects in it |
| `Refii.player_for(e)` | the player `e` deals with, 1 or 2: a hero its own, a dog its master's, anything else the nearer living hero (3 m of hysteresis). Defined by the core co-op Lua |
| `Refii.acting_player()` | 1 or 2: who "the hero" (`GetPlayerHero()`) means right now — 2 inside `Refii.run_as_player(2, …)` and in AI acting for player two. Defined by the core co-op Lua |
| `Refii.run_as_player(p, fn, ...)` | runs `fn` with `GetPlayerHero()`, `GetPlayerHenchman()` and `QuestManager.HeroEntity` as player p's (the partner as the henchman), so a shipped script acts for either hero. Defined by the core co-op Lua |
| `Refii.combat_state(p)` → midst, close_to_combat, in_any_combat | player p's combat state, kept per hero (the shipped global is one state). Defined by the core co-op Lua |
| `Refii.show_to_player(p, name, ...)` | runs the function a global path names (`"GUI.DisplayMessageBox"`, `"GUI.AskYesNoQuestion"`, `"TutorialManager.DisplayTutorial"`, …) for player p, on p's screen: online on the machine that plays p (a question waits — call it from a coroutine — and returns the answer p gave), else here as p (split screen: in p's half, on p's pad). Defined by the core co-op Lua. See [Player two's screen](handles-and-players.md#player-twos-screen) |
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
| `Refii.use_item(p, def)` · `Refii.equip_weapon(p, def)` · `Refii.perform_expression(p, expression)` · `Refii.assign_spell(p, spell)` | the quick menu's own actions for player p's hero, gated as the quick menu is: use an inventory item by definition, equip a weapon the inventory holds (an online joiner's pick goes through the co-op kit to the host — the caller never cares), perform a known expression, put spell type 0..7 on every tier the hero knows; true when performed (see [Items, weapons, expressions and spells](gameplay.md#items-weapons-expressions-and-spells)) |
| `Refii.expressions(p)` · `Refii.spells_known(p)` · `Refii.selected_expression(p)` | `{ {record = "0x…", id = "0x…", uses = n}, … }` the performer knows (nil without one; `id` is the record, what `perform_expression` takes) · `{ type, … }` the hero can assign (nil without a spell manager) · the record the expression wheel highlights while it is up for player p, or nil |
| `Refii.record_int(def, field)` | a GDB record's integer or enum property, inherited, through the game's typed getter — `Refii.record_int(def, "Category")`, `Refii.record_int(def, "WeaponType")`; nil for a record the database lacks or a property it has not got |
| `Refii.record_string(def, field)` | a GDB record's string property, inherited, through the game's own getter — `Refii.record_string(def, "NameTag")`, `Refii.record_string(def, "IconGraphic")`; nil for a record the database lacks or a property it has not got |
| `Refii.near(x, y, z, r [, creatures_only])` | handles within a radius, nearest first (at most 2048) |
| `Refii.line_of_sight(x1, y1, z1, x2, y2, z2)` | true when the game's physics sees nothing between |
| `Refii.spawn(def, x, y, z)` | a new entity through the game's own CreateEntityFromIDAtPosition: its handle, or nil |
| `Refii.entity(handle)` · `Refii.handle(entity)` | the game's own Entity userdata for a handle — what the shipped scripts and the game's natives take — and back |
| `Refii.play_fx(name, x, y, z [, options])` · `Refii.play_fx_on(e, name [, options])` | an effect of the particle bank or of an enabled mod's `fx/effects/`, drawn by Godot at a world position (the guest's axes) or riding an entity handle in that entity's own frame (it turns with it; `offset` is along its axes); `options = { duration = s, scale = n, tint = {r, g, b}, intensity = n, loop = false, offset = {x, y, z}, at = "Weapon.Weapon.Top" }` — `at` names a helper point of the entity's model (see [play_fx](effects.md#play_fx)); returns the id, or nil |
| `Refii.stop_fx(id)` | stops a played effect by any surface's id (native, Lua or Godot) |
| `Refii.mark(target, icon, text [, p])` · `Refii.unmark(id)` | a marker on the HUD's world-icon channel over an entity handle or at a point `{x, y, z}` for player p (nil: every player this machine plays): `icon` a game texture path or a mod PNG (`art/x.png`), `text` a line under it; the id `unmark` takes (see [Markers, fades and meters](ui-and-hud.md#markers-fades-and-meters)) |
| `Refii.fade(p, dir [, seconds])` | fades player p's screen (0: every player played here) `"to_black"` / `"from_black"` (`"out"` / `"in"`) over seconds (nil: at once); the host's own layer, never the game's fade |
| `Refii.meter(id, label, value, max [, p [, style]])` · `Refii.meter_remove(id)` | a HUD meter (a timer, a boss bar) for player p in style `"bar"` (nil), `"boss"` or `"timer"` (the HUD's primitives), or `"generic"`, `"crucible"`, `"price"` (the game's own meter art, see [Markers, fades and meters](ui-and-hud.md#markers-fades-and-meters)); the same id restates it in place |
| `Refii.input_claim(p, action, on)` | claims (or releases) a mod's input action `<mod>.<action>` for player p (0: both): its buttons, triggers and keys leave the pad the guest sees — a chord's modifier too while held — while the mod's own `input_action` events still fire (see [Claims](settings-and-input.md#claims-taking-a-button-from-the-game)); true when posted |
| `Refii.hud_icon(p, element, texture)` · `Refii.hud_slide(p, element, slide)` · `Refii.hud_hold(p, piece, on)` | a HUD override by element name for player p (0: both): the texture an element shows (a game texture path or a mod PNG, fitted to the slot; `""` clears), the slide it sits on as the game counts them (0 clears), a piece held visible (see [HUD overrides](ui-and-hud.md#hud-overrides-icons-slides-and-holds)); true when posted |
| `Refii.light(target, color, energy, range [, o])` · `Refii.light_remove(id)` | an omni light on an entity handle (in its own frame, riding the body; `o.at` a model helper, `o.offset` along its axes, `o.flicker` 0..1) or at a point `{x, y, z}`; `color = {r, g, b}`, `energy` 1 a placed lamp's, `range` metres (0: 6); the id `light_remove` takes, or nil (see [Lights](effects.md#lights)) |
| `Refii.send_to_partner(mod, name, payload)` | a mod's own message to the other machine of an online session, raised there as `partner_message` once and in order (see [Partner messages](handles-and-players.md#partner-messages)); true when posted, false alone or in local co-op |
| `Refii.message_box(title, text [, accept [, cancel [, p]]])` | the game's own box for player p: a question when `cancel` is given (nil captions keep the box's own), else a notice; returns the id `mod_message_box` carries (see [Boxes](ui-and-hud.md#boxes-messages-notices-and-menu-boxes)) |
| `Refii.notice(title, text [, seconds [, p]])` | a notice for player p (nil: the menu owner here): the HUD's info box in game, queued behind the others; the id `mod_notice` carries (0 shown, -1 never) |
| `Refii.menu_box(title, text, options [, p [, cancelable]])` | the game's own menu box for player p: up to 12 options from the table, paged past six; B closes it unless `cancelable` is false; the id `mod_menu_box` carries with the option's index (-1 none) |
| `Refii.open_menu(name [, p])` | opens a mod's menu (`ui/templates.json`) for player p over the list that has the input, or on its own with none up; true when asked (see [A mod's own menus](ui-and-hud.md#a-mods-own-menus)) |
| `Refii.set_menu_rows(menu, rows [, p])` | states a menu list's rows for player p (nil: every player): strings or `{ name, label, command, ghosted }` — what a menu or entry with `"source": "rows"` shows, at once if open; `{}` clears; true when posted |
| `Refii.open_screen(id [, p])` | opens a host-owned screen for player p: the game's, or a screens record a mod states (id 1000+); true when posted |
| `Refii.signed_in()` · `Refii.save_count()` · `Refii.screen()` | title screen only (`mod/frontend.lua`): the signed-in user, the profile's saves, the current presentation (see [The front-end surface](ui-and-hud.md#the-front-end-surface)) |
| `Refii.api_version` | the API version the chunk was composed for |
| `RefiiModSave.<key>` | state that persists with the save game |
| `RefiiModErrors` | every init and handler error, appended; a handler's error never stops the others |

## Native helpers

All C linkage in `core/refii_mod.h`. `ctx` is the event's or hook's context, `base` guest memory.
Objects are addresses natively.

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
  `refii_mod_entity_untint(ctx, base, entity)` (see [Tint](gameplay.md#appearance-tint-alpha-scale)).
- The other appearance setters: `refii_mod_entity_alpha(ctx, base, entity, alpha)`,
  `refii_mod_entity_scale(ctx, base, entity, scale)`, `refii_mod_entity_saturation_mask(ctx, base,
  entity, on)`, `refii_mod_entity_drawable(ctx, base, entity, on)` (see [Appearance](gameplay.md#appearance-tint-alpha-scale)).
- The target lock: `refii_mod_lock_target(ctx, base, player, entity)`, `refii_mod_release_lock(ctx,
  base, player)`, `refii_mod_switch_target(ctx, base, player, left)`, `refii_mod_locked_target(ctx,
  base, player)` (see [The target lock](gameplay.md#the-target-lock)).
- Cutscenes: `refii_mod_in_cutscene(ctx, base, player)` — the `REFII_CUTSCENE_*` bits holding now
  for player 1 or 2 (0: either) (see [Cutscenes](cutscenes.md)).
- Status effects: `refii_mod_apply_status(ctx, base, entity, kind, seconds, level, source)`,
  `refii_mod_clear_status(ctx, base, entity, kind)`, `refii_mod_has_status(base, entity, kind)`;
  kinds `REFII_STATUS_ELECTRIFIED` 1, `REFII_STATUS_CHAOS` 2, `REFII_STATUS_BURNING` 3,
  `REFII_STATUS_VORTEX` 4 (see [Status effects](gameplay.md#status-effects)).
- Knockback and stagger: `refii_mod_knockback(ctx, base, entity, dir, force)`,
  `refii_mod_stagger(ctx, base, entity)` (see [Knockback and stagger](gameplay.md#knockback-and-stagger)).
- Movement speed: `refii_mod_set_speed_multiplier(base, player, factor)`,
  `refii_mod_clear_speed_multiplier(base, player)`, `refii_mod_speed_multiplier(base, player, float*
  out)` (see [Movement speed](gameplay.md#movement-speed)).
- Actions: `refii_mod_current_action(base, entity)`, `refii_mod_action_elapsed(ctx, base, action,
  float* out)`, `refii_mod_action_interruptible(base, action)`, `refii_mod_allow_action_interrupt(base,
  action)`; `refii_mod_entity_living(base, entity)` (see [Actions and commitment](gameplay.md#actions-and-commitment)).
- What an action is: `refii_mod_action_type(base, action)` (EScriptableAction, -1 unknown),
  `refii_mod_action_name(base, action, out, size)` (its enum name),
  `refii_mod_scriptable_action_name(type)` (see [Actions and commitment](gameplay.md#actions-and-commitment)).
- The front end: `refii_mod_signed_in_user(base)` (the signed-in pad's user index, -1 none),
  `refii_mod_save_count(ctx, base)` (the profile's saves on every device, -1 with no front end),
  `refii_mod_frontend_screen(ctx, base, out, size)` (the current presentation's name) — see
  [The front-end surface](ui-and-hud.md#the-front-end-surface).
- The slot table: `refii_mod_spell_in_slot(base, player, slot)` (0..7, -1 none).
- A live cast: `refii_mod_spell_parts(ctx, base, serial, refii_mod_cast_parts* out)` (see [A live cast](gameplay.md#a-live-cast-its-parts-and-lifecycle)).
- Sound: `refii_mod_play_sound_on(ctx, base, entity, cue, tag)`, `refii_mod_play_sound_at(ctx,
  base, xyz, cue, tag)`, `refii_mod_stop_sound(ctx, base, entity, tag, fade_ms)`,
  `refii_mod_play_music(ctx, base, name)`, `refii_mod_stop_music(ctx, base, name)` (see [Sound](gameplay.md#sound)).
- Animation: `refii_mod_play_animation(ctx, base, entity, name, loops, blend_seconds, speed)`,
  `refii_mod_stop_animation(ctx, base, entity)` (see [Animation](gameplay.md#animation)).
- Opinion and deeds: `refii_mod_modify_opinion(ctx, base, villager, hero, axis, delta)`,
  `refii_mod_add_deed(ctx, base, villager, hero, deed)` (see [Opinion and deeds](gameplay.md#opinion-and-deeds)).
- NPC behaviour: `refii_mod_npc_follow(ctx, base, entity, target, radius)`, `refii_mod_npc_go_to(ctx,
  base, entity, xyz)`, `refii_mod_npc_flee(ctx, base, entity, from)`, `refii_mod_npc_attack(ctx, base,
  entity, target)`, `refii_mod_npc_set_hostile(ctx, base, entity, other, on)`, `refii_mod_npc_stop(ctx,
  base, entity)` (see [NPC behaviour](gameplay.md#npc-orders)).
- Time and weather: `refii_mod_set_time_of_day(ctx, base, hours)`, `refii_mod_time_of_day(base,
  &hours)`, `refii_mod_set_weather(ctx, base, theme, weight, seconds)` (see [Time and weather](gameplay.md#time-and-weather)).
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
- The quick menu's actions, by player: `refii_mod_use_item(ctx, base, player, definition)`,
  `refii_mod_equip_weapon(ctx, base, player, definition)` (an online joiner's pick crosses to the
  co-op kit under `REFII_MOD_HOST_HERO_KIND`), `refii_mod_perform_expression(ctx, base, player,
  expression)`, `refii_mod_assign_spell(ctx, base, player, spell)`; what they choose from:
  `refii_mod_expression(base, player, index, &record, &uses)`, `refii_mod_spells_known(base,
  player, &mask)`, `refii_mod_selected_expression(base, player)`; and a record's inherited string,
  `refii_mod_record_string(ctx, base, definition, field, out, size)` and its integer or enum,
  `refii_mod_record_int(ctx, base, definition, field, &out)` (see
  [Items, weapons, expressions and spells](gameplay.md#items-weapons-expressions-and-spells)).
- Space and spawning: `refii_mod_entities_near(ctx, base, xyz, radius, creatures_only, out, max)`,
  `refii_mod_line_of_sight(ctx, base, from, to)`, `refii_mod_spawn(ctx, base, definition, xyz)`.
- Effects (Godot draws them; the guest spawns nothing): `refii_mod_play_fx(name, xyz, options)`,
  `refii_mod_play_fx_on(name, entity, options)`, returning an id,
  and `refii_mod_stop_fx(id)`. `options` is a `refii_mod_fx_options` — `duration` (seconds; 0 until
  stopped, or with `loop` 0 until the effect has run out), `scale`, `tint[3]`, `intensity`, `loop`,
  `offset[3]` (from the entity, along the entity's own axes) — or null for `refii_mod_fx_default_options()`. Any
  thread. `options.at` (or `refii_mod_play_fx_at(name, entity, helper, options)`) names
  a helper point of the entity's model — see [play_fx](effects.md#play_fx).
- The HUD (Godot draws it; the guest is not told): `refii_mod_mark(player, entity, xyz, icon, text)`
  / `refii_mod_unmark(id)`, `refii_mod_fade(player, to_black, seconds)`,
  `refii_mod_meter(player, id, label, value, max, style)` / `refii_mod_meter_remove(id)` — see
  [Markers, fades and meters](ui-and-hud.md#markers-fades-and-meters). Any thread.
- Input claims and HUD overrides (Godot applies them): `refii_mod_input_claim(player, action, on)`
  — see [Claims](settings-and-input.md#claims-taking-a-button-from-the-game); `refii_mod_hud_icon(player,
  element, texture)` / `refii_mod_hud_slide(player, element, slide)` / `refii_mod_hud_hold(player, piece, on)`
  — see [HUD overrides](ui-and-hud.md#hud-overrides-icons-slides-and-holds). Any thread.
- Lights: `refii_mod_light(entity, xyz, rgb, energy, range, options)` / `refii_mod_light_remove(id)`
  — an omni light on an entity (its own frame; `options.at` a model helper, `options.offset`,
  `options.flicker`) or at a world point, see [Lights](effects.md#lights). Any thread.
- The partner: `refii_mod_send_to_partner(mod, name, payload)` — a mod's own message to the other
  machine of an online session, raised there as PartnerMessage, see [Partner messages](handles-and-players.md#partner-messages). Any thread.
- Handles and hashes: `refii_mod_handle(address)` / `refii_mod_handle_address(handle)` (what Lua
  and Godot hold for an object, and back), `refii_mod_fnv1(text)` (the game's FNV-1 of a name).
- The game VM and the save: `refii_mod_run_game_lua` (any length), `refii_mod_game_lua_int`,
  `refii_mod_lua_generation`, `refii_mod_save_set_int`, `refii_mod_save_get_int`.
- Godot: `refii::mods::Publish / Published / Setting` (mod values and settings).
- Guest memory and calls: `refii::mods::LoadU8/U16/U32/F32`, `StoreU8/U32/F32`, `CallGuest`,
  `CallVirtual`, `Frame`, `GetVector`, `SetVector`.
- The plain tick: `REFII_MOD_ON_TICK(fn)` — the same moment as the Tick event.

## Godot: RefiiHost

`Engine.get_singleton("RefiiHost")` in a mod's `godot/*.gd`. Entities are handles, as in Lua.
See [Godot scripts](events.md#godot-scripts) for signals, verdicts and how queries answer.
An argument written `name = value` may be left off, from the right: `notice(title, text)` is
`notice(title, text, 0, 0)` and `in_cutscene()` is `in_cutscene(0, 7)`.

### Members and signals

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
| `player_set(p, key, v)` · `player_add(p, key, d, saved)` · `player_save(p, key, v)` | per-player values in the store `Refii.player_set` / `_add` / `_save` write — the same on both co-op machines; saved ones go home with the hero (see [Player state](handles-and-players.md#the-player-store)) |
| `mods() -> Array[String]` | the enabled mods, in load order |
| signal `mod_menu_row(e)` | a row of a mod's menu was chosen: `menu`, `row`, `player` (1 or 2, whose menu it is), `mod` (the mod that stated the row or its menu, `""` for a shipped row). Raised by Godot where the row is chosen, so it arrives the same frame, on the title screen too |
| `open_menu(name) -> bool` | opens a menu a mod defines in `ui/templates.json` as a list over whichever menu list has the input — the front end, the pause list or a screen's list; B returns to it. False when no list is up (plain gameplay) or no template has that name |
| `message_box(title, text, buttons) -> int` | the game's own box: `buttons` empty or one caption is a notice (one dismiss), two captions a question (A and B, captioned by `buttons[0]` and `buttons[1]`). `title` sits above `text`. Returns an id; the answer comes as `message_box_answered(id, button)`. In game only (the front end has no host box) |
| signal `message_box_answered(id, button)` | `button` 0 for A (a question's accept, a notice's dismiss), 1 for B, -1 only when the box was withdrawn before it was answered. One question and one notice stand at a time, the rest queued in order: a second question is asked once the first is answered, a notice told under a notice follows it |
| `publish(key, value)` | publishes a mod value under `key` (`"<mod>.<name>"`, no spaces): what a HUD piece reads as `mod:<key>`, Lua as `Refii.value(key)`, native code as `refii::mods::Published`. Set on the guest's next tick, the moment every reader sees it |
| `play_fx(name, x, y, z, options) -> int` | an effect of the bank or a mod's `fx/effects/` as a `FableFx` node at a world position (guest axes); `options` is a Dictionary: `duration`, `scale`, `tint` (Color or `[r, g, b]`), `intensity`, `loop` (true); the id, 0 for an unknown effect |
| `play_fx_on(name, handle, options) -> int` | the same on an entity by its HANDLE: it stands in the entity's own frame, `offset` (Vector3 or `[x, y, z]`) along the entity's axes, and moves and turns with it, freed with it; `"at": "Weapon.Weapon.Top"` stands it at that helper point of the model (see [play_fx](effects.md#play_fx)) |
| `stop_fx(id) -> bool` | stops any surface's id; false when nothing plays under it |
| `mark(target, icon, text = "", player = 0) -> int` · `unmark(id) -> bool` · `markers() -> Array` | a marker on the HUD's world-icon channel over an entity handle (int) or at a Vector3 point for player 1 or 2 (0: every player this machine plays); `icon` a game texture path or a mod PNG; the id `unmark` takes; `markers()` lists what stands (id, player, entity, x, y, z, icon, text) |
| `fade(player, to_black, seconds = 0) -> bool` · `fade_state(player) -> Dictionary` | fades the player's screen to black or back over seconds (the host's own layer over their half, never the game's fade); `fade_state` answers `alpha` (0 clear .. 1 black), `target`, `seconds` |
| `meter(id, label, value, max, player = 0, style = "") -> bool` · `meter_remove(id) -> bool` · `meters() -> Array` | a HUD meter for player 1 or 2 (0: every player played here) in style `bar`, `boss` or `timer` (the HUD's primitives), or `generic`, `crucible`, `price` (the game's own meter art); the same id restates it in place; `meters()` lists what stands in stack order |
| `claim_input(player, action, on = true) -> bool` · `input_claimed(player, action) -> bool` · `input_claims(player) -> Array` | claims (or releases) a mod's input action `<mod>.<action>` for player 1 or 2 (0: both): its buttons, triggers and keys leave the pad the guest sees — a chord's modifier too while held — while the `input_action` signal still fires; `input_claimed` / `input_claims` read the state (see [Claims](settings-and-input.md#claims-taking-a-button-from-the-game)) |
| `hud_icon(player, element, texture) -> bool` · `hud_slide(player, element, slide) -> bool` · `hud_hold(player, piece, on = true) -> bool` · `hud_overrides(player) -> Dictionary` | a HUD override by element name for player 1 or 2 (0: both): the texture an element shows (a game texture path or a mod PNG, fitted to the slot; `""` clears), the slide it sits on (0 clears), a piece held visible; `hud_overrides` answers `icons`, `slides`, `holds` (see [HUD overrides](ui-and-hud.md#hud-overrides-icons-slides-and-holds)) |
| `light(target, color, energy, range, options = null) -> int` · `light_remove(id) -> bool` · `lights() -> Array` | an omni light on an entity handle (int; its own frame, riding the body) or at a Vector3 / `[x, y, z]` point (guest axes); `color` a Color or `[r, g, b]`, `energy` (1: a placed lamp's), `range` metres (0: 6); `options` `{ offset, at, flicker }` as play_fx_on's `offset` / `at`, `flicker` 0..1; the id `light_remove` takes, 0 for an unknown handle; `lights()` lists what stands (id, entity, r, g, b, energy, range, x, y, z) — see [Lights](effects.md#lights) |
| `send_to_partner(mod, name, payload = "") -> bool` · signal `partner_message(e)` | a mod's own message to the other machine of an online session (the guest command `partner_send`), raised there as `partner_message` — `mod`, `name`, `payload`, `from` — once and in order; false alone or in local co-op, for a bad name or a payload over 256 bytes (see [Partner messages](handles-and-players.md#partner-messages)) |
| `api_version() -> int` | the API version |
| `notice(title, text, seconds = 0, player = 0) -> id` · signal `notice_done(id, outcome)` | a notice on the channel the game's co-op notices use — in game the HUD's info box for `seconds` (0: its own length), on the front end its dialog — queued behind the others; `outcome` 0 once shown, -1 when it never showed |
| `menu_box(title, text, options, player = 0, cancelable = true) -> id` · signal `menu_box_answered(id, option)` | the game's own menu box, on the front end and in game, for player 1 or 2 (0: the menu owner here): up to 16 options, paged past six; `option` is the index chosen, -1 for B, a box replacing it, or a player played on the other machine |
| `set_menu_rows(menu, rows, player = 0)` | states a menu list's rows for player 1 or 2 (0: every player), replacing the last: what a `ui/templates.json` menu or entry with `"source": "rows"` (or `rows:<list>`) shows, at once if it is open. A row is a string or `{ name, label, command, ghosted }`; `label` a Babel tag or literal, `command` any row verb; `[]` clears. `"source": "values:mod:<key>\|<format>=<label>,..."` lists published values instead |
| `open_screen(screen, player = 0) -> bool` | opens a host-owned screen by id — the `Screen:open:<id>` verb any row can name: the game's (1013 achievements, 1014 multiplayer) or a `ui/templates.json` screens record a mod states (id 1000+). It stands over whichever in-game list opened it — the pause list, a screen's list, a mod menu over either or a mod's own menu — takes the whole pad while it stands, and B returns to that list |
| `register_command(verb, handler) -> bool` | a row verb every list runs (the front end, the pause book, screen lists, mod menus, a screen's own buttons): a row whose command is `verb` — or begins with it when it ends in `:` — calls `handler(argument, menu, row, player)`; false refuses the row. The game's verbs (`PauseTab:`, `Screen:open:`, `Opens:`, `Mod:lua:`, `setting:`, `Inventory:`, `Ability:learn\|unlearn:<1..14>`, `Shop:buy\|sell:<ticket>` …) stay the game's |
| `register_source(name, rows) -> bool` | a ROW SOURCE every list's template may name (`"source": "<name>[:<argument>]"` on an entry or a menu): `rows(argument)` returns an Array of Dictionaries, one row each — `name` (required), `label`, `icon`, `command` (the verb the row runs on A), `rating` (0..10), `ghosted`, `group` (a header the row sits under), `emphasised`, `counted`, `unviewed`. Called whenever the list is built or re-sourced. The game's sources stay the game's |
| `register_condition(name, holds) -> bool` | a ROW CONDITION entries may name in `when`: `holds()` answers whether the rows naming it show, asked when a list is built. The game's conditions stay the game's |
| `on_verdict(event, callable) -> bool` · `remove_verdict(event, callable)` | a [verdict](events.md#verdicts) handler inside the seam |
| `save_snapshot(key) -> bool` · `snapshot_time(key) -> int` · `load_snapshot(key) -> bool` · signal `save_written(e)` | a copy of this machine's player-one save under `key` (`"<mod>.<name>"`), kept beside the save and never in the load list: `save_snapshot` copies the save as it stands on disk — take it on `save_written` (`save`, `player`), after the game's own save; `snapshot_time` answers when it was taken (unix seconds, 0 for none); `load_snapshot` loads it back in the running game through the game's own fail-quest reload and puts the save folder's own files back once the level stands. False before a save is started or written, for a bad key or no snapshot; `load_snapshot` also in co-op and while a load is in flight. See [Snapshots](saves-and-sharing.md#snapshots-a-quicksave-and-a-quickload) |
| `node_for(handle) -> Node3D` | the node Godot draws the entity with, or null; parent to it, never move it |
| `world_here() -> bool` | answers at once from the last `coop_state` |

### Helper calls

Lines the guest tick runs; fire-and-forget, silent when there is nothing to act on. Queries answer
later on `reply(id, result)`; every verb is in [Commands and queries](reference-commands.md#godot-queries).

| Member | Meaning |
|---|---|
| `give_gold(entity, delta)` | adds (negative: takes) gold |
| `modify_health(entity, delta, attacker)` | changes health, attributed to `attacker` (0 for none) |
| `set_health(entity, value)` | sets current health |
| `add_item(entity, definition)` | one of an item definition (its GDB record hash) into the inventory |
| `use_item(definition) -> bool` | uses one of an item definition (`"0x…"` or a number) from this machine's hero's inventory, as the Items page's Use does, with no menu open: a potion drinks, food is eaten, `item_used` fires. Nothing happens when the hero carries none or the item states no use; false when the definition does not parse |
| `give_experience(entity, pool, amount)` / `spend_experience(...)` | one experience pool |
| `cast_spell(caster, type, targeted, target, level)` · `cast_spell_at(caster, type, targeted, target, level, pos, dir)` | a cast as `caster`; `target` 0 for none, level 0..4; `cast_spell_at` starts it at `pos` (Vector3, guest axes) facing `dir`, as `refii_mod_cast_spell`'s pos / dir (the line: `cast_spell C T TARGETED TARGET LEVEL [X Y Z [DX DY DZ]]`) |
| `set_position(entity, x, y, z)` | moves an entity |
| `entity_tint(entity, r, g, b, a = 1)` / `entity_untint(entity)` | tints an entity through the game's own appearance setter (see [Tint](gameplay.md#appearance-tint-alpha-scale)); untint puts back 1, 1, 1, 1 |
| `entity_alpha(entity, alpha)` / `entity_scale(entity, scale)` / `entity_saturation_mask(entity, on)` / `entity_drawable(entity, on)` | the other setters of the same renderable (see [Appearance](gameplay.md#appearance-tint-alpha-scale)) |
| `lock_target(player, entity = 0)` / `release_lock(player)` / `switch_target(player, left)` | the target lock as the game's LT states it (see [The target lock](gameplay.md#the-target-lock)); `query locked_target P` answers the target held |
| `use_item(player, definition)` / `equip_weapon(player, definition)` / `perform_expression(player, expression)` / `assign_spell(player, spell)` | the quick menu's own actions for player 1 or 2's hero (see [Items, weapons, expressions and spells](gameplay.md#items-weapons-expressions-and-spells)); `query expressions P` answers `count`, `e1..eN` (records), `u1..uN` (uses), `query spells_known P` `mask`, `count`, `query selected_expression P` `expression` (ok false with no wheel up), `query record_string DEF FIELD` `value` |
| `in_cutscene(player = 0, kinds = 7) -> bool` / `cutscene_kinds(player = 0)` | the cutscene event as this host last heard it (heard while any script is loaded; a new host seeds it with `query in_cutscene P`): whether any of `kinds` holds for player 1 or 2 (0: either), and the bits (see [Cutscenes](cutscenes.md)); `query in_cutscene P` answers `kinds` from the guest now |
| `wheel_switches_target(handle, on = true) -> bool` | hero `handle`'s target lock switches by the mouse wheel, as `Refii.wheel_switches_target` states each tick — STICKY here: a Godot script runs off the guest tick, so the tick restates it (the command `wheel_switches_target E ON`) on every tick from the call until `on = false` or the hero's player no longer holds a lock (the game's own release, or `release_lock`); false for a bad handle |
| `apply_status(entity, kind, seconds, level = 0, source = 0)` / `clear_status(entity, kind)` | a status through the spell's own installer: 1 electrified, 2 chaos, 3 burning (see [Status effects](gameplay.md#status-effects)); `query has_status E KIND` asks |
| `knockback(entity, x, y, z, force = 1)` / `stagger(entity)` | a Force Push's knockdown hit along a direction, or a flinch (see [Knockback and stagger](gameplay.md#knockback-and-stagger)) |
| `set_speed_multiplier(player, factor)` / `clear_speed_multiplier(player)` | the player component's own movement-speed override (see [Movement speed](gameplay.md#movement-speed)); `query speed_multiplier P` answers `overridden`, `factor` |
| `allow_action_interrupt(action)` | grants an action's ALLOW_ACTION_INTERRUPT (see [Actions and commitment](gameplay.md#actions-and-commitment)); `query current_action E` answers `action`, `elapsed`, `interruptible` |
| `query action A` / `query frontend` | `type`, `name` — EScriptableAction and its name (ok false for an action the factory never saw; see [Actions and commitment](gameplay.md#actions-and-commitment)) · `signed_in`, `saves`, `screen` — the front end's own state (ok false with no front end up; see [The front-end surface](ui-and-hud.md#the-front-end-surface)) |
| `query living E` / `query spell_in_slot P SLOT` / `query spell_parts SERIAL` | `living` · `type` · `spell, type, targeted, level, caster, caster_player, vortex, anchor, sword_count, s1..s8, x, y, z, has_origin, exploded` (ok false for an unknown cast) |
| `play_sound(cue, entity, tag = "")` / `play_sound_at(cue, x, y, z, tag = "")` / `stop_sound(entity, tag, fade_ms = 0)` / `play_music(name)` / `stop_music(name)` | an audio cue through the game's own cue player, stopped by tag; a music cue through SoundTools' own bodies (see [Sound](gameplay.md#sound)) |
| `play_animation(entity, name, loops = 1, blend_seconds = 0.1, speed = 1)` / `stop_animation(entity)` | a named animation as the shipped AI plays one (see [Animation](gameplay.md#animation)) |
| `modify_opinion(villager, hero, axis, delta)` / `add_deed(villager, hero, deed)` | a villager's opinion of a hero through the game's own setter, or a deed through its own message (see [Opinion and deeds](gameplay.md#opinion-and-deeds)); `deed` the record's hash |
| `npc_follow(entity, target, radius = 2)` / `npc_go_to(entity, x, y, z)` / `npc_flee(entity, from)` / `npc_attack(entity, target)` / `npc_set_hostile(entity, other, on)` / `npc_stop(entity)` | orders a creature as the shipped quests do, through the game's own entries (see [NPC behaviour](gameplay.md#npc-orders)) |
| `set_time_of_day(hours)` / `set_weather(theme, weight = 1, seconds = 1)` | the game clock through its own setter; the GAME's environment theme blended as the quests do (see [Time and weather](gameplay.md#time-and-weather)); `query time_of_day` answers `hours` |
| `save_set(key, int)` | `RefiiModSave.<key>`, persisted with the save |
| `query(what, args) -> int` | asks; the id the answer carries |
| signal `reply(id, result)` | the answer: `ok` plus the fields of [the query](reference-commands.md#godot-queries) |

## Native: guest memory

For native code reading the game directly (`refii::mods::LoadU8/U16/U32/F32`,
`StoreU8/U32/F32`, `CallGuest`, `CallVirtual`, `Frame`, `GetVector`, `SetVector`). Prefer a helper
where one exists.

Every entity component keeps its owner entity at +4. `refii_mod_component(base, entity, id)` finds
a component by id. Entity: +0x1C definition hash, +0x7C placement, +0x80 name (a pointer to a body
whose first word is the text), +0x90 bit 0x40 in world.

| Id | Component | Fields the API reads |
|---|---|---|
| 3 | graphic appearance | +80..+95 tint {r, g, b, a}, +108 scale, +112 alpha |
| 13 | inventory | +16 list sentinel (node: +0 next, +8 item; item: +4 definition, +12 count), +20 count |
| 20 | creature | +0x18 alive |
| 24 | held items | slots by name |
| 28 | weapon set | +84 melee record, +88 ranged record |
| 31 | hit receiver | the hit dispatch |
| 33 | player | +4 owner |
| 36 | health | +0x14 current, +0x1C max, +0x131 invulnerable, +0x133 can't die |
| 49 | action manager | +0x10 current action |
| 56 | spell manager | +0x80 slot table |
| 57 | stats | +0x110 + 4 × type: ability levels |
| 77 | targeting | +0x14 force lock |
| 81 | purse | +12 gold |
| 133 | faction | +0x14 faction id |
| 164 | experience | +0x10 pools float[4]: General, Strength, Skill, Will |

Hit record: +0x18 knockdown, +0x19 unblockable, +0x38 damage, +0x44 serial, +0x48 level, +0x54
spell type (-1 melee). Spell object: +0x40 level, +0x44 serial.
