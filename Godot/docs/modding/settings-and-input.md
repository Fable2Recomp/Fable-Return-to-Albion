# Settings and input

Numbers a player can tune, and buttons a mod can bind.

## Contents

- [settings.json](#settingsjson)
- [The settings page: settings.ui.json](#the-settings-page-settingsuijson)
- [Input actions: input.json](#input-actions-inputjson)
  - [Who presses it](#who-presses-it)
  - [The input_action event](#the-input_action-event)
  - [Rebinding](#rebinding)

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
	"dash": { "label": "Dash", "keys": ["F"], "buttons": ["L3"], "axis": null },
	"aim":  { "label": "Aim",  "keys": ["Ctrl"], "buttons": ["LT"], "axis": "-RightY" }
}
```

| Field | Meaning |
|---|---|
| `label` | the name the rebinding page shows; defaults to the action's name |
| `keys` | Godot key names: `F`, `Ctrl`, `Space`, `F5`, `BracketLeft`, … |
| `buttons` | `JoyButton` names (`A`, `LeftShoulder`, `DpadUp`, `Guide`, …) or the Xbox aliases `A B X Y LB RB L3 R3 LT RT Start Back`; `LT` and `RT` are the triggers, pressed past half their travel |
| `axis` | one `JoyAxis` name (`LeftX`, `LeftY`, `RightX`, `RightY`, `TriggerLeft`, `TriggerRight`) or null; pressed past half deflection either way, or one way with a leading `+` / `-` |

- A list field also takes a single string.
- An action with nothing bound is ignored with a warning.
- A later mod in the load order declaring the same scoped name replaces the earlier one.
- Each action is also registered in Godot's `InputMap` as `mod.<mod>.<action>`, for Godot
  scripts.
- The editor writes entries with `RefiiModApi.SetInputAction(action, binding)` (every name is
  checked) and `RemoveInputAction(action)`.

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

### Rebinding

The player rebinds a mod's actions in the game: **Options › Controls** folds the game's own
Controls page with one row per enabled mod that declares actions. The mod's page has a key row for
each action that declares `keys` and a pad row for each that declares `buttons`; an `axis` is
never rebound. Left and right step a row through the mod's default, Unbound, then every bindable
key or pad input; A writes the page, B leaves it.

- Rebinds live in `refii.toml` beside the game's own keys: `key_<mod>.<action>` (a comma list of
  Godot key names) and `pad_<mod>.<action>` (a comma list of `buttons` names), `none` for
  unbound. A key absent or empty leaves the mod's binding in force, and a row left at its default
  stores nothing, so a later change to the mod's default reaches the player.
- The game reads them every two seconds and at once after the page commits, so a hand edit while
  the game runs lands too. The page binds one input per row; `refii.toml` takes the comma list,
  shown on the page by its first name.
- The launcher's settings panel lists the same rows, a "Controls: <mod title>" group per enabled
  mod; an empty value stands for the mod's own binding, so the two places agree on every key.
