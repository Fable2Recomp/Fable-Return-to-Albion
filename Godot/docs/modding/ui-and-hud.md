# UI and HUD

Godot draws every screen, so a mod's UI is data the engine's own views read — the same views the
shipped UI runs through, with no second implementation.

## Contents

- [How mod UI composes](#how-mod-ui-composes)
- [The HUD: ui/hud.json](#the-hud-uihudjson)
  - [Moving the game's own pieces: layout](#moving-the-games-own-pieces-layout)
  - [Copies of the game's own pieces: clones](#copies-of-the-games-own-pieces-clones)
  - [Each player's from one record](#each-players-from-one-record)
  - [Text pieces](#text-pieces)
- [Markers, fades and meters](#markers-fades-and-meters)
  - [The game's own world icons](#the-games-own-world-icons)
- [HUD overrides: icons, slides and holds](#hud-overrides-icons-slides-and-holds)
- [Boxes: messages, notices and menu boxes](#boxes-messages-notices-and-menu-boxes)
- [Modals: who answers the game's boxes](#modals-who-answers-the-games-boxes)
- [A mod's own menus](#a-mods-own-menus)
  - [How a game screen stands: its file and Godot's statements](#how-a-game-screen-stands-its-file-and-godots-statements)
  - [Row actions on the game's screens](#row-actions-on-the-games-screens)
- [Lua on the title screen](#lua-on-the-title-screen)
  - [The front-end surface](#the-front-end-surface)
- [Mod abilities: ui/abilities.json](#mod-abilities-uiabilitiesjson)
- [Spell slot picks: ui/spellwheel.json](#spell-slot-picks-uispellwheeljson)
- [Starters](#starters)

## How mod UI composes

`ui/hud.json` and `ui/templates.json` **compose across mods**: they are read from every enabled
package, lowest priority first, so a higher mod wins a shared piece and both mods' other pieces
land. A piece two mods state is recorded as a conflict and logged.

The Studio's **🖼️ UI** tab edits the HUD, menus, modals, presentations and the settings page
against the game's own HUD view and list view. Its **🖥 Screens** page shows any game screen as the
game draws it — the screen's book built by the game's own code from its file and template, its
slides playing — with the open replayable, slowed, paused or stepped and any component's slide
stated by hand; a presentation exported from the Presentations page is what it stands once the
preview reloads. Outside the editor, `ui_viewer.tscn` does the same per presentation (B book/file,
R replay, P pause, F step, Tab and `,` `.` to state a component's slide). Its editor calls are in
[Commands and queries](reference-commands.md#refiimodapi-in-the-editor).

## The HUD: ui/hud.json

### Moving the game's own pieces: layout

`"layout"` takes EXACTLY the entry shape of the player's own `user://hud/layout.json` (the HUD
editor's file), keyed by the shipped piece's id (`RefiiModApi.ListShippedHudPieces()`, or
`user://hud/layout.snapshot.json` after a run):

```json
{
  "layout": {
    "group.PlayerHealth.PlayerHealth": { "anchor": [0, 1], "offset": [64, -96] }
  }
}
```

- `anchor` is the fraction of the screen the piece hangs from ((0,0) top-left, (1,1)
  bottom-right); `offset` the distance from it in 720-high reference units, +X right, +Y down.
  Both are required.
- The fields the HUD editor writes beside them (`art`, `value`, `driven`, `placed`) are allowed
  and ignored. Moving a piece places it, including one the shipped layout leaves unplaced.
- **Precedence:** the shipped arrangement, then every mod's `layout` in load order, then the
  player's own file — a piece the player placed stays where they put it.

### Copies of the game's own pieces: clones

A copy is the game's own widget built again beside the original — it moves, fades and hides with
it — from the same art, with the differences the record states:

```json
{
  "clones": [
    { "id": "my_mod.stamina", "of": "group.PlayerHealth.PlayerHealth", "offset": [0, 34],
      "hue": 0.35, "hueElements": ["HealthBar"],
      "values": { "PlayerHealth": "player:my_mod.stamina/player:my_mod.stamina_max" },
      "bars": { "HealthBar": "PlayerHealth" } },
    { "id": "my_mod.boss", "of": "slidefader2.GenericMeter.GenericMeter", "offset": [0, 120],
      "values": { "GenericMeter": "mod:my_mod.boss/mod:my_mod.boss_max" },
      "slides": { "GenericMeter": 2 } }
  ]
}
```

| Field | Meaning |
|---|---|
| `id` | required: the copy's own id |
| `of` | required: the shipped piece copied (`<kind>.<Name>.<ElementName>`, or a `widget.` id — a widget copy draws exactly what the widget draws and takes none of the fields below) |
| `offset` | from the original, design units, +Y down |
| `hue`, `hueElements` | a hue rotation 0..1 of the named elements' art (red health to blue or green); the original keeps its colour |
| `icon` | `{ "element", "textures": [...] }`: replaces one element's picture with the first candidate that loads |
| `values` | meter name → `source` or `source/cap` (`mod:`, `player:`, `game:`, `hero:`), drawn as `source / cap × 100`; `"none"` holds a meter still; every meter not named stays the game's |
| `bars` | element → meter name: the elements a meter fills, redrawn at the meter's own value |
| `slides` | element name → slide number: the copy's own slide for that element |

**Slides.** A HUD piece's visibility is shared by the original and every copy, so raising a fader
by name raises the original too. A slide stated in `slides` is resolved for the copy alone, while
the original keeps the frame's state. That is how a piece the shipped game authors dark and never
lights — `GenericMeter` and `CrucibleMeter` are inactive on their master slide and lit by slide 2,
`On` — becomes a mod's own. Slides are numbered as the game counts them: 1 past the master slide.
The Studio's copy card lists the copied piece's slides in a picker ("Lit on slide"). A value that
is not a slide number is warned and ignored.

### Each player's from one record

A copy of player one's widget whose meters read `player:` values, or a piece that follows player
one's widget and reads one, is **each player's**: the engine builds it beside player two's widget
too, and every `player:` read is that player's. Player one's view, player two's half on split
screen and an online joiner's window all draw the right player from the one record. The twin's id
is the record's with `2` after it; a mod that states that id itself keeps its own. A record reading
only `mod:` values (one number for everyone) stays on the widget it names.

### Text pieces

```json
{
  "pieces": [
    { "id": "my_mod.gold_text", "kind": "text", "anchor": [1, 0], "offset": [-48, 40],
      "text": "mod:my_mod.gold", "format": "{0:0} gold",
      "size": 18, "colour": "#d8b23a", "align": "right" }
  ]
}
```

| Field | Meaning |
|---|---|
| `text` | required: a literal, a Babel tag (drawn as written when the book has no such tag), or `mod:<key>`, a published value; an unpublished value draws nothing, never a zero |
| `format` | for a `mod:` value: a .NET composite (`"{0:0} gold"`) or a bare numeric format (`"0.0"`, `"N0"`); empty draws up to two decimals |
| `size` | the point size (default 18, the HUD's own) |
| `colour` | HTML hex; default the HUD text's gold |
| `align` | `left`, `centre` (or `center`) or `right` of the piece's point; the text hangs from its top edge |
| placement | `anchor` + `offset`, or `follow` / `followElement` |

It is drawn by the HUD's own text path — the theme's fonts, inline icon tags included — and scales
with the HUD. Publish the value from any surface: `Refii.publish(key, n)`, `host.publish(key, v)`,
`refii::mods::Publish(key, v)`.

## Markers, fades and meters

Three HUD calls on every surface, drawn by Godot for the player named — 1 or 2, or 0 for every
player this machine plays — on split screen in that player's half. A machine draws only the
players it plays; the mod's code runs on the other machine too.

| Surface | Calls |
|---|---|
| Native | `refii_mod_mark(player, entity, xyz, icon, text)` · `refii_mod_unmark(id)` · `refii_mod_fade(player, to_black, seconds)` · `refii_mod_meter(player, id, label, value, max, style)` · `refii_mod_meter_remove(id)` |
| Lua | `Refii.mark(target, icon, text [, p])` · `Refii.unmark(id)` · `Refii.fade(p, dir [, seconds])` · `Refii.meter(id, label, value, max [, p [, style]])` · `Refii.meter_remove(id)` |
| Godot | `mark(target, icon, text, player)` · `unmark(id)` · `markers()` · `fade(player, to_black, seconds)` · `fade_state(player)` · `meter(id, label, value, max, player, style)` · `meter_remove(id)` · `meters()` |

From GDScript, the trailing arguments may be left off, from the right: `mark`'s `text` ("") and
`player` (0), `fade`'s `seconds` (0), and `meter`'s `player` (0) and `style` ("").

```lua
local id = Refii.mark(e.victim, "art/gui/wheelicons/abilities/will_shock.tex", "Marked", 0)
Refii.mark({x, y, z}, "art/my_mod_flag.png", "Go here", 2)
Refii.fade(1, "to_black", 0.5)
Refii.meter("my_mod.boss", "Thag", hp, hp_max, 0, "boss")
Refii.meter_remove("my_mod.boss"); Refii.unmark(id); Refii.fade(1, "from_black", 0.5)
```

- **Markers** ride the HUD's world-icon channel — the layer the game's own property signs, quest
  markers and opinion splashes are drawn on, with the same projection and clipping. A marker on an
  entity rides the drawn body (lifted by its height) and goes five seconds after the entity does;
  a point marker stands a little above its point. It is sized by the game's own distance curve
  (full size within 10 m, 0.6 beyond 17 m). `icon` is a game texture path (`art/gui/.../x.tex`),
  one of the [world icon aliases](#the-games-own-world-icons), or a PNG an enabled mod ships
  under `art/`; an icon that loads nothing draws the text alone, warned once.
- **Fades** are the host's own black layer per player, drawn under the game's fade: to black
  (`"to_black"` / `"out"`) or back (`"from_black"` / `"in"`) from the alpha standing, over the
  seconds asked (0 or nil at once). They never touch the game's own fade, which level changes and
  save loads wait on. `fade_state` answers `alpha` (0 clear .. 1 black), `target`, `seconds`.
- **Meters** are stacked under the top centre in the order first shown. Styles: `bar` (gold, the
  default), `boss` (wide, red), `timer` (narrow, pale), and the game's own meter art, which the
  shipped game never shows: `generic` (the ornate bar with the red orbs), `crucible` (the plain
  red bar) and `price` (the bowl with the arrows and the A prompt; the value drawn as a whole
  number). The value and maximum are read live; the same id restates a meter in place.
- Ids: any surface's `unmark` takes any surface's id. Everything a session put up is cleared when
  the game goes down.

### The game's own world icons

`icon = "quest"` names the texture the game's own world-icon builder uses for that kind:

| Alias | Texture (under `art/gui/`) | The game draws it for |
|---|---|---|
| `quest` | `hud/inworld_reward_icon_quest` | the quest marker over a giver or objective |
| `reward`, `freebie` | `hud/inworld_reward_icon`, `hud/inworld_reward_icon_freebie` | a reward, a free one |
| `reward_quest` | `wheelicons/rewards/reward_quest` | the quest reward sign |
| `follow`, `wait` | `wheelicons/npcstatus/icon_npc_follow`, `…_wait` | a follower's status |
| `engaged`, `married`, `child` | `wheelicons/npcstatus/icon_npc_engaged`, `…_married`, `…_child` | a family member |
| `romance`, `love` | `wheelicons/icon_romantically_interested`, `wheelicons/opinions/opinion_love` | a love interest |
| `exit` | `wheelicons/icon_level_exit` | a region exit |
| `button_a` | `controller/icon_button_a` | an interaction prompt |
| `gold` | `wheelicons/icon_gold_coin` | gold on the ground, a sale |
| `player1`, `player2` | `wheelicons/splashopinions/icon_player1`, `…_player2` | the co-op partner's splash |
| `dig`, `chest`, `pet_quest` | `wheelicons/pet/icon_pet_point_dig`, `…_chest`, `…_quest` | the dog's finds |
| `talk` | `multiplayer/orb_icon_talk` | the co-op orb's talk prompt |
| `lock` | `icon_lock` | the default for a kind without a texture |
| `opinion_attractiveness`, `opinion_ridiculousness`, `opinion_love`, `opinion_morality`, `opinion_respect`, `opinion_ugly`, `opinion_fear`, `opinion_hate`, `opinion_evil`, `opinion_disrespect`, `opinion_renown`, and each with `_gold` (not renown) | `wheelicons/splashopinions/<alias>` | the opinion splashes over a villager |
| `morph_fat`, `morph_thin`, `morph_tall`, `morph_short`, `morph_strong`, `morph_weak`, `morph_attractive`, `morph_ugly`, `morph_good`, `morph_evil` | `wheelicons/splashopinions/<alias>` | the morph splashes |

An unknown name is a path as given.

## HUD overrides: icons, slides and holds

Three calls change what the game's own HUD elements show, per player, by element name — the way a
quickslot mod puts its own icons on the D-pad and keeps the D-pad on screen:

| Surface | Calls |
|---|---|
| Native | `refii_mod_hud_icon(player, element, texture)` · `refii_mod_hud_slide(player, element, slide)` · `refii_mod_hud_hold(player, piece, on)` |
| Lua | `Refii.hud_icon(p, element, texture)` · `Refii.hud_slide(p, element, slide)` · `Refii.hud_hold(p, piece, on)` |
| Godot | `hud_icon(player, element, texture)` · `hud_slide(player, element, slide)` · `hud_hold(player, piece, on)` · `hud_overrides(player)` |

```lua
Refii.hud_icon(1, "DPadUp", "Art\\GUI\\WheelIcons\\expressions.tex")  -- a game texture
Refii.hud_icon(1, "DPadLeft", "art/my_mod/potion.png")                  -- a PNG the mod ships
Refii.hud_slide(1, "DPadUp", 2)                                         -- the slot's "on" slide
Refii.hud_hold(1, "dpad", true)                                         -- keep the D-pad on screen
Refii.hud_icon(1, "DPadUp", ""); Refii.hud_slide(1, "DPadUp", 0); Refii.hud_hold(1, "dpad", false)
```

- **Player** is 1 or 2, or 0 for both. The shared HUD is the local player's (player two's on an
  online joiner); local player two's half on split screen takes player two's overrides.
- **Element** is a slide-toggle binding name — the name the guest's own `GUI.ToggleElement` states:
  `DPadUp`, `DPadDown`, `DPadLeft`, `DPadRight` — or an element path of `hud.bgf` matched by its
  trailing segments (`dpadmenu.group.up.up`). An icon lands on the element's quad, or the leaf
  quads beneath it.
- **Icons** are a game texture path or a PNG an enabled mod ships under `art/`, resolved as
  markers resolve theirs. Art that is not 128 × 128 is fitted the way the game's own slot icons
  are: 84% of the slot on its longer side, centred, transparent around. `""` clears.
- **Slides** are counted as the game counts them (1 the first; a toggle's off is 1, on is 2); 0
  clears and the guest's own stands.
- **Holds** name a HUD piece (`dpad`): every slide owner under it is kept on screen — one with an
  `in` slide is forced onto it, the others keep the slide the guest states — so a piece the guest
  tweens away stays up.
- Everything is cleared when the mod's Lua reloads and when the game goes down; state it again from
  `lua_ready` / `hero_ready`.

## Boxes: messages, notices and menu boxes

| Surface | Calls | Answer |
|---|---|---|
| Godot | `message_box(title, text, buttons) -> id` | `message_box_answered(id, button)` |
| Lua (game and title) | `Refii.message_box(title, text [, accept [, cancel [, p]]])` · `Refii.notice(title, text [, seconds [, p]])` · `Refii.menu_box(title, text, options [, p [, cancelable]])` | the events `mod_message_box`, `mod_notice`, `mod_menu_box` |

From Godot, `notice(title, text, seconds = 0, player = 0) -> id` and
`menu_box(title, text, options, player = 0, cancelable = true) -> id` open the other two boxes and
answer with `notice_done(id, outcome)` and `menu_box_answered(id, option)`; `message_box` also
takes `player = 0`. An argument written `= value` may be left off, from the right.

- A Godot `message_box` with `buttons` empty or one caption is a notice (one dismiss); two
  captions are a question (A and B). `title` sits above `text`. In game only.
- `button` is 0 for A (accept, or a notice's dismiss), 1 for B, -1 only when the box was
  withdrawn before it was answered. One question and one notice stand at a time; the rest queue
  in order — a second question is asked once the first is answered, a notice told under a notice
  follows it. A box is never answered -1 on another box's account.
- `mod_menu_box` answers `option` (-1 none); `mod_notice` answers `outcome` (0 shown, -1 never
  shown).

## Modals: who answers the game's boxes

`ui/templates.json` takes a `"modals"` block: the answer table for the game's query and message
boxes — which input codes A and B send back through the game's own router — one whole record per
box:

```json
{
  "modals": {
    "13":   { "kind": "query", "confirm": 39, "cancel": 37 },
    "20":   { "kind": "query", "confirm": 36 },
    "0@22": { "kind": "message", "confirm": 36 },
    "42":   null
  }
}
```

- The key is the game's modal id, or `<id>@<screen id>` for a row that holds over one screen only
  (a screen-specific row wins over the any-screen one).
- `kind` is `query` (A `confirm`, B `cancel`; no `cancel` means B does nothing) or `message` (one
  dismiss code). `null` removes the row: the box is still drawn, but keeps the game's own input.
- The table decides who ANSWERS a box, never whether it is drawn. The shipped rows, and every row
  as it ran, are in `user://ui/templates.snapshot.json` under `"modals"`.
- Editor: `RefiiModApi.SetModal(key, record)`, `ReleaseModal(key)`, `RemoveModal`, `GetModals`,
  `ListModals`, `ShippedModals`.

## A mod's own menus

A menu a mod defines in `ui/templates.json` opens as a list OVER the one it was chosen from — the
front end's list, the pause list or a screen's list — from a row whose `opens` names it or whose
command is `Opens:<menu>`. B collapses a fold first, then closes the menu and returns to exactly
where it was opened; menus nest. The list is built by the template's own construction, so a mod
menu folds, ghosts and sources rows as the shipped lists do. On the pause list a mod menu closes
with the root.

```json
{
  "menus": {
    "pause-root": {
      "patch": true,
      "entries": [ { "name": "my_mod.menu", "tag": "MY_MOD_MENU", "opens": "my_mod.menu", "hostOnly": true } ]
    },
    "my_mod.menu": {
      "title": "MY_MOD_MENU",
      "entries": [
        { "name": "greet", "tag": "MY_MOD_MENU_GREET", "command": "Mod:lua:my_mod_menu.greet" },
        { "name": "gold",  "tag": "MY_MOD_MENU_GOLD",  "command": "Mod:lua:my_mod_menu.give_gold:100" },
        { "name": "more",  "tag": "MY_MOD_MENU_MORE",  "command": "Opens:my_mod.more" }
      ]
    }
  }
}
```

- A row patched onto the **pause list** must carry `"hostOnly": true`: the pause rows take their
  labels from the game's own list by order, and a row the game never spawned has to say so.
- The shipped **options**, **upgradefurniture** and **decoration** screens carry
  `"commitsByHighlight": true` (a screens-record flag, default false): on such a screen a press
  puts the game's own menu cursor on the row and sends the game's A, instead of pushing the row's
  identity. A mod's row on one of these screens therefore needs a `command` (a host action), or a
  name the game's own list also carries.
- `Mod:lua:<function>[:<arg>]` calls a global Lua function — a name or a dotted path
  (`my_mod_menu.give_gold`), looked up, never compiled — in the game VM on the next guest tick,
  protected, as `function(arg, e)`: `arg` is everything after the second colon (`nil` when there
  is none), `e` the row's `mod_menu_row` table. On the title it runs in `mod/frontend.lua`.
- Any other command on a mod menu's row runs as a host action (`setting:toggle:…`,
  `setting:set:…`, an options page) and never reaches the game.
- The game's own screen verbs can be named on any row: `Ability:learn:<1..14>` buys one level of
  that ability through the game's own purchase, `Ability:unlearn:<1..14>` gives one back through
  its own unlearn (without the confirm Y asks first); `Shop:buy:<ticket>` / `Shop:sell:<ticket>`
  is a shop row's transaction, pushed at the shop screen's own handler, whose own confirm follows.
  Shop rows carry it already.
- A Godot script opens a menu itself with `host.open_menu(name)` (false when no list is up, or no
  template has that name); Lua with `Refii.open_menu(name [, p])` on the title.

```lua
my_mod_menu = {}
function my_mod_menu.give_gold(arg, e)
  local hero = Refii.hero(e.player)     -- the player who chose the row
  if hero ~= nil then Refii.give_gold(hero, tonumber(arg) or 0) end
end
```

Every row of a mod's menu, every row a mod patched into a shipped menu, and every `Opens:` /
`Mod:lua:` row raises **`mod_menu_row`** on all three surfaces — `menu` (the mod menu's name, or
the list's own: `frontend-root`, `pause-root`, a screen's), `row`, `player` (whose menu it is) and
`mod` (the mod that stated the row or its menu):

```cpp
static void OnRow(refii::mods::ModMenuRowEvent& e) { /* e.menu, e.row, e.player, e.mod */ }
REFII_MOD_ON(ModMenuRow, OnRow);
```
```lua
Refii.on("mod_menu_row", function(e) Refii.log(e.menu .. "/" .. e.row) end)
```
```gdscript
Engine.get_singleton("RefiiHost").mod_menu_row.connect(func(e): print(e.menu, "/", e.row))
```

Godot raises it where the row is chosen, so a script sees it the same frame; native and Lua
handlers get it on the next guest tick. It is not cancellable.

Editor: `RefiiModApi.SetMenu(name, record)`, `RemoveMenu`, `GetMenu`, `ListModMenus`,
`SetMenuRow(menu, entry)`, `RemoveMenuRow`, `OpensCommand(menu)`, `LuaCommand(function, arg)`,
`ParseMenuCommand(command)`.

### How a game screen stands: its file and Godot's statements

Godot draws every book from its own `.bgf`, not from the game's hidden copy of it. A screen
record says how:

```json
{
  "screens": {
    "23": {
      "restFromFile": true,
      "restPose": { "Foreground.ExpandableMenu.KeyLight": [22] },
      "restOpacity": { "Foreground.Book.Pages.QuestsPage.Contents.Scrollbar": 0 }
    }
  }
}
```

- `"restFromFile": true` stands the book on the file's REST state: every component on its
  first slide (one whose first slide is its opening `PlayIntro` on the one after), every
  element switched on, and nothing read back from the game. Whatever the screen shows beyond
  that comes from Godot: the page provider's values, switches, slides, positions, textures and
  tints, and the game's GUI registries (below). The shipped books all carry it; a mod screen
  that sets it gets a picture that never drifts with the game's hidden cursor.
- **The slides play.** A stood book runs each component's slide over real time as the game
  does: the slide's animation tracks (position, scale, rotation, pivot, opacity) evaluate from
  0 to the slide's length, a slide plays its passes and holds its end (or loops / ping-pongs as
  its flags say), a go-next slide hands on to the next one, and an element's `timeoffset` /
  `endtime` window hides it outside that span. Changing a slide — by a provider's slide value,
  a registry write or a page turn — enters the new one from 0, so a mod screen's intros and
  transitions animate with no code. A `restPose` / `restOpacity` value or a provider's stated
  position or opacity is a later write and wins over a track.
- `"restPose"` puts an element where the game's own open leaves it, when that is not its file
  pose: a path to up to six numbers — position x, y, z, then scale x, y, z — with `null` for
  "keep the file's".
- `"restOpacity"` does the same for opacity, 0..100.
- `"flowWhenDrawn": true` limits a screen's measured reflow (`flowMeasured` / `flowBase` /
  `flowMoved`) to when the measured element is on screen — the pause legend moves under the quest
  objective only while there is an objective.
- **The GUI registries come for free.** An element the file binds by `TextName`,
  `SlideToggleName`, `SlideFaderName`, `SlideFader2Name`, `SlideSetName`, `MeterName` or
  `ImageName` draws what the game publishes under that name — the text, the slide, the meter
  (scrubbing the element's own timeline at percent × 10 ms) or the picture — on any book, a
  mod's included. A provider's own value still wins over a registry one.
- Elements with no authored name (a file's root) are addressed as `#<ordinal>` in any path a
  provider states, `#0` being the root.

### Row actions on the game's screens

A mod puts a button of its own on a shipped screen list with the `"rowActions"` block of
`ui/templates.json`, keyed by the list's menu template name — `ItemsMenu` and `WeaponsMenu` (the
inventory's two lists), `ExpressionsMenu` (the logbook's expressions), `AbilitiesMenu`, or a mod
menu's own name:

```json
{
  "rowActions": {
    "ItemsMenu":       [ { "button": "Y", "tag": "QUICKSLOTS_ASSIGN", "command": "Mod:lua:quickslots.assign" } ],
    "WeaponsMenu":     [ { "button": "Y", "tag": "QUICKSLOTS_ASSIGN", "command": "Mod:lua:quickslots.assign" } ],
    "ExpressionsMenu": [ { "button": "Y", "tag": "QUICKSLOTS_ASSIGN", "command": "Mod:lua:quickslots.assign" } ],
    "AbilitiesMenu":   [ { "button": "X", "tag": "QUICKSLOTS_ASSIGN", "command": "Mod:lua:quickslots.assign" } ]
  }
}
```

- While that list is up and a leaf row is highlighted, the prompt — the button's icon and the
  tag's text (a Babel tag, or a literal) — is drawn beside the page buttons where the game draws
  its own. The pad under a Godot-owned list is the host's: X, Y, LB and RB never reach the game
  while one stands (`X` is the equip hold, `Y` the flip hold: the two a screen list can take; the
  shipped prompt for the same button stands aside).
- On `AbilitiesMenu`, Y is the game's unlearn — its confirm, then one level back. A mod's Y row
  action on that list runs instead of it.
- On the press, or a click on the prompt, the row raises **`mod_menu_row`** on every surface with
  `menu` the list's template name, `row` the highlighted row's name, `player`, `mod`, and the two
  trailing fields `command` — the row's OWN command, `Inventory:use:1234`, `Inventory:equip:…`,
  `Ability:learn:9`, or on the expressions list the expression id — and `button`. Then the
  action's `command` runs exactly as a mod menu row's: `Mod:lua:<function>[:<arg>]` calls your
  Lua with `(arg, e)`, `e` carrying the same fields.
- A later mod's list for the same menu replaces the earlier one's; `null` or `[]` removes it. The
  first action's prompt is drawn; every action's button is held and answered.

```lua
quickslots = {}
function quickslots.assign(arg, e)
  -- e.menu "ItemsMenu", e.row the item row, e.command "Inventory:use:1234", e.button "Y"
end
```

## Lua on the title screen

There is no game on the title — no hero, no save, no world, no game VM — so a mod's Lua for the
front end is its own file, `mod/frontend.lua`, run in the current front-end presentation's own Lua
state the first time a front-end chunk needs it (a row's `Mod:lua:` call, a box's answer, a
`start_game`); another presentation starts fresh. `Refii.frontend` is true there. A row on the
title is a patch on `frontend-root`:

```json
{ "menus": { "frontend-root": { "patch": true, "entries": [
  { "name": "my_mod.title", "tag": "MY_MOD_TITLE_ROW", "after": "Multiplayer", "command": "Mod:lua:my_mod_frontend.ask" } ] } } }
```
```lua
my_mod_frontend = {}
local asked = nil
function my_mod_frontend.ask(arg, e)
  asked = Refii.menu_box("My Mod", "Pick one.", { "Hello", "Goodbye" }, e.player)
end
Refii.on("mod_menu_box", function(e)
  if e.id == asked and e.option >= 0 then Refii.notice("My Mod", "You chose " .. (e.option + 1), 0, e.player) end
end)
```

The **Front end** starter writes exactly this; Spell Weaving's `mod/frontend.lua` puts a "Spell
Weaving" row on the title whose menu box explains the mod.

### The front-end surface

What `Refii` offers in `mod/frontend.lua`:

| In `Refii` on the front end | What it is |
|---|---|
| `Refii.on(name, fn)` for `start_game`, `mod_menu_row`, `mod_message_box`, `mod_notice`, `mod_menu_box` | the events raised without a world: the start target stated (`save`, `level`, `new_game`, `player`, `commit`), a row chosen, a box or notice answered. No other event reaches here — every other seam needs the world |
| `Refii.signed_in()` | the signed-in pad's user index, nil for none |
| `Refii.save_count()` | the signed-in profile's saved games on every device, nil with no front end — a content enumeration: ask once a screen (on a `mod_menu_row`), never per tick |
| `Refii.screen()` | the current presentation's name (`FrontendStartup`, `FrontendMainMenu`, …), nil for none |
| `Refii.hash(name)` | the game's FNV-1 of a name as a `"0x…"` string, as in a game |
| `Refii.setting(key, default)` | the mods' `settings.json` values, the values in force |
| `Refii.log(text)` | a line in the game log, `[mod lua, front end]` |
| `Refii.publish(key, n)` · `Refii.value(key)` | the shared value store every surface and the UI read; a published value outlives the presentation |
| `Refii.notice(title, text [, seconds [, p]])` · `Refii.message_box(title, text [, accept [, cancel [, p]]])` · `Refii.menu_box(title, text, options [, p [, cancelable]])` | the host's own notice, question and menu box; answers come as the events above |
| `Refii.open_menu(name [, p])` · `Refii.menu_player()` | a mod's menu over the front end's list; whose menus are open (1, or 2 for a joiner) |
| `Refii.frontend == true` | the mark a chunk tests |

**Not here, and why:** `hero`, `player_hero`, `local_hero`, health, position, spawn, `play_fx`,
`player_*`, `save_get_int` / `save_set_int` and `run_lua` (no world, no game VM, no loaded save);
`open_screen` (a host screen over the pause book — in game); `require` (one file,
`mod/frontend.lua`). A save's name and level reach a mod the moment they are chosen
(`start_game`); its mod values once it is loaded (`save_loaded`, in `init.lua`). A profile sign-in
change is not an event; ask `Refii.signed_in()` when you need it.

## Mod abilities: ui/abilities.json

An ability of the mod's own on the abilities book — a row after its category's own abilities, or
on a page only mods declare (a `ui/templates.json` patch on `AbilitiesMenu` with source
`abilities:<category>`). It is **declared**; the engine runs it, the same for every mod and both
players:

```json
{ "abilities": [ {
  "name": "SoulsCombatStamina", "category": "Strength",
  "nameTag": "SOULS_COMBAT_STAMINA_NAME", "descriptionTag": "SOULS_COMBAT_STAMINA_DESCRIPTION",
  "maxLevel": 5, "levelKey": "souls_combat.stamina_ability",
  "costLike": 3, "refund": "half",
  "stats": [100, 128, 185, 214, 242, 271],
  "legacySaveKey": "SoulsStamina",
  "image": "art/gui/abilitytarot/souls_combat_stamina.tex" } ] }
```

| Field | Meaning |
|---|---|
| `levelKey` | the SAVED player value each player's level lives in (it goes home with that hero's save). The mod reads it where it applies the effect: `refii_mod_player_value(p, key, 0)`, `Refii.player_value`, `player:<key>` |
| `costLike` | the ability type (1..14) whose experience table prices the levels, through the game's own cost call; `costLikeSetting` names a mod setting that overrides it |
| `category`, `disciplines` | the page, and the pools the price is drawn from: split evenly (the first named takes the odd point), each share from its own pool and then General |
| `refund` | `"half"`: Y unlearns a level through the game's own confirm and result boxes and half its price comes back into General; `"none"` (default): no unlearn |
| `stats` | the description's `%1` per level 0..maxLevel (the page shows the next level's); or `stat`, a value source read for the book's player |
| `legacySaveKey` | the `RefiiModSave` key an older save kept player one's level in; a hero with none saved takes it |

A on the row buys a level for the book's player: the engine checks the price against that hero's
pools, raises **`mod_ability`** — a handler may refuse it — spends through the game's own Spend
and saves the level one higher. Beside the level the engine states, per player,
`<levelKey>.cost` (the next level's price, -1 at the maximum), `.refund` and `.afford` (1 when A
can buy), which a HUD or menu can read as `player:` sources.

```cpp
static void OnAbility(refii::mods::ModAbilityEvent& e) {
  if (std::strcmp(e.ability, "MyAbility") == 0 && !e.refund && e.level == 5 && !Ready(e.player)) e.cancel = true;
}
REFII_MOD_ON(ModAbility, OnAbility);
```

Keep ability levels in the player store, never in the game's `HeroAbility` stats: the game's
loader has no bound on that index.

## Spell slot picks: ui/spellwheel.json

A pick per spell slot beside the slot's own spell — Spell Weaving's partner. The game's own pick
(the slot's spell) and a mod's are one data form; a mod states its picks:

```json
{ "picks": [ {
  "name": "spell_weaving.partner",
  "store": "spell_weaving.partner_{slot}",
  "inEffect": "player:spell_weaving.partner_active_{slot}",
  "unlocked": "player:spell_weaving.ability",
  "exclude": [2, 7], "empty": "art/gui/hud/spell_weaving_slot.tex", "scale": 0.55,
  "apply": "SPELL_WEAVING_SET_PARTNER", "remove": "SPELL_WEAVING_REMOVE_PARTNER" } ] }
```

- On the quick wheel and the abilities blade, X on a will picks it for the highlighted slot and Y
  clears it, for the selectors' player. The engine raises **`spell_pick`** (`spell` writable,
  cancel keeps the old pick) and saves the pick under `store` (`{slot}` is the slot's number,
  1..5; -1 none) in that player's store — nothing else writes it, so a mod reads its players'
  picks from the store.
- `inEffect` and `unlocked` are value sources read for the selectors' player: what the mod says is
  in effect (a stored pick that is not is drawn faint) and when the pick is offered at all.
- The game's own pick raises the same event — `pick` "spell", `mod` "" — before it writes: every
  step of the selectors' carousel, the scripts' spell-table natives and the host's own assignment.
  A changed `spell` is the one stored; a cancel leaves the slot as it was. The template's
  defaults, the other machine's pick arriving and a save load raise nothing.
- The selectors draw every enabled mod's pick under each slot, one small icon per pick in load
  order along the slot icon's lower edge, scaled so together they cover no more of the slot than
  one. X and Y act on each player's ACTIVE pick, drawn larger; a press with nothing to do on it
  steps to the next pick, and LB/RB step while the abilities blade is up. The blade's X/Y prompts
  read the active pick's `apply` and `remove`.
- A pick of the same `name` from a later mod replaces an earlier one, so the validator warns a
  `name` without the mod's prefix.

## Starters

**New mod from template** offers, among others:

| Starter | Writes |
|---|---|
| **Mod menu** | a pause-list row opening a menu whose rows call the mod's Lua |
| **HUD text** | a top-right line showing the hero's gold from a published value |
| **HUD piece** | a gold meter under the health widget, fed by a value `init.lua` publishes every tick |
| **Front end** | a title-screen row whose `mod/frontend.lua` raises a menu box and answers it with a notice |

The game's own options pages are the same kind as a mod's settings page: the Dynamic Sky and
Weather page is a description in exactly the `settings.ui.json` shape, read by the same parser
over the game's config switches. A `choice` there may store text with `aliases`; a mod's choices
stay numbers, since a mod setting holds a number.
