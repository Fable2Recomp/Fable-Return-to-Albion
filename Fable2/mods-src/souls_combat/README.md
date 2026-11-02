# Souls Combat

Refii's combat overhaul, and the worked example for everything a mod can carry: Fable 2's combat
reworked toward Elden Ring, in Lua alone, on the mod API's stated events and calls — no native
code, no hooks, no guest memory. Plan and evidence: `docs/hard_mods_plan.md` (Part C; Part I for
the move onto the stated primitives).

It ships with the app. The package carries this tree as `bundled-mods/souls_combat/`, and the
launcher packs it into `assets/mods/souls_combat.f2mod` on start (`Engine_ModBundled.cs`) —
installed switched off; enable it in the Mods panel. It is Lua and data only — a soft mod: nothing
is compiled, no consent is asked. In a dev tree the launcher packs straight from `Fable2/mods-src/`. A copy edited in the Mod Studio is never
overwritten by an update. Every file here can also be written from a script in the editor
(`RefiiModApi`, Modding Guide → "Script API & Hard Mods").

Nothing in this tree is game data: the Lua patches the shipped scripts' tables after they load and
answers the mod API's events, it never replaces a script; the icons and the card are our own art
(`tools/souls_combat_icons.py`).

## What it changes

| Piece | File | What it does |
|---|---|---|
| Movement | `mod/movement.lua` | The hero turns toward the stick at 720°/s standing and 540°/s running (shipped 350/115) and reaches speed faster (friction 9, shipped 4). These are each hero's OWN locomotion values (`Refii.set_locomotion`), stated for each player when their hero arrives (`hero_ready`) and again when a setting changes (`setting_changed`): two players may move differently, and a retune is felt at once. The turn-in-place and run-turn animations are taken out of every locomotion state's `States` list, `LocomoteForwards` accepts any stick direction (shipped ±120°) and the idle pose may turn, so the hero pivots on the spot. Those are the locomotion STATES' own content, shared by every hero and patched in the state tables before the hero exists (the states are parsed once, on the first activation, `sub_827F7A78`): a state's own `MovementParams` win over the hero's values while it is active, so the Idle state's 1°/s turn cap is raised in the state, not through the hero. |
| Camera | `mod/camera.lua` | Every camera that frames one hero: single player, online co-op and split screen, where each player's camera follows their own hero. A shared screen keeps the shipped camera, which has to frame both heroes. The camera orbits the hero at a fixed 4 m (shipped: anywhere from 3.4 to 5 m), centred on the hero, without the shipped slot the hero may wander in. Nearby foes and villagers no longer pull the frame, and there is no high combat camera (the shipped one blends in with a foe inside 10 m). Stick Y pitch stays where it is put; the shipped pitch springs back to level. A lock keeps the target within a cos 0.97 cone (shipped 0.9) and turns at 420°/s (shipped 250). The camera is all Lua (`camera/stringandcagecamera.lua`), and each override wraps a method `FrameUpdate` calls by name. |
| Enemy pacing | `mod/ai.lua` | The shipped wheel grants one attack token per defender. Each defender now has 2 tokens and 4 inner-ring slots (shipped 3), and a token held without attacking lapses after 1.5 s (shipped 3). `NonFormation`, the game's own aggro mode, is kept. Swing gaps and the courtesy waits after anyone hits the target are capped by tier: fodder 2 s, elite 1.2 s, boss 0.7 s; a style that ships quicker keeps its own. A boss is a leader, a style with a boss health bar or a troll (the styles' own data); elite is what the creature's definition states (`Refii.creature_tier`: `CREATURE_ELITE` or `CREATURE_LIEUTENANT`). The caps are read off the style, so a style takes the elite caps once an elite creature fights with it. Attack combos are `Uninterruptable`, so a roll away leaves the enemy finishing its string. Right after a hero heals (`health_changed` rising) or ends a swing that hit nothing (`melee_swing_ended` with `hits` 0), up to two enemies in reach attack as if they held a token. |
| Enemy defence | `mod/ai.lua` | Every hit that lands (`hit_landed`) on an enemy holding no guard rolls its tier's chance to raise the guard (15/35/50%) at its next blocking update. The shipped styles block only after N hits. Bandit-like styles that are pushed back by a block get a two-strike riposte. The shipped name typo that kept the balverines from ever dodging (`BLOCK_MELEE_DODGE`) is aliased here until the core fixes it for every mod. Waiting enemies circle with the shipped strafes instead of standing and taunting. Kiting is answered: a token holder sprints to within 3 m before it strafes (shipped: strafe-walks from 5 m, slower than a hero backs off), a spell launch hands one more enemy a token for 1.5 s, and the shipped anti-spam rush (the group breaks formation for 8 s) fires on 2 spells or 3 shots in 6 s on every grouped style (shipped 4 in 4 s, bandits only). Boss-tier styles with an `Enrage` table now enrage below half health; no shipped style ever ran `UpdateEnrage`. Trolls wait at most 3 s after landing a hit. |
| Lock-on | `mod/lockon.lua` | LT TOGGLES the lock instead of being held. The lock is the mod's own state per hero, flipped by the LT press (`game_action`, `Refii.ACTION_LOCK_SET`) and written into the game's two switches — the targeting component's force byte and the player's look-at flag — through `Refii.lock_target` / `Refii.release_lock`; while locked the release ends nothing, since the game's own case honours the force byte and the look-at flag is held equal to the lock by the `look_at_button` handler, whoever writes it. While locked the game adds player mode 4, which outlines the target and aims guns and spells at it, and re-targets the next enemy when the locked one dies. When the game drops that mode (`lock_lost`: the target died or left reach), and when a toggle finds nothing to lock, the lock has half a second to hold a living target again, and is released otherwise. On the keyboard and mouse the lock stays right mouse, and while it holds the mouse wheel switches target — up the next target to the left, down the next to the right, one switch per notch through the game's own target switch — and moving the mouse never retargets (`Refii.wheel_switches_target(hero, true)`: stated once, restated by the engine, stated again when the lock's target changes and turned off with the lock). A pad flicks the right stick as shipped. In a cutscene LT is the game's: while `Refii.in_cutscene` holds for a hero — a full cutscene, a foreground interactive cutscene, or a camera the game gave held LT (a conversation's talking camera on the speaker, a quest's look-at) — the press toggles nothing and the look-at flag passes through as the game writes it, so holding LT looks where the game chose, as shipped; a scene starting releases that hero's lock (the `cutscene` event), and it stays released after. A hero that stops existing (`hero_gone`) holds no lock. |
| Stamina | `mod/stamina.lua`, `mod/roll.lua`, `mod/guard.lua`; the pool's curve in `mod/common.lua` | A pool per player hero, the runtime's regenerating pool (`Refii.pool`). A committed swing costs 18, a roll 22, a blocked hit 1.2 per point of damage; at an empty pool the next swing, roll or block is refused (the pool may dip to −25 so the emptying swing still lands: the pool's floor is below zero, so a spend from a pool holding anything goes into debt), and a block the pool cannot pay for breaks and the hit lands in full. Refills at 55/s after 0.7 s without spending. A refused swing press refuses its release on the bus itself. |
| Stamina ability | `ui/abilities.json`, `language/en-uk/text/book.babel.csv`; the effect in `mod/common.lua` (`StaminaMax`) | A sixth Strength ability, five levels, DECLARED and run by the engine (`Godot/docs/modding/ui-and-hud.md`, "Mod abilities"): `costLike` 3 prices it exactly as Toughness (the game's own cost table, spent through the game's own Spend, Strength then General), `"refund": "half"` makes Y unlearn a level through the game's own confirm and result boxes with half its price back, `stats` is the description's next maximum, and `legacySaveKey` takes player one's level from saves made before. The mod's own code only applies the level: max stamina follows Toughness's health steps — 100 untrained, then along the same curve to 271 — so a full stamina bar is as long as a maxed health bar (`StaminaMax` in the Lua, read from the level in the player store). Each player has their own level: the row acts for, and shows, the player whose book is open. The level is a saved player value (`souls_combat.stamina_ability`): it goes home with the hero's own save (see Co-op). |
| Poise | `mod/poise.lua` | A hit staggers its victim only once 30% of their maximum health has landed within 3 s; lesser hits are absorbed without a flinch. Enemies and the hero alike. Each victim's tally is a per-entity value (`Refii.entity_value`), forgotten when it dies or leaves the world. A parried or blocked hit, a knockdown, a ranged hit and a killing blow keep the game's own response. |
| Commitment | `mod/commitment.lua` | Once the hero's swing has started it cannot be cancelled into a roll, another swing or a block until a quarter-second later in the animation than the game allows: the strike's `ALLOW_ACTION_INTERRUPT` event is cancelled and the permission handed to `Refii.allow_action_interrupt_at`, which grants it when the strike's own clock gets there, or drops it if the strike ends first. |
| Timed parry | `mod/movement.lua` | The parry window is 0.25 s (shipped 1 s) in a 90° wedge (shipped 70°), in each hero's own copy of the balance table (`Refii.set_parry`), stated for each player and again when the setting changes. |
| Cast recovery | `mod/cast_recovery.lua` | A launched spell holds its caster in the cast for up to 0.6 s. The game holds a quick cast's movement only until the spell launches; from then any stick input cancels the rest of the animation, which made a step back between casts free. The hold is the game's own: every frame the game would let the stick end the cast (`action_move_interrupt`), the mod cancels, so the cast animation plays on and the stick neither ends it nor moves the hero. It ends when the 0.6 s are up or the cast animation finishes, whichever comes first. |
| Mana | `mod/mana.lua` | Mana for spells, the runtime's regenerating pool (`Refii.pool`). Max grows from 100 to 271 evenly over the 40 spell ranks (`Refii.ABILITY_LIGHTNING` .. `ABILITY_RAISE_DEAD`), worked out when the hero arrives and whenever one of those abilities is set (`ability_level`). Every spell that launches costs 12 per power level, taken when it launches (a level-L release is Fable 2's cascade of slots 1..L, so it costs the sum); an empty pool refuses the charge, a short one caps the charge at the top slot it can pay for, and a lower slot of the cascade is skipped when paying it would leave the top slot unpaid. Refills 6/s after 2.5 s. |
| HUD | `ui/hud.json`, `art/gui/hud/*.tex.png` | Fable 2's own health widget three times: the original, then two copies of it (`clones`) — mana with a drop icon and its bar hue-shifted blue, stamina with a bolt icon and its bar shifted green — each reading the player's own pool from the player store (`player:souls_combat.mana` / `mana_max`, `player:souls_combat.stamina` / `stamina_max`) and drawn against the shared cap of 271 (`mod:souls_combat.bar_cap`), the way the health bar is drawn against the game's maximum health. The copies drop and fade with the original; all three are drawn 15% larger. ONE record per bar serves both players: a copy that reads `player:` values is each player's, so the engine draws it under player two's health widget (`CoopHealth`) too, reading player two's pools, and on split screen and on a joiner's screen player two's three bars stand where player one's do. |
| Bars shown while short | `mod/bars.lua` | While any of a player's health, mana or stamina is below its maximum, all three of that player's bars stay on screen; once all three are full the game's own show and fade decide again (dark at full health, lit while hurt). The mod holds the player's health readout (`Refii.hud_hold`: `PlayerHealth` for player one, `CoopHealth` for player two, on both players' HUDs, so a shared screen, split screen and either machine of an online session each show the right player's), and the two copies follow it. Health is `Refii.health`, mana and stamina their pools (`Refii.pool_value`). Checked when a pool is spent (`melee_swing`, `roll_started`, `block`, `spell_launched`), when health moves (`health_changed`), on a hit on a hero (`hit_landed`: the host takes a blocked hit's cost from a joiner's pool), when a hero arrives or goes, and when a maximum grows (`ability_level`, `mod_ability`); while a bar is short, again every quarter second on the runtime's clock, since the pools refill on the tick with no event of their own. Off with `bars_always_when_not_full`. |
| Ability card | `art/gui/abilitytarot/souls_combat_stamina.tex.png` | The still the abilities book shows for Stamina, where the game's abilities play a movie. |

Found on the way, so not built: **the roll is already invulnerable for its whole length** — the
hit dispatch ignores a victim whose component-31 flag the roll clears for its duration.

## Co-op

Both players are principals, in every co-op mode (shared screen, split screen, online):

- Stamina, mana, poise, the guard, roll, commitment, cast recovery, the turn rates, the parry
  window and the lock-on are per hero.
- Each player's maximum stamina follows their own Stamina level, and each player's maximum mana
  follows the spells their own hero has learned.
- The Stamina row in the abilities book acts for the player who opened the book: player two's own
  book in local co-op, the joiner's book on the joiner's machine. The engine's purchase spends that
  player's experience and the row shows that player's level, cost and refund.
- Every per-player value is in Refii's player store (`Refii.player_*`, `Godot/docs/modding/handles-and-players.md`),
  so both machines of an online session read the same pools, and each spend counts once wherever
  the game decides it: a swing, a roll and a spell charge where the hero is played, a block (and
  the guard breaking) on the host, which resolves every hit. The refill runs where the hero is
  played. A cast runs on both machines (the host's copy is the spell that deals the damage), so
  the mana cap applies on both, from the same pool, and the launch is paid once. The cast is held
  where the hero is played. Each hero's turn rates and parry window are stated on both machines,
  since the host resolves the hits a parry answers.
- Enemies punish either hero's heal or whiff. The AI runs only on the host. A heal is the
  game's own health change, which it runs on both machines (`health_changed`; the copy is flagged
  a replica), so the host times the window from its own. A strike's end is relayed
  (`melee_swing_ended`), so the host hears player two's from the joiner, as a replica, with the
  hits the joiner counted. Its stated limit: the joiner counts a hit the host relays only while
  its strike is still current, so a late one is missed and a relayed whiff may have hit. On a
  relayed copy a hit by that hero landed on the host since the hero's previous strike ended
  therefore cancels the whiff — a real whiff may go unpunished, a hit is never punished. Heroes
  are told apart with `Refii.player_of`, since player two's body on the host is not a `Player`
  there.
- Poise is decided where the hit's response is authoritative. The host runs every hit's response,
  so it keeps every pool, player two's included (from the same hits the joiner sees, so its copy
  agrees). The joiner runs only hits on or by player two: it decides player two's own pool, and
  its copy of a hit by player two on an enemy (`Refii.event_replica()`) leaves the pool alone
  and plays the game's own predicted response; the host decides that enemy's stagger.
- The Stamina level is a SAVED player value: it is written beside the save the game writes that
  hero into (`refii_player.txt` in the save's folder) — player one's main save, a local player
  two's own save, an online joiner's own save on the joiner's machine — and read back when the
  game loads that hero. So player two keeps their level when they go home, and brings it to the
  next session. Saves from before this kept player one's level in the world save's Lua
  (`RefiiModSave.SoulsStamina`); the engine takes it from there once (`legacySaveKey`).

## How the pieces talk

- The combat rules are Lua handlers on the mod API's events (`Godot/docs/modding/events.md`, `Refii.on`),
  one module per rule under `mod/` (`init.lua` only requires them; the loader composes every
  `mod/*.lua`, and `require` runs each once): stamina refuses on `ability_request` and
  `roll_can_start` and spends on `melee_swing` and `roll_started`; the guard breaks through `block`
  (fired once per hit, so a block is charged once); poise absorbs through `hit_response`; the
  commitment records the strike on `melee_swing`, cancels its `anim_event` and hands the grant to
  `allow_action_interrupt_at`; cast recovery records the cast on `spell_launched` and holds it
  through `action_move_interrupt`; the lock-on toggles through `game_action`, holds the flag through
  `look_at_button`, both stepping aside while `Refii.in_cutscene` holds, releases on `cutscene`,
  checks for a target after `lock_lost` and restates the wheel on `target_changed`; mana gates
  `spell_charge`, caps `spell_charge_level`, skips unpaid cascade slots through `spell_launch`,
  spends on `spell_launched` and sizes the pool on `hero_ready` and `ability_level`; the enemies
  read `health_changed`, `melee_swing_ended`, `hit_landed` and `spell_launched`; the bars hear the
  spends after the rules that pay them, `health_changed`, `hit_landed`, the heroes' arrivals and
  the maximums growing. No rule polls the tick (the bars' quarter-second recheck runs only while a
  bar is short): the pools are the runtime's (`Refii.pool`), every delay is `Refii.after` or a time on the
  runtime's clock (`Refii.now`), per-entity state is `Refii.entity_value`, and what is stated once
  (each hero's locomotion and parry, the styles' caps, the camera's class fields) is stated again
  on `setting_changed`. Nothing is hooked by name and nothing reads guest memory: every value is a
  `Refii.*` call.
- The Stamina ability is data (`ui/abilities.json`): the engine buys, unlearns, prices and saves
  it, raising `ModAbility` first; the mod only reads the level (`souls_combat.stamina_ability`)
  where it sizes the pool.
- Each player's pools live in the player store, written where that hero is played (the runtime
  ticks them there); the HUD's copies read them as `player:souls_combat.<name>`, for whichever player's widget they stand
  beside. The one value the same for everyone, the bars' common length, is published
  (`Refii.publish("souls_combat.bar_cap", …)`, read as `mod:souls_combat.bar_cap`).
- Every seam is an event the core hooks once, named in both `versions/*/refii_config.toml`, so the
  same Lua runs on the disc and the Title Update; the contract is in each section's header.

## Balance

Every number is a setting in `settings.json`, read by the Lua at the moment it is used
(`Refii.setting(key, default)`), so tuning needs no rebuild: change it on the Options › Mods page,
with the Studio's live tune or a script's `set_setting`, or on the debug channel
(`mod-setting souls_combat.swing_cost 24`), and feel it at once; edit the file in the Mod Studio (or
`RefiiModApi.SetSetting`) and re-mount to change the default.

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
| `lock_drop_seconds` | 0.5 | After the game drops the lock (or a toggle finds nothing), how long the lock has to hold a living target before it releases. |
| `parry_window_seconds` | 0.25 | The timed parry's window (shipped 1). |
| `cast_recovery_seconds` | 0.6 | The longest a launched spell holds its caster in the cast animation; 0 turns it off. |
| `turn_speed_static` / `turn_speed_running` | 720 / 540 | The hero's turn rate in degrees per second, standing and at full speed. |
| `move_friction` | 9 | How fast the hero's speed follows the stick, per second. |
| `camera_distance` | 4 | The orbit distance outdoors; indoors is 85% of it. |
| `camera_height` / `camera_focus_height` | 0.9 / 1.6 | The camera's rise over the focus at the orbit distance, and the focus's height over the hero. |
| `camera_pitch_speed` | 0.09 | Right-stick pitch per update. The orbit speed is the host's per-device camera feel, scaled by the player's sensitivity option. |
| `camera_lock_tightness` / `camera_lock_turn_speed` | 0.97 / 420 | The cosine of the cone a locked target is kept inside, and how fast the camera turns to keep it there. |
| `ai_attackers_at_once` / `ai_ring_slots` / `ai_token_seconds` | 2 / 4 / 1.5 | Attack tokens per defender, inner-ring slots, and how long a token lasts unused. |
| `ai_gap_fodder` / `ai_gap_elite` / `ai_gap_boss` | 2 / 1.2 / 0.7 | The most each tier waits between attack sequences. |
| `ai_block_chance_fodder` / `_elite` / `_boss` | 15 / 35 / 50 | Percent chance an unblocked hit raises the guard. |
| `ai_punish_heal_seconds` / `ai_punish_whiff_seconds` | 1.6 / 1.1 | How long after a heal, and after a missed swing ends, enemies punish. |
| `ai_punish_cast_seconds` | 1.5 | How long after a hero's spell launch one more enemy gets a token, even out of reach, and comes for them. |
| `ai_close_in_distance` | 3 | How close a token holder sprints before it strafes (a style's own distance, 5 m by default, otherwise). |
| `bars_always_when_not_full` | 1 | 1: health, mana and stamina stay on screen while any of them is below its maximum; 0: the game's own show and fade alone. On the Options › Mods page. |

Every change made through those paths raises `setting_changed`, and what the mod states once is
stated again on it: each hero's turn rates, friction and parry window, the AI tiers' swing-gap caps,
and the camera's class fields and zoom levels. Two values are the shipped scripts' own content and
take effect only where the game reads them again: the Idle state's turn cap (`turn_speed_static`),
parsed once per session on the hero's first locomotion activation, and the token count, ring slots
and token time, read at the next wheel update. A direct `RefiiSettings[...]` write from the debug
channel's Lua is read by every number taken at use, but raises no `setting_changed`.

## Still open

- **Difficulty multipliers** (`DifficultyLevelMultipliers.DamageCaused` / `DamageReceived`):
  reported to apply only above difficulty 1; not verified, not used.
- **Death and respawn** (Pass 3): a checkpoint system of its own, planned separately. Its seam is
  the `HeroDowned` event, which cancels the game's whole knockdown flow with the hero kept at 1
  health.
