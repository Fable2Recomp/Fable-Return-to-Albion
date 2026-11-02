# Health Bars

A health bar over every enemy near the heroes. The bar hangs over the creature's body, shrinks
with distance, and a pale trail follows each hit down so the damage reads. Bars show in play
only: not in the pause menu, the menus, a cutscene or a loading screen.

## Settings (Options > Mods)

| Setting | Default | Meaning |
|---|---|---|
| Show enemy health bars | on | the whole mod on or off |
| Range | 25 m | how far away an enemy still gets a bar |
| Only hurt enemies | off | leave out enemies still at full health |
| Show numbers | off | current / most health over the bar |
| Size | 1.0 | how big the bars are drawn |

## How it works

- `mod/init.lua` looks around each hero this machine plays ten times a second for living
  creatures the game counts as that hero's enemies (`Refii.relation` 4) and publishes up to 24 of
  them, nearest first: `health_bars.n`, and per bar `health_bars.e<i>` (the handle),
  `.c<i>` (current health) and `.m<i>` (most health).
- `godot/health_bars.gd` draws each over the node the host draws the creature with
  (`node_for`), lifted to the top of its mesh bounds, through the view's camera.

In split screen the bars are drawn through the main view's camera only.
