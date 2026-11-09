# Quicksave

Bethesda-style quicksave and quickload.

| Key | Does |
|---|---|
| F8 | **Quicksave**: the game saves as it does itself, and a copy of that save is kept as your quicksave |
| F9 | **Quickload**: loads your quicksave back, in the game, with the loading screen — no trip to the title |

A notice says what happened: "Quicksaved.", "Quickloaded the save from 14:32.", or why not ("You
can't save right now." where the game itself would not save, "There is no quicksave yet."). The keys
work in play only, not in menus, a cutscene or a loading screen. Both are rebindable in
Options › Controls.

**Single-player only.** A quicksave is a copy of player one's save on this machine, and a quickload
reloads the world for one hero; in co-op both keys say so and do nothing.

**Your own save is safe.** F8 is the game's normal save, so it updates your save as an autosave
would. F9 leaves your save as it was: the quicksave stands in for it only while it loads, and your
save (and the quest's own "fail quest" save) are put back once the level is up, or at the next
start if the game crashed meanwhile. If you quickload and then quit without saving, Continue loads
your last real save, not the quicksave.

There is one quicksave per hero, replaced by each F8. It is kept beside the save, in
`<save root>/<profile>/refii-snapshots/<hero>/quicksave.quick/`, and never shows in Load Game.

On a controller both are on the mod wheel: press **Back** and choose one.

On a phone the **Quicksave** and **Quickload** pills in the top row of the touch controls are F8
and F9.

## How it works

- `mod/init.lua`: `Quicksave.save(p)` asks for the game's own save with `Refii.save_game(p)` —
  `AutoSaveGame(true, true)` where `Player.IsAbleToSaveGame` allows one — and answers whether it
  started with the signal `quicksave.answer` (`"saving"` or `"refused"`).
- `godot/quicksave.gd`: the keys (`input.json`), gated by `in_play`; the notices; the answer
  (`mod_signal`); and the copy and load through the host's save snapshots — `save_snapshot` on the
  game's `save_written` when its `ok` holds, `snapshot_time`, `load_snapshot` (see the modding
  guide, *Saves, sharing and co-op sync › Snapshots*). The load is the game's own in-game reload of
  its fail-quest save, staged with the quicksave and put back once the level stands.
