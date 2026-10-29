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
  to pick from everything you carry, know or have learned, or to clear it.
- **In play.** The direction performs the slot. The HUD's D-pad shows what each slot holds, and
  the bank a held trigger reaches. LT keeps the target lock while it reaches its bank; RT stops
  opening the spells menu only while an RT slot is assigned.

## Files

| File | What it does |
|---|---|
| `mod/common.lua` | The slots in the player store (saved with the hero: `quickslots.<n>.kind/hi/lo`, `quickslots.revision`), the catalog of what a slot may hold, labels and icons from the records (`Refii.record_string`, `Refii.record_int`, `Refii.expressions`, `Refii.spells_known`). |
| `mod/slots.lua` | The press (`input_action` → `Refii.use_item` / `equip_weapon` / `perform_expression` / `assign_spell`), the claims that withhold an assigned direction from the game (`Refii.input_claim`), and the HUD's D-pad (`Refii.hud_icon`, `hud_slide`, `hud_hold`). |
| `mod/assign.lua` | The menus: `quickslots.assign` (a thing looking for a slot), `quickslots.menu` and `quickslots.choose` (a slot looking for a thing), fed by `Refii.set_menu_rows`; the books' row actions and the wheel's Y land here. |
| `input.json` | The actions: the four directions, the LT and RT chords (`"chord"`), the held triggers, and Y on the wheel. All rebindable in Options › Controls. |
| `ui/templates.json` | The pause-list row, the three menus, and the `rowActions` on the inventory, expressions and abilities books. |
| `settings.json`, `settings.ui.json` | The LT and RT banks and the HUD icons, each a switch on the mod's settings page. |
| `language/en-uk/text/book.babel.csv` | The text. |
