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
| Movement | `mod/init.lua` | The hero turns toward the stick at 720°/s standing and 540°/s running (shipped 350/115) and reaches speed faster (friction 9, shipped 4). The turn-in-place and run-turn animations are taken out of every locomotion state's `States` list, `LocomoteForwards` accepts any stick direction (shipped ±120°) and the idle pose may turn, so the hero pivots on the spot. Patched before the hero exists: the player control copies the speed table by name when it loads one (`sub_8283E408`), and the states are parsed once on the first activation (`sub_827F7A78`). |
| Camera | `mod/camera.lua` | Every camera that frames one hero: single player, online co-op and split screen, where each player's camera follows their own hero. A shared screen keeps the shipped camera, which has to frame both heroes. The camera orbits the hero at a fixed 4 m (shipped: anywhere from 3.4 to 5 m), centred on the hero, without the shipped slot the hero may wander in. Nearby foes and villagers no longer pull the frame, and there is no high combat camera (the shipped one blends in with a foe inside 10 m). Stick Y pitch stays where it is put; the shipped pitch springs back to level. A lock keeps the target within a cos 0.97 cone (shipped 0.9) and turns at 420°/s (shipped 250). The camera is all Lua (`camera/stringandcagecamera.lua`), and each override wraps a method `FrameUpdate` calls by name. |
| Enemy pacing | `mod/init.lua` | The shipped wheel grants one attack token per defender. Each defender now has 2 tokens and 4 inner-ring slots (shipped 3), and a token held without attacking lapses after 1.5 s (shipped 3). `NonFormation`, the game's own aggro mode, is kept. Swing gaps and the courtesy waits after anyone hits the target are capped by tier: fodder 2 s, elite 1.2 s, boss 0.7 s; a style that ships quicker keeps its own. Attack combos are `Uninterruptable`, so a roll away leaves the enemy finishing its string. Right after a hero heals or whiffs a swing, up to two enemies in reach attack as if they held a token. |
| Enemy defence | `mod/init.lua` | Every unblocked hit rolls a tier chance to raise the guard (15/35/50%). The shipped styles block only after N hits. Bandit-like styles that are pushed back by a block get a two-strike riposte. The shipped name typo that kept the balverines from ever dodging (`BLOCK_MELEE_DODGE`) is aliased. Waiting enemies circle with the shipped strafes instead of standing and taunting. Kiting is answered: a token holder sprints to within 3 m before it strafes (shipped: strafe-walks from 5 m, slower than a hero backs off), a spell launch hands one more enemy a token for 1.5 s, and the shipped anti-spam rush (the group breaks formation for 8 s) fires on 2 spells or 3 shots in 6 s on every grouped style (shipped 4 in 4 s, bandits only). Boss-tier styles with an `Enrage` table now enrage below half health; no shipped style ever ran `UpdateEnrage`. Trolls wait at most 3 s after landing a hit. |
| Lock-on | `native/lockon.cpp` | LT TOGGLES the lock instead of being held. The lock is the mod's own state per hero, flipped by the LT press (GameAction 49) and written into the game's two switches — the targeting component's force byte and the player's look-at flag — through the game's own setter; while locked the release (50) ends nothing, since the game's own case honours the force byte and the look-at flag is held by the look-at writer hook. While locked the game adds player mode 4, which outlines the target and aims guns and spells at it, and re-targets the next enemy when the locked one dies. Half a second with nothing valid to lock (dead, gone, or nothing in range) releases the lock. |
| Stamina | `native/stamina.cpp`, `native/roll.cpp`, `native/guard.cpp` | A pool per player hero. A committed swing costs 18, a roll 22, a blocked hit 1.2 per point of damage; at an empty pool the next swing, roll or block is refused (the pool may dip to −25 so the emptying swing still lands), and a block the pool cannot pay for breaks and the hit lands in full. Refills at 55/s after 0.7 s without spending. |
| Stamina ability | `native/stamina_ability.cpp`, `ui/abilities.json`, `language/en-uk/text/book.babel.csv` | A sixth Strength ability, five levels, priced exactly as Toughness (the same experience costs, bought through the game's own spend, Strength then General). Max stamina follows Toughness's health steps: 100 untrained, then along the same curve to 271 — so a full stamina bar is as long as a maxed health bar. Y unlearns a level through the game's own confirm and result boxes, refunding what the game would. Each player has their own level: the row acts for, and shows, the player whose book is open. The level is a saved player value (`souls_combat.stamina_ability`): it goes home with the hero's own save (see Co-op). |
| Poise | `native/poise.cpp` | A hit staggers its victim only once 30% of their maximum health has landed within 3 s; lesser hits are absorbed without a flinch. Enemies and the hero alike. A parried or blocked hit, a knockdown, a ranged hit and a killing blow keep the game's own response. |
| Commitment | `native/commitment.cpp` | Once the hero's swing has started it cannot be cancelled into a roll, another swing or a block until a quarter-second later in the animation than the game allows (the strike's `ALLOW_ACTION_INTERRUPT` permission is granted late). |
| Timed parry | `mod/init.lua` | The parry window is 0.25 s (shipped 1 s) in a 90° wedge (shipped 70°), through the hero's own balance table. |
| Cast recovery | `native/cast_recovery.cpp` | A launched spell plants its caster for 0.6 s. The game holds a quick cast's movement only until the spell launches; from then any stick input cancels the rest of the animation, which made a step back between casts free. The plant is the player component's own movement-speed override, the pair the game's scripts set (`Player.SetPlayerMovementSpeedOverridden` / `SetOverriddenMovementSpeedMultiplier`: byte +0x1E, float +0xCC, read by the locomotion update at 0x822b2c2c). It is set to 0 and given back afterwards. Turning, rolling and blocking still work. The override is taken only when no script holds it. |
| Mana | `native/mana.cpp` | Mana for spells. Max grows from 100 to 271 evenly over the 40 spell ranks (ids 7..14 on the stats component). Every spell that launches costs 12 per power level, taken when it launches (a level-L release is Fable 2's cascade of slots 1..L, so it costs the sum); an empty pool refuses the charge, a short one caps the charge at the top slot it can pay for, and a lower slot of the cascade is skipped when paying it would leave the top slot unpaid. Refills 6/s after 2.5 s. |
| HUD | `ui/hud.json`, `art/gui/hud/*.tex.png` | Fable 2's own health widget three times: the original, then two copies of it (`clones`) — mana with a drop icon and its bar hue-shifted blue, stamina with a bolt icon and its bar shifted green — each fed from the mod's values and drawn against the shared cap of 271, the way the health bar is drawn against the game's maximum health. The copies drop and fade with the original; all three are drawn 15% larger. Player two gets the same pair under their own health widget (`CoopHealth`), fed from `mana2` and `stamina2`; on split screen and on a joiner's screen, player two's three bars stand where player one's do. |
| Ability card | `art/gui/abilitytarot/souls_combat_stamina.tex.png` | The still the abilities book shows for Stamina, where the game's abilities play a movie. |

Found on the way, so not built: **the roll is already invulnerable for its whole length** — the
hit dispatch ignores a victim whose component-31 flag the roll clears for its duration.

## Co-op

Both players are principals, in every co-op mode (shared screen, split screen, online):

- Stamina, mana, poise, the guard, roll, commitment, cast recovery and the lock-on are per hero.
- Each player's maximum stamina follows their own Stamina level, and each player's maximum mana
  follows the spells their own hero has learned.
- The Stamina row in the abilities book acts for the player who opened the book
  (`refii_mod_menu_player`): player two's own book in local co-op, the joiner's book on the
  joiner's machine. It spends that player's experience and shows that player's level, cost and
  refund.
- Every per-player value is in Refii's player store (`refii_mod_player_*`, `docs/mod_api.md`),
  so both machines of an online session read the same pools, and each spend counts once wherever
  the game decides it: a swing, a roll and a spell charge where the hero is played, a block (and
  the guard breaking) on the host, which resolves every hit. The refill runs where the hero is
  played. A cast runs on both machines (the host's copy is the spell that deals the damage), so
  the mana cap applies on both, from the same pool, and the launch is paid once. Casting plants
  the hero where the hero is played.
- Enemies punish either hero's heal or whiff. The AI runs only on the host, and a swing or an
  item use fires where the hero is played; the mod API relays both to the other machine, so the
  host receives player two's `melee_swing` and `item_used` as replicas (`Refii.event_replica()`)
  and the mod simply times each window from the event it receives, for `e.player`, on any machine
  — no co-op code of its own. A hit by that hero landing on the host at or after the swing cancels
  the whiff; a relayed swing arrives a network moment after it was made, so for one a hit up to
  0.25 s before its arrival counts too. Heroes are told apart with `Refii.player_of`, since player
  two's body on the host is not a `Player` there.
- Poise is decided where the hit's response is authoritative. The host runs every hit's response,
  so it keeps every pool, player two's included (from the same hits the joiner sees, so its copy
  agrees). The joiner runs only hits on or by player two: it decides player two's own pool, and
  its copy of a hit by player two on an enemy (`refii_mod_event_replica`) leaves the pool alone
  and plays the game's own predicted response; the host decides that enemy's stagger.
- The Stamina level is a SAVED player value: it is written beside the save the game writes that
  hero into (`refii_player.txt` in the save's folder) — player one's main save, a local player
  two's own save, an online joiner's own save on the joiner's machine — and read back when the
  game loads that hero. So player two keeps their level when they go home, and brings it to the
  next session. Saves from before this kept player one's level in the world save's Lua
  (`RefiiModSave.SoulsStamina`); it is taken from there once.

## How the pieces talk

- The native parts are handlers on the mod API's events (`docs/mod_api.md`,
  `core/refii_mod_events.h`): stamina spends on `MeleeSwing` and `RollStarted` and refuses on
  `AbilityRequest` and `Roll`; the guard breaks through `Block` (fired once per hit, so a block
  is charged once); poise absorbs through `HitResponse`; the lock-on toggles through
  `GameAction`; mana gates `SpellCharge`, caps `SpellChargeLevel`, skips unpaid cascade slots
  through `SpellLaunch` and spends on `SpellLaunched`; the Stamina ability buys and refunds through `refii_mod_ability_cost`,
  `refii_mod_spend_experience` and `refii_mod_give_experience`, and keeps each player's level as a
  saved player value (`refii_mod_player_save`, `souls_combat.stamina_ability`), which goes home
  with that hero's own save. The one function still
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
| `parry_window_seconds` | 0.25 | The timed parry's window (shipped 1). |
| `cast_recovery_seconds` | 0.6 | How long a launched spell plants its caster; 0 turns it off. |
| `turn_speed_static` / `turn_speed_running` | 720 / 540 | The hero's turn rate in degrees per second, standing and at full speed. |
| `move_friction` | 9 | How fast the hero's speed follows the stick, per second. |
| `camera_distance` | 4 | The orbit distance outdoors; indoors is 85% of it. |
| `camera_height` / `camera_focus_height` | 0.9 / 1.6 | The camera's rise over the focus at the orbit distance, and the focus's height over the hero. |
| `camera_pitch_speed` | 0.09 | Right-stick pitch per update. The orbit speed is the host's per-device camera feel, scaled by the player's sensitivity option. |
| `camera_lock_tightness` / `camera_lock_turn_speed` | 0.97 / 420 | The cosine of the cone a locked target is kept inside, and how fast the camera turns to keep it there. |
| `ai_attackers_at_once` / `ai_ring_slots` / `ai_token_seconds` | 2 / 4 / 1.5 | Attack tokens per defender, inner-ring slots, and how long a token lasts unused. |
| `ai_gap_fodder` / `ai_gap_elite` / `ai_gap_boss` | 2 / 1.2 / 0.7 | The most each tier waits between attack sequences. |
| `ai_block_chance_fodder` / `_elite` / `_boss` | 15 / 35 / 50 | Percent chance an unblocked hit raises the guard. |
| `ai_punish_heal_seconds` / `ai_punish_whiff_seconds` | 1.6 / 1.1 | How long after a heal, and up to how long after a missed swing, enemies punish. |
| `ai_punish_cast_seconds` | 1.5 | How long after a hero's spell launch one more enemy gets a token, even out of reach, and comes for them. |
| `ai_close_in_distance` | 3 | How close a token holder sprints before it strafes (a style's own distance, 5 m by default, otherwise). |

The settings the Lua reads (`mod/init.lua`, `mod/camera.lua`, through `Refii.setting`) take effect
at the next session. `mod-setting` changes only the native code's copy. In a running game, set the
table field directly with the debug channel's Lua (`refii_lua`), for example
`SACCamera.HeightAdjustSpeed = 0.12`. For the zoom-level settings, also change
`RefiiSettings["souls_combat.camera_distance"]` and raise `SoulsCamera.Generation` so the levels
are reshaped.

## Still open

- **Difficulty multipliers** (`DifficultyLevelMultipliers.DamageCaused` / `DamageReceived`):
  reported to apply only above difficulty 1; not verified, not used.
- **Death and respawn** (Pass 3): a checkpoint system of its own, planned separately. Its seam is
  the `HeroDowned` event, which cancels the game's whole knockdown flow with the hero kept at 1
  health.
