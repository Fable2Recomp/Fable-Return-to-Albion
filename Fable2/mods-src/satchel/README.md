# Hero's Satchel

Tab (the mod wheel on a controller) opens the inventory as one see-through screen. Escape still opens the game's pause book. The world
holds behind the screen while it is open.

Category icons run along the top: Favourites, All, Melee, Ranged, Clothing, Potions, Food, Gifts,
Books, Dyes, Augments, Trophies, Furniture, Keys and Tools. Under them is one list, with columns that
suit the category and what you have equipped at the top. The chosen item's 3D model turns above its
card on the right, and an outfit shows as the pieces it is made of. The card's stats are set against
what you have in hand or on, in green and red.

| | Keyboard and mouse | Pad |
|---|---|---|
| Category | A and D, the arrows, a click | D-pad left and right, LB and RB |
| Move in the list | W and S, the arrows, the wheel, a click, dragging the scrollbar | D-pad up and down |
| Equip, wear, take off or use | E, Enter, Space, a double click | A |
| Favourite | F | Y |
| Remove all clothing, in Clothing | R, or click it in the bar | Back |
| Sort | C, or click a column's head | X |
| Filter by name | `/`, or click the box; Enter ends it | |
| Close | Tab, Escape, right click | B, Start |

Equip, Favourite, Remove all clothing, Sort, Filter and Close are rebindable in Options › Controls,
and the bar along the bottom shows each as it is bound now, as your controller's glyphs on a pad. The
screen is the player's whose Tab opened it: their hero's inventory, their favourites.

On a phone the **Inventory** pill in the top row of the touch controls opens it. Tap a category or a
column's head, tap a row to choose it and tap it again to use it, swipe the list to scroll, and tap
the buttons along the bottom: Equip, Wear or Use, Favourite, Sort and Close. Tap the Filter box
for the phone's keyboard.

## How it works

This mod is a Godot script, `godot/satchel.gd`. `input.json`'s Tab action is claimed from the game
(`claim_input`), which takes Tab and leaves Escape on the pause. Tab holds Start for the pause of the
player who pressed it, and `own_screen(player, true, self)` stands the screen where their pause book would, as the satchel's own:
only the satchel takes it down.
Everything closes on the `paused` signal, because a pad's Start ends the pause too. The screen's other
keys and buttons are `input.json` actions too, read through Godot's InputMap so the player's rebinds
hold, and drawn with `input_glyph` / `input_label`.

`inventory_items(player)` is what the game's own pages show that player, and `inventory_action(player, ...)`
does what their rows do. The category names are the game's own inventory page tags.
Favourites are saved with the hero (`player_save`). The model is drawn in a `SubViewport` with
`dress_model`'s lit material and posed by `item_pose`.
