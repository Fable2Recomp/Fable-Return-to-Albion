# Hotbar

A Fable 1-style hotbar: ten slots along the bottom of the screen on the keys 1-0, one bar for each
player. 1, 2 and 3 keep the game's own job (the attack button on melee, ranged or magic) and show
the weapons in hand and the spell at the first charge level, which you choose with the spell wheel
as always; the mode in force is lit. 4-0 hold what you put there, adding to the game's controls
without changing them:

| On the slot | The key |
|---|---|
| a spell | charges it through your own magic charge: a tap casts it at once, holding charges it through the power levels you have exactly as holding the magic button does, and letting go casts it. Your spell wheel choices are left as they were. The slot is lit while it casts |
| an expression | performs it: a tap plays its basic form, holding extends it as holding the wheel's button does, until you let go |
| food, a potion or another usable item | uses one, as the Items page's Use does, even while running or fighting; the slot shows how many you carry |

A slot you cannot use right now (a spell you have not learned, an expression you do not know, an
item you have run out of) is drawn dimmed.

Fill the slots from **Hotbar** in the pause menu: pick a slot, then Spells, Expressions, Food,
Potions or Other items, then the thing — each list is what your hero has right now, named as the
game names it. "Empty the slot" clears it. The bar is part of the HUD: it shows and hides with it,
scales with it, and on split screen each player's bar is in their own half. Keys are rebindable in
Options › Controls. Options › Mods › Hotbar has whether the bar is drawn (the keys work either
way), its Size (0.6-1.6) and its Height above the bottom of the screen (0-200, to clear the
subtitles); both change it at once.

What is on each slot is the hero's and is saved with them (player values `hotbar.k<key>`,
`hotbar.a<key>`, `hotbar.h<key>`). Slots saved by Hotbar 0.1 are read once and kept.

On a phone the bar is drawn larger, and a tap on slot 4-0 is its key: a tap uses it, and holding a
spell or an expression holds it as holding the key does.

## How it works

This mod is written as a Godot script: the logic is `godot/hotbar.gd` on the `RefiiHost` singleton,
with the bar and the menus as data (`ui/hud.json`, `ui/templates.json`), and the mod ships no Lua.

- **The keys** are `input.json`'s actions, heard on the `input_action` signal for whichever player
  pressed them, in play only (`in_play`, `in_cutscene`).
- **Spells.** `charge_spell(p, type, true, callback)` on the key's press and `charge_spell(p, type,
  false)` on its release: the game's own charge, with the spell lent to every power level the hero
  has it on and the player's own slots put back once the cast is out. The callback says whether the
  game took the charge, so the slot lights only then and a key let go before the answer is let go
  after it. A tap holds the button long enough for the game to read it; `spell_charge_ended` and
  `spell_ended` end the lit slot.
- **Expressions.** `perform_expression(p, record, true, {"ungated": true}, callback)` on the press
  and `release_expression(p)` on the release — the wheel's own start and the extend button's own
  end, released only for an expression the game started.
- **Items.** `use_item(p, definition, {"ungated": true})`: the Items page's use, for that player's
  hero, with no menu open, when `query item_count` says the hero carries one.
- **The slots** are typed values in the player store, read and written at once:
  `player_value(p, "hotbar.k<key>")`, `player_value_hash(p, "hotbar.h<key>")`, `player_save` and
  `player_save_hash`. A 0.1 slot is migrated the first time it is read.
- **The lists** are host lists in `ui/templates.json` (`spells_known`, `expressions`,
  `inventory:usable` by category) whose rows run the verbs `Hotbar:assign` and `Hotbar:clear`,
  registered by the script with the row (`register_command(verb, handler, true)`), so a chosen row
  arrives with its typed subject (`subject_kind`, `definition`, `spell`). The slot list is stated
  with `set_menu_rows` when the Hotbar row is chosen (`mod_menu_row`), each slot named `{NAME_TAG}`
  in the player's language.
- **The bar** is `ui/hud.json`: pictures cut from the HUD's own atlas (`art/gui/persistant/hud.tex`,
  the d-pad's glass orb, pewter ring, soft light and the eye ornament's filigree), the slot's icon
  filled with `hud_icon`, the key numbers and stack counts as text pieces reading the player store
  (`player_set`), ringed in dark (`outline`). Every piece is `each`, so player two's bar reads player
  two's values, and in the group `hotbar.bar`, scaled by `setting:hotbar.size` about the bottom
  margin's centre and raised by `setting:hotbar.lift`, read live in each player's half. What the
  hero holds is asked of the guest when the bar is restated — `query spells_known`, `expressions`,
  `inventory` (with the game's own usable test), `equipped`, `spell_in_slot` — and a record's icon
  and name once (`query record_string`, `record_ref`). It is restated on the signals that change it
  (`item_added`, `item_removed`, `item_used`, `weapon_equipped`, `spell_pick`, `ability_level`,
  `attack_mode_changed`, `hero_ready`, `setting_changed`), never polled. The lit mode is the host's
  keyboard attack mode (`attack_mode`).
- **The look** is Fable Anniversary's bar. The pictures are the mod's own
  (`art/gui/hud/hotbar_*.png`), written with the pieces that place them by `source/make_art.py`, in
  place of the atlas cuts above. The key numbers are the Fable II face's superscript figures.

## Limits

- The actions are bound to the keyboard; a pad has no default binding for them.
