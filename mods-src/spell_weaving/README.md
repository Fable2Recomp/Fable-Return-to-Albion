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
| The picker | `ui/spellwheel.json`, `language/en-uk/text/book.babel.csv` | The partner is a spell slot PICK the engine runs (`docs/mod_api.md`, "Spell slot picks"): X on a highlighted spell in either selector makes it the highlighted slot's partner; Y removes the partner. The engine raises `SpellPick` and saves the pick as the selectors' player's `spell_weaving.partner_<slot>`; `weave.cpp` only refuses Slow Time and Raise Dead on that event. Its icon sits small under that slot's icon, and goes with it on Y. Once Spell Weaving is learned (`unlocked`: `player:spell_weaving.ability`), the spell selection page shows two prompts beside its own, X "Set Partner" and Y "Remove Partner" (`SPELL_WEAVING_SET_PARTNER`, `SPELL_WEAVING_REMOVE_PARTNER`). Another enabled mod's picks are drawn beside the partner under each slot; X and Y act on one pick at a time, drawn larger (a press with nothing to do, or LB/RB on the spell page, steps to the next). |
| The pair | `native/weave.cpp`, `native/woven.cpp` | One combined cast, as Fable 3 has it. Of the slot's spell and the partner, the higher in the hierarchy below is the base and is the one spell launched, through the game's own launch with the base's type in the slot for the length of the call. The lower spell, the overlay, is never launched by the player: at the base's own moment — every victim it hits (for the Vortex, every creature its shudder hits reach); for a Fireball that strikes nothing alive, the impact point itself — a real Targeted cast of the overlay is made `shot_delay` seconds later (0.25, ±5 %) by the game's own spell factory with the hero as caster, exactly how Fable III's primary casts its partner. The partner's hits land at `weave_damage` (0.7) of their damage. One partner cast per victim per base cast; a weave-side spell never casts a partner of its own. The partner must be a spell the hero has learned. Slow Time and Raise Dead are never partners. |
| Fable 3 pacing | `native/weave.cpp` | By default only the charged slot casts. Fable 2's cascade, which fires slots 1, 2, 3 in turn up to the charge, is skipped slot by slot; `top_only = 0` restores it, pair included. |
| The Hybrid tree | `ui/abilities.json`, `ui/templates.json`, `language/en-uk/text/book.babel.csv` | A fourth page on the abilities screen, **Hybrid**, holding the Spell Weaving ability: five levels, level N weaves casts up to power N. DECLARED and run by the engine (`docs/mod_api.md`, "Mod abilities"): priced like a spell (`costLike` 8, Fireball's experience table, or the one the setting `cost_like` names) and **paid half from Will, half from Skill** (`disciplines`), General filling each half's gap as Fable 2 fills one pool's. The page lights both orbs and shows their combined total. Y unlearns a level through the game's own confirm, refunding half the level's cost to General (`"refund": "half"`). The level is a saved player value (`spell_weaving.ability`) that goes home with the hero's own save (see below); the mod only reads it. |
| The HUD glyph | `ui/hud.json`, `native/weave.cpp` | While a hero charges, the partner the charged slot would weave is drawn beside the charge meter: the native code states `spell_weaving.hud_partner` in that player's store from the charge level each frame, -1 when the slot has no partner in effect or nothing is charging, and the HUD picture piece shows the matching will icon from the list in `ui/hud.json` (`textures` chosen by `index`: `player:spell_weaving.hud_partner`). ONE piece serves both players: a piece that follows player one's charge meter and reads a `player:` value also follows player two's (`SpellCharging2`), reading player two's. |
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

Each pair's look is an EFFECT of the mod's own, `fx/effects/spell_weaving_<base>_<overlay>.json`,
in the particle bank's own model and made of the bank's own entities, so the guest's bank carries
it and BOTH looks draw it (`docs/mod_api.md`, "Mod effects"). `native/woven.cpp` plays it per cast
with `refii_mod_play_fx_on` on each thing the cast rides — the vortex entity, the fireball's shot,
each hovering Blades sword, each victim of a bolt, shove or chaos for its hold — and
`refii_mod_stop_fx` ends each play when that thing is gone (the vortex's shutdown, the fireball's
impact, a sword's stab, a victim's hold running out, the weave finishing). Liveness comes from the
spell registry for every base but Vortex: the vortex spell object finishes itself the moment it has
spawned its entity, so the mod hooks the vortex component's init and shutdown
(`rex_VortexComponent_Init` / `rex_VortexComponent_Shutdown`).

`fx/recipes.json` keeps one recipe per pair for the base spell's OWN effects: `"with"` names the
pair's effect, and a base effect is dressed only where a play of it rides the same entity or stands
within `withRadius` (10) — so each cast tints its own base and no other, and two vortices from two
slots each carry their own partner's look. The recipe gives the base's effects the overlay's tint,
intensity and size; nothing else, and no table of casts is published.

**What the looks are made of.** Fable 3's weaves are authored looks, not a second spell played
over the first (read from Fable III's own `globals.gdb` and particle bank — see
`/home/loreaxe/Fable 2/Fable 3/README.md` for the table and Lionhead's emitter numbers). Each pair's
effect is built the way Fable III builds that pair's look — the same kinds of bank entity, chained
the same way, at the same rates, speeds, lives and sizes — drawn with Fable II's own textures
(nothing of Fable III's is shipped):

| pairs | Fable III's look | the build |
|---|---|---|
| Vortex + Fire, Shock, Force Push, Chaos | `fxspl_vortex_enflame_twister`, `_lightning_twister`, `_force_twister` (Chaos: the same build in violet) | a narrow column some six metres tall: wind sheets (`fxt_smokepuff_long_01`) shot up at 3–6.5 m/s and accelerating, off an emitter an orbital swings round a 0.3 m circle at 7.33 rad/s; small at birth, a 2 m sheet at 1.2 s, a wide faint one at 2 s; bright bits flung from a 0.1 m disc at its foot; a point light low in it. Fire adds flames at the foot; Shock a sky-high bolt (`fxt_lightning_vertical_cyan_01`) flickering down the column, a faint ring on the ground and a hot flash at the foot; Force Push is lit brown smoke with grey-blue puffs shot out and braked, the air bending over it and a pulse of edge smoke; Chaos winds twirls up the funnel |
| Blades + Fire, Shock, Force Push, Chaos | `fxspl_swords_enflame_blades`, `_lightning_blades`, `_forcepush_blades` (Chaos: the fire build in violet) | a burn riding each sword, not round it: a spline along the blade in the sword's own frame (its +Y, from the guard 0.08 m out to 0.87 m by the tip — `npc_sword_damocles.mdl`, whose `.mdl.gmd` puts Weapon.Weapon.Hold at -0.083 and Weapon.Weapon.Top at 0.847), so the look lies on the blade and turns with it however the sword flies, with the emitters chained to it, sprites a fraction of a metre — flames over a blue spark and a flickering flare (Fire), white glow, electrical patches and curls (Shock), faint edge smoke, grit and the air bending (Force Push) |
| Fireball + Shock, Force Push, Chaos | `fxspl_enflame_lightning_targetball`, `fxspl_enflame_push` (Chaos: the push build in violet) | the shot's own look, on the shot: Shock is a crackling core in a violet storm cloud with bolts flashing off it; Force Push is a fire core wrapped in flame tongues, flinging blobs and sparks, with smoke rolling off; each with a point light |
| Shock + Force Push, Shock + Chaos, Force Push + Chaos | `fxspl_lightning_push` (the Chaos pairs: the same burst with chaos riding it) | one smoke burst blown out over a 2 m disc round the victim each play, with the air rippling outward |
| Vortex + Blades | none: Fable III's twister for it is the vortex's plain dust, and the swords are the Blades casts on each held creature | Fable II's own Blades swords orbiting in the funnel on one attractor, each carrying the sword's own effects, under a point light |

`SpellWeavingLookTests` pins each build (the orbital, the column's speed, lift and keys and the
height they reach; the spline down the blade and the size of what rides it; the shot's emitters
on the ball; the one burst a play) and that no texture is one of Fable III's own.

The side-by-side renders against Fable III's own effects come from the FX Lab scene
(`Godot/src/tools/FxWeaveLab.cs`: `REFII_LAB_BANK` draws Fable III's bank, `REFII_LAB_SWORD_ORBIT`
flies Fable II's Blades sword round a pivot, tilting and rolling, with the look played on it as
`play_fx_on` plays one — in the sword's own frame — so a Blades look, ours or Fable III's, is seen
staying on the blade as it turns). Under the classic look the vortex's look rides the vortex entity's own position, which can
stand behind the travelling twister (an open item).

**The tinted swords.** Each sword of a woven Blades cast wears its partner's colour — red for
Fire, blue for Shock, violet for Chaos, dark slate steel for Force Push — under the woven burn
that rides it. `native/woven.cpp` sets it through the game's own appearance tint setter
(`refii_mod_entity_tint`, the mod API's tint: `sub_82573770` on the sword's renderable), so the
modern and the classic look both draw it and every machine running the cast tints its own copy;
either player's weave tints that player's swords. It is set once per sword: the sword's own update
restates its scale and its fade every frame but never its tint, and a sword is destroyed 0.35 s
after its stab, so nothing is put back. The tint multiplies the blade's lit colour — its glow
included — so blue and violet need a lane above 1 to show on gold, and Force Push's dark steel is
the gold's glow taken away. "More solid" is that: the tint's alpha is drawn by no shader, and the
sword's opacity is its own fade, which the mod leaves alone. The four colours were tuned in the
weave lab (`REFII_LAB_SWORD_TINT=r,g,b` draws the flown sword with that stated tint).

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

**Tune them in the FX Lab**, the Mod Studio's fourth tab: open a pair's effect in the effect editor
(every entity, timeline and visual), or pick a recipe and one of its base effects and turn the tint
against a live preview dressed by the same code the game uses, with Fable 3's description of the
pair beside it. Save writes `fx/effects/<name>.json` or `fx/recipes.json` back into the mod; a
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
on the slot and the partner under it. X on the slot's own spell changes nothing (Y removes the
partner), and X on the spell already the partner keeps it (it never toggles off); with another
mod's pick enabled as well, those presses step X and Y on to the other pick instead. Every pick is
logged as `[spell-wheel] spell_weaving.partner: ...` where it is made, and the engine's
`Spell picks:` line where it is saved. Every assigned slot carries a small frame in the
lower-right corner of its icon (`ui/spellwheel.json`, the host's secondary pick): faint and empty
while the slot has no partner, the partner's icon at full strength once it is in effect, at half
strength while it is chosen but not yet learned or above the Spell Weaving level, and greyed down
while the slot's own spell is Slow Time or Raise Dead, which can take no partner. While the wheel is
held the host withholds X from the game, so the press never reaches the attack or parry the combat
scheme binds to it. The partner must be learned to take effect; an unlearned pick simply waits.

The choices are saved player values (`spell_weaving.partner_1..5`, each player their own), written
only by the engine's pick (the SpellPick event first, which may refuse or change it): a spell type
id, or -1 for none. Saves from before kept them in the world save's Lua
(`RefiiModSave.SpellWeavingPartner1..5`, `Partner2_1..5`); a slot with no saved choice takes that
one. There is no settings row for them: a settings page holds one machine's values, a partner is
each player's own, and the selectors are where each player picks theirs (the title's "Spell
Weaving" row says how).

| id | spell | id | spell |
|---|---|---|---|
| 0 | Lightning (Shock) | 4 | Vortex |
| 1 | Fireball (Inferno) | 5 | Chaos |
| 2 | Slow Time (not weavable) | 6 | Force Push |
| 3 | Blades | 7 | Raise Dead (not weavable) |

In a running game the debug channel's game Lua (`refii_lua`) can set one directly, without the
SpellPick event — player 1, slot 2's partner becomes Vortex:
`Refii.player_save(1, "spell_weaving.partner_2", 4)`.

The partner in effect per slot (chosen, learned, and the ability at that slot's level or more) is
stated where the hero is played as `player:spell_weaving.partner_active_1..5` (-1 when none); the
selectors draw a chosen partner that is not in effect at half strength.

## Settings

| Key | Default | Meaning |
|---|---|---|
| `top_only` | 1 | Only the charged slot casts (Fable 3). 0 keeps Fable 2's cascade, each slot with its partner. |
| `allow_same` | 0 | A partner equal to the slot's own spell casts it twice. |
| `cost_like` | 8 | The ability whose experience table prices the levels (8 = Fireball; the eight spells share one curve). |
| `woven` | 1 | Weaving on. 0 turns it off: every slot casts its own spell alone and no partner is cast. |
| `woven_cooldown_seconds` | 0.5 | How often one victim can receive the partner from the same base. |
| `shot_delay` | 1 | Seconds between the base's hit and the partner's cast on that victim (±5 %, Fable 3's `WeaveSpellShotDelayTime` default). |
| `shot_delay_vortex` | 0.25 | The same for a vortex base (Fable 3's vortex records). |
| `weave_damage` | 0.7 | The share of their damage the partner's hits land at (Fable 3's general damage multiplier). |
| `sword_tint_fire_r/_g/_b` | 1.35, 0.24, 0.1 | A Blades sword woven with Fire: a red blade. |
| `sword_tint_shock_r/_g/_b` | 0.35, 0.75, 3.0 | Woven with Shock: a cold blue blade. |
| `sword_tint_chaos_r/_g/_b` | 0.5, 0.22, 3.0 | Woven with Chaos: a violet blade. |
| `sword_tint_push_r/_g/_b` | 0.28, 0.32, 0.55 | Woven with Force Push: a dark slate steel with no golden glow. |

The sword tints are multipliers of the golden blade's lit colour (1 leaves a lane alone); see "The
tinted swords" below.

Each player has their own Spell Weaving level and their own partner choices. The ability row in
the book acts for, and shows, the player whose book is open: player two's own book in local co-op,
the joiner's book on the joiner's machine; the engine's purchase spends that player's experience.
The selectors pick for their own player, and the partner picker's prompts unlock from that same
player's level. The level and the choices are SAVED player values
in Refii's player store (`refii_mod_player_*`, `docs/mod_api.md`): the same on both machines of an
online session, written beside the save the game writes that hero into (`refii_player.txt`) —
player one's main save, a local player two's own save, an online joiner's own save on the
joiner's machine — and read back when the game loads that hero. So player two keeps their level
and partners when they go home. Saves from before kept player one's level in the world save's Lua
(`RefiiModSave.SpellWeaving`); the engine takes it from there once (`legacySaveKey`).
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
