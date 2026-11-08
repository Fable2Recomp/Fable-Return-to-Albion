# Settings and input

Numbers a player can tune, and buttons a mod can bind.

## Contents

- [settings.json](#settingsjson)
- [The settings page: settings.ui.json](#the-settings-page-settingsuijson)
- [Input actions: input.json](#input-actions-inputjson)
  - [Who presses it](#who-presses-it)
  - [The input_action event](#the-input_action-event)
  - [Chords](#chords)
  - [Claims: taking a button from the game](#claims-taking-a-button-from-the-game)
  - [Presses: pressing a button for the player](#presses-pressing-a-button-for-the-player)
  - [The keyboard's attack mode](#the-keyboards-attack-mode)
  - [Rebinding](#rebinding)
  - [Pad bindings name the game's buttons](#pad-bindings-name-the-games-buttons)

## settings.json

A mod's tuning lives in `settings.json` at the package root, one object under the mod's name:

```json
{ "my_mod": { "hero_damage": 1.5, "regen_delay": 0.7 } }
```

| Surface | Read |
|---|---|
| Lua | `Refii.setting("my_mod.hero_damage", 1.5)` — the value with the player's choice over it, read at the call |
| Godot | `host.setting("my_mod.hero_damage", 1.5)`; `host.set_setting(key, value)` sets one for the session |
| Native | `refii::mods::Setting("my_mod.hero_damage", 1.5f)` |

Every setting is a number. A change made live — the Options › Mods page, a script's
`set_setting`, the Studio's live tune — reaches native code at once and the running Lua at the
next guest tick, so a mod that reads `Refii.setting` where it uses the value plays the new number
without a reload.

A mod that keeps something built from a setting (a marker's size, a pool's maximum) hears the
change: `Refii.on("setting_changed", function(e) ... end)` with `e.key`, `e.value` and
`e.previous`, raised in the game VM after `Refii.setting` answers the new value, and the Godot
signal `setting_changed(key, value)`. Both fire only on a real change, never at session start.

## The settings page: settings.ui.json

An enabled mod whose author offers settings gets a page in the game: **Options → Mods → the mod's
title**. Left and right step a value, applied to the running game at once and saved to the
player's `user://mod_settings.json`, which stands over the file's values in every later session.
A and B leave the page.

The author **offers** rows in `settings.ui.json` beside `settings.json`, keyed by the setting's
name without the `<mod>.` prefix. Only described settings are shown; the rest stay the author's
tuning, and a mod with no `settings.ui.json` has no page at all.

```json
{
  "swing_cost": { "label": "Swing cost", "min": 0, "max": 100, "step": 1, "type": "slider",
                  "description": "Stamina a melee swing spends." },
  "woven":      { "label": "Weaving", "type": "toggle" },
  "difficulty": { "label": "Difficulty", "type": "choice",
                  "options": [ { "value": 0, "label": "Story" },
                               { "value": 1, "label": "Normal" },
                               { "value": 2, "label": "Hard" } ] }
}
```

| `type` | Row |
|---|---|
| `slider` | a number stepped from `min` to `max` by `step` |
| `toggle` | Off / On, 0 or 1 |
| `choice` | a fixed list of labelled values, stepped in the file's order; writes the chosen option's `value` (a number) |

- A value no option states (an old value, a hand edit) shows the nearest option.
- A `label`, `description` or option `label` written `{KEY}` is your text table's row, drawn in the
  player's language (see [Text keys](gdb-and-text.md#text-keys)); the page's title is `mod.toml`'s
  `name`, which may be a key too. Off / On are the game's own words.
- An option with no `value` is skipped; one with no `label` is captioned by its number; a
  `choice` with no usable option falls back to a slider. `min`, `max` and `step` are ignored on a
  choice.
- The editor writes rows with `RefiiModApi.SetSettingRow(setting, row)`,
  `SetSettingChoice(setting, label, options)`, `RemoveSettingRow`, `GetSettingsUi`; the UI tab's
  Settings page edits them with a preview.

## Input actions: input.json

A mod declares its own input actions in `input.json` at the package root and receives them as
events, on the keyboard and on either player's pad. One entry per action, keyed by its name; the
game scopes it `<mod>.<action>`:

```json
{
	"dash":    { "label": "Dash", "keys": ["F"], "buttons": ["L3"], "axis": null },
	"aim":     { "label": "Aim",  "keys": ["Ctrl"], "buttons": ["LT"], "axis": "-RightY" },
	"slot_up": { "label": "Slot up", "keys": ["F1"], "buttons": ["DpadUp"] },
	"alt_up":  { "label": "Alt slot up", "keys": ["F1"], "buttons": ["DpadUp"], "chord": "LT" }
}
```

| Field | Meaning |
|---|---|
| `label` | the name the rebinding page shows; defaults to the action's name. `{KEY}` is a row of your text table, in the player's language ([Text keys](gdb-and-text.md#text-keys)) |
| `keys` | Godot key names: `F`, `Ctrl`, `Space`, `F5`, `BracketLeft`, … |
| `buttons` | `JoyButton` names (`A`, `LeftShoulder`, `DpadUp`, `Guide`, …) or the Xbox aliases `A B X Y LB RB L3 R3 LT RT Start Back`; `LT` and `RT` are the triggers, pressed past half their travel |
| `axis` | one `JoyAxis` name (`LeftX`, `LeftY`, `RightX`, `RightY`, `TriggerLeft`, `TriggerRight`) or null; pressed past half deflection either way, or one way with a leading `+` / `-` |
| `chord` | optional: a held modifier the action fires under — `LT`, `RT`, `LB` or `RB` on the pad, or a Godot key name on the keyboard (see [Chords](#chords)) |

- A list field also takes a single string.
- An action with nothing bound is ignored with a warning.
- A later mod in the load order declaring the same scoped name replaces the earlier one.
- Each action is also registered in Godot's `InputMap` as `mod.<mod>.<action>`, for Godot
  scripts.
- The editor writes entries with `RefiiModApi.SetInputAction(action, binding)` (every name is
  checked, the chord too) and `RemoveInputAction(action)`.

### Who presses it

Player 1 is the keyboard plus the pad holding the game's pad 0; player 2 is the pad holding the
game's pad 1 — the same assignment the game's own pads use, so whichever controller pressed START
to drop in is the one player 2's actions read. Devices are read only while the game window has
focus; losing focus releases every held action once. The keyboard does not count while the dev
menu has it.

### The input_action event

| Field | Meaning |
|---|---|
| `name` | the scoped action, `<mod>.<action>` |
| `player` | 1 or 2 |
| `pressed` | true on the press, false on the release |

Edges only: one event when the action goes down and one when it comes up. Hold the pressed flag in
your own handler if you need "while held".

```lua
local dashing = { false, false }
Refii.on("input_action", function(e)
  if e.name == "my_mod.dash" then dashing[e.player] = e.pressed end
end)
```

### Chords

An action with a `"chord"` fires only while its modifier is held: `LT`, `RT`, `LB` or `RB` on the
pad, or a key (`Shift`, `Alt`, …) on the keyboard. While a chord's modifier is held, a PLAIN action
(no chord) on the same button or key does not fire — so `slot_up` above is D-pad up alone and
`alt_up` is LT + D-pad up, never both. A pad chord gates the pad and the keyboard both (player 1's
keyboard reads player 1's pad for its trigger); a key chord gates the keyboard alone. The chord is
not rebound; the keys and buttons under it are, as usual.

### Claims: taking a button from the game

Declaring an action does not stop the game reading the same button: D-pad up still changes the
item slot while your `slot_up` fires. A CLAIM does. While an action is claimed for a player, its
pad buttons, triggers and keys are withheld from the pad the guest sees for that player — the
game's D-pad is F1–F4 on the keyboard, so a claimed action bound to F1 takes F1 from the item slot
too — and a chord's modifier goes with it while the chord is held (claim an LT chord and the guest
does not see LT while LT + the button is down, so the lock-on does not fire under your chord). Your
own `input_action` events still fire; the guest's edge detection never sees a release it was not
shown a press for.

| Surface | Call |
|---|---|
| Lua | `Refii.input_claim(p, "my_mod.slot_up", true)` — p 1 or 2, 0 for both |
| Godot | `host.claim_input(player, "my_mod.slot_up", true)`; `host.input_claimed(player, action)`, `host.input_claims(player)` read the state |
| Native | `refii_mod_input_claim(player, "my_mod.slot_up", 1)` |

Claims are per player and are cleared when the mod's Lua reloads and when the game goes down, so a
mod claims again from its `lua_ready` (or its `hero_ready`), and releases with `false` when it no
longer wants the button — a menu of its own closing, say. An action no enabled mod declares is
refused (false).

```lua
Refii.on("lua_ready", function()
  for p = 1, 2 do Refii.input_claim(p, "my_mod.slot_up", true) end
end)
```

### Presses: pressing a button for the player

The mirror of a claim: a PRESS puts a pad button down on a player's pad. Godot ORs it into that
player's pad state after the claims are withheld — on both pad paths, so player two's pad as well
as player one's — and the guest reads it as the player's own pad. It is a scripted source, not a
device, so it reaches the guest whether the window has focus or not, and a menu that owns the
game's buttons masks it as it masks the devices. Never fake a key event (`Input.parse_input_event`)
instead: that is player one's keyboard only, lost without focus.

| Surface | Hold / release | Tap |
|---|---|---|
| Lua | `Refii.press(p, "RT", true)` / `Refii.press(p, "RT", false)` | `Refii.tap(p, "B", 2)` |
| Godot | `host.press(player, "RT", true)` / `host.press(player, "RT", false)`; `host.presses(player)` lists what is held | `host.tap(player, "B", 2)` |
| Native | `refii_mod_press(player, "RT", 1)` / `refii_mod_press(player, "RT", 0)` | `refii_mod_tap(player, "B", 2)` |

- `player` is 1 or 2 — a press is one player's pad (0 is refused).
- Button names are input.json's: `A` `B` `X` `Y` `LB` `RB` `L3` `R3` `Start` `Back`, the d-pad as
  `DpadUp` `DpadDown` `DpadLeft` `DpadRight`, and `LT` / `RT` for the triggers.
- `held` false / 0 releases. A button is down for any other value; a trigger is pulled by it —
  true / 1 a full pull, 2..255 that much (the guest counts a trigger past 30 as pressed).
- A tap is down for at least `ticks` whole guest game ticks (1..600; Lua's default 1), counted on
  the guest's own tick counter (`game+0xFC`) that the pad publish reads, then let go. With no world
  up (the front end) the ticks count Godot frames; a tap whose guest stops ticking (a pause) is let
  go after 10 s. A trigger tap is a full pull.
- Holds and taps are cleared with the claims: when the mods' Lua reloads and when the game goes
  down. Release what you hold when you are done.

```lua
-- Sprint ends when the sprint button is let go: a two-tick tap of the game's own button.
Refii.tap(p, "A", 2)
```

### The keyboard's attack mode

On the keyboard one key attacks with the mode the player chose — Fable III's 1, 2 and 3 put it on
melee (the game's X), ranged (Y) or magic (B). The mode is the host's, so a mod reads it rather
than tracking the mode keys itself:

| Surface | Read | Hear |
|---|---|---|
| Lua | `Refii.attack_mode(p)` | `Refii.on("attack_mode_changed", function(e) ... end)` — `e.player`, `e.mode` |
| Godot | `host.attack_mode(player)` | signal `attack_mode_changed(e)` |
| Native | `refii_mod_attack_mode(player)` | `REFII_MOD_ON(AttackModeChanged, fn)` |

- The mode is 1 melee, 2 ranged, 3 magic (`Refii.ATTACK_MODE_MELEE` / `_RANGED` / `_MAGIC`,
  `REFII_ATTACK_MODE_*`), and 0 for a player no keyboard plays on this machine — a joypad has no
  mode, its attacks are its own buttons.
- It belongs to the player the keyboard's pad is for the mods — the `player` its claims, presses
  and `input_action` events name — and is stated on that machine only.
- The event follows each change, and Godot restates the mode once when the game comes up, so a
  mod's Lua can read it from its first tick.

### Rebinding

The player rebinds a mod's actions in the game on the one Controls page: **Options › Controls ›
Controller and Keyboard** lists the controller and the keyboard first, then every enabled mod that
declares actions, under the mod's title. A mod's section has a key row for each action that
declares `keys` and a pad row for each that declares `buttons`; an `axis` is never rebound. Left
and right step a row through the mod's default, Unbound, then every bindable key or pad input;
stepping to the far left ("Press a key..." / "Press a button...") takes the next key or button the
player presses; Escape cancels. A writes the page, B leaves it.

- Rebinds live in `refii.toml` beside the game's own keys: `key_<mod>.<action>` (a comma list of
  Godot key names) and `pad_<mod>.<action>` (a comma list of `buttons` names), `none` for
  unbound. A key absent or empty leaves the mod's binding in force, and a row left at its default
  stores nothing, so a later change to the mod's default reaches the player.
- The game reads them every two seconds and at once after the page commits, so a hand edit while
  the game runs lands too. A row steps through single inputs; a comma list written by hand is
  kept as one more position at the right end.
- The launcher's settings panel lists the same rows, a "Controls: <mod title>" group per enabled
  mod; an empty value stands for the mod's own binding, so the two places agree on every key.

### Pad bindings name the game's buttons

The player can remap their controller (`padmap_<button>` / `padmap2_<button>`, the Controls page's
controller rows): which physical button presses each of the GAME's buttons. That remap applies
first, everywhere: **a mod's `buttons`, `chord` and `pad_<mod>.<action>` name the game's buttons**,
not the physical ones. An action on `"A"` fires on whatever the player bound to the game's A (the
bottom button by default), a claim on it withholds the game's A, and the prompt glyph for it shows
the physical button the player presses. One remap moves the game and every mod together, and a
mod never sees which controller is in the player's hands. A `JoyButton` the game has no use for
(`Guide`, a paddle) is read as itself. The `InputMap` action `mod.<mod>.<action>` is registered
with the physical inputs player one's remap binds to those game buttons. A `"Press a button..."`
capture on a mod's pad row stores the game button the pressed one stands for.
