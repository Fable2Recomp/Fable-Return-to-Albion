# Quickslots

Twelve assignable slots on the D-pad: the four directions, LT + a direction and RT + a direction.
A slot holds an item to use, a weapon to draw, an expression to perform or a spell to ready. A
direction with nothing assigned keeps the game's own quick use, so an empty D-pad plays as shipped.

A soft mod: Lua and data only, on the mod API. Nothing reads guest memory and no game function is
hooked by name. Every piece of it is an API the core offers every mod.

## How it is used

- **Assign from a book.** On the inventory and expressions books, Y on a highlighted row offers
  "Assign quickslot"; on the abilities book it is X. A list of the twelve slots opens; choose one,
  or press the direction (with LT or RT for the other banks) while the list is up.
- **Assign from the expression wheel.** Hold the wheel, highlight an expression, press Y.
- **From the pause list.** Quickslots lists the twelve slots and what each holds; choose a slot
  to pick from what you can use from the inventory (weapons among it), the expressions you know
  and the spells you can assign, or to clear it.
- **In play.** The direction performs the slot. The HUD's D-pad shows what each slot holds, and
  the bank a held trigger reaches. LT keeps the target lock while it reaches its bank; RT stops
  opening the spells menu only while an RT slot is assigned.
- **Expressions are held as the wheel holds them.** A quick press plays the expression's basic
  form; keep the direction down and an extendable expression extends after the game's own second
  and plays on until you let go (`Refii.perform_expression(p, record, true)`, then
  `Refii.release_expression(p)` on the direction's release) — the expression wheel's own
  tap-or-hold, not a tap only.

## Files

| File | What it does |
|---|---|
| `mod/common.lua` | The slots in the player store (saved with the hero: `quickslots.<n>.kind`, the record as a typed hash `quickslots.<n>.record` — `Refii.player_save_hash` / `player_value_hash` — or the spell type `quickslots.<n>.spell`), the binding a chosen row's typed subject names, and labels and icons: an item's or weapon's off its `InventoryItemComponent` (`Refii.record_ref`, then `Refii.record_string` / `record_int`), an expression's off its record, a spell's from `Refii.spell_info`. |
| `mod/slots.lua` | The press (`input_action` → `Refii.use_item` / `equip_weapon` / `perform_expression` held / `assign_spell`), the claims that withhold an assigned direction from the game (`Refii.input_claim`), and the HUD's D-pad (`Refii.hud_icon`, `hud_slide`, `hud_hold`), stated when they change: `hero_ready`, `save_loaded`, `lua_ready`, a slot written, `setting_changed`, a trigger edge, and `expression_wheel` (which also gives Y on the wheel its expression). `hero_gone` gives them back. |
| `mod/assign.lua` | The menus: `quickslots.assign` (a thing looking for a slot) and `quickslots.menu` (the twelve slots), fed by `Refii.set_menu_rows`; `quickslots.choose` (a slot looking for a thing), the host's own lists. The books' row actions and the wheel's Y land here. |
| `input.json` | The actions: the four directions, the LT and RT chords (`"chord"`), the held triggers, and Y on the wheel. All rebindable in Options › Controls. |
| `ui/templates.json` | The pause-list row, the three menus — `quickslots.choose` sources `inventory:usable`, `expressions` and `spells_known` with a `rowCommand` — and the `rowActions` on the inventory, expressions and abilities books. |
| `settings.json`, `settings.ui.json` | The LT and RT banks and the HUD icons, each a switch on the mod's settings page. |
| `language/en-uk/text/book.babel.csv` | The text. |

## What a chosen row names

Every row that reaches the mod — a book's row action, a host list's row on `quickslots.choose` —
carries its subject typed on `mod_menu_row`: `subject_kind` (`item`, `weapon`, `expression`,
`will`), `definition` (the record as `"0x%08x"`) and `spell` (0..7). An `item` whose
`InventoryItemComponent` states category 1 is a weapon and is drawn (`equip_weapon`) rather than
used. Nothing parses a row's command.

The choose list is the host's: `inventory:usable` is what the game's own `UsableFromInventory`
test lets the hero use, `expressions` and `spells_known` what the hero knows and can assign, each
asked of that player's hero. No rows cross the bus for it, so nothing can be cut.

## Saves from before the typed store

Slots saved by an earlier Quickslots kept a record split over two numbers, `quickslots.<n>.hi`
(the high 8 bits) and `.lo` (the low 24). The record had already been a Lua number — a float — when
it was split, so a record past 2^24 lost its low bits. The first time a slot is read with the hero
present and `quickslots.<n>.record` (or `.spell`) absent, the old halves are read once: the slot
becomes the carried item, or the known expression, whose number splits into the same two halves,
else the halves joined when the database has that record, and the typed form is written. A slot
that names neither once the hero states its inventory and expressions is cleared, with a line in
the log. A spell slot's type was always exact and moves as it was.
