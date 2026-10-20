# Spell Weaving

Fable 3's spell weaving brought to Fable 2: a second spell, the **partner**, is cast together with
the spell on the charged slot, at the same power, at the same target, from the same release. A
hard mod; the design and the binary evidence are in `docs/hard_mods_plan.md` (Part D) and the
memory notes behind it.

It ships with the app and installs switched off, like Souls Combat. Its native parts are handlers
on the mod API's events (`docs/mod_api.md`): the pair is stood in through `SpellLaunch`, the
partner is cast from `HitLanded` with `refii_mod_cast_spell`, and the weave side's damage is
scaled on `Hit`. Enabled beside Souls Combat, a weave's launch raises the same `SpellLaunched`
event Souls Combat charges mana on, so a weave costs a cast's mana without either mod knowing
about the other. What it still hooks by name is a seam no event covers: the vortex component's
init and shutdown, and the targeted fireball's impact.

## What it changes

| Piece | File | What it does |
|---|---|---|
| The picker | `ui/spellwheel.json`, `language/en-uk/text/book.babel.csv` | X on a highlighted spell in either selector makes it the highlighted slot's partner; Y removes the partner. Its icon sits small under that slot's icon, and goes with it on Y. Once Spell Weaving is learned, the spell selection page shows two prompts beside its own, X "Set Partner" and Y "Remove Partner" (`SPELL_WEAVING_SET_PARTNER`, `SPELL_WEAVING_REMOVE_PARTNER`). |
| The pair | `native/weave.cpp`, `native/woven.cpp` | One combined cast, as Fable 3 has it. Of the slot's spell and the partner, the higher in the hierarchy below is the base and is the one spell launched, through the game's own launch with the base's type in the slot for the length of the call. The lower spell, the overlay, is never launched by the player: at the base's own moment — every victim it hits (for the Vortex, every creature its shudder hits reach); for a Fireball that strikes nothing alive, the impact point itself — a real Targeted cast of the overlay is made `shot_delay` seconds later (0.25, ±5 %) by the game's own spell factory with the hero as caster, exactly how Fable III's primary casts its partner. The partner's hits land at `weave_damage` (0.7) of their damage. One partner cast per victim per base cast; a weave-side spell never casts a partner of its own. The partner must be a spell the hero has learned. Slow Time and Raise Dead are never partners. |
| Fable 3 pacing | `native/weave.cpp` | By default only the charged slot casts. Fable 2's cascade, which fires slots 1, 2, 3 in turn up to the charge, is skipped slot by slot; `top_only = 0` restores it, pair included. |
| The Hybrid tree | `native/weaving_ability.cpp`, `ui/abilities.json`, `ui/templates.json`, `language/en-uk/text/book.babel.csv` | A fourth page on the abilities screen, **Hybrid**, holding the Spell Weaving ability: five levels, level N weaves casts up to power N. Priced like a spell (Fireball's experience table) and **paid half from Will, half from Skill**, General filling each half's gap as Fable 2 fills one pool's. The page lights both orbs and shows their combined total. Y unlearns a level through the game's own confirm, refunding half the level's cost to General. The level is a saved player value (`spell_weaving.ability`) that goes home with the hero's own save (see below). |
| The HUD glyph | `ui/hud.json`, `native/weave.cpp` | While a hero charges, the partner the charged slot would weave is drawn beside the charge meter: the native code publishes `spell_weaving.hud_partner` (and `hud_partner2`) from the charge level each frame, -1 when the slot has no partner in effect or nothing is charging, and the HUD picture piece shows the matching will icon from the list in `ui/hud.json` (`textures` chosen by `index`). |
| Ability card, slot frame | `art/gui/abilitytarot/spell_weaving.tex.png`, `art/gui/hud/spell_weaving_slot.tex.png` | The still the abilities book shows for Spell Weaving, and the partner slot's empty frame on the selectors (`tools/spell_weaving_icons.py`, our own art). |
| Woven effects | `native/woven.cpp` | When a hit from the base lands on a victim, a Targeted cast of the overlay is queued on that victim — the game's own spell factory, the hero as caster, the victim as target, placed on the victim, `shot_delay` later at the base's power. The partner spell then does what it always does: burns, shocks, shoves, turns, or sends blades. Its hits are scaled by `weave_damage`. The one place every hit arrives is hooked; the attacker, the victim, the serial, the level, the spell type and the damage are read from the hit. |

## The fifteen pairs

Six of Fable 2's spells weave, so there are fifteen pairs, as in Fable 3; Chaos stands where Ice
Storm did. What each one does, in Fable 2's terms:

| Pair | Cast | What rides on it |
|---|---|---|
| Vortex + Blades | Vortex | Each creature the twister takes gets blades cast on it. |
| Vortex + Fire | Vortex | Everything the vortex shakes catches fire. |
| Vortex + Shock | Vortex | Everything the vortex shakes is electrified. |
| Vortex + Force Push | Vortex | Everything the vortex shakes is shoved outward. |
| Vortex + Chaos | Vortex | Everything the vortex shakes turns on its allies. |
| Blades + Fire | Blades | Flaming blades: a stab sets the victim on fire. |
| Blades + Shock | Blades | Electrified blades: a stab electrifies. |
| Blades + Force Push | Blades | A stab shoves the victim back. |
| Blades + Chaos | Blades | A stab turns the victim on their allies. |
| Fire + Shock | Fireball | Fire hits electrify. |
| Fire + Force Push | Fireball | Fire hits shove the victim back. |
| Fire + Chaos | Fireball | Fire hits turn the victim. |
| Shock + Force Push | Lightning | Shock hits shove the victim back. |
| Shock + Chaos | Lightning | Shock hits turn the victim. |
| Force Push + Chaos | Force Push | Pushed victims turn on their allies. |

A status is applied to one victim at most once per `woven_cooldown_seconds`, so a burn tick does
not re-apply the overlay every frame.

## The merged look

One spell of the pair is the **base** and the other's look is applied onto it, by a fixed order:

| Base first | What is applied onto it |
|---|---|
| Vortex | blades spinning in it, fire on it, lightning in it, a pulse through it, chaos running through it |
| Blades | flaming blades, electrified blades, pulsing blades, chaotic blades |
| Fireball | lightning in the fire, a pulsing fire, chaotic fire |
| Shock | a pulsing shock, chaotic shock |
| Force Push | a chaotic push |

The native code publishes each live weave's look as `base × 8 + overlay` in the instance table,
and the newest one per player as `mod:spell_weaving.look` / `look2`. Liveness comes from the spell registry for
every base but Vortex: the vortex spell object finishes itself the moment it has spawned its
entity, so the mod hooks the vortex component's init and shutdown (`rex_VortexComponent_Init` /
`rex_VortexComponent_Shutdown`) and keeps its own table of live vortices by caster. `fx/recipes.json` holds one recipe per
pair (`Engine_FxRecipes`): the base spell's effects are listed by name, and while the look holds,
each live one of them takes the overlay's tint, brightness and size, and the overlay's own effects
are attached to it — a fire ring and three fire trails turning about a vortex, blade trails
orbiting a fireball, sparks circling a chaos stream. The guest still states which effect plays and
where; Godot changes what it looks like. Everything in the file is tunable: the tint, the
attached effects, their count, ring radius, height and turn rate.

**What the looks are made of.** Fable 3's weaves are authored looks, not a second spell played
over the first (read from Fable III's own `globals.gdb` and particle bank — see
`/home/loreaxe/Fable 2/Fable 3/README.md` for the table and Lionhead's emitter numbers): Vortex + Fire is a fire tornado, Vortex + Shock has lightning climbing the funnel,
Vortex + Force Push pulses shockwaves out, Vortex + Chaos has chaos running through it, and
Vortex + Blades has the swords spinning inside the funnel. None of those exist as effects in the
Fable 2 banks (its spell effects are one-shot bursts, trails that draw only while moving, and
build-ups that draw nothing alone), so each recipe carries authored `pieces` that Godot draws
with the game's own effect textures and meshes (`Engine_FxRecipePieces`):

| piece | what it is | fields |
|---|---|---|
| `embers` | particles climbing a spiral cone, their motion in a particle shader | `texture`, `color`, `colorEnd`, `height`, `radius`, `radiusTop`, `speed` (spin), `size`, `sizeEnd`, `count`, `period` (lifetime) |
| `arcs` | lightning ribbons re-struck `rate` times a second, each crawling a stretch of the cone | `texture`, `color`, `height`, `radius`, `radiusTop`, `rate`, `count`, `width` |
| `ring` | a shockwave disc growing to `radius` and fading over `period`, again and again | `texture`, `color`, `radius`, `period`, `height` |
| `blades` | the Blades spell's own cutlass mesh, `count` of them orbiting tangent to the swirl, each a step higher and further out, with its aura | `mesh`, `texture` (aura), `color`, `count`, `radius`, `height`, `speed` (turns/s), `tilt` (climb, degrees), `size`, `period` (height step), `width` (aura size) |

Every piece has `intensity`, and `blend` (`add`, the default, lights the scene; `mix` covers it,
for smoke and a dark twister). Textures are the bank's `art/fx/pfx_textures` leaf names (the fire
tornado is `fxt_flames_double_03` and `fxt_sparkle_orange_01`, shock is `fxt_lightning_vertical_cyan_01`
over `fxt_sparkle_white_01`, chaos is `fxt_silk_streams_01` with `fxt_blue_twirl_01` discs, push is
`fxt_white_ring_01` and `fxt_ring_glow`, the blades carry `fxt_democles_aura`). Every pair follows the Fable III effect it has one of, drawn from its disc in the lab. Fireball
with Shock becomes a purple-blue cloud core throwing big branching bolts (its
`fxspl_enflame_lightning_targetball`); Fireball with Force Push is wrapped in flame tongues,
ash sparks and orange shock rings (`fxspl_enflame_push`); Shock with Force Push casts a grey
smoke burst (`fxspl_lightning_push`); the Blades carry compact per-partner bits on each sword
(`fxspl_swords_enflame_blades`: white flame, a blue sparkle, a lamp flare; `_lightning_blades`:
glow, electrical patch, curl; `_forcepush_blades`: smoke rings, dark spread). Chaos pairs have no
Fable III original and keep the family's own build at the right scale. The vortex looks follow Fable III's own twister: a column six
metres tall and under a metre wide of rising sheets in the partner colour, bright bits at its
foot, the vortex's ground dust turned into partner clouds. The lab can dump any of
them to PNG (`REFII_LAB_DUMP_TEX=<names file>`) to see what a sheet holds: many are alpha-only with
black colour, which is why the pieces draw through a glow shader that takes coverage from whichever
channel carries the shape. The older `attach` list (copies of a shipped effect on a ring, stacked and
staggered) still works and is empty in every recipe now.

**The partner is a position cast, as Fable III's is.** Read from Fable III's own classes: every
weave request is cast with "shot from the caster" off, and Lightning, Fireball and Force Push
branch on that — no projectile, no bolt from the caster, no hand effect; the spell resolves at
once at the request position (the creature the vortex holds, the blade's target, the fireball's
impact point) with its impact effect there and its hit on the target. The pair reads as ONE
spell whose hits carry the second effect. Here the partner is started ON the victim: a
zero-length lightning strike, a fireball that detonates where it spawns, a push centred on the
base's own place so the victim is shoved away from the vortex or the impact, chaos and blades on
the victim. A fireball that struck nothing alive casts the partner at the impact point, lightning
from fifteen units up striking straight down (an untargeted bolt otherwise re-acquires a random
nearby creature at the start's height, which is what made it jump to the last victim).

**Every cast is its own weave.** A woven release is a record from its launch until its base has
finished — the base's serial, its pair, its power, and what it put in the world: the vortex
entity, the fireball's shot, each hovering sword. The partner a hit casts is the partner of the
weave whose serial that hit carries, so two vortices out at once from two slots each cast their
own partner, and re-picking a slot's partner changes nothing already in flight.

**The look is a template, played per cast.** The native code publishes an INSTANCE TABLE
(`spell_weaving.inst_<i>_look`, `_x _y _z`, `_hi _lo`, `_serial`): one entry per vortex, per
fireball in flight, per hovering sword, per instant cast's origin, with its look, its place, its
entity and its cast serial. The recipes read it (`"instances": "mod:spell_weaving.inst_"` at the
top of `fx/recipes.json`), and for every entry the recipe of its look gets its OWN node, placed
where the mod says each frame, carrying the recipe's light, pieces and attached effects — a
Fire Vortex is always that combination, at every fire vortex, whatever the game's own effects
are called. The game's base effects bound to the cast (by the entity the live-effect feed states
they belong to, or by standing within `instanceRadius` of it) take the tint, intensity and size
only. A cast with no partner shows nothing. Every recipe is `everyNode`: each live anchor effect carries
the pieces and the light, not only the first. The Blades' hovering swords are ENTITIES with no
particle effect of their own while they wait. The spell factory runs the cast's Start itself, and
that Start builds the sword controller and keeps it on the spell object (at +0xE4 for a targeted
cast, +0xC8 for an area cast — two classes); its sword records each hold the guest's own weak
reference to a sword entity, so the mod takes the swords when the factory reports the spell, and a
sword counts as out for exactly as long as the guest keeps that reference. Each live sword is an
entry of the instance table, so each carries the look's node.

**What blends them.** Every recipe puts a `light` in the overlay's colour inside the base, so the
vortex's own dust and the ground take the glow from within; that is the strongest cue that the two
are one thing. A spell plays several effects per cast and the recipe names them all, so the column
and the light go on ONE of them at a time, the first live one, and the rest take the tint only.

**Tune them in the FX Lab**, the Mod Studio's fourth tab: pick a recipe, pick one of its base
effects, and turn the numbers against a live preview dressed by the same code the game uses, with
Fable 3's description of the pair beside it. Save writes `fx/recipes.json` back into the mod; a
running game takes it with `refii_action fx-recipes-reload`, and in a dev tree it reads this
folder's `fx/recipes.json` directly, so an edit here needs no repack. The same preview runs from a
shell as `res://debug/fx_weave_lab.tscn` (`Godot/src/tools/FxWeaveLab.cs`), which draws a base
plain, dressed and an optional reference effect side by side and writes PNGs at chosen frames.

## How Fable III builds a weave (from its binary; `/home/loreaxe/Fable 2/Fable 3/README.md`)

One cast makes ONE spell object: the primary, carrying its partner's type, the combo index and
the power. The primary's own class then issues a SECOND, real Targeted cast of the partner on the
victim at its own moment — the vortex per creature it grabs or holds, the fireball on impact,
lightning per victim hit, each blade on its stab — through a three-slot delayed caster with
`WeaveSpellShotDelayTime` from the combo's `WeaveSpellData` (0.25 s for the vortex combos,
default 1 s, ±5 %); the partner's balance is its `WeaveSpellData` times the 0.7 damage and
duration multipliers (0.75 in the vortex records). Burning, shock and push are the partner
spell's own hit code. Vortex + Swords is one Swords cast per held victim with 3–5 blades from the
weave balance, which is the "5 blades appear over each enemy". Slow Time combos repeat the partner
every `WeaveSpellRepeatTime`; only the Vortex rolls `WeaveSpellAffectProbability` (shipped 100). Our port does the same with Fable 2's spells, at the same moments: a victim the base hits
(the `HitLanded` event — for the Vortex that is every creature its shudder hits reach; a
creature the vortex only holds without hitting gets nothing, as the game's capture keeps no
reference a mod could follow), and a fireball's detonation that struck nothing alive
(`rex_TargetedFireball_Impact`, the partner cast at the impact point with no target — every
targeted spell's post-init falls back to its position and direction when the target is empty, as
the game's own launcher casts with no lock-on). Each queues a Targeted cast of the partner through
the game's own spell factory (`rex_CSpellRegistry_Create`), `shot_delay` later, and the partner's
hits are scaled by `weave_damage`. There is no status
installing any more: burning, shock, push, chaos and blades are the partner spell itself.

## Choosing the partners

Each of the five slots has its own partner, as each has its own spell.

**In the game:** on either spell selector, the quick wheel held on the trigger or the abilities
book's spell blade, move to a slot, turn the carousel to the spell you want as its partner and
press **X**. The carousel commits as it turns (that is the game's own widget), so the pick also
puts the slot's own spell back where it was when the selector came up: you end with the same spell
on the slot and the partner under it. X without turning, on the slot's own spell, clears the
partner. X on the spell already the partner keeps it (it never toggles off). Every pick is logged
on the host as `[spell-wheel] partner pick: ...`. Every assigned slot carries a small frame in the
lower-right corner of its icon (`ui/spellwheel.json`, the host's secondary pick): faint and empty
while the slot has no partner, the partner's icon at full strength once it is in effect, at half
strength while it is chosen but not yet learned or above the Spell Weaving level, and greyed down
while the slot's own spell is Slow Time or Raise Dead, which can take no partner. While the wheel is
held the host withholds X from the game, so the press never reaches the attack or parry the combat
scheme binds to it. The partner must be learned to take effect; an unlearned pick simply waits.

The choices are saved player values (`spell_weaving.partner_1..5`, each player their own) and are
made through the settings `spell_weaving.partner_1..5` / `partner2_1..5`: changing a value chooses.
A spell type id, or -1 for none. Saves from before kept them in the world save's Lua
(`RefiiModSave.SpellWeavingPartner1..5`, `Partner2_1..5`); a slot with no saved choice takes that
one, then settings.json's.

| id | spell | id | spell |
|---|---|---|---|
| 0 | Lightning (Shock) | 4 | Vortex |
| 1 | Fireball (Inferno) | 5 | Chaos |
| 2 | Slow Time (not weavable) | 6 | Force Push |
| 3 | Blades | 7 | Raise Dead (not weavable) |

In a running game, through the debug channel (slot 2's partner becomes Vortex):

```
refii_action "mod-setting spell_weaving.partner_2 4"
```

The partner in effect per slot (chosen, learned, and the ability at that slot's level or more) is
published as `mod:spell_weaving.partner_active_1..5` (-1 when none).

## Settings

| Key | Default | Meaning |
|---|---|---|
| `partner_1..5` / `partner2_1..5` | -1 | The partner spell type per slot and player; a change chooses. |
| `top_only` | 1 | Only the charged slot casts (Fable 3). 0 keeps Fable 2's cascade, each slot with its partner. |
| `allow_same` | 0 | A partner equal to the slot's own spell casts it twice. |
| `cost_like` | 8 | The ability whose experience table prices the levels (8 = Fireball; the eight spells share one curve). |
| `woven` | 1 | Weaving on. 0 turns it off: every slot casts its own spell alone and no partner is cast. |
| `woven_cooldown_seconds` | 0.5 | How often one victim can receive the partner from the same base. |
| `shot_delay` | 1 | Seconds between the base's hit and the partner's cast on that victim (±5 %, Fable 3's `WeaveSpellShotDelayTime` default). |
| `shot_delay_vortex` | 0.25 | The same for a vortex base (Fable 3's vortex records). |
| `weave_damage` | 0.7 | The share of their damage the partner's hits land at (Fable 3's general damage multiplier). |

Each player has their own Spell Weaving level and their own partner choices. The ability row in
the book acts for, and shows, the player whose book is open (`refii_mod_menu_player`): player
two's own book in local co-op, the joiner's book on the joiner's machine. The partner picker's
prompts unlock from that same player's level. The level and the choices are SAVED player values
in Refii's player store (`refii_mod_player_*`, `docs/mod_api.md`): the same on both machines of an
online session, written beside the save the game writes that hero into (`refii_player.txt`) —
player one's main save, a local player two's own save, an online joiner's own save on the
joiner's machine — and read back when the game loads that hero. So player two keeps their level
and partners when they go home. Saves from before kept player one's level in the world save's Lua
(`RefiiModSave.SpellWeaving`); it is taken from there once.
The HUD glyph follows each player's charge meter, in their own half on split screen.

Online, the mod has no co-op plumbing of its own; it follows the mod API's rules. A cast runs on
both machines and the host's copy is the spell that deals the damage: hits are decided on the
host. So partners are cast only where the world runs (`refii_mod_world_here`: the host, or the one
machine alone and in local co-op) — player two's included — one per victim, or one at the impact
point for a fireball that struck nothing alive; an online joiner casts none, which would otherwise
have cast a second "struck nothing alive" partner for every fireball whose blast it saw land no
hits. The look follows the real victims on both machines: the API relays every hit a hero deals
or takes from the host to the joiner as a replica (`refii_mod_event_replica`), and a replica only
notes its victim on the weave of the caster's player whose base it is — it never casts. A targeted
Lightning, Force Push or Chaos rides on the target the cast states until those hits arrive; an
untargeted one stands at the caster for that network moment, then on its victims.

## Next

- Tune the HUD glyph's place beside the charge meter in a run (`ui/hud.json` offset).
