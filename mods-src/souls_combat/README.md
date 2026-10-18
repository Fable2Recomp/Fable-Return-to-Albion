# Souls Combat

Refii's first hard mod, and the worked example for everything a mod can carry: Fable 2's combat
reworked toward Elden Ring. Plan and evidence: `docs/hard_mods_plan.md` (Part C).

It ships with the app. The package carries this tree as `bundled-mods/souls_combat/`, and the
launcher packs it into `assets/mods/souls_combat.f2mod` on start (`Engine_ModBundled.cs`) —
installed switched off; enable it in the Mods panel and allow its native code when asked. In a
dev tree the launcher packs straight from `mods-src/`. A copy edited in the Mod Studio is never
overwritten by an update. Every file here can also be written from a script in the editor
(`RefiiModApi`, Modding Guide → "Script API & Hard Mods").

Nothing in this tree is game data: the Lua patches the shipped scripts' tables after they load,
it never replaces a script; the icons and the card are our own art (`tools/souls_combat_icons.py`).

## What it changes

| Piece | File | What it does |
|---|---|---|
| Enemy pacing | `mod/init.lua` | 2 attackers hold the attack token at once (shipped 3), a token is held 5 s (shipped 3), at least 1.6 s between one enemy's swings, and every style queues for the token (`NonFormation` cleared). |
| Lock-on | `native/lockon.cpp` | LT TOGGLES the lock instead of being held. The lock is the mod's own state per hero, flipped by the LT press (GameAction 49) and written into the game's two switches — the targeting component's force byte and the player's look-at flag — through the game's own setter; the release (50) is swallowed while locked. While locked the game adds player mode 4, which outlines the target and aims guns and spells at it, and re-targets the next enemy when the locked one dies. Half a second with nothing valid to lock (dead, gone, or nothing in range) releases the lock. |
| Stamina | `native/stamina.cpp`, `native/roll.cpp`, `native/guard.cpp` | A pool per player hero. A committed swing costs 18, a roll 22, a blocked hit 1.2 per point of damage; at an empty pool the next swing, roll or block is refused (the pool may dip to −25 so the emptying swing still lands), and a block the pool cannot pay for breaks and the hit lands in full. Refills at 55/s after 0.7 s without spending. |
| Stamina ability | `native/stamina_ability.cpp`, `ui/abilities.json`, `language/en-uk/text/book.babel.csv` | A sixth Strength ability, five levels, priced exactly as Toughness (the same experience costs, bought through the game's own spend, Strength then General). Max stamina follows Toughness's health steps: 100 untrained, then along the same curve to 271 — so a full stamina bar is as long as a maxed health bar. Y unlearns a level through the game's own confirm and result boxes, refunding what the game would. The level persists in the save as `RefiiModSave.SoulsStamina`. |
| Poise | `native/poise.cpp` | A hit staggers its victim only once 30% of their maximum health has landed within 3 s; lesser hits are absorbed without a flinch. Enemies and the hero alike. A parried or blocked hit, a knockdown, a ranged hit and a killing blow keep the game's own response. |
| Commitment | `native/commitment.cpp` | Once the hero's swing has started it cannot be cancelled into a roll, another swing or a block until a quarter-second later in the animation than the game allows (the strike's `ALLOW_ACTION_INTERRUPT` permission is granted late). |
| Timed parry | `mod/init.lua` | The parry window is 0.25 s (shipped 1 s) in a 90° wedge (shipped 70°), through the hero's own balance table. |
| Mana | `native/mana.cpp` | Mana for spells. Max grows from 100 to 271 evenly over the 40 spell ranks (ids 7..14 on the stats component). Every spell that launches costs 12 per power level, taken when it launches (a level-L release is Fable 2's cascade of slots 1..L, so it costs the sum); an empty pool refuses the charge, a short one caps the charge at the top slot it can pay for, and a lower slot of the cascade is skipped when paying it would leave the top slot unpaid. Refills 6/s after 2.5 s. |
| HUD | `ui/hud.json`, `art/gui/hud/*.tex.png` | Fable 2's own health widget three times: the original, then two copies of it (`clones`) — mana with a drop icon and its bar hue-shifted blue, stamina with a bolt icon and its bar shifted green — each fed from the mod's values and drawn against the shared cap of 271, the way the health bar is drawn against the game's maximum health. The copies drop and fade with the original; all three are drawn 15% larger. The co-op partner gets a stamina bar under their own health widget. |
| Ability card | `art/gui/abilitytarot/souls_combat_stamina.tex.png` | The still the abilities book shows for Stamina, where the game's abilities play a movie. |

Found on the way, so not built: **the roll is already invulnerable for its whole length** — the
hit dispatch ignores a victim whose component-31 flag the roll clears for its duration.

## How the pieces talk

- The native parts are handlers on the mod API's events (`docs/mod_api.md`,
  `core/refii_mod_events.h`): stamina spends on `MeleeSwing` and `RollStarted` and refuses on
  `AbilityRequest` and `Roll`; the guard breaks through `Block` (fired once per hit, so a block
  is charged once); poise absorbs through `HitResponse`; the lock-on toggles through
  `GameAction`; mana gates `SpellCharge`, caps `SpellChargeLevel`, skips unpaid cascade slots
  through `SpellLaunch` and spends on `SpellLaunched`; the Stamina ability buys and refunds through `refii_mod_ability_cost`,
  `refii_mod_spend_experience` and `refii_mod_give_experience`, and keeps its level in the save
  through `refii_mod_save_set_int`, re-read on `Lua` and `SaveLoaded`. The one function still
  hooked by name is the player's look-at writer (`lockon.cpp`), which no event covers.
- Native code publishes floats (`refii::mods::Publish("souls_combat.stamina", v)`); the HUD and
  the ability row read them as `mod:souls_combat.<name>`.
- The UI hands the native code a setting (`souls_combat.buy_stamina`, `refund_stamina`) as an
  incrementing count; the handler sees the change and does the purchase or refund.
- Every seam is named in both `versions/*/refii_config.toml`, so the same source builds against
  the disc and the Title Update; the reasoning is in each file's header.

## Balance

Every number is a setting in `settings.json`, read by the native code at the moment it is used
(`refii::mods::Setting(key, default)`), so tuning needs no rebuild: edit the file in the Mod
Studio (or `RefiiModApi.SetSetting`) and re-mount, or change a value in a running game through the
debug channel — `refii_action "mod-setting souls_combat.swing_cost 24"` — and feel it at once.

| Key | Default | Meaning |
|---|---|---|
| `swing_cost` | 18 | Stamina a committed swing costs. |
| `roll_cost` | 22 | Stamina a roll costs. |
| `block_cost_per_damage` | 1.2 | Stamina a blocked hit costs per point of its damage. |
| `stamina_floor` | -25 | How far below empty the emptying swing may take the pool. |
| `stamina_regen_delay` / `stamina_regen_per_second` | 0.7 / 55 | Refill after the last spend. |
| `mana_per_level` | 12 | Mana a cast costs per charge level. |
| `mana_regen_delay` / `mana_regen_per_second` | 2.5 / 6 | Refill after the last cast. |
| `poise_fraction` | 0.3 | Share of maximum health that must land within the window to stagger. |
| `poise_window_seconds` | 3 | The window. |
| `commit_extra_seconds` | 0.25 | How much later than the game a swing may be cancelled. |
| `lock_drop_seconds` | 0.5 | Time without a valid target before the lock releases. |

The AI pacing knobs are Lua (`mod/init.lua`, the `SoulsCombat` table) and the parry values are the
hero's balance table there too; both re-read on the next session.

## Still open

- **Difficulty multipliers** (`DifficultyLevelMultipliers.DamageCaused` / `DamageReceived`):
  reported to apply only above difficulty 1; not verified, not used.
- **Death and respawn** (Pass 3): a checkpoint system of its own, planned separately. Its seam is
  the `HeroDowned` event, which cancels the game's whole knockdown flow with the hero kept at 1
  health.
