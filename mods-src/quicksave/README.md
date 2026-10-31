# Quicksave

Bethesda-style quicksave and quickload.

| Key | Does |
|---|---|
| F8 | **Quicksave**: the game saves as it does itself, and a copy of that save is kept as your quicksave |
| F9 | **Quickload**: loads your quicksave back, in the game, with the loading screen — no trip to the title |

A notice says what happened: "Quicksaved.", "Quickloaded the save from 14:32.", or why not ("You
can't save right now." where the game itself would not save, "There is no quicksave yet."). The keys
work in play only, not in menus, and alone only, not in co-op. Both are rebindable in
Options › Controls.

**Your own save is safe.** F8 is the game's normal save, so it updates your save as an autosave
would. F9 leaves your save as it was: the quicksave stands in for it only while it loads, and your
save (and the quest's own "fail quest" save) are put back once the level is up, or at the next
start if the game crashed meanwhile. If you quickload and then quit without saving, Continue loads
your last real save, not the quicksave.

There is one quicksave per hero, replaced by each F8. It is kept beside the save, in
`<save root>/<profile>/refii-snapshots/<hero>/quicksave.quick/`, and never shows in Load Game.

## How it works

- `mod/init.lua`: `Quicksave.save(n)` checks `Player.IsAbleToSaveGame` and calls
  `AutoSaveGame(true, true)`, then answers through the published values `quicksave.answer` and
  `quicksave.seq`.
- `godot/quicksave.gd`: the keys (`input.json`), the notices, and the copy and load through the
  host's save snapshots — `save_snapshot` on `save_written`, `snapshot_time`, `load_snapshot` (see
  the modding guide, *Saves, sharing and co-op sync › Snapshots*). The load is the game's own
  in-game reload of its fail-quest save, staged with the quicksave and put back once the level
  stands.
