# Saves, sharing and co-op sync

How a mod travels to players, how two co-op players agree on their mods, and how mods fuse into a
save without breaking it.

## Contents

- [Sharing a mod](#sharing-a-mod)
- [Installing a mod](#installing-a-mod)
- [Co-op: the same mods on both sides](#co-op-the-same-mods-on-both-sides)
- [Mod sync in co-op](#mod-sync-in-co-op)
  - [What the joiner does with the host's set](#what-the-joiner-does-with-the-hosts-set)
  - [Consent and the transfer](#consent-and-the-transfer)
  - [Archon's Toolbox mods](#archons-toolbox-mods)
- [Mods and saves](#mods-and-saves)
  - [The save's layers](#the-saves-layers)
  - [When a save is stamped](#when-a-save-is-stamped)
  - [When a save is loaded](#when-a-save-is-loaded)
  - [Copies for an Xbox 360 or Xenia](#copies-for-an-xbox-360-or-xenia)
  - [For a mod author](#for-a-mod-author)
  - [Snapshots: a quicksave and a quickload](#snapshots-a-quicksave-and-a-quickload)

## Sharing a mod

A mod travels as one `.f2mod`. Players need no dev setup to install one.

The Studio dock's ☰ menu has **📦 Share package…**: choose a folder and the active mod is written
there as `<mod>.f2mod` (a compacted copy of its package), checked by the same rules the launcher's
installer applies. From a script: `RefiiModApi.SharePackage(folder)`, empty on success, otherwise
the reason. **The file name is the install name**: it is what `loadorder.toml` and other mods'
`requires` call the mod, so keep it when you upload.

## Installing a mod

In the launcher's **Mods** tab, **Install from file…** picks a `.f2mod`; **Install from URL…**
downloads an https link with progress. Before anything is installed the player sees the title,
version, whether it replaces an installed mod, the unmet `requires`, and — for a package with
`native/*.cpp` — that it is a **hard mod** whose native code is compiled into the game on their
machine. The package is copied into `assets/mods/` (a same-named one is kept as
`<name>.f2mod.bak`) and added to the load order **disabled**; the player ticks it to turn it on, and
a hard mod also asks for consent to its exact native sources.

Refused: not a `.f2mod`; over 2 GB; an entry with an absolute path, a drive, `.`/`..` or a control
character in its path, or whose bytes lie outside the file; a name that is not letters, digits,
spaces, `_ - .`; a link that is not https, or that redirects more than 5 times or to anything but
https.

## Co-op: the same mods on both sides

Two co-op players run the same mods. A damage multiplier on one side only is a desync, not a
feature, so a mismatch is never played through.

- **What counts**: every enabled mod, in load order, by name, version and the SHA-256 of its
  `.f2mod` bytes, plus the mod API version. Mods that only change visuals count too: any package
  can carry Lua. Two builds of "0.1.0" with different bytes are different mods, and the same mods
  in a different order are a different set.
- **When**: both games restate their set twice a second, and compare as soon as a peer is in the
  session. A mismatch the [mod sync](#mod-sync-in-co-op) cannot settle is refused on BOTH
  machines, and the difference is written on the multiplayer page — "joiner lacks Souls Combat
  0.1.0; host lacks My Mod 1.2".

A new version of your mod is a different mod to a player who has the old one. Tell co-op players
to update together, or let mod sync hand it across.

## Mod sync in co-op

A joiner whose mods differ from the host's is offered the host's: **the host's enabled set, in the
host's order, is the session's** — for the session only.

### What the joiner does with the host's set

| The host's mod is… | The joiner… |
|---|---|
| installed here with the same package bytes (enabled or not) | uses its own copy as-is |
| received from this host in an earlier session | uses the cached copy |
| not on this machine, and data + Lua only | **downloads** it from the host's machine |
| a **hard mod** (anything under `native/`) | cannot take it on: native code is compiled into the game at launch. The join is refused; install it, enable it and relaunch |

- The joiner's own enabled mods the host does not run are **suspended** for the session; one the
  host runs in another version is replaced by the host's. A host with no mods suspends all of the
  joiner's. A joiner's own hard mod the host does not run cannot be suspended, so that join is
  refused too.
- The host never adopts anything: its set wins whichever side has more.
- The joiner's `loadorder.toml`, settings and mods folder are never written. On every session end
  — leave, kick, a lost connection — the joiner's own mods are remounted and their Lua run again.
  Pins are not synced.
- Applying the session set is a live remount with the mods' Lua run again, so Lua, textures, text,
  HUD, templates, FX, settings, input and the databases take effect at once; models and save
  layers were read at boot (see [Live iteration](live-iteration.md)).

### Consent and the transfer

**Consent, once per join, before any byte.** The joiner is asked in the game's own box, naming
every mod to be downloaded with its version and size, the ones used as-is, replaced and suspended,
and stating plainly that mods contain scripts that run inside the game and come from the host's
machine. **Accept** proceeds; **Decline** leaves the session. While a package is received the box
shows progress ("Receiving Souls Combat 0.1.0 from Curious Cat… 2.1 MB / 5.3 MB (mod 1 of 2)"); the
host is told "Your partner is receiving your mods." and, once the sets agree, "Your partner is
running your mods."

**The transfer.** Packages cross on Refii's own reliable stream, one at a time, in 64 KB chunks.
The joiner verifies each package's SHA-256 against the one the host stated before keeping it. A
session may download **64 MB in all** (a plan over it is refused up front and says so). A stream
that fails to open in 30 s, or goes quiet for 60 s, fails the sync and the session is refused; so
does a joiner that has not finished in 15 minutes.

**Where the packages go**: `user://mods-sync/<sha256>.f2mod` — keyed by hash, never the player's
mods folder, never shared on. They are reused the next time the same host's set names them, and
the oldest are evicted past 256 MB.

### Archon's Toolbox mods

Mods running under Archon's Toolbox (the Lua mod manager that patches `gamescripts_r.bnk` and
writes loose scripts under `data/scripts/`) are stated after the player's own mods as
`archon:<NameID>` entries and count in the comparison. They are never synced: loose files in the
game data and a byte-patched script bank are not a package Refii can send. The same mod against
the same bank on both sides is agreed on; anything else blocks the join with the reason — the
joiner lacks it ("install it with Archon's Toolbox, enable it and relaunch"), has it against
another bank ("your copy differs from the host's"), or runs one the host does not ("disable it in
Archon's Toolbox and relaunch").

## Mods and saves

Mods fuse into a save: a mod's items and equipment are record ids in the game's own inventory
data, a mod's player values live in the player store beside the save, and `RefiiModSave` is in
the save's Lua table. Refii stamps every save with the mod set it was written under and filters a
save loaded with some of those mods missing, so the game never meets a record it cannot resolve
and a mod enabled again gets its things back. A Refii save stays the console game's format.

### The save's layers

A save is the folder `user_data_root/<xuid>/4D5307F1/00000001/<name>/` — Xenia's content layout —
holding the game's seven files byte for byte as the console writes them. Refii's own files beside
them form layers that apply only when present; the game reads none of them.

| Layer | File(s) | Present | Holds |
|---|---|---|---|
| base | the game's seven files | always | the save, as a 360 or Xenia reads it |
| `refii` | `refii_save.json` → `"refii"`; `refii_player.txt`, `refii_blob_*.bin` | a save written by Refii | the build, when, and the player store's saved values |
| `mods` | `refii_save.json` → `"mods"` | a save written under at least one mod, or holding filtered records | the stamp and the held records |
| `archon` | `refii_save.json` → `"archon"` | a save written with Archon's Toolbox installed | the toolbox's bank hash, runner version and enabled mods |

`refii_save.json`, schema `refii-save/1`:

```json
{
  "schema": "refii-save/1",
  "refii": { "build": "2", "written": "2026-10-22T20:14:03Z",
             "player_state": ["refii_player.txt", "refii_blob_*.bin"] },
  "mods": {
    "api": 2, "digest": "5c1e…",
    "set": [
      { "name": "souls_combat", "title": "Souls Combat", "version": "0.1.0", "sha256": "…",
        "order": 0, "native": false, "session": false,
        "records": ["0x3A1F0C22"], "keys": ["souls_combat."] }
    ],
    "held": {
      "fancy_hats": { "title": "Fancy Hats", "version": "1.2",
                      "inventory": [ { "n": "InventoryItem", "c": [ … ] } ],
                      "worn": [ { "record": "0x7B2…", "modifier": { "n": "Modifier", "c": [ … ] } } ],
                      "melee": "0x00000000", "ranged": "0x00000000",
                      "player": { "1": { "fancy_hats.polish": "3" } } }
    }
  }
}
```

- `set` is the stamp: every enabled mod when the save was written, in load order, by name, title,
  version and package SHA-256 (the same identity co-op compares), with `native` (a hard mod),
  `session` (applied for a co-op session only: it never fuses, and its absence raises no box),
  `records` (the GDB record ids the mod's patch ADDS) and `keys` (the save-key prefixes the mod
  owns: its name and a dot).
- `held` is what the filter took out of the save for a missing mod, kept to put back: inventory
  items, worn modifiers pointing at them, last-wielded weapon ids among them, and the player
  store's lines under the mod's prefixes.
- Archon's Toolbox mods are listed on the "Mods in this save" page under their own heading, and
  nothing of theirs is filtered: their state lives inside the save's script table, which cannot be
  cut apart safely, so it stays verbatim and the toolbox's own runner deals with a missing mod.

### When a save is stamped

After the game writes a save — manual, autosave, checkpoint, player one's and player two's, an
online joiner's own — the sidecar is written: the running set, the `refii` layer, and the held
records of mods still missing. A mod the save never saw is safe to add: no box, and the next save
stamps it.

### When a save is loaded

The front end's own start (the `start_game` event) runs the check before the game reads anything.
Presence is by NAME: a mod present at another version is not missing.

- Same set, or only additions or version changes: the save loads as it is.
- Mods in the stamp not enabled now (session mods excepted): the start is held, and a box names
  them — *"This save was made with mods that are not enabled: Souls Combat 0.1.0 … Items and
  equipment from them will be left out, and so will their saved values. They are kept with the
  save and come back when the mod is enabled again. Continue?"* **Continue** filters the save and
  starts it; **Cancel** leaves the front end on the save list.
- Held records of a mod enabled again are put back before the game reads the save.

The filter takes the missing mods' records out of every entity's inventory (a chest can hold a
mod's item too), re-points the worn modifiers, zeroes the weapon ids among them and drops the
player store's keys; the hero's removals go under `held`. The unfiltered main save is kept beside
it as `refii_mainsave_unfiltered.bin` until the next save replaces both. Once the save's Lua table
is loaded (`save_loaded`), the missing mods' `RefiiModSave` keys are dropped too. Player two's
hero read off a save is filtered with the same sets; online, a joiner's own save is checked on the
joiner's machine.

The game itself tolerates an unresolved record as a dead inventory entry, so the filter is what
keeps saves clean, not what keeps the game up.

### Copies for an Xbox 360 or Xenia

A Refii save folder copied into a Xenia profile's `4D5307F1/00000001/` loads there; a real 360
takes the same files wrapped into a console-signed package (Refii does not sign one). A save made
with mods carries mod record ids the vanilla game does not know — it tolerates them, but as dead
entries. **Copy for Xbox / Xenia** — X on a card of the Load Game list — writes a vanilla-only copy
under `<assets>/save-export/4D5307F1/00000001/<name>/`: the seven files, filtered of every record
of every mod the save was stamped with, nothing of Refii's beside them. The original keeps
everything. The Load Game list marks a stamped save: *Mods: Souls Combat 0.1.0*.

### For a mod author

- **Declare the records you add in your patch.** The stamp's `records` are what your `.gdb.tu` /
  `.gdb.csv` adds; an item given any other way cannot be filtered for your mod when it is missing.
- **Namespace your save keys.** Player store keys and `RefiiModSave` keys must start with your
  mod's folder name and a dot (`souls_combat.level`): that prefix is what is filtered and restored.
- **Keep ability levels in the player store** (`levelKey` in `ui/abilities.json`), never in the
  game's `HeroAbility` stats: the game's loader has no bound on that index.
- **Keep record ids stable across versions.** A new version is not a missing mod: a save stamped
  with 0.1.0 loads under 0.2.0 unfiltered and is re-stamped on the next save.

### Snapshots: a quicksave and a quickload

A Godot script can keep its own copies of the hero's save and load one back without a trip to the
title (RefiiHost, see [the calls](reference-calls.md#members-and-signals)). The Quicksave mod is
built on them.

- **Taking one.** Have the game save first, the way it saves itself: from Lua,
  `AutoSaveGame(true, true)` when `Player.IsAbleToSaveGame(GetPlayerHero())` allows it. When
  `save_written` arrives with `player` 1, `save_snapshot("<mod>.<name>")` copies every file of the
  save folder to `user_data_root/<xuid>/refii-snapshots/<save>/<mod>.<name>/`, beside the title's
  own `4D5307F1/`, so the Load Game list never shows it. A key holds one copy; taking it again
  replaces it once the new copy is whole.
- **Loading one.** `load_snapshot(key)` loads it through the game's own fail-quest reload
  (`LoadFailQuestSave`, the region-lock box's "reload"): the same load-from-save a Continue runs,
  with the loading screen, in the running game. The snapshot's files go into the save folder for
  the load, its main save also as `failquestsave.bin`. Once `level_loaded` arrives the folder gets
  its own files back: the player's main save and the quest's fail-quest save are as they were, and
  a file the game wrote meanwhile is kept. A load the game never finishes is put back after three
  minutes, and a hold a crash left behind is put back before that save next starts.
- **Per save.** A snapshot belongs to the save it was taken from. `snapshot_time` and
  `load_snapshot` answer for the save the session started or last wrote, so another hero never
  loads it.
- **Alone only.** `load_snapshot` refuses in co-op: the partner's game would not follow the load.
