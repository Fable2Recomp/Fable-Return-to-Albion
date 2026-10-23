# Live iteration

Change a mod while the game runs, see its errors, and drive it all from a script.

## Contents

- [The Live Game tab](#the-live-game-tab)
- [What a reload reaches](#what-a-reload-reaches)
- [init.lua runs again](#initlua-runs-again)
- [Errors](#errors)
- [From a script or the debug channel](#from-a-script-or-the-debug-channel)
- [Trying Lua in a running game](#trying-lua-in-a-running-game)

## The Live Game tab

The Mod Studio's **🎮 Live Game** tab works on a RUNNING game through its debug channel (the game
needs `debug_channel = true` in its package config; the tab says when none is up and checks every
2 s). The Studio writes straight into the mod's package, so what is saved is what reloads. The tab
warns when the game reads mods from another folder than the one the Studio writes.

- **↻ Reload mods in game** folds the installed mods into the running game again: the same passes
  the mount runs at start, on a worker, while the game keeps serving the previous mount (a reload
  takes seconds). Once the new mount is in, everything those passes feed is reset.
- **↻ Reload mods + Lua** does the same, then runs the re-composed Lua (`init.lua`, and
  `camera.lua` once a world is up) in the game VM, after dropping the handlers the previous run
  registered, so no handler is subscribed twice.

## What a reload reaches

| Change | Live after a reload | Why |
|---|---|---|
| Images (`images/`, `.tex`) | yes | refreshed in place in every material and menu holding the texture |
| Text rows (`.babel.csv`) | yes, the text Godot draws | menus, HUD and item names read the reloaded string book |
| HUD pieces (`ui/hud.json`) | yes | the HUD is rebuilt |
| Templates (`ui/templates.json`) | yes | a screen already open takes them when it next opens |
| FX recipes (`fx/recipes.json`) | yes | re-dressed on the next frame |
| Abilities (`ui/abilities.json`), spell picks (`ui/spellwheel.json`) | yes | declared to the engine again |
| Spells (`spells/*.json`) | names, icon and look; the Lua half with **+ Lua** | the spell's Lua joins the init chunk |
| Settings (`settings.json`) | yes | pushed again; the player's choices and live values stand |
| Input actions (`input.json`) | yes | the bindings are read again; the player's rebinds stand over them |
| Godot scripts (`godot/*.gd`) | yes, restarted | their state starts over |
| Lua (`mod/init.lua`, `mod/camera.lua`, the modules `mod/*.lua`) | with **+ Lua** | otherwise at the next session; a chunk's next `require` runs the new file |
| GDB rows (`.gdb.csv`), `.gdb.tu`, `.gdb` | yes | the game's own loader reads the patch again and every record an entity holds takes the new values. A row the base never had **stays until a relaunch**; see [GDB and text](gdb-and-text.md#live-reload-and-its-limits) |
| Models (`.mdl`, `.mdl.glb`) | **relaunch** | a mesh already placed in the world or on a character is not rebuilt |
| `.save`, `.save.tu` | **relaunch** for the world | the game reads its save layers once, at boot; Godot's views refresh now |
| Native code (`native/*.cpp`) | **relaunch** | compiled into the game module at launch |
| Any other file | **relaunch** if the game already loaded it | served from now on, but the game loads most files once |

The reply names, per kind, the changed files that need a relaunch.

## init.lua runs again

On **+ Lua**, `Refii.on` handlers are cleared first; everything else `init.lua` does, it does again
over the live state. A mod that wraps a shipped function would wrap its own wrapper, so guard the
wrap:

```lua
if not MyModWrapped then
  MyModWrapped = true
  local shipped = Villager.SomeFunction
  Villager.SomeFunction = function(...) --[[ ... ]] return shipped(...) end
end
```

`RefiiModSave` keeps its contents.

## Errors

The tab lists the mods' Lua errors as the game reports them, newest first, polled every second
while a game runs: an init error is named by its mod, a handler error by its event
(`handler for "hit"`). Each also lands in the game's log as `[mods] lua error: …`. The game keeps
the last 500; **Clear** empties the list on screen only. In Lua the same list is `RefiiModErrors`.

## From a script or the debug channel

| Call | Answers |
|---|---|
| `RefiiModApi.RemountInGame(lua)` | first line `ok`, then one line per thing refreshed, per kind needing a relaunch, and the Lua's outcome; otherwise the reason |
| `RefiiModApi.ModErrorsInGame()` | every kept error, newest first |
| debug op `{"op":"mods-remount","lua":true}` | when the remount is done: `refreshed`, `relaunch` (`[{kind, reason, paths}]`), `changed`, `mods`, `modsRoot` and `lua` (`{queued, seqBefore, note}`; the Lua's outcome is the first `call-result` numbered after `seqBefore`) |
| debug op `{"op":"mod-errors","since":N}` | `{seq, errors: [{seq, time, mod, text}]}` |

## Trying Lua in a running game

- A Godot script's `host.run_lua(chunk)` runs a chunk in the game VM on the next tick (false when
  over 4 KB).
- The Studio runs Lua in a live game over the debug channel; the shipped scripts show each game
  native's arguments in use.
- `RefiiModApi.ListGameLuaNatives(table)` and `FindGameLuaNatives(text)` find a native to try.
