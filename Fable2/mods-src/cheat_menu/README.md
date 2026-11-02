# Cheat Menu

A **Cheat Menu** row on the pause menu, with pages that hand out rewards to the hero of the player
whose menu it is (in co-op each player cheats for their own hero):

| Page | Rows |
|---|---|
| Experience | +1,000 / +10,000 general, strength, skill or will; +10,000 of each |
| Gold | +1,000 / +10,000 / +100,000 / +1,000,000 |
| Renown | +1,000 / +10,000 / +100,000 |
| Morality | good or evil, by 100 or 1,000 |
| Purity | pure or corrupt, by 100 or 1,000 |
| Items | the strongest health, experience and Will/Strength/Skill potions, resurrection phials, a few one-off potions and Crunchy Chicks, labelled with the game's own names (the list is `ITEMS` in `mod/init.lua`) |
| Time of day | dawn, noon, dusk, midnight |
| Heal fully | health back to the most |

A notice says what was given. Nothing is saved by the menu itself; the game's next save keeps it.

## How it works

`ui/templates.json` lays out the pages; each row runs `Mod:lua:CheatMenu.<name>:<arg>` in
`mod/init.lua` for the hero of the player whose menu it is, which goes through the game's own
setters: `Refii.give_experience`, `Refii.give_gold`, `Refii.modify_renown` / `modify_morality` /
`modify_purity` (the game's `Stats.Modify*`, so the renown, morality and purity events fire),
`Refii.add_item` (the inventory's own add, once per item), `Refii.set_health` and
`Refii.set_time_of_day`. The Items page is a row list the Lua states (`Refii.set_menu_rows`) when
the Cheat Menu opens: each row whose record the database has (`Refii.record_exists`), labelled
`{NameTag} xN` from the record (`Refii.record_string`), which the menu draws through the game's
string book. Morality and purity change only on the machine that decides the session (the host
online, or the one machine alone), as the game's own setters do.
