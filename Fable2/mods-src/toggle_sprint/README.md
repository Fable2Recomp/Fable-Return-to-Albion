# Toggle Sprint

Sprint is a toggle instead of a hold: press Shift (or A on a pad) once to start sprinting and
again to stop. Standing still for a moment (`stop_after`, 0.5 s, in Options › Mods) ends it too, so
the next step is a walk until you press again. Each co-op player has their own toggle, and both
players' sprints end on standing still.

## How it works

This mod is written as a Godot script: everything is `godot/toggle_sprint.gd` on the `RefiiHost`
singleton, and the mod ships no Lua. The game sprints while A is held (the keyboard's Shift holds
A), told by two game actions: `ACTION_SPRINT_SET` (66) on the press and `ACTION_SPRINT_RELEASE` (67)
on the release; the release ends a sprint only while the sprint component's flag is set. The toggle
is a `game_action` verdict (`on_verdict`) that cancels one of the two on each press:

| Press | SET (66) | RELEASE (67) |
|---|---|---|
| while not sprinting | passes: the sprint starts | cancelled: the game keeps the sprint |
| while sprinting | cancelled: already sprinting | passes: the sprint ends |

The flag is the game's own, read inside the verdict with `host.read("sprinting", [p])`: the verdict
runs inside the game's own action, on the game's thread, so it reads the flag as it stands when the
press arrives — as a Lua handler reads `Refii.sprinting(p)`. So when the game ends a sprint by itself
the next press starts a new one rather than being taken for the second press. The latch clears on
every level load.

The toggle keeps the game's own SET and RELEASE rather than holding A with `press(p, "A", 1)`: a
pressed A is OR'd into the player's pad, so while it was held the player's own presses of A would
never reach the game as edges — the second press could not be seen — and every other use of A the
game reads while it is down would see it held.

## Standing still

Each guest tick (the `tick` signal), for each player whose hero is played on this machine
(`player_here`) and who is in play (`in_play`): while the hero's own velocity (`query velocity`,
asked each tick) across the ground is under 1 m/s — slower than a walk, so the slide to a stop
counts — the still time adds up, and a press starts it again. At `stop_after` seconds
`end_sprint(p)` runs the sprint button's release as the game runs it: while the flag is set, the
flag cleared and the sprint movement stopped (nothing without it). The next press of A is a fresh
one.
