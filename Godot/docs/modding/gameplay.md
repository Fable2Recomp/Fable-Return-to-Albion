# Gameplay

The calls that act on the world through the game's own code: bodies, combat, spells, sound,
animation, villagers and NPCs, the clock and the weather. Every call exists on all three surfaces
unless noted; the [calls reference](reference-calls.md) lists exact signatures. Lua and Godot
calls run on the game's tick thread.

## Contents

- [Appearance: tint, alpha, scale](#appearance-tint-alpha-scale)
- [The target lock](#the-target-lock)
- [Status effects](#status-effects)
- [Knockback and stagger](#knockback-and-stagger)
- [Movement speed](#movement-speed)
- [Actions and commitment](#actions-and-commitment)
- [A live cast: its parts and lifecycle](#a-live-cast-its-parts-and-lifecycle)
- [Custom spells](#custom-spells)
- [Sound](#sound)
- [Animation](#animation)
- [Opinion and deeds](#opinion-and-deeds)
- [NPC orders](#npc-orders)
- [Time and weather](#time-and-weather)

## Appearance: tint, alpha, scale

| Call (native / Lua / Godot) | What it sets | Reset |
|---|---|---|
| `refii_mod_entity_tint` / `Refii.entity_tint(e, r, g, b [, a])` / `entity_tint(handle, r, g, b, a = 1)` | a linear multiplier of the entity's lit colour per lane | `entity_untint` → 1, 1, 1, 1 |
| `refii_mod_entity_alpha` / `Refii.entity_alpha(e, a)` / `entity_alpha(handle, alpha)` | opacity, clamped 0..1; 0 hides the body | 1 |
| `refii_mod_entity_scale` / `Refii.entity_scale(e, s)` / `entity_scale(handle, scale)` | the body's scale (positive; the entity needs a placement) | 1 |
| `refii_mod_entity_saturation_mask` / `Refii.entity_saturation_mask(e, on)` / `entity_saturation_mask(handle, on)` | "draw desaturated" | off |
| `refii_mod_entity_drawable` / `Refii.entity_drawable(e, on)` / `entity_drawable(handle, on)` | off takes the body out of the drawn set; the entity keeps simulating | on |

- Each goes through the game's own appearance setter, so both looks draw it and co-op replicates
  it wherever the game replicates the entity. Restating an unchanged value costs nothing.
- **Tint:** 1 is untouched, `{1, 0.3, 0.3}` reads red, `{0.4, 0.4, 0.4}` darker; above 1
  brightens. `a` is stored and replicated but no shader reads it — opacity is `entity_alpha`.
  Untint only what you tinted: the game tints corpses and ghosts itself.
- **A game-driven value wins where the game restates it.** A Blades sword restates its scale and
  alpha every frame, and some deaths ramp alpha to 0; a value stated on such an entity holds for
  one frame. Do not fight those paths.

## The target lock

What a held LT states, as calls, for player 1 or 2:

- **`lock_target(player, entity = 0)`** (`refii_mod_lock_target` / `Refii.lock_target(p [, e])`):
  with an entity, the targeting component's own SetTarget (it refuses an entity not in the world,
  and raises `target_changed`), then the force-lock flag, which the game's targeting update turns
  into the TARGET_LOCK mode while a target exists and re-selects one while none does, then the
  look-at flag every lock camera reads. Entity 0 is the game's own pick.
- **`release_lock(player)`**: clears the force flag and the look-at flag; the game drops the lock
  on its next targeting pass. It cannot end a lock the player is holding LT for.
- **`switch_target(player, left)`**: the camera's own "better target in this direction" — it keeps
  the old target when nothing better stands that way; needs a current target. Answers the target
  held afterwards.
- **`locked_target(player)`** (Godot: `query locked_target P`): the target while the lock mode or
  the force flag holds; 0 otherwise.
- **`wheel_switches_target(hero)`** (Lua: state it each tick the lock holds; Godot: sticky until
  `on = false` or the lock ends): while it holds and the hero is locked under the keyboard-and-mouse
  camera, mouse movement never switches target and each wheel notch is one switch (down the next
  target right, up the next left). A pad is untouched.

Souls Combat's toggled lock is these calls, in Lua (`mod/lockon.lua`); see also
[the look-at button](cutscenes.md#the-look-at-button).

## Status effects

`apply_status(entity, kind, seconds, level = 0, source = 0)` puts a status on an entity as the
spell's own hit would, through the installer each spell calls, so `status_effect` fires; an entity
not in the world is refused.

| Kind | Status | Notes |
|---|---|---|
| 1 | electrified | Lightning's installer, 0 damage |
| 2 | chaos | Chaos's installer; its effects and zero-damage chaos hit take their direction from the source's body |
| 3 | burning | Fireball's ignite, 0 burn damage |
| 4 | caught in a vortex | refused: a vortex holds a creature itself |

- `source` 0 makes the entity its own source. No damage is dealt: these are statuses, not hits.
- `clear_status(entity, kind)` ends one through the game's own remover — what its expiry and a
  death call — stopping the effects (the ended event fires). Vortex is refused.
- `has_status(entity, kind)` (Godot: `query has_status E KIND`) reads whether it holds.
- The `status_effect` event reports more kinds than a mod can apply: 4 vortex, 5 knocked down
  (every ragdoll fall), 6 mauled by the dog, 7 unconscious, 8 a hollow man crumbled to bones.

## Knockback and stagger

Both are one hit of the game's own shape through its one hit receiver, with no attacker and no
damage:

- **`knockback(entity, x, y, z, force = 1)`**: the Force Push's knockdown hit along the direction
  (`dir × force`). The victim takes the knockdown unless the creature cannot be knocked down.
  Whether the physics keeps the vector's length is unproven: the direction is read, the magnitude
  may be normalised away.
- **`stagger(entity)`**: the same hit without the knockdown, so the victim plays the flinch,
  blockable like a tap.

Both fire `hit` and `hit_landed` for every mod. Neither is offered to `hit_response`: a stagger a
mod states is already the mod's decision. Refused when the receiver drops hits.

## Movement speed

What the scripts' `Player.SetOverriddenMovementSpeedMultiplier` states, for player 1 or 2:

- **`set_speed_multiplier(player, factor)`**: the whole walking velocity is multiplied by
  `factor` while the override holds, so 0 holds the hero in place. Turning still happens, and
  rolls and blocks are actions that never pass through this velocity.
- **`clear_speed_multiplier(player)`**: ends the override.
- **`speed_multiplier(player)`** (Godot: `query speed_multiplier P` → `overridden`, `factor`):
  whether an override holds, and its factor; nil without a player component.

The game never clears the override itself: a script that set it keeps it until it clears it. Take
the override only while `speed_multiplier` says none holds, and give it back — Souls Combat's cast
recovery plants the caster at 0 for 0.6 s this way.

## Actions and commitment

An action is what an entity's body is doing: a strike, a roll, a scripted animation.

| Call | Answers |
|---|---|
| `current_action(e)` (Godot: `query current_action E` → `action`, `elapsed`, `interruptible`) | the action the entity's action manager runs; nil for none and once it was stopped. The handle is reused by the next action anyone plays: compare it only while this still answers it |
| `action_type(a)`, `action_name(a)` (Godot: `query action A` → `type`, `name`) | what the action is: its definition's `Type`, the game's `EScriptableAction` (0 PLAY_ANIMATION, 1 MELEE_STRIKE, 2 STRAFE — the roll, 3 TALK_TO_ENTITY, 16 BATCH, 27 SHOOT, 48 LOOP, 70 MELEE_STRIKE_UNSHEATHE_AND_SHEATHE_WEAPON, … 88 DIG) and that enum's name; -1 / nil for an action made before the mods loaded |
| `action_elapsed(a)` | the action's clock in seconds (what writes it is unproven; the mods time commitment by it as elapsed animation seconds) |
| `action_interruptible(a)` | whether the action has let the next one of the same priority in. Strikes, rolls and plain blocks play at priority 6; the preemptive block and the parry at 8 need no permission; a higher priority always passes |
| `allow_action_interrupt(a)` | grants it — exactly what the animation's own `ALLOW_ACTION_INTERRUPT` event does |
| `living(e)` (Godot: `query living E`) | in the world and, for a creature, not yet died — the target selector's own test; a lock that should drop when its target dies asks this |

`melee_swing` and `anim_event` carry `action`, `action_type` and `action_name` too. An action's
script name (the table's name in the quest or AI script) is not kept anywhere the game reads, so
it is not answered.

**A committed swing**, the Souls Combat way: cancel the `anim_event` named
`ALLOW_ACTION_INTERRUPT` for the strike — matched on `e.action`, the action `melee_swing` handed
you (the same handle on both events), not on `current_action`, which can differ — and call
`allow_action_interrupt` when your own clock says so, while `current_action` still answers that
action.

## A live cast: its parts and lifecycle

`spell_parts(serial)` (Godot: `query spell_parts SERIAL`) answers what a cast put in the world, by
the serial its hits and events carry (`spell_created`, `vortex_started`, `fireball_impact`, a hit's
`spell_serial`, `spell_serial(spell)`). Every part is read live and is nil once its thing is gone:

| Part | Meaning |
|---|---|
| `spell` | the spell object while it is still this cast's |
| `type`, `targeted`, `level`, `caster`, `caster_player` | as the factory stated them (the last 32 casts are kept) |
| `vortex`, `anchor`, `origin` | a Vortex cast: its live component, its entity, and the twister's point as it moves |
| `anchor`, `exploded`, `origin` | a Fireball cast: the shot while in the world, whether an impact detonated it, the impact point (else the shot's position) |
| `swords = { … }` | a Blades cast: the live swords, at most 8 |
| `origin`, `has_origin` | falls back to the caster's position; `has_origin` says whether any was read |

**The serial is a cast's identity**, not the spell handle: a spell object's handle is reused by
the next cast of the same class.

The lifecycle is three events:

- `vortex_started`, inside the factory and before `spell_created` (a vortex's spell object
  finishes at once, so `spell_parts` is the only way back to it);
- `vortex_ended`, before the vortex shuts down, with the twister's last point;
- `fireball_impact`, after each impact, with `detonated` and `hits` — so a blast that found
  nothing alive is one test: `e.detonated and e.hits == 0`.

**Casting.** `Refii.cast_spell(caster, type, targeted, target, level [, pos [, dir]])` casts a real
spell through the game's factory, no will cost, returning the spell's handle and its serial (nil
for no cast). It starts at `pos = {x, y, z}` facing `dir`, the caster's own position and facing
when absent: Lightning draws its bolt from there to the target, Fireball spawns its shot there,
Force Push shoves the target away from there — so a cast started ON a victim is Fable III's
position cast. Godot: `cast_spell` / `cast_spell_at`.

Spell Weaving's vortex, fireball and Blades looks are these three events plus `spell_parts`, in
pure Lua (`mods-src/spell_weaving/mod/init.lua`).

## Custom spells

The spell factory has exactly eight spell types — 0 Lightning, 1 Fireball, 2 SlowTime, 3 Blades,
4 Vortex, 5 Chaos, 6 ForcePush, 7 RaiseDead — and the wheel, the abilities book and every hit
record carry the type as that index. A mod cannot add a ninth class. **A custom spell stands in for
one of the eight**: it keeps that type's mechanics, slot, ability record and levels, and gets its
own name, description, icon, damage, look and Lua behaviour. Two mods replacing the same type: the
later in the load order wins, and the mount log says so.

One file per spell, `spells/<id>.json`:

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

| Field | What it does |
|---|---|
| `replaces` | required: a type name (any case, spaces ignored; `Swords`, `DeadRising` also resolve) or 0..7 |
| `name` | the type's name on the wheel and in the book, in every language |
| `description` | the book's description, one text for every level |
| `icon` | a package path served as the wheel icon (a `.png` is converted) |
| `card` | the same for the abilities book's tarot card |
| `damage` | a multiplier on `e.damage` of every `hit` of the type |
| `look` | one `fx/recipes.json` recipe body (`base`, `tint`, `attach`, `pieces` …), shown from `spell_launched` for `seconds` (default 8) |
| `on_cast` | Lua statements run on `spell_launched` of the type, `e` in scope (`caster`, `player`, `slot`, `type`) |
| `on_hit` | Lua statements run on `hit_landed` of the type, `e` in scope (`victim`, `attacker`, `damage`, `spell_level`, `spell_serial` …) |

The shipped names behind each type, for a mod that writes its own text rows or art:

| Type | Text tag `<X>` | Wheel icon stem | Tarot card stem |
|---|---|---|---|
| 0 Lightning | `LIGHTNING` | `will_shock` | `shock_spell` |
| 1 Fireball | `FIREBALL` | `will_blast` | `blast_spell` |
| 2 SlowTime | `SLOWTIME` | `icon_will_time_control` | `time_spell` |
| 3 Blades | `SWORDS` | `will_sword_of_damocles` | `swords_spell` |
| 4 Vortex | `VORTEX` | `will_vortex` | `vortex_spell` |
| 5 Chaos | `CHAOS` | `will_chaos` | `chaos_spell` |
| 6 ForcePush | `FORCE_PUSH` | `will_force_push` | `forcepush` |
| 7 RaiseDead | `DEAD_RISING` | `will_dead_rising` | `dead_rising` |

Name tags are `GUI_SCREEN_ABILITIES_WILL_<X>_NAME_LEVEL_01..06` (06 is the wheel's), descriptions
`GUI_SCREEN_ABILITIES_WILL_<X>_DESC_LEVEL_01..05`; icons are served at
`art/gui/wheelicons/abilities/<stem>.tex` and cards at `art/gui/abilitytarot/<stem>.tex`.

`on_cast` and `on_hit` run protected; an error lands in `RefiiModErrors` labelled
`<mod>/spells/<id>`. A body that does not compile is named in the mount log and left out.
Everything the file does not state is left as the replaced spell ships it. Editor:
`RefiiModApi.SetSpell(dict)`, `RemoveSpell(id)`, `ListSpellTypes()`. The **Custom spell** starter
writes Frost Nova.

## Sound

The game plays a cue by name through one path — the body of the scripts' `Sound.PlayEvent`:

- **`play_sound(cue, entity, tag = "")`**: on the entity's sound emitter; an entity without one (a
  prop) plays at its position instead. True when a voice started — an unknown cue starts none.
- **`play_sound_at(cue, x, y, z, tag = "")`**: at a point. Eight point emitters are kept; the
  ninth play frees the oldest, ending whatever it still played.
- **`stop_sound(entity, tag, fade_ms = 0)`**: every voice on the emitter with that tag stops over
  `fade_ms` (the scripts pass 3000). Entity 0 walks the point emitters. The tag given at play time
  is the handle: the game has no per-voice stop.
- **`play_music(name)`** / **`stop_music(name)`**: `SoundTools.PlayMusic` / `StopMusic`'s own
  bodies; the stop fades over the game's 2000 ms.

There is no volume argument: the cue's own volume and attenuation apply.

**Cue names** are event names in the audio banks (`.adb` files under the extracted `audio/`
folder; `music.adb` holds `MUSIC_MENU`, `MUSIC_HERO_DEATH`, …; `SE_*` are effects, `atmos_*` banks
the ambiences). The quest scripts show which cues play where (`Sound.PlayEvent(e,
"SE_HERO_HILL_WHITEOUT", "")`, `SoundTools.PlayMusic("MUSIC_QC030_IN_WAREHOUSE")`). A bank must be
loaded for its cues to play: the global ones (globals, music, music_3d, expressions, pub_games,
crescendo) always are; a level's own banks come with the level.

## Animation

The shipped AI plays a named animation as a scripted action
(`Action.SetCurrentAction(entity, { Type = EScriptableAction.PLAY_ANIMATION, Anim = name, … })`),
and the game's own evaluator plays it, so the pose shows in Godot with nothing added.

- **`play_animation(entity, name, loops = 1, blend_seconds = 0.1, speed = 1)`**: that table, run
  in the game VM; `loops` above 1 wraps it as `PlayLoopedAnimation` does (a BATCH with
  `LoopAction` and `NumLoops`). True when the chunk ran: a higher-priority action (combat, a
  cutscene) keeps the body, which the call cannot see.
- **`stop_animation(entity)`**: `Action.FinishScriptedActions(entity)`, what the behaviours call
  to cut a played animation.

**Animation names** are GDB animation record names the scripts use (`"DrunkOutOf"`,
`"GreetingPosh"`, `"HeroHillWhiteout"`…). The quest and AI scripts list every name the game plays,
and the Studio's model view lists a model's bound animations. A name is letters, digits and `_`; a
name the skeleton has no clip for plays nothing.

## Opinion and deeds

A villager's opinion of a hero is five axes: 0 love, 1 fear, 2 attractiveness, 3 morality,
4 respect, each -1..1. Each villager keeps one set per hero (see
[Handles and players](handles-and-players.md#each-villagers-opinion-of-each-hero)).

- **`opinion(villager, p)`** → love, fear, attractiveness, morality, respect; nil when it holds no
  opinions.
- **`modify_opinion(villager, hero, axis, delta)`**: the game's own `ModifyAxisValue`, clamped to
  [-1, 1]. With player two's hero the write lands in player two's set; `opinion_modified` fires
  with that player.
- **`add_deed(villager, hero, deed)`**: the deed message the game's own opinion system takes, so
  the record's strength and kind and whether this villager cares are the game's rules;
  `opinion_deed` fires. `deed` is the Deed record's hash (`Refii.hash("Generic_Scary")`). Refused
  when the hash names no record. Whether the message reaches this villager alone is unproven.

**Deed names** are GDB records (`Generic_Scary`, the `Deed*` records); the Studio's GDB browser
lists them.

## NPC orders

A creature is ordered about the way the shipped quests and AI scripts order it — each call is the
game's own entry, run in the game VM against the entity, so the AI mind sees the order as a
quest's. All six return true when the chunk ran; a behaviour the mind runs later (combat, a
cutscene, a group mind's job) may retake the body. Every target is an entity — player two's hero
is its own entity, never "the hero".

| Call | The game's own entry | Notes |
|---|---|---|
| `npc_follow(entity, target, radius = 2)` | `Follow.FollowEntity` | `radius` ≤ 0 is the quests' 2 |
| `npc_go_to(entity, x, y, z)` | `ScriptFunction.StartScriptControlledMovement` | not waited for; `npc_stop` ends it |
| `npc_flee(entity, from)` | `Navigation.FleeFromEntity` at sprint | one order: the body runs until the navigator is done or the mind takes it back |
| `npc_attack(entity, target)` | `CombatRegister:SetScriptTarget` | enemy relationship plus script target, taken before any other; a dead or non-combatant entity is refused by the shipped function (the chunk still ran) — check `relation` after |
| `npc_set_hostile(entity, other, on)` | `Faction.AddTemporaryEntityRelationship` / `RemoveTemporaryEntityRelationship` | `Refii.npc_set_hostile(e, Refii.player_hero(2), true)` is hostility to player two alone |
| `npc_stop(entity)` | `Follow.StopFollowing`, `StopScriptControlledMovement`, `ClearScriptTarget` | the mind then picks its next behaviour itself |

`relation(a, b)` answers 1 neutral, 2 ally, 4 enemy — the faction rule.

## Time and weather

- **`set_time_of_day(hours)`**: the game clock to `hours` (0..24; 13.5 is half past one) through
  `Timing.SetTimeOfDayAbsolute`. Online the host's clock is everyone's. `time_of_day` fires from
  the clock's next update. (`Timing.SetTimeOfDay`, the quests' call, takes HH.MM; ours takes
  decimal hours.)
- **`time_of_day()`** (Godot: `query time_of_day` → `hours`): the clock, 0..24.
- **`set_weather(theme, weight = 1, seconds = 1)`**: the game's environment theme — its weather
  and sky look — blended as the quests blend it, so `weather_theme` fires and a handler may change
  or cancel it. `weight` is clamped to [0, 1]; 0 blends the theme out. Theme names are `EnvTheme*`
  records (`EnvThemeWestcliffNight`, `EnvThemeTurnOnRain`, `EnvThemeCemeterySummon`, …); the
  Studio's GDB browser lists them all.

The Dynamic Sky's weather (the `dynamic_weather` event) is Godot's own, decided on the host —
Auto, or a set weather — and adopted by an online joiner; `set_weather` states the game's theme,
which the sky reads like any other. In co-op, run the blend where `world_here()` is true and let
the joiner's copy follow.
