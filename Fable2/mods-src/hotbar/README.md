# Hotbar

A Fable 1-style hotbar: ten slots along the bottom of the screen on the keys 1-0. 1, 2 and 3 keep
the game's own job (the attack button on melee, ranged or magic) and show the weapons in hand and
the spell at the first charge level, which you choose with Ctrl as always; the mode in force is
lit. 4-0 hold what you put there, adding to the game's controls without changing them:

| On the slot | The key |
|---|---|
| a spell | casts it: a tap casts it at once, holding charges it through the power levels exactly as holding the magic button does, and letting go casts it. Your Ctrl choices are left as they were. The slot is lit while it casts |
| an expression | performs it, through the game's own expression performer |
| food, a potion or another usable item | uses one, as the Items page's Use does; the slot shows how many you carry |

Fill the slots from **Hotbar** in the pause menu: pick a slot, then Spells, Expressions, Food,
Potions or Other items, then the thing. "Empty the slot" clears it. The bar shows in play only: it
hides in menus, on loading screens and in cutscenes. Keys are rebindable in Options › Controls; the bar's
size, height and visibility are in Options › Mods › Hotbar. Keyboard only for now.

What is on each slot is the hero's and is saved with them (player values `hotbar.k<key>`,
`hotbar.a<key>`, `hotbar.b<key>`).

## How it works

`mod/init.lua` keeps the slots, answers the keys for spells and expressions, and builds the
Hotbar page's lists (`ui/templates.json` declares the pages; the lists are rows stated with
`Refii.set_menu_rows`, a page at a time, since a Lua row list crosses the bus in about 1.8 KB).

- **Spells.** The game charges and casts from the hero's spell table while the magic button is
  held. A spell key borrows it for one cast: `init.lua` keeps the table, puts the key's spell on
  every level the hero has learned it to (`SetEntitySpellTableItem` in both cast modes, which the
  game's own writer keeps mirrored; the levels above emptied so the charge stops there), and
  `godot/hotbar.gd` holds the magic key (`key_magic` in refii.toml, C by default) through Godot's
  input for as long as the slot's key is held. Once the key is up and the charge is spent the
  table goes back as it was.
- **Expressions.** `ExpressionPerformer.PerformExpression(hero, { ExpressionRecord = … })`, for
  the records in `EXPRESSIONS` the hero has learned (`IsExpressionAvailable`).
- **Items.** `godot/hotbar.gd` uses the item with the host's `use_item(definition)`: the Items
  page's own `Inventory:use:<record>`, with no menu open. (The older `use_item(player, definition)`
  goes through the quick menu's action and is gated as the quick menu is.)

`godot/hotbar.gd` draws the bar from values `init.lua` publishes per slot. An icon path does not
fit in a published number, so it travels three characters to a number (`hotbar.icon<s>.<j>`,
24 bits, exact in a float). The icons are the game's own textures, loaded through the UI's
presentation host. 1-3's lit mode follows the last of those keys pressed, as the game's own
keyboard mode does (`GameInput.KeyboardMouse.cs`, starting on melee).

## Limits

- Player one's keyboard only; a pad has no binding yet.
- The slot list names spells in English and expressions and items by words taken from their
  record or text tag, because a menu row is one tag or one literal and "4  <name>" has to be a
  literal. The pick lists use the game's own names.
- An expression plays its basic form: the key is a tap, so there is no hold to extend it.
