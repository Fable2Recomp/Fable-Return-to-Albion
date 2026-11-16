# Souls Combat (Fable III)

Stamina for swings, rolls and blocks; mana for spells; poise; committed strikes; cast recovery; and
a toggled lock-on with a reticle on the target. A Lua mod on the Fable III mod API (Godot/docs/modding/fable3.md): every
rule is a `Refii.on` handler in `mod/`, every number a setting in `settings.json`.

The mana and stamina bars are the mod's own (`ui/hud.json` meter pieces reading the pools from the
player store), top left.

## The enemies, the camera and the hero's turning

`ai.lua` patches Fable III's own combat AI tables (`CombatWheel`, `CombatInfo`, the combat styles): two
attackers at once instead of one, shorter attack gaps by tier (fodder, elite, boss), enemies that
punish a heal, a whiffed swing or a cast, a riposte out of a block, circling instead of standing, and
boss enrage. Fable III blocks in the engine, so the block chance is each style's own
`PercentChanceToBlockInitialHit`, raised by tier. `camera.lua` gives every camera that frames one hero
a fixed orbit, no combat or spell camera, no pull toward foes, a pitch that stays where it is put, and
the lock framed; a shared screen keeps the game's camera. `movement.lua` sets each hero's turn rates
and friction and takes the turn-in-place animations out, so the hero pivots. Fable III has no parry
window to set, so there is no parry setting here.

## Lock-on

R3 on the pad (G on the keyboard; both rebindable on the controls page) toggles the lock-on, as in
Dark Souls. Fable III's LT is left to the game — its own hold-to-lock, Hold Hands and the ranged
zoom — so holding a hand never locks on. The mod takes R3 from the game: the camera mode stays on V
on the keyboard. While locked, the target carries the mod's reticle; it moves with the game's pick
and goes when the lock ends or the target dies.

## Balance

Every number below is a setting in `settings.json`, read live.

**Stamina** starts at 100 and grows with the Road to Rule's Melee and Ranged levels, the way health
grows from an ability in the game's own data (AbilityHealthBase, 150 to 750). The 750 is spread over
the ten levels, so each level adds 30: 100 at none, 190 at Melee 3, 250 at Melee 5, 400 at both 5.
`stamina_scale` scales the whole curve. A swing costs `swing_cost` (18), a roll `roll_cost` (22), a
block `block_cost_per_damage` (1.2) per point of damage stopped. Stamina refills at
`stamina_regen_fraction` (55%) of its maximum a second, `stamina_regen_delay` (0.7 s) after a spend.
The swing that empties it still lands, down to `stamina_floor` (-25).

**Mana** starts at 100 and grows with the Magic level: the same 750 over its five levels, so each
level adds 60, up to 400. Every spell that launches costs `mana_per_level` (12) per power level, so a fully
charged spell costs more than a quick cast. Mana refills in `mana_regen_seconds` (20 s, Fable III's
own health refill time), `mana_regen_delay` (2.5 s) after a cast. `mana_scale` scales the curve.

**Health** is the game's own, and the mod does not change it. Fable III grows the hero's maximum
health through General Health (ability 16), the real estate and worship bonuses, and its bonus
containers. The weapon levels do not raise it. Each of those writes the saved maximum, so a mod that
raised health would leave the save changed after it is removed.

**Healing** is Fable III's own: 15 s without a hit, then a twentieth of the maximum a second, plus
potions and food. The pools follow the potions. A health potion restores stamina by its share of the
health ceiling (the standard potion 50%, Health 5 70%, Life 100%). A mana or Wisdom potion restores
mana by its share of the 300 mana ceiling. `potion_restore_scale` scales both. A hero brought back
from being downed gets both pools full.

**Health bar.** Fable III draws none; the mod's red bar reads the game's Health.Get / Health.GetMax
each tick, for each player.
