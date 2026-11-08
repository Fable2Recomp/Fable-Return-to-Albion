# First Person

Play Fable II from the hero's eyes. Walk, fight and look around with the mouse or the right stick.
Your body stays in view when you look down; the head (with its hair and hat) is hidden so it never
fills the screen. **H** switches between first and third person (rebind it in Options › Controls).

Each player has their own: in split screen and in an online game whoever presses H switches their
own view, and each holds first person or not on their own. On one shared screen the camera frames
both heroes, so it stays the game's own there.

The switch is written as a Godot script (`godot/first_person.gd`); the game's camera is game Lua, so
what is adjusted inside it stays Lua (`mod/camera.lua`).

On a phone the **1st Person** pill in the top row of the touch controls is H.

## How it works

| Piece | File | What it does |
|---|---|---|
| The switch | `godot/first_person.gd`, `input.json` | `first_person.toggle` (H) flips first person for the player who pressed it (`input_action`'s `player`) and states it as that player's camera hold: `camera_override(p, CAMERA_FIRST_PERSON, {"fov": …, "pitch_limit": …, "aim": …})`, or `camera_override(p, 0)` to let it go. The hold is the game's own per-player camera override, the one its quests use for their first-person shots, so the game's own camera manager does the rest: a scene or quest that sets its own camera replaces the hold and it comes back after, a full cutscene and the canned actions (chests, doors, ladders, finishers) take over, the gun aim takes over with the third-person-aim setting on, and the hold is never written into a save. Holds are cleared each time the mods' Lua loads, so the script states them again on `lua_ready` (and starts each player's toggle as `start_on` says, as every new game VM does); it states them again when a hold setting changes (`setting_changed`), when an interactive scene starts or ends (the `cutscene` signal, the scenes setting) and when a hero arrives or leaves (`hero_ready`, `hero_gone`: a co-op partner makes a shared screen or not). No hold on a shared screen (`coop_mode()` 1 without `split_screen()`). Each hold is also stated as the player value `first_person.held` (1 or 0) for each player this machine plays (`player_here`): the camera's Lua reads it there. |
| The camera | `mod/camera.lua` | What the game's first-person camera (`camera/firstpersoncamera.lua`, `FirstPersonCamera`) takes no data for, on a camera whose player holds first person (`Refii.player_value(p, "first_person.held")`, which the script states) and that the game's own look button did not open: only the look input turns it (the shipped look mode also turned with the left stick, which here walks); the eye sits a little above and in front of the head; the hero turns to the view as `face_camera` says; the shipped body fade is left out. With the scenes setting on, a scene's look-at and framed-shot overrides (`SimpleCamera`, `SACCamera` in the manager's override set) become this camera, which faces what they frame (the speaker's mouth) until you look away. Each camera is its own player's, so every wrap serves either player. |
| The head | the core | Refii hides a hero's head, hair and hat in the drawing while that hero's player has the first-person camera (`camera_mode` 8: the game's own look button and this mod's hold alike). The body and the shadow keep theirs. |

The game's own first-person button (Z / middle mouse) keeps its shipped behaviour.

## Settings (Options › Mods › First Person)

| Setting | Default | Meaning |
|---|---|---|
| `start_on` | 1 | each player starts a session in first person |
| `fov` | 80 | field of view, 50–120 degrees (the shipped first-person view is 75); the hold's `fov` |
| `eye_height`, `eye_forward` | 0.05, 0.12 | metres above and in front of the head's collision point |
| `pitch_limit` | 75 | how far up or down you can look; the hold's `pitch_limit` |
| `face_camera` | 1 | turn the hero to the view: 0 never, 1 while standing, 2 always |
| `third_person_aim` | 1 | gun aim uses the game's over-the-shoulder camera; the hold's `aim` |
| `scenes` | 1 | stay in first person in conversations and interactive scenes, facing the speaker |

## Limits

- Both halves of a split screen draw the same hero, so a head hidden for its player's first person
  is hidden in the partner's half too.
- A shared screen (local co-op without split screen) has one camera for both heroes: no first
  person there.
- The eye offset, the left-stick fix, the facing and the scene conversion are wraps of the game's
  first-person camera, because the game's camera takes no data for them.

## Iterating

The launcher packs this folder into `assets/mods/first_person.f2mod` on start. With the game
running, the Studio's Live Game tab (or the debug op `mods-remount` with `lua=true`) reloads the
Lua; the camera wrappers are reinstalled on every run for that reason. The hold lives in the player
store, so a Lua reload keeps it.
