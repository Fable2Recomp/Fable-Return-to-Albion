# Cutscenes

Knowing when the game has taken a hero's camera and controls away from free play — and leaving LT
to the game while it has.

## Contents

- [Three statements](#three-statements)
- [The event and the query](#the-event-and-the-query)
- [Why these](#why-these)
- [In combat](#in-combat)
- [Co-op](#co-op)
- [The look-at button](#the-look-at-button)

## Three statements

The game states it three ways; Refii reads each where the game's own setter writes it.

| Bit | Name | The game's statement |
|---|---|---|
| 1 | `REFII_CUTSCENE_FULL` / `Refii.CUTSCENE_FULL` | a full cutscene is playing: what `GUI.IsFullCutscenePlaying` answers. Every player's (`player` 0) |
| 2 | `REFII_CUTSCENE_INTERACTIVE` / `Refii.CUTSCENE_INTERACTIVE` | a foreground interactive cutscene holds the hero: `Player.SetForegroundCutsceneInEffect`. The shipped scenes set it with the vignette and the expression-only HUD when a non-background scene starts, and clear it when the last one ends |
| 4 | `REFII_CUTSCENE_LOOK_AT` / `Refii.CUTSCENE_LOOK_AT` | the game has given the hero's HELD LT a camera: the LOOK_AT scope of `Player.SetOverrideCameraMode`, the native under `CameraManager.SetCameraOverride`. Set by a conversation's talking camera, a quest's `SetLookAtCamera` and a scene's camera records |

## The event and the query

| Surface | Event | Query |
|---|---|---|
| Native | `Cutscene` | `refii_mod_in_cutscene(ctx, base, player)` → the bits holding for player 1 or 2 (0: either) |
| Lua | `Refii.on("cutscene", fn)` | `Refii.in_cutscene([p [, kinds]])` → inside, kinds |
| Godot | signal `cutscene` | `in_cutscene(player = 0, kinds = 7)`, `cutscene_kinds(player = 0)`, `query in_cutscene P` |

The `cutscene` event (100) fires per kind on each edge, before `tick`: `player` (1 or 2; 0 for a
full cutscene), `active`, `kind`, `kinds` (every bit holding for the player after the edge),
`hero`. It is read only while someone listens: a handler subscribed mid-scene hears it start on
its first tick.

```lua
Refii.on("cutscene", function(e)
  if e.active and e.kind == Refii.CUTSCENE_LOOK_AT then Refii.release_lock(e.player) end
end)

local inside, kinds = Refii.in_cutscene(1)                       -- any kind, player one
local talking = Refii.in_cutscene(0, Refii.CUTSCENE_LOOK_AT)     -- either player, look-at only
```

A Godot script's `in_cutscene` answers from the event as this host last heard it (heard while any
script is loaded); `query in_cutscene P` asks the game now and answers `kinds`.

## Why these

The question a mod asks is "is LT the game's right now?". The shipped camera scripts answer it
with the LOOK_AT override: while LT is held, a held LT frames what the scene or quest chose — the
speaker, the point of interest — instead of the normal camera. The interactive and full statements
cover the scenes that hold the hero with no LT camera of their own.

## In combat

A LOOK_AT override counts whether or not it supersedes combat (that flag lives in the camera
data's Lua table, never passed to the game). The shipped camera keeps the normal camera in combat
for an override without it, so there a held LT does what it always did. A mod that steps aside
under `in_cutscene` leaves LT exactly as the shipped game has it.

## Co-op

Each machine reads its own game. The two hero statements are per player, so player two's are
player two's hero's. Online, whether the joiner's game runs a scene's script for its own hero (and
so states INTERACTIVE / LOOK_AT there) is unproven; the full cutscene is the state of the machine
that reads it.

Souls Combat steps aside on it: while `Refii.in_cutscene` holds for a hero, LT's press toggles
nothing, the look-at flag passes through as the game writes it, and the rising edge releases any
lock (`mods-src/souls_combat/mod/lockon.lua`).

## The look-at button

`look_at_button` (94) is raised before the game writes a player hero's look-at flag — the byte
every lock camera script reads, what a held LT states — with `down` writable and `cancel` leaving
the flag as it was. Its writers are the camera's LT-down event (1), the hero's release handler (0)
and the API's own `lock_target` / `release_lock`. So a mod whose lock is its own (Souls Combat's
toggled LT) writes `e.down = locked` and the flag equals its lock whoever asks, with no hook of
its own.

Seam: `rex_CPlayer_SetLookAtButtonDown`.
