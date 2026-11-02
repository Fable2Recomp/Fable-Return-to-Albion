# The Refii modding guide

A living guide to modding Fable II under Refii: what a mod is, how it reaches the game, and every
event, call and file the mod API offers. It ships with the modding SDK (`sdk/docs/modding/`) and
the Mod Studio shows it on the Modding Guide tab, under **Mod API Guide**.

Mod API version: **2**. The version rises only when an event's fields change meaning or an event
is removed; new events and new trailing fields do not bump it.

## Contents

- [Start here](#start-here)
  - [What a mod is](#what-a-mod-is)
  - [The three surfaces](#the-three-surfaces)
  - [Your first mod in five minutes](#your-first-mod-in-five-minutes)
  - [Rules every mod follows](#rules-every-mod-follows)
- [Concepts](#concepts)
- [References](#references)

## Start here

### What a mod is

A mod is one package, `<name>.f2mod`, in the game's `assets/mods/` folder. It is an overlay on
the game's files: whatever it ships stands in for, or merges into, what the game reads. A package
can hold any mix of:

| Kind | Files | Chapter |
|---|---|---|
| Lua | `mod/init.lua`, `mod/*.lua`, `mod/camera.lua`, `mod/frontend.lua` | [Events and verdicts](events.md) |
| Godot scripts | `godot/*.gd` | [Events and verdicts](events.md#godot-scripts) |
| Native code (a "hard mod") | `native/*.cpp` | [Events and verdicts](events.md#native-handlers) |
| Settings and controls | `settings.json`, `settings.ui.json`, `input.json` | [Settings and input](settings-and-input.md) |
| Database rows and text | `*.gdb.csv`, `*.gdb.tu`, `*.babel.csv` | [GDB and text overlays](gdb-and-text.md) |
| Art | `*.tex.png`, `*.mdl.glb`, `*.bgf` | [Models and textures](models-and-textures.md) |
| UI | `ui/hud.json`, `ui/templates.json`, `ui/abilities.json`, `ui/spellwheel.json` | [UI and HUD](ui-and-hud.md) |
| Effects | `fx/effects/*.json`, `fx/recipes.json` | [Effects](effects.md) |
| Spells | `spells/*.json` | [Gameplay](gameplay.md#custom-spells) |

`mod.toml` at the package root names the mod (`name`, `version`, `author`, `description`).
Which mods are on, and in what order, is the player's `mods/loadorder.toml`: the list is highest
priority first, and the higher mod wins a file two mods both ship.

### The three surfaces

The mod API is **events first, hooks second**. The game's seams are named, typed events a mod
subscribes to; a direct hook on a game function is the primitive underneath, for a seam no event
covers yet. Every event and almost every call exists on three surfaces:

| Surface | Where it lives | Good for |
|---|---|---|
| **Lua** | `mod/init.lua`, run in the game's own Lua VM after the shipped scripts | gameplay rules, quests, anything the shipped scripts can do; no compiler needed |
| **Godot script** | `godot/*.gd`, run inside the game's scene tree | visuals, audio, UI, anything Godot draws |
| **Native** | `native/*.cpp`, compiled into the game on the player's machine | per-frame work, guest memory, hooks on game functions |

The three share one event bus, one value store and one set of handles, so a Lua rule and a Godot
effect can work on the same hit.

### Your first mod in five minutes

1. **Make the mod.** In the Mod Studio, open the ☰ menu and pick **+ New Mod**, or **New mod
   from template** → **Combat tweak**. The Studio writes the package into `assets/mods/`.
2. **Write a rule.** `mod/init.lua`:

   ```lua
   -- Heroes hit harder; the multiplier is a setting the player can tune.
   local multiplier = Refii.setting("my_mod.hero_damage", 1.5)

   Refii.on("hit", function(e)
     if e.attacker_player ~= 0 then        -- 1 or 2: a player's hero dealt the hit
       e.damage = e.damage * multiplier    -- writable: the victim takes this
     end
   end)
   ```

   and `settings.json`:

   ```json
   { "my_mod": { "hero_damage": 1.5 } }
   ```

3. **Turn it on.** In the launcher's Mods tab, tick the mod.
4. **Play.** Launch the game and hit something.
5. **Iterate.** With the game running, the Studio's **🎮 Live Game** tab → **↻ Reload mods + Lua**
   runs your edited Lua without a restart. Errors are listed there too. See
   [Live iteration](live-iteration.md).

The same rule as a Godot script (`godot/hit_rule.gd`):

```gdscript
extends Node

func _ready() -> void:
	Engine.get_singleton("RefiiHost").on_verdict("hit", _on_hit)

func _on_hit(e: Dictionary) -> bool:
	if e.attacker_player != 0:
		e.damage *= 1.5
	return false   # true would drop the hit
```

and as native code (`native/handler.cpp`):

```cpp
#include "core/refii_mod.h"

static void OnHit(refii::mods::HitEvent& e) {
  if (e.attacker_player != 0) e.damage *= 1.5f;
}
REFII_MOD_ON(Hit, OnHit);
```

### Rules every mod follows

- **Both players, always.** Co-op is two heroes, and every event and call serves both. Events
  carry the player they are about (1 or 2); helpers take a player or an entity. Never assume
  player one. See [Handles and players](handles-and-players.md).
- **Handles, not addresses.** Lua and Godot scripts get small integer handles for game objects
  and `"0x…"` strings for hashes. See [Handles and players](handles-and-players.md#handles).
- **Namespace your keys.** Settings, published values, player values and save keys start with
  your mod's folder name and a dot: `my_mod.hero_damage`.
- **Never ship an edited copy of a shipped script.** Patch the shipped tables from `init.lua`.
- **Handlers are short.** They run on the game's thread inside the seam; never block.

## Concepts

| Chapter | What it covers |
|---|---|
| [Events and verdicts](events.md) | subscribing, cancelling, writable fields, priorities, nested and deferred events, Godot verdicts, native handlers and hooks |
| [Handles and players](handles-and-players.md) | handles and hashes, both players, the player store, relayed events and replicas, player two's screen, partner messages |
| [Settings and input](settings-and-input.md) | `settings.json`, the in-game settings page, input actions and rebinding |
| [UI and HUD](ui-and-hud.md) | HUD layout and copies, text pieces, modals, mod menus, the title screen, mod abilities, spell picks, markers, fades and meters |
| [Effects](effects.md) | mod effects, `play_fx`, helper points, lights, the FX Lab |
| [Gameplay](gameplay.md) | appearance, the target lock, status effects, knockback, movement, actions, live casts, custom spells, sound, animation, opinion, NPC orders, time and weather |
| [GDB and text overlays](gdb-and-text.md) | database rows, new records, text rows |
| [Models and textures](models-and-textures.md) | textures, models, presentations, art through the mod mount |
| [Cutscenes and the camera](cutscenes.md) | knowing when the game holds the hero's camera and controls; each player's camera mode, a first-person hold, hidden parts |
| [Saves, sharing and co-op sync](saves-and-sharing.md) | installing and sharing, co-op mod checks, mod sync, how mods fuse into saves |
| [Live iteration](live-iteration.md) | reloading into a running game, errors, the Studio's scripting class |

## References

| Reference | What it lists |
|---|---|
| [Events](reference-events.md) | every event: number, names, when it fires, what cancelling does, its seam |
| [Calls](reference-calls.md) | every Lua call, native helper and Godot `RefiiHost` member |
| [Commands and queries](reference-commands.md) | Godot queries and their answers, the debug channel's mod operations, `RefiiModApi` in the editor, the game's own Lua natives |

The Mod Studio's **⚡ Events & the Mod API** page lists every event's fields with their meaning,
generated from the same catalog the game uses, with a copy button for a native or Lua handler.
