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
- [Appearance (Murgo's Mirror)](#appearance-murgos-mirror)
  - [Mod morphs](#mod-morphs)
- [NPC orders](#npc-orders)
- [Time and weather](#time-and-weather)
- [Items, weapons, expressions and spells](#items-weapons-expressions-and-spells)

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
  target right, up the next left). A pad is untouched. **Sticky from Lua and native too:**
  `Refii.wheel_switches_target(hero, true)` / `refii_mod_wheel_switches_target_sticky(ctx, base,
  hero, 1)` state it once and the guest tick restates it until `false` / 0 or the lock ends — the
  same store a Godot script's call lives in (at most four heroes).
- **`lock_lost`** (event): the game dropped its TARGET_LOCK mode — whichever of its drops ended it
  (the target died or left reach, the force flag was released), raised at the drop and only when the
  mode was really held: `player`, `hero`, `target` (what the targeting component still holds). The force flag
  stays as it was: with it set, the targeting update re-selects and locks again when a target turns
  up, so a toggled lock that should end with nothing to lock releases on this event.

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
the override only while `speed_multiplier` says none holds, and give it back. To keep a hero in
an action rather than stop its walking, cancel `action_move_interrupt` instead — Souls Combat's cast
recovery holds the cast that way.

### Sprint, turning, friction and the parry, per hero

- **`Refii.sprinting(p)` / `Refii.end_sprint(p)`** (Godot `end_sprint(player)`, `query sprinting
  P`): the sprint component's own flag, and the sprint button's release as the game runs it — the
  flag cleared, the sprint movement stopped. A held button sprints again only on its next press.
  `Player.SetPlayerSprintingAllowed` and `Debug.SetHeroSprintingEnabled` never end a held sprint;
  this does.
- **`Refii.set_locomotion(p, {MaxTurnSpeedStatic = 720, MaxTurnSpeedRunning = 540,
  FrictionCoefficient = 9})`**: each hero's locomotion component copies the global
  `HeroLocomotionData*` table into its own copy when it loads one (at creation, indoors/outdoors, a
  movement-mode change) and the game reads that copy. This writes THAT hero's copy, live, and holds
  the value across the game's next copy, so two players can move differently. Any of the ten fields
  the game copies; `false` stops holding one and puts back what the game copied;
  `Refii.locomotion(p, key)` reads it. A locomotion state's own `MovementParams` (the Idle state's
  1°/s turn cap) still win while that state is active — they are the state's content, not the
  hero's.
- **`Refii.set_parry(p, window, wedge)`**: each hero's player component copies
  `HeroCombatBalanceParams` when it is made and the parry reads that copy: this writes that hero's
  `InputWindowSecondsForParry` (seconds) and `MeleeParryWedgeAngle` (degrees), live, and holds them
  for the player's next hero; `Refii.parry(p)` reads both. Patching the global tables reaches only
  copies made after the patch, and every hero alike.

## Actions and commitment

An action is what an entity's body is doing: a strike, a roll, a scripted animation.

| Call | Answers |
|---|---|
| `current_action(e)` (Godot: `query current_action E` → `action`, `elapsed`, `interruptible`) | the action the entity's action manager runs; nil for none and once it was stopped. The handle is reused by the next action anyone plays: compare it only while this still answers it |
| `action_type(a)`, `action_name(a)` (Godot: `query action A` → `type`, `name`) | what the action is: its definition's `Type`, the game's `EScriptableAction` (0 PLAY_ANIMATION, 1 MELEE_STRIKE, 2 STRAFE — the roll, 3 TALK_TO_ENTITY, 16 BATCH, 27 SHOOT, 48 LOOP, 70 MELEE_STRIKE_UNSHEATHE_AND_SHEATHE_WEAPON, … 88 DIG) and that enum's name; -1 / nil for an action made before the mods loaded |
| `action_elapsed(a)` | the action's clock in seconds (what writes it is unproven; the mods time commitment by it as elapsed animation seconds) |
| `action_interruptible(a)` | whether the action has let the next one of the same priority in. Strikes, rolls and plain blocks play at priority 6; the preemptive block and the parry at 8 need no permission; a higher priority always passes |
| `allow_action_interrupt(a)` | grants it — exactly what the animation's own `ALLOW_ACTION_INTERRUPT` event does |
| `allow_action_interrupt_at(a, seconds)` (native `refii_mod_allow_action_interrupt_at(ctx, base, action, seconds)`; Godot `allow_action_interrupt_at(action, seconds)`) | grants it once the action's own clock (`action_elapsed`) reaches `seconds` — at once when it already has, else on the first guest tick it does, with no polling. Dropped, ungranted, when the action stops, its clock runs backwards (the handle is another action's now) or a minute of ticks passes; a second call restates the time; 32 may wait at once |
| `living(e)` (Godot: `query living E`) | in the world and, for a creature, not yet died — the target selector's own test; a lock that should drop when its target dies asks this |

`melee_swing` and `anim_event` carry `action`, `action_type` and `action_name` too. An action's
script name (the table's name in the quest or AI script) is not kept anywhere the game reads, so
it is not answered.

**A committed swing**, the Souls Combat way: cancel the `anim_event` named
`ALLOW_ACTION_INTERRUPT` for the strike — matched on `e.action`, the action `melee_swing` handed
you (the same handle on both events), not on `current_action`, which can differ — and call
`allow_action_interrupt_at(e.action, seconds)` with the time the strike commits to: the engine
grants it when the strike's clock gets there, or drops it if the strike ends first.

**A refused melee press refuses its release.** Cancelling an `ability_request` of type 16
(`Refii.REQUEST_MELEE_PRESSED`) refuses that press; the bus then raises the same entity's next
release (17, `REQUEST_MELEE_RELEASED`) already cancelled and refuses it whatever a handler says, so
the game never sees half a pair. A mod no longer keeps the press itself.

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

The lifecycle is four events:

- `vortex_started`, inside the factory and before `spell_created` (a vortex's spell object
  finishes at once, so `spell_parts` is the only way back to it), with `damage` (one hit on a
  creature it holds, before the creature's own damage factor) and a writable `lifts`. Every half
  second the vortex's update asks how it takes each creature in reach: lift it (the capture
  below) or shudder it — the one hit per cast it gives a creature it cannot lift, a hero or a
  creature not flagged liftable, carrying the cast's `serial` and `tag`. `e.lifts = false` answers
  "cannot lift" for every creature: the vortex picks none up and passes through, each creature it
  reaches taking that one hit (props are still lifted). Online the deciding machine's answer holds
  on both machines (below, *One cast, one identity online*) — a mod states it where it decides the
  cast and does nothing for the other machine's copy;
- `vortex_caught`, each time the vortex catches something (`caught` true) or lets it go (false),
  whether or not it ever hit it, with the cast's `serial` and `tag`; the in-vortex `status_effect`
  (kind 4) the capture starts carries the same `serial` and the caster as `source`. The capture is
  the update's one call for EVERY entity it takes — creatures (its creature cap) and the props it
  lifts (the world's prop cap) alike — so a mod acting on creatures only tests the victim itself
  (`Refii.creature_tier(e) ~= nil`: the creature component the vortex's own classification reads).
  `Refii.vortex_held(serial)` (Godot: `query vortex_held SERIAL`) lists who it holds now;
- `vortex_ended`, before the vortex shuts down, with the twister's last point (the shutdown then
  lets go of everyone it held, each a `vortex_caught` with `caught` false);
- `fireball_impact`, after each impact, with `detonated` and `hits` — so a blast that found
  nothing alive is one test: `e.detonated and e.hits == 0`.

**A spell's own numbers.** `Refii.spell_balance(caster, type, level)` (Godot: `query spell_balance C T L`)
answers one power of a spell as its Targeted class's own data read loads it for that caster — the
values the cast itself reads: the status its Start installs on each victim (`status_seconds` before
the victim's own factor, plus a uniform draw in [0, `status_spread`): Lightning's ElectrifyDuration
and 5, Fireball's CreatureBurnTime and 0, Chaos's Duration and 2), Vortex's `duration` and Blades'
`swords` per cast.

**Casting.** `Refii.cast_spell(caster, type, targeted, target, level [, pos [, dir [, options]]])` casts a real
spell through the game's factory, no will cost, returning the spell's handle and its serial (nil
for no cast). It starts at `pos = {x, y, z}` facing `dir`, the caster's own position and facing
when absent: Lightning draws its bolt from there to the target, Fireball spawns its shot there,
Force Push shoves the target away from there — so a cast started ON a victim is Fable III's
position cast. `options = { tag = n, damage_scale = f }` stamps the cast (below) and scales every
hit carrying its serial, where the hit is decided; `options.at` casts at a target or a point by the
spell's own rule ([below](#casting-at-a-target-or-a-point)). Godot: `cast_spell` / `cast_spell_at` /
`cast_spell_with`.

**A cast's identity.** `spell_launch` has a writable `tag` (a number; 0 none): the spell that launch
creates carries it, and so does every later event of that cast — `spell_created` (with the launch's
`slot`), `vortex_started`, `hit`, `hit_landed`, `fireball_impact`, `status_applying` — by the cast's
serial. The factory takes that serial from its registry's counter before it builds the spell, so a
targeted Lightning's bolt, which lands inside the factory, carries the tag too. `cast_spell`'s
`options.tag` stamps the same way, and so do `summoned` (the summoning spell's serial: Raise Dead's
bind states it, a Blades sword set holds it) and `spell_ended`.

**One cast, one identity online.** In online co-op the game makes a cast's spell on BOTH machines,
each with its own serial: the machine deciding the caster replicates its cast action's release
(message 0x5B area, 0x5C targeted, 0x5E a stated spell cast) and the other machine's copy of the
action launches through its own factory. The core pairs the two: a cast by its replicated release —
the caster's network id and the fields the message carries, matched in the order the game's
guaranteed, sequential channel starts them — and its spells by creation order. Then:

- the machine not deciding the caster takes the decider's `tag` into its own spell, so every later
  event of that spell carries it;
- `spell_created` carries `host_serial`, the host's serial of the spell (the serial itself on the
  host; on the joiner 0 until the pair forms — the announcement can arrive just after the spell);
- a relayed `hit_landed` arrives on the joiner with the joiner's own `spell_serial` and that spell's
  `tag` (a hit for a spell the host announced and the joiner has not made yet waits up to a second);
- the machine not deciding the caster takes the decider's spell state too: a vortex whose
  `vortex_started` set `lifts = false` lifts no creature on the other machine's copy either. Until
  the pair forms the copy takes no creature it could lift: its half-second check passes over them
  and looks again at the next, so a no-lift vortex never holds (or hits) a creature on the copy,
  and a lifting one starts lifting at most one check (half a second) after the pair forms — at
  once when the pair formed before the copy's first check. Creatures it could never lift shudder
  as usual. A copy whose cast cannot pair (nothing came from the peer within the eight seconds an
  unpaired cast is kept) follows its own `lifts` from then on, and a pair that forms later than
  that lets go of any creature the copy caught, through the game's own let-go. Both ways: the
  host's casts are decided on the host, player two's on the joiner. The game delivers a vortex's
  hits on the host alone, so the joiner's copy of a host's cast hits nothing either way, while the
  host's copy of player two's cast is the one whose hits count — it passes through as the joiner
  decided;
- `Refii.host_serial(serial)` and `Refii.local_serial(host_serial)` (native
  `refii_mod_host_serial` / `refii_mod_local_serial`; Godot `query host_serial SERIAL` /
  `query local_serial SERIAL`) translate either way — the serial itself on the host, alone and in
  local co-op; nil / 0 / ok false on the joiner while unpaired.

A spell no cast action made (`cast_spell`, a creature spell outside one) has no copy on the other
machine and never pairs.

**A cast's end.** `spell_ended` fires when the spell registry's update stops a spell and takes it out
— the one place the game removes a live spell: `serial`, `type`, `caster`, `tag`, `level`, `reason`
(0 its own update finished it, 1 it was stopped from outside first). `spell_charge_ended` fires once
per magic charge on the machine that plays the hero, `released` true when the button was let go into
a cast (the cast's `spell_launch` follows). `melee_swing_ended` closes each committed strike with the
`hits` it landed (0 a whiff); it is relayed like `melee_swing`.

**Holding a cast.** A cast's animation lets the stick cancel it from its PERFORM on (an
ALLOW_MOVEMENT_INTERRUPT anim event). `action_move_interrupt` fires every frame the game's own test
answers that movement may interrupt an entity's current action; `return true` holds the action — the
stick neither cancels it nor moves the body — for as long as the handler keeps cancelling, so a mod
holds a cast's recovery to the animation instead of a timer.

**Charging a chosen spell.** `Refii.charge_spell(p, type, held)` (`refii_mod_charge_spell(ctx, base,
player, type, held)`; Godot `charge_spell(player, type, held)`) charges spell `type` through the
player's own magic charge: `held` true holds the magic button with the spell lent to every tier the
hero has it and the tiers above emptied; `false` lets the button go and the cast goes out as the game
casts it. The tiers are lent through the slot table's own writer with its "not a choice" flag — no
`spell_pick`, nothing sent to the session — and come back the same way once the charge is over (the
charge ended without a release, the cast's last slot out, a second after a release that charged
nothing); a real pick made meanwhile is what comes back for its slot.

**Statuses.** `status_applying` fires before a spell's own installer puts electrified, chaos or
burning on an entity (a cast's hit, `apply_status`): `seconds` writable, `cancel` puts nothing on.
Chaos fires where it is decided, and a written `seconds` is what the game sends the co-op partner.
`status_effect`'s start carries the installing cast's `serial` and `source`.

Spell Weaving is this section in pure Lua (`mods-src/spell_weaving/mod/init.lua`): each woven
launch writes a tag, its partner casts carry that tag negated with a `damage_scale`, their statuses
are shortened on `status_applying`, and the looks follow `vortex_started` / `vortex_ended`,
`summoned`, `fireball_impact`, `spell_ended` and `spell_parts`.

### Casting at a target or a point

`Refii.cast_spell`'s `options.at` — an entity handle, or `{x, y, z}` — has the core place the
cast by the spell class's own rule for that place, so a mod never works out a start
position: the cast is the Targeted class (`targeted` true, `target` 0 or the same entity), `pos` is
the ORIGIN it comes from (the caster's position when absent) and `dir` its facing when the origin
stands on the place (the caster's facing when absent). The game has no cast-at mode of its own
(`m_CastDirMode` is only Targeted or Area); these are its Starts' own placements:

| Spell | at an entity | at a point |
|---|---|---|
| Lightning | starts ON the point its bolt strikes (the target's position, or its body point when the power's `CreateImpactFX` is off, 0.25 up): a bolt of zero length, nothing between to stop it | one 15.0 reach above the point, facing down: the bolt ends on the point, and the 50 % re-acquire matches only creatures within 1.35 of the start's height |
| Fireball | within the 1.0 its Start detonates at once, set back toward the origin (the blast's normal is the cast's direction) | refused: a shot detonates at once only on a target |
| Blades | its swords are made at the entity's position | made at the point |
| Vortex | its twister spawns on the entity (the start set back the 1.25 the twister spawns ahead) | spawns on the point |
| Chaos | starts ON the point its stream strikes | refused: chaos acts only on its target |
| Force Push | from the origin, shoving the target away from it | from the origin along the line to the point (25 reach) |
| Slow Time, Raise Dead | refused | refused |

A refusal returns nil and says why on the mod errors channel. Native: `refii_mod_cast_options`
`at_kind` (`REFII_MOD_CAST_AT_ENTITY` / `_POINT`), `at_entity`, `at_point`; Godot:
`cast_spell_with(..., { "at": handle_or_Vector3 })`; the line: `cast_spell_with ... [at E | at X Y Z]`.

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
  The wrapper is a `LOOP` action, not a BATCH: `{ Type = EScriptableAction.LOOP, LoopAction = …,
  NumLoops = loops, KillFXOnFinish = true }`, so the clip plays `loops` times.
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

## Appearance (Murgo's Mirror)

Each hero's LOOK — the body morphs and what villagers judge — is either the game's own (automatic:
the hero's stats drive it, as shipped) or a stated set of inputs (manual). The Mirror sets it, and a
mod can read and set the same look through the same path.

| Call (native / Lua / Godot) | What it does |
|---|---|
| `refii_mod_appearance` / `Refii.appearance(p)` / `query appearance P` | the look in force: `automatic`, `tan`, `fat`, `height`, `strength`, `skill`, `will`, `morality`, `purity`, `age` — the stated inputs when manual, the game's own when automatic; nil without a hero to read |
| `refii_mod_set_appearance` / `Refii.set_appearance(p, t)` / `set_appearance(player, inputs)` | manual with the named inputs; an input not named keeps how the hero looks now |
| `refii_mod_set_appearance_automatic` / `Refii.set_appearance_automatic(p, on)` / `set_appearance_automatic(player, on)` | the game's own look again (true), or back to the manual inputs held (false) |
| event `appearance_changed` | `player`, `automatic`, `hero`: the look in force changed (manual on or off, or a manual input moved) |

- **Ranges:** `height` -1..1, `age` 18..60 (years), the rest 0..1; `morality` and `purity` are
  normalised over the hero's own range (0 evil / corrupt, 1 good / pure). Out-of-range values are
  clamped.
- **What it changes:** manual inputs drive every morph the game draws from the weight function —
  body shape, height, the aged and evil/good skin and eye layers, will lines — and the inputs the
  villagers judge by: the appearance opinion (attractive/ugly, scary/funny; `fat`, `tan`,
  `strength`, `age`) and a reaction line's appearance checks (`fat`, `strength`, `height`, `tan`,
  `will`, `purity`, `age`). Nothing is written into the hero's stats: combat, fat gain and loss,
  renown and Lua's `Stats.Get*` keep the real values, and automatic shows them again.
- **Both players.** Each player has their own look. It lives in the player store (the
  `character_morphs.*` keys, owned by the machine that plays that hero), so both co-op machines
  draw both heroes the same and the host's villagers judge the joiner's look. A call for a player
  played on the other machine reaches that machine.
- **It is saved with the hero.** The look is saved values in the player store, written with the
  save and read back when that save loads, so each save keeps its own look; a save made without one
  (or a new game) loads in the game's own look. The Mirror's settings (`character_morphs` /
  `character_morph_<input>` for the machine's first player, `character_morphs2` /
  `character_morph2_<input>` for local co-op's player two) follow the loaded hero's look, so the
  Mirror opens on it. There is no session-only form.
- Inside an event copy (`event_replica()`) the write is dropped, as for every player-store write.

```lua
-- A hero who eats this mod's own pie looks better fed, until the Mirror or automatic says otherwise.
-- item_used is relayed: the copy's write is dropped, so the pie counts once.
Refii.on("item_used", function(e)
    if e.player ~= 0 and e.definition == Refii.hash("MyMod_PieOfPlenty") then
        local look = Refii.appearance(e.player)
        Refii.set_appearance(e.player, { fat = math.min(1, (look and look.fat or 0) + 0.25) })
    end
end)

Refii.on("appearance_changed", function(e)
    Refii.log("player " .. e.player .. (e.automatic and " looks as they are" or " wears a stated look"))
end)
```

### Mod morphs

A mod can add MORPHS OF ITS OWN: a slider in the Mirror and a weight the game draws records at.
The game's one weight function asks each morph record for its `MorphType` and weighs types 1..18
(the stats); every other type weighs 0 — so a type of the mod's own is drawn at whatever weight
Refii answers for it, and only Refii answers it.

1. **Declare it** in `morphs.json` at the mod's root:

   ```json
   [
       { "name": "hollow", "label": "Hollow", "default": 0, "min": 0, "max": 1, "step": 0.05 }
   ]
   ```

   `name` is required (letters, digits, `_`); `label` defaults to the name, `min` 0, `max` 1,
   `default` `min`, `step` 0.05. The morph's TYPE is `morph_id("<mod>.<name>")`: FNV-1 of the name
   in lower case, top bit cleared, moved past 0x100 — never a shipped type. Two mods' morphs that
   hash alike are reported and the later one refused.
2. **Give it records** in a `.gdb.csv` (see [GDB and text](gdb-and-text.md#mod-morphs-in-rows)):
   `@morph:<name>` as the `MorphType` of a MorphTargets pair (a model blended in), a TextureMorphs
   layer (a texture laid over the skin), a SkeletalMorphs item (a `.mof` bone offset file) or a
   ParticleMorphs item (an emitter on a dummy), added to the heroes' lists.
3. **The Mirror shows a slider** for it after the stat sliders, in both modes — mod morphs do not
   wait for manual. A player edits their own; the weight is written as the slider moves, and
   Cancel puts it back.

| Call (native / Lua / Godot) | What it does |
|---|---|
| `refii_mod_morph_weight` / `Refii.morph_get(p, morph)` / `morph_get(player, morph)` · `query morph P MORPH` | the player's weight: the stored one clamped to the declared range, else the default; nil / NaN / `registered` false for a morph no enabled mod declares |
| `refii_mod_set_morph_weight` / `Refii.morph_set(p, morph, w)` / `morph_set(player, morph, weight)` | sets it, clamped; the hero is redrawn |
| `refii_mod_morph_id` / `Refii.morph_id(name)` / `morph_id(name)` | the type as `"0x…"` (a `uint32_t` natively) |
| `refii_mod_morph_list` · `refii_mod_morph_registered` | natively: the registered ids, and one's default and range |
| event `morph_changed` | `player`, `morph` (`"0x…"`), `weight`, `hero`: a weight moved — the Mirror, a mod, either machine |

- **A morph is named** `"<mod>.<name>"` or by its id `"0x…"`. A Lua number is a 32-bit float and
  cannot hold an id, so ids are strings on Lua and Godot.
- **Per player, saved with the hero.** The weight is the player store's `morph.<id>` (the id in
  decimal): owned by the machine that plays the hero, the same on both co-op machines, and kept
  with that hero's own save. Player two's hero is given the game's morph component like every
  adult hero, so its records are drawn at player two's weights.
- **Both modes.** The hook answers a mod morph for either player's hero whether the look is
  automatic or manual, behind the game's own age gate (a child weighs 0). Other entities carrying
  the type weigh 0, as shipped.
- **What a record does at a weight** is the game's own rule for its kind: a MorphTargets pair
  blends `weight × (target − base)` (unclamped, so a range past 0..1 exaggerates); a TextureMorphs
  layer lays its texture over the original at `weight` opacity (by its `BlendType`); a
  SkeletalMorphs file scales and offsets its bones by `weight`; a ParticleMorphs item is ON while
  the weight lies in its `StartAtMorphStrength..EndAtMorphStrength` (and above 1e-4) and off
  otherwise — it does not fade. A change redraws the hero: its appearance is flushed, which rebuilds
  its skeletal composite and its particle morphs, and its texture layers are rebuilt.
- **Live reload** re-reads `morphs.json` and the rows; a MorphTargets pair added to a hero who is
  already built reaches it when the game builds its parts again (a level load).

```lua
-- A hero who drinks this mod's tonic turns a little more hollow, on whichever machine plays them.
Refii.on("item_used", function(e)
    if e.player ~= 0 and e.definition == Refii.hash("MyMod_HollowTonic") then
        Refii.morph_set(e.player, "hollow.hollow", (Refii.morph_get(e.player, "hollow.hollow") or 0) + 0.25)
    end
end)

Refii.on("morph_changed", function(e)
    if e.morph == Refii.morph_id("hollow.hollow") then
        Refii.log("player " .. e.player .. " is " .. math.floor(e.weight * 100) .. "% hollow")
    end
end)
```

The bundled **Hollow** mod (`Fable2/mods-src/hollow`) is the worked example: one morph, in rows
three particle morphs (the Hollow Man's eye glow on both eye dummies, its wisps on the head), an
aged head pair per hero and twelve skin layers per hero, and a Godot script for what no record
carries (the body's colour).

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

## The inventory as a screen reads it

Everything the game's own inventory pages show about what this machine's hero carries, read at the
call, and what those pages do to an item. For a mod that draws its own inventory.

| Surface | Read | Act |
|---|---|---|
| Lua | `Refii.ask_host("inventory_items", fn)`: `fn` gets the answer on a later tick | `Refii.ask_host("inventory_action", fn, operation, definition)` |
| Godot | `host.inventory_items() -> Array` | `host.inventory_action(operation, definition) -> bool` |

Each stack is a Dictionary, in the game's order, without what the game's pages hide (`VisibleInGUI`
and an outfit none of whose pieces are carried):

| Key | Meaning |
|---|---|
| `definition` | `"0x…"`, the record that `use_item` and `equip_weapon` take |
| `count` | how many |
| `category` | the item's `Category`: 1 weapons, 3 gifts, 4 trophies, 5 food, 6 potions, 7 augments, 8 dyes, 15 furniture, 16 books, 17-22 hats, coats, shirts, gloves, trousers, shoes, 23 accessories, 25 outfits, 2 and 26 tools, keys and quest items |
| `name`, `description` | in the player's language |
| `icon` | the texture path; `load("game://" + icon.replace("\\", "/").to_lower())` |
| `model` | the item's model path, empty for none; `load("game://" + model.replace("\\", "/").to_lower())` is a `Mesh` |
| `pieces` | an outfit's model paths, one per piece it names, empty for anything else: an outfit has no `model` of its own |
| `rotation`, `rotation_axis` | the pose the item page turns it in; `host.item_pose(item, yaw)` makes them a `Basis` |
| `value` | `BasePrice`, 0 for none |
| `rating` | the item page's star rating |
| `usable` | the item's own `UsableFromInventory` test |
| `worn` | this hero wears it now; an outfit when every piece of it they carry is on |
| `weapon_class` | 1 melee, 2 ranged, 0 neither |
| `new` | not yet seen in a menu |
| `lines` | the item page's stat lines, each a Dictionary of `label` and `value`, already in the player's language |

`operation` is `use`, `equip`, `wear` or `outfit`, as the pages' own rows run them, or `remove`, which
empties the clothing slot of the item named. An outfit is no slot, so `remove` refuses one: take off
the worn items that are its pieces. Weapons in hand are `query equipped`.

## Items, weapons, expressions and spells

The quick menu's own actions, as calls, for player 1 or 2 — what a quick slot does when pressed:

| Call (native / Lua / Godot) | What it does |
|---|---|
| `refii_mod_use_item` / `Refii.use_item(p, def)` / `use_item(player, definition)` | uses an inventory item by definition (its `"0x…"` record hash): the item handle by record, the game's own Use on the hero, the handle released; the UI data generation moves |
| `refii_mod_equip_weapon` / `Refii.equip_weapon(p, def)` / `equip_weapon(player, definition)` | equips a weapon the inventory holds through the weapon set's own EquipWeaponByRecord; one already held is left as it is |
| `refii_mod_perform_expression` / `Refii.perform_expression(p, expression)` / `perform_expression(player, expression)` | StartExpression on the hero's performer for an expression it knows (its record); refused while one plays |
| `refii_mod_assign_spell` / `Refii.assign_spell(p, spell)` / `assign_spell(player, spell)` | puts spell type 0..7 (0 Lightning .. 7 Dead Rising) on every tier the hero knows, through the game's own assign — the d-pad, the UI and the net event follow |

- **Gated as the quick menu is.** Each runs only while the hero is alive, the d-pad is up and the
  game's own `CanPlayerOpenQuickMenu` says yes for that player (not mid-cutscene, not in a menu);
  otherwise it answers false and nothing happens.
- **Ungated.** `{ungated = true}` as the last argument (Godot `{"ungated": true}`; natively
  `refii_mod_quick_action(…, REFII_QUICK_UNGATED)`) skips the d-pad and `CanPlayerOpenQuickMenu`
  — a mod's own hotbar acts while the hero runs or swings — and is still refused for a dead hero
  and while any cutscene kind holds (`in_cutscene`). Godot's `use_item(player, definition,
  {"ungated": true})` is the per-player form of the Items page's `use_item(definition)`.
- **A tap or a hold.** `perform_expression` is a tap: the expression starts and is released at
  once, as a tap on the wheel is, and plays its basic form. `Refii.perform_expression(p, rec, true)`
  holds it: nothing releases it, so an extendable expression extends when the game's own one-second
  countdown runs out, and plays on until `Refii.release_expression(p)` — the performer's own end,
  the call the extend button's release (GameAction 25) makes.
- **The wheel as events.** `expression_wheel` (`player`, `open`, `highlighted`) fires when a
  player's wheel opens, closes, or highlights a different expression — no need to poll
  `selected_expression` every tick. `item_removed` (`entity`, `player`, `definition`, `count`,
  `remaining`) fires once per unit that leaves an inventory, by whatever path took it.
- **Both players.** `p` names the hero; player two's quick actions act on player two's hero,
  wherever the call is made. **An online joiner's `equip_weapon`** does not touch the joiner's weapon
  set (it is the host's replica): the pick crosses to the co-op kit (`CoopHeroKit.SetLocalWeapon`,
  under `REFII_MOD_HOST_HERO_KIND`), the kit carries it to the host, the host equips it and the held
  item replicates back — the same route the joiner's own weapon menu takes. The caller never cares.
- **What to choose from:** `Refii.expressions(p)` is the performer's known expressions
  (`{ record, id, uses }`; `id` is the record), `Refii.spells_known(p)` the spell types the hero can
  assign (known and offered on some tier — the quick wheel's own test), and
  `Refii.selected_expression(p)` the record the expression wheel highlights while it is up for that
  player (nil otherwise). Natively `refii_mod_expression(base, player, index, &record, &uses)`
  walks the list, `refii_mod_spells_known` answers a bit mask, `refii_mod_selected_expression` the
  record; in Godot `query expressions P`, `query spells_known P`, `query selected_expression P`.
- **Naming a record:** `Refii.record_string(def, "NameTag")` and `Refii.record_string(def,
  "IconGraphic")` read a GDB record's string property, inherited, through the game's own getter
  (`refii_mod_record_string(ctx, base, definition, field, out, size)`; `query record_string DEF
  FIELD`). A text tag goes through the game's text as usual. Nil for a record the database lacks or
  a property it has not got. `Refii.record_int(def, "Category")` and `Refii.record_int(def,
  "WeaponType")` read an integer or enum property the same way (`refii_mod_record_int(ctx, base,
  definition, field, &out)`; `query record_int DEF FIELD`): an inventory's items are category 2
  (potions) to 26, a weapon is category 1 with WeaponType 1..4 melee and 5..8 ranged. **An item's
  properties are on its components, not on the record an inventory states:** that record is the
  item entity, which names its components by reference properties — NameTag, IconGraphic, Category
  and UsableFromInventory are on `Refii.record_ref(def, "InventoryItemComponent")` (all 1728 item
  entities in the shipped database), WeaponType on `Refii.record_ref(def, "WeaponComponent")`. An
  expression's record carries its own NameTag and IconGraphic.
  `Refii.record_number(def, field)` reads a float property (the database's type 3) as stored, or an
  int / enum one as a number; `Refii.record_bool(def, field)` a bool property (type 0), true or
  false; `Refii.record_exists(def)` whether the record is in the database at all — natively
  `refii_mod_record_number(ctx, base, definition, field, &out)`, `refii_mod_record_bool(…, &out)`,
  `refii_mod_record_exists(ctx, base, definition)`; in Godot `query record_number DEF FIELD`,
  `query record_bool DEF FIELD`, `query record_exists DEF`. `Refii.record_ref(def, field)` reads a
  reference property (type 6): the record it names, as `"0x…"` — the hash the game itself looks up
  (`refii_mod_record_ref(ctx, base, definition, field, &out)`; `query record_ref DEF FIELD`).
- **A creature's rank:** `Refii.creature_tier(e)` (`refii_mod_creature_tier(base, entity)`; `query
  creature_tier E`) answers the bits its definition states — `Refii.CREATURE_ELITE` 1 (the GDB bool
  IsElite) and `Refii.CREATURE_LIEUTENANT` 2 (IsLieutenant), what the game's own `Creature.IsElite` /
  `Creature.IsLieutenant` answer; nil without a creature component.
- **Adding several:** `Refii.add_item(e, def, n)` (→ the last item's handle and how many were
  added), `add_item(entity, definition, count)` in Godot and `refii_mod_add_items(ctx, base, entity,
  definition, count, &last)` natively add up to 100 at once, each through the game's own add — its
  notifications and `item_added` each time.

```lua
-- A quick slot of our own: on the mod's action, drink a health potion, or strike a pose when full.
Refii.on("mod_action", function(e)
  if e.name ~= "my_mod.quick" then return end
  local cur, max = Refii.health(Refii.player_hero(e.player))
  if cur < max then
    Refii.use_item(e.player, Refii.hash("OBJECT_HEALTH_POTION"))
  else
    local known = Refii.expressions(e.player)
    if known and known[1] then Refii.perform_expression(e.player, known[1].record) end
  end
end)
```
