# Minimap

A round map of the region in the top right corner, with Fable 2's own region art. North stays up
and the map never moves or zooms; the hero is an arrow that points the way they face.

**M** steps through the sizes, and is rebindable in Options › Controls:
Normal › Bigger › Biggest (mid-screen) › Hidden › Normal.

| Mark | Means |
|---|---|
| white arrow | the hero |
| green | other quests' objectives |
| red | enemies near the hero |
| brown | the dog |

Shops and exits are painted on the art. The dial on the rim is the time of day: day on the left,
night on the right, with the hand over the sun at noon. A place with no map of its own keeps the
last map up without marks. The map shows in play only, not in menus, on loading screens or in
cutscenes. Options › Mods › Minimap has its opacity and size, and the enemies' range.

On a phone the map stands below the touch controls' top row, left of Spells, and a tap on it is M.
Hidden, it leaves a small compass where it was, turning with the camera, its red needle and N
toward north; tap that to bring the map back.

## How it works

`godot/minimap.gd` draws it, from `RefiiHost.minimap(player)`: the data the game's own pause and
world map minimaps plot from, the other quests' objectives among them. `mod/init.lua` publishes what only the guest's Lua can answer, the
clock, the dog (`Refii.player_dog`) and the enemies near the hero, and `minimap_point` puts those on
the map. The map is the player this machine plays (`player_here`): player one alone, in split screen
and when hosting, player two on an online joiner. Its text is in `language/<culture>/text`.
