# Toggle Sprint

Sprint is a toggle instead of a hold: press Shift (or A on a pad) once to start sprinting and
again to stop. Standing still for a moment (`stop_after`, 0.5 s, in Options › Mods) ends it too, so
the next step is a walk until you press again. Each co-op player has their own toggle. Lua and one
Godot script, nothing is compiled.

## How it works

`mod/init.lua`. The game sprints while A is held (the keyboard's Shift holds A), told by two
game actions: `GAME_ACTION_SPRINT_SET` (66) on the press and `GAME_ACTION_SPRINT_RELEASE` (67)
on the release. A mod can cancel a game action (the `game_action` event) but not send one, so:

| Press | SET (66) | RELEASE (67) |
|---|---|---|
| first | passes: the sprint starts | cancelled: the game keeps holding A |
| second | cancelled: already sprinting | passes: the sprint ends |

The latch clears on every level load.

## Standing still

The game ends a sprint only when the RELEASE reaches it, and a mod cannot send one (neither
`Player.SetPlayerSprintingAllowed` nor `Debug.SetHeroSprintingEnabled` ends a held sprint). But the keyboard's sprint is read from Godot's key state
(`GameInput.KeyboardMouse.cs`), so after `stop_after` seconds still, `mod/init.lua` publishes
`toggle_sprint.park_request` and `godot/end_sprint.gd` taps Shift with `Input.parse_input_event`.
The tap reaches the hero as SET and RELEASE: press two, which ends the sprint. The request repeats
a second later if the tap never arrived.

This is player one's: the keyboard is player one. Player two's pad sprint stays held until they
press again.
