# Health Bars

A health bar over every enemy near the heroes. The bar hangs over the creature's body, shrinks
with distance, and a pale trail follows each hit down so the damage reads. Bars show in play
only: not in the pause menu, the menus, a cutscene or a loading screen.

In split screen each player sees bars over the enemies near their own hero, in their own half;
on one shared screen one set of bars covers the enemies near either hero.

## Settings (Options > Mods)

| Setting | Default | Meaning |
|---|---|---|
| Show enemy health bars | on | the whole mod on or off |
| Range | 25 m | how far away an enemy still gets a bar |
| Only hurt enemies | off | leave out enemies still at full health |
| Show numbers | off | current / most health under the bar |
| Size | 1.0 | how big the bars are drawn |

## How it works

This mod is written as a Godot script: everything is `godot/health_bars.gd` on the `RefiiHost`
singleton, and the mod ships no Lua.

- Ten times a second (the `tick` signal's seconds) it looks around each hero this machine plays
  (`player_here`, `in_play`) for living creatures the game counts as that hero's enemies within
  range, and keeps a bar over up to 24 of them, nearest first. A look asks the guest with `ask`: the
  hero's `position`, the creatures `near` it (a page of 100 at a time, the next page only while fewer
  than 24 enemies are found), each one's `relation` to the hero (`RELATION_ENEMY` from
  `constants()`) and whether it is `living` — asked once per creature and kept with `entity_set`,
  which the host forgets at `entity_removed`; `entity_died` marks it dead.
- Each bar is a world meter (`world_meter`, style `health`, with `numbers` and the size as `x<n>`)
  on the HUD's world-icon channel, so it is drawn through the camera of the player it is for, lifted
  by the drawn body and sized by the game's own distance curve; the trail is the meter's own.
- The health is asked once when a bar is made and then restated in place from the game's
  `health_changed` signal (`mark_meter`); `entity_died` and `entity_removed` take a bar away,
  `level_unloading` takes them all, and a changed setting (`setting_changed`) makes them again in
  the new style.
