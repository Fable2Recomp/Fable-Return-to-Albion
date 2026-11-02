# Spell Weaving

Fable 3's spell weaving brought to Fable 2: a second spell, the **partner**, is cast together with
the spell on the charged slot, at the same power, at the same target, from the same release. A
hard mod with NO native code: everything it does is Lua on the mod API (`Godot/docs/modding/`), one
file, `mod/init.lua`. The design and the binary evidence are in `docs/hard_mods_plan.md` (Part D,
Part H.15) and the memory notes behind it.

It ships with the app and installs switched off, like Souls Combat. The pair is stood in through
`spell_launch` (the slot's type, writable for that launch) and known from there by the `tag` that
launch writes; the partner is cast from `hit_landed` and a Blades-woven vortex's `vortex_caught`
with `Refii.cast_spell`, placed by the spell's own rule (its `at` option), carrying Fable III's
damage share as the cast's `damage_scale` and its duration share through `status_applying`; where
no partner is cast the base's own hits carry the overlay (`hit`'s writable `knockdown` and
`damage`, `vortex_started`'s writable `lifts`, `Refii.apply_status`, `Refii.knockback`); the
base's parts and lifecycle come from `vortex_started`, `vortex_ended`, `summoned`,
`fireball_impact`, `spell_ended` and `spell_parts`. Enabled
beside Souls Combat, a weave's launch raises the same `spell_launched` event Souls Combat charges
mana on, so a weave costs a cast's mana without either mod knowing about the other. It hooks
nothing by name and reads no guest memory; a soft mod, it builds nothing into the game module (the
vortex's `lifts` is an API field the core answers).

## What it changes

| Piece | File | What it does |
|---|---|---|
| The picker | `ui/spellwheel.json`, `language/en-uk/text/book.babel.csv` | The partner is a spell slot PICK the engine runs (`Godot/docs/modding/ui-and-hud.md`, "Spell slot picks"): X on a highlighted spell in either selector makes it the highlighted slot's partner; Y removes the partner. The engine raises `spell_pick` and saves the pick as the selectors' player's `spell_weaving.partner_<slot>`; the engine refuses Slow Time and Raise Dead on every path (the pick's `exclude`). Its icon sits small under that slot's icon, and goes with it on Y. Once Spell Weaving is learned (`unlocked`: `player:spell_weaving.ability`), the spell selection page shows two prompts beside its own, X "Set Partner" and Y "Remove Partner" (`SPELL_WEAVING_SET_PARTNER`, `SPELL_WEAVING_REMOVE_PARTNER`). Another enabled mod's picks are drawn beside the partner under each slot; X and Y act on one pick at a time, drawn larger (a press with nothing to do, or LB/RB on the spell page, steps to the next). |
| The pair | `mod/init.lua` | One combined cast, as Fable 3 has it. Of the slot's spell and the partner, the higher in the hierarchy below is the base and is the one spell launched, through the game's own launch with the base's type in the slot for the length of the call (`spell_launch`, `e.type`). The lower spell, the overlay, is never launched by the player. It rides the base's hits, in one of three ways (the pair table below): a Force Push under Shock, Fireball or Blades is the knockdown of the base's own hits, nothing cast; a Vortex lifts only for Blades, and casts Blades on each creature it holds or cannot lift by Fable III's rule (below) — with Fire, Shock, Chaos or Force Push it lifts nobody and its one hit on each creature it passes carries the overlay; every other pair casts the overlay at the base's own moment — every victim it hits; for a Fireball that strikes nothing alive, the impact point itself — a real Targeted cast made `shot_delay` seconds later (0.25 for a vortex, 1 otherwise, ±5 %) by the game's own spell factory with the hero as caster (`Refii.cast_spell`), as Fable III's primary casts its partner. The partner's hits land at `weave_damage` (0.7) of their damage (the cast's `damage_scale`), and the burn, shock or chaos it puts on lasts `weave_duration` (0.7) of its own time (`status_applying`), Fable III's two weave multipliers. One partner cast per victim per base cast; a weave-side spell never casts a partner of its own. The partner must be a spell the hero can assign (`Refii.spells_known`). Slow Time and Raise Dead are never partners. |
| Fable 3 pacing | `mod/init.lua` | By default only the charged slot casts. Fable 2's cascade, which fires slots 1, 2, 3 in turn up to the charge, is skipped slot by slot (`spell_launch` with `cascading` cancelled); `top_only = 0` restores it, pair included. |
| The Hybrid tree | `ui/abilities.json`, `ui/templates.json`, `language/en-uk/text/book.babel.csv` | A fourth page on the abilities screen, **Hybrid**, holding the Spell Weaving ability: five levels, level N weaves casts up to power N. DECLARED and run by the engine (`Godot/docs/modding/ui-and-hud.md`, "Mod abilities"): priced like a spell (`costLike` 8, Fireball's experience table, or the one the setting `cost_like` names) and **paid half from Will, half from Skill** (`disciplines`), General filling each half's gap as Fable 2 fills one pool's. The page lights both orbs and shows their combined total. Y unlearns a level through the game's own confirm, refunding half the level's cost to General (`"refund": "half"`). The level is a saved player value (`spell_weaving.ability`) that goes home with the hero's own save (see below); the mod only reads it (`Refii.player_value`). |
| The HUD glyph | `ui/hud.json`, `mod/init.lua` | While a hero charges, the partner the charged slot would weave is drawn beside the charge meter: the Lua states `spell_weaving.hud_partner` in that player's store from the charge level (`spell_charge_level`; the charge's end, `spell_charge_ended`, clears it, released into a cast or not), -1 when the slot has no partner in effect or nothing is charging, and the HUD picture piece shows the matching will icon from the list in `ui/hud.json` (`textures` chosen by `index`: `player:spell_weaving.hud_partner`). ONE piece serves both players: a piece that follows player one's charge meter and reads a `player:` value also follows player two's (`SpellCharging2`), reading player two's. |
| Ability card, slot frame | `art/gui/abilitytarot/spell_weaving.tex.png`, `art/gui/hud/spell_weaving_slot.tex.png` | The still the abilities book shows for Spell Weaving, and the partner slot's empty frame on the selectors (`tools/spell_weaving_icons.py`, our own art). |
| Woven effects | `mod/init.lua` | When a hit from the base lands on a victim (`hit_landed`), or a Blades-woven vortex's roll comes due for a creature it holds (`vortex_caught`, below) or passes for one it cannot lift (its shudder hit, `hit_landed`), a Targeted cast of the overlay is queued on that victim — the game's own spell factory, the hero as caster, the victim as target, `shot_delay` later at the base's power (a vortex's Blades at the power that makes Fable III's swords, and on a creature it holds only if it still holds it then). The partner spell then does what it always does: burns, shocks, turns, or sends blades. Its hits are scaled by `weave_damage` and its status's duration by `weave_duration`. Where the pair casts nothing the base's hit carries the overlay before it is delivered (`hit`): Force Push sets the hit's `knockdown`; a pass-through vortex's hit lands the vortex's own damage, Fire and Shock `weave_damage` more, and `hit_landed` then burns, shocks or turns the victim (`Refii.apply_status`, `weave_duration` of the overlay's own time at that power, `Refii.spell_balance`) or shoves it away from the twister (`Refii.knockback`). The one place every hit arrives is the event; the attacker, the victim, the serial, the level, the spell type and the damage are its fields, and a capture states its cast's tag and serial the same way. |

## The fifteen pairs

Six of Fable 2's spells weave, so there are fifteen pairs, as in Fable 3; Chaos stands where Ice
Storm did. What each one does, in Fable 2's terms:

| Pair | Cast | What rides on it |
|---|---|---|
| Vortex + Blades | Vortex | The twister lifts as it always does; each creature it reaches — never a prop, a sword or another spell's entity it lifts — gets one Blades cast on it, once per vortex, with Fable III's 3–5 swords: one it holds at a moment between 15 % and 60 % of the twister's life, while it still holds it; one it cannot lift (a hero, or a creature not flagged liftable) the vortex's shot delay (0.25 s ±5 %) after its shudder hit on it. |
| Vortex + Fire | Vortex | Picks nobody up: it passes through, and its one hit on each creature it reaches lands the vortex's own damage and `weave_damage` more, and sets the creature on fire. No fireball is cast. |
| Vortex + Shock | Vortex | The same, electrifying. No bolt is cast. |
| Vortex + Force Push | Vortex | Picks nobody up: each creature it reaches is knocked down and thrown away from the twister. No push is cast. |
| Vortex + Chaos | Vortex | Picks nobody up: each creature it passes through turns on its allies. No chaos is cast. |
| Blades + Fire | Blades | Flaming blades: a stab sets the victim on fire. |
| Blades + Shock | Blades | Electrified blades: a stab electrifies. |
| Blades + Force Push | Blades | Each stab knocks the victim down — the stab's own hit carries Force Push's knockdown. No push is cast and no push look plays. |
| Blades + Chaos | Blades | A stab turns the victim on their allies. |
| Fire + Shock | Fireball | Fire hits electrify. |
| Fire + Force Push | Fireball | Every blast hit knocks its victim down — the hit's own knockdown. No push is cast and no push look plays. |
| Fire + Chaos | Fireball | Fire hits turn the victim. |
| Shock + Force Push | Lightning | Every bolt hit knocks its victim down — the hit's own knockdown — with Force Push's own hit effect over the bolt's on the victim. No push is cast. |
| Shock + Chaos | Lightning | Shock hits turn the victim. |
| Force Push + Chaos | Force Push | Pushed victims turn on their allies. |

A status is applied to one victim at most once per `woven_cooldown_seconds`, so a burn tick does
not re-apply the overlay every frame; a pass-through vortex reaches each creature once per cast
(the game's own shudder rule), and puts its status on once.

**How the vortex weaves Blades** — Fable III's rule, read from its vortex entity in the Fable III
executable (the capture `sub_82A34F88`, the roll `sub_82A33B30`, the held step `sub_82A33D40`, the
cast `sub_82A33F30`). A creature the vortex catches — and only a creature: the
roll sits behind the victim's creature test — is rolled once against `WeaveSpellAffectProbability`
(100) and given a moment between 15 % and 60 % of the twister's life, never before the catch; when
the moment comes and the vortex still holds it, it goes on the vortex's list of partnered
creatures (once per creature per vortex: let go and caught again, it gets nothing more) and its
Swords cast follows the vortex's shot delay (0.25 s ±5 %, never past 95 % of the time the twister
has left). That cast makes 3, 4, 4, 4 or 5 blades at power 1..5 (the `NumSwords` of the vortex's
`WEAVE_SPELL_VORTEX_SWORDS` weave balance). Here, the capture event is raised for EVERY entity the
vortex lifts — the props it carries too (`vortex_caught`, `Godot/docs/modding/gameplay.md`) — so the
mod takes a creature by its creature component (`Refii.creature_tier`) and only while it lives;
Blades swords, props and any other spell's entities are no creature and never get a cast, so the
vortex's own swords can never start another. Fable II's Blades makes the swords its own power's
record states (`Refii.spell_balance`: 3, 4, 5, 6, 8 in the shipped data); the partner is cast at the
highest power up to the vortex's that makes no more than Fable III's count — 3, 4, 4, 4, 5 again.
A creature the vortex cannot lift — a hero, or one not flagged liftable — Fable III casts on
through its shudder (`sub_82A33C58`, called after the shudder hit): the same probability, without
the moment (the cast is requested at once, the same shot delay later), never held, and on the SAME
list of partnered creatures, so a creature gets one Swords cast per vortex whichever way the vortex
takes it. Fable II's vortex gives such a creature one shudder hit per cast, which carries the cast's
tag: the mod knows it as the cast's `hit_landed` on a creature the live vortex does not hold
(`Refii.vortex_held` — every other hit of the vortex is for a creature on its held list) and casts
there, by the same creature test. Fable III rolls again at each later half-second pass while the
creature stays in reach; Fable II's vortex raises nothing on those passes, so the roll here is made
once, at the hit — identical at the shipped probability of 100.

**How the vortex passes through.** Every half second the vortex's update asks the game how it
takes each creature in reach: lift it, or — a hero, or a creature not flagged liftable — give it
one shudder hit per cast instead. `vortex_started`'s writable `lifts` (false here) has the core
answer "cannot lift" for every creature while that vortex updates
(`Godot/docs/modding/gameplay.md`, "A live cast"), so the vortex holds nobody and its shudder hit,
which carries the cast's tag, is the hit the overlay rides — that hit only, on a living creature
the vortex does not hold: its pulse every 1.2 s on the props in reach and its hits on the props it
still lifts carry the tag too, and ride nothing (the drop's hit on a creature let go carries no
serial at all): the mod raises its damage on `hit` to
at least the vortex's own hit (`vortex_started`'s `damage`, what each hit on a held creature starts
from) — Fire and Shock `weave_damage` more — and on `hit_landed` puts the status on through the
spell's own installer (`Refii.apply_status`) or shoves the creature with Force Push's knockdown hit
along the line from the twister's point (`Refii.knockback`, on the next tick). The shudder hit's
own direction points into the twister, so the shove is stated as its own hit rather than the
shudder's knockdown. Props are still lifted.

## The merged look

One spell of the pair is the **base** and the other's look is applied onto it, by a fixed order:

| Base first | What is applied onto it |
|---|---|
| Vortex | blades spinning in it, fire on it, lightning in it, a pulse through it, chaos running through it |
| Blades | flaming blades, electrified blades, chaotic blades (Force Push: no effect, only the slate tint) |
| Fireball | lightning in the fire, chaotic fire (Force Push: no effect) |
| Shock | a pulsing shock with Force Push's own hit over the bolt's, chaotic shock |
| Force Push | a chaotic push |

Each pair's look is an EFFECT of the mod's own, `fx/effects/spell_weaving_<base>_<overlay>.json`,
in the particle bank's own model and made of the bank's own entities, so the guest's bank carries
it and BOTH looks draw it (`Godot/docs/modding/effects.md`, "Mod effects"). `mod/init.lua` plays it per cast
with `Refii.play_fx_on` on each thing the cast rides — the vortex entity, the fireball's shot,
each hovering Blades sword, each victim of a bolt, shove or chaos for its hold — and
`Refii.stop_fx` ends each play when that thing is gone (the vortex's shutdown, the fireball's
impact, a sword's stab, a victim's hold running out, the weave finishing). What the cast put in
the world is stated by the cast's own events: the vortex entity and its life by `vortex_started`
/ `vortex_ended` (the vortex spell object finishes itself the moment it has spawned its entity, so
nothing else says when the twister ends), each Blades sword by `summoned` as the game creates it
(tinted there; it leaves with `entity_removed`), the fireball's shot by `Refii.spell_parts(serial)`,
and every base's end by `spell_ended`.

Blades + Force Push and Fireball + Force Push have no pair effect and no recipe: their Force Push
is the hits' knockdown alone, so the mod ships thirteen effects for the fifteen pairs. Shock + Force
Push keeps its burst on each victim and adds the bank's own Force Push hit effect
(`fxspl_forcepush_hit`, played once per victim with `Refii.play_fx_on`) over the bolt's.

`fx/recipes.json` keeps one recipe per pair that has an effect, for the base spell's OWN effects: `"with"` names the
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
| Blades + Fire, Shock, Chaos | `fxspl_swords_enflame_blades`, `_lightning_blades` (Chaos: the fire build in violet; Fable III's `_forcepush_blades` has no counterpart: Force Push is the stab's knockdown) | a burn riding each sword, not round it: a spline along the blade in the sword's own frame (its +Y, from the guard 0.08 m out to 0.87 m by the tip — `npc_sword_damocles.mdl`, whose `.mdl.gmd` puts Weapon.Weapon.Hold at -0.083 and Weapon.Weapon.Top at 0.847), so the look lies on the blade and turns with it however the sword flies, with the emitters chained to it, sprites a fraction of a metre — flames over a blue spark and a flickering flare (Fire), white glow, electrical patches and curls (Shock), faint edge smoke, grit and the air bending (Force Push) |
| Fireball + Shock, Chaos | `fxspl_enflame_lightning_targetball`, `fxspl_enflame_push` (Chaos: the push build in violet; Fable III's push pair itself has no counterpart: Force Push is the blast's knockdown) | the shot's own look, on the shot: Shock is a crackling core in a violet storm cloud with bolts flashing off it; Force Push is a fire core wrapped in flame tongues, flinging blobs and sparks, with smoke rolling off; each with a point light |
| Shock + Force Push, Shock + Chaos, Force Push + Chaos | `fxspl_lightning_push` (the Chaos pairs: the same burst with chaos riding it) | one smoke burst blown out over a 2 m disc round the victim each play, with the air rippling outward |
| Vortex + Blades | none: Fable III's twister for it is the vortex's plain dust, and the swords are the Blades casts on each held creature | Fable II's own Blades swords orbiting in the funnel on one attractor, each carrying the sword's own effects, under a point light |

`SpellWeavingLookTests` pins each build (the orbital, the column's speed, lift and keys and the
height they reach; the spline down the blade and the size of what rides it; the shot's emitters
on the ball; the one burst a play) and that no texture is one of Fable III's own.
`SpellWeavingLuaTests` pins the port: no native folder, every `Refii.*` name in the catalog, the
chunk compiling with the game's own compiler, the keys the Lua shares with the declared UI, and
that each cast is stated (its tag, `damage_scale`, `status_applying`, `spell_ended`, `summoned`,
`spell_charge_ended`, `vortex_caught`, `Refii.after`, the partner's placement by `cast_spell`'s
`at`) rather than reconstructed, and the pairs' rules: the vortex lifts only for Blades
(`vortex_started`'s `lifts`) and casts Blades only on the creatures it holds or shudders, once each, by Fable III's roll and window (`Refii.vortex_held`, `Refii.creature_tier`, `Refii.spell_balance`), a
pass-through vortex's shudder on a creature carries the overlay (each pair against each way the
vortex hits: shudder, held hit, ragdoll hit, pulse, drop), and Force Push under Shock, Fireball and
Blades is the hit's `knockdown`.

The side-by-side renders against Fable III's own effects come from the FX Lab scene
(`Godot/src/tools/FxWeaveLab.cs`: `REFII_LAB_BANK` draws Fable III's bank, `REFII_LAB_SWORD_ORBIT`
flies Fable II's Blades sword round a pivot, tilting and rolling, with the look played on it as
`play_fx_on` plays one — in the sword's own frame — so a Blades look, ours or Fable III's, is seen
staying on the blade as it turns). Under the classic look the vortex's look rides the vortex entity's own position, which can
stand behind the travelling twister (an open item).

**The tinted swords.** Each sword of a woven Blades cast wears its partner's colour — red for
Fire, blue for Shock, violet for Chaos, dark slate steel for Force Push — under the woven burn
that rides it. `mod/init.lua` sets it through the game's own appearance tint setter
(`Refii.entity_tint`, the mod API's tint: `sub_82573770` on the sword's renderable), so the
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
branch on that — no projectile, no bolt from the caster, no hand effect; the spell resolves at once
at the request position (the creature the vortex holds, the blade's target, the fireball's impact
point) with its impact effect there and its hit on the target. The pair reads as ONE spell whose
hits carry the second effect. The partner is cast with `Refii.cast_spell`'s `at` on the victim
(or, for a fireball that struck nothing alive, the impact point), and the core places it by the
spell's own rule (`Godot/docs/modding/gameplay.md`, "Casting at a target or a point"): a lightning
strike of zero length on the victim, or from one bolt's reach above the point; a fireball that
detonates where it spawns; blades made over the victim; chaos streamed onto the victim. Force Push
is never cast as a partner: under Shock, Fireball and Blades it is the hits' own knockdown, and
under a Vortex the shove is its own knockdown hit. Chaos has nothing to turn at a point, so a
fireball that struck nothing alive casts no Chaos partner (nor a Force Push one).

**Every cast is known by its tag.** The launch writes `spell_launch`'s `tag`, and the game's
events of that cast carry it — `spell_created`, `vortex_started`, `summoned`, `hit_landed`,
`fireball_impact`, `spell_ended` — a hit the factory lands inside the cast itself (a targeted
Lightning's bolt) included, since the core stamps the cast's serial before the factory builds it.
A partner is cast with its base's tag negated (`Refii.cast_spell`'s `options.tag`) and Fable III's
damage share (`options.damage_scale`, applied by the hit receiver to every hit of that cast where
the hit is decided), so its hits are the weave side wherever they arrive — they never bind as a
base or cast a partner — and its status is matched on `status_applying` by the same tag. The
tags are the mod's own range, exact in the game Lua's 32-bit numbers.

**Every cast is its own weave.** A woven release is a record from its launch until its base has
finished — the tag its launch wrote, the base's serial, its pair, its power, and what it put in
the world: the vortex entity, the fireball's shot, each hovering sword. A launch that made no spell
leaves a record no event of a cast reaches, dropped at the next tick. The partner a hit casts is
the partner of the weave whose tag that hit carries, so two vortices out at once from two slots each cast their
own partner, and re-picking a slot's partner changes nothing already in flight.

**Tune them in the FX Lab**, the Mod Studio's fourth tab: open a pair's effect in the effect editor
(every entity, timeline and visual), or pick a recipe and one of its base effects and turn the tint
against a live preview dressed by the same code the game uses, with Fable 3's description of the
pair beside it. Save writes `fx/effects/<name>.json` or `fx/recipes.json` back into the mod; a
running game takes it with `refii_action fx-recipes-reload`, and in a dev tree it reads this
folder's `fx/recipes.json` directly, so an edit here needs no repack. The same preview runs from a
shell as `res://debug/fx_weave_lab.tscn` (`Godot/src/tools/FxWeaveLab.cs`), which draws a base
plain, dressed and an optional reference effect side by side and writes PNGs at chosen frames.
The Lua itself reloads live with the Studio's **↻ Reload mods + Lua** (`Godot/docs/modding/live-iteration.md`, "Live
iteration"): the handlers are dropped and `mod/init.lua` runs again over the live state.

## How Fable III builds a weave (from its binary; `/home/loreaxe/Fable 2/Fable 3/README.md`)

One cast makes ONE spell object: the primary, carrying its partner's type, the combo index and
the power. The primary's own class then issues a SECOND, real Targeted cast of the partner on the
victim at its own moment — the vortex per creature it grabs or holds, the fireball on impact,
lightning per victim hit, each blade on its stab — through a three-slot delayed caster with
`WeaveSpellShotDelayTime` from the combo's `WeaveSpellData` (0.25 s for the vortex combos,
default 1 s, ±5 %); the partner's balance is its `WeaveSpellData` times the 0.7 damage and
duration multipliers (0.75 in the vortex records). Our partner takes both: `weave_damage` and
`weave_duration`, 0.7 for every pair (the vortex records' 0.75 is not applied). Burning, shock and push are the partner
spell's own hit code. Vortex + Swords is one Swords cast per victim it holds or cannot lift with 3–5 blades from the
weave balance, which is the "5 blades appear over each enemy". Slow Time combos repeat the partner
every `WeaveSpellRepeatTime`; only the Vortex rolls `WeaveSpellAffectProbability` (shipped 100). Our port does the same with Fable 2's spells, at the same moments: a victim the base hits
(the `hit_landed` event), each creature a vortex base holds (`vortex_caught`, caught true — its
own capture, for Blades only, by the vortex rule below; none from its hits on what it holds) and
each creature its shudder hits (`hit_landed` on a creature it does not hold, Blades only), and
a fireball's detonation that struck nothing
alive (`fireball_impact` with `detonated` and no `hits`, the partner cast at the impact point).
Each queues a Targeted cast of the partner through the game's own spell factory
(`Refii.cast_spell` with `at`), `shot_delay` later (`shot_delay_vortex` for a vortex), and the
partner's hits are scaled by `weave_damage`. Where Fable III's partner is only an effect on the
victim, ours is cast nowhere: the vortex casts only Blades, on what it holds (and checks it still
holds it when the delay runs out) and on what it cannot lift; a Vortex with Fire, Shock, Chaos or Force Push passes through,
its one hit per creature carrying the overlay; and Force Push under Shock, Fireball or Blades is
the knockdown of their own hits. There is no status installing of the mod's own:
burning, shock, push, chaos and blades are the partner spell itself, and the burn, shock or chaos
it installs lasts `weave_duration` of its own time — the installer's `seconds` on
`status_applying`, matched by the partner's tag (chaos: the scaled time is also what the game
sends the co-op partner).

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
only by the engine's pick (the `spell_pick` event first, which may refuse or change it): a spell type
id, or -1 for none. Saves from before kept them in the world save's Lua
(`RefiiModSave.SpellWeavingPartner1..5`, `Partner2_1..5`); a slot with no saved choice takes that
one (the engine, through the pick's `legacySaveKey` / `legacySaveKeyTwo` in `ui/spellwheel.json`,
once after each Lua rebuild or save load, on the first tick that player's hero stands where it is
played). There is no settings row for them: a settings page holds one machine's values, a partner
is each player's own, and the selectors are where each player picks theirs (the title's "Spell
Weaving" row says how).

| id | spell | id | spell |
|---|---|---|---|
| 0 | Lightning (Shock) | 4 | Vortex |
| 1 | Fireball (Inferno) | 5 | Chaos |
| 2 | Slow Time (not weavable) | 6 | Force Push |
| 3 | Blades | 7 | Raise Dead (not weavable) |

In a running game the debug channel's game Lua (`refii_lua`) can set one directly, without the
`spell_pick` event — player 1, slot 2's partner becomes Vortex:
`Refii.player_save(1, "spell_weaving.partner_2", 4)`. (The in-effect value below follows at the
next pick, purchase, spell rank or load: nothing raises an event for a direct store write.)

The partner in effect per slot (chosen, learned, and the ability at that slot's level or more) is
stated where the hero is played as `player:spell_weaving.partner_active_1..5` (-1 when none),
restated on the tick after a pick (`spell_pick`), a purchase or unlearn (`mod_ability`), a spell's
rank (`ability_level`), the hero arriving (`hero_ready`), a save load or a Lua rebuild; the
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
| `weave_damage` | 0.7 | The share of their damage the partner's hits land at (Fable 3's general damage multiplier; the cast's `damage_scale`), and the extra share of the vortex's own hit a pass-through Vortex + Fire or Shock lands. |
| `weave_duration` | 0.7 | The share of its own duration the partner's burn, shock or chaos lasts (Fable 3's duration multiplier; `status_applying`'s `seconds`), and of the overlay's own time a pass-through vortex puts on. |
| `vortex_affect_probability` | 100 | Vortex + Blades: the chance in a hundred a creature it catches is rolled to get its Blades (Fable III's `WeaveSpellAffectProbability`, 100 at every power of the vortex combos). |
| `vortex_window_start`, `vortex_window_end` | 0.15, 0.6 | The share of the twister's life (its own Duration, `Refii.spell_balance`) between which the roll puts the creature's Blades, never before the catch (Fable III's vortex). |
| `vortex_delay_cap` | 0.95 | `shot_delay_vortex` never reaches past this share of the time the twister has left (Fable III). |
| `vortex_swords_1` .. `_5` | 3, 4, 4, 4, 5 | The swords Fable III's Vortex + Swords makes per creature at each power; the Blades partner is cast at the highest power up to the vortex's whose own swords per cast are no more. |
| `sword_tint_fire_r/_g/_b` | 1.35, 0.24, 0.1 | A Blades sword woven with Fire: a red blade. |
| `sword_tint_shock_r/_g/_b` | 0.35, 0.75, 3.0 | Woven with Shock: a cold blue blade. |
| `sword_tint_chaos_r/_g/_b` | 0.5, 0.22, 3.0 | Woven with Chaos: a violet blade. |
| `sword_tint_push_r/_g/_b` | 0.28, 0.32, 0.55 | Woven with Force Push: a dark slate steel with no golden glow. |

The sword tints are multipliers of the golden blade's lit colour (1 leaves a lane alone); see "The
tinted swords" above. The Lua reads every setting through `Refii.setting`, the values composed in
at session start and pushed again on a mods reload.

Each player has their own Spell Weaving level and their own partner choices. The ability row in
the book acts for, and shows, the player whose book is open: player two's own book in local co-op,
the joiner's book on the joiner's machine; the engine's purchase spends that player's experience.
The selectors pick for their own player, and the partner picker's prompts unlock from that same
player's level. The level and the choices are SAVED player values
in Refii's player store (`Refii.player_*`, `Godot/docs/modding/handles-and-players.md`): the same on both machines of an
online session, written beside the save the game writes that hero into (`refii_player.txt`) —
player one's main save, a local player two's own save, an online joiner's own save on the
joiner's machine — and read back when the game loads that hero. So player two keeps their level
and partners when they go home. Saves from before kept player one's level in the world save's Lua
(`RefiiModSave.SpellWeaving`); the engine takes it from there once (`legacySaveKey`).
The HUD glyph follows each player's charge meter, in their own half on split screen.

Online, the mod has no co-op plumbing of its own; it follows the mod API's rules. A cast runs on
both machines and the host's copy is the spell that deals the damage: hits are decided on the
host. So partners are cast only where the world runs (`Refii.world_here`: the host, or the one
machine alone and in local co-op) — player two's included — one per victim, or one at the impact
point for a fireball that struck nothing alive; an online joiner casts none, which would otherwise
have cast a second "struck nothing alive" partner for every fireball whose blast it saw land no
hits. The look follows the real victims on both machines: the API relays every hit a hero deals
or takes from the host to the joiner as a replica (`Refii.event_replica`), and a replica only
notes its victim on its weave — it never casts. Each cast is one identity on both machines
(`Godot/docs/modding/gameplay.md`, "One cast, one identity online"): the machine that did not
decide a cast takes the decider's tag into its own copy, and a relayed hit arrives with this
machine's serial of the cast and that tag, so the weave is found by tag, else by this machine's
serial — the same lookup as every other event. The two machines write their tags from the two
halves of the mod's range, so a tag taken from the peer never names a weave launched here under its
own. The one case the mapping cannot state is a hit for a cast whose pairing has not arrived (the
core holds such a hit up to a second first); it carries the host's numbers, `Refii.host_serial`
answers nil for it, and the weave leaves it alone rather than guess — the cast's later events carry
its tag and the look. A targeted Lightning, Force Push or Chaos rides on the target the cast states
until its hits arrive; an untargeted one stands at the caster for that network moment, then on its
victims.

## Next

- Tune the HUD glyph's place beside the charge meter in a run (`ui/hud.json` offset).
